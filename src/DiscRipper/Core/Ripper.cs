using System.IO.Hashing;

namespace DiscRipper.Core;

public enum TrackRipStatus { Pending, Reading, Verifying, Encoding, AccurateRip, Consistent, Mismatch, Errors, Failed, Cancelled, Done }

public sealed class TrackRipResult
{
    public required TocTrack Track { get; init; }
    public int AudioIndex { get; init; }
    public byte[] Buffer { get; set; } = Array.Empty<byte>();
    public int PcmOffset { get; set; }
    public int PcmLength { get; set; }
    public ReadOnlyMemory<byte> Pcm => Buffer.AsMemory(PcmOffset, PcmLength);

    public uint ArV1 { get; set; }
    public uint ArV2 { get; set; }
    public ArMatch? Ar { get; set; }
    public uint Crc32 { get; set; }
    public bool SecureRead { get; set; }
    public int SuspectSectors { get; set; }
    public int UnresolvedSectors { get; set; }
    public int ReadErrorsPass1 { get; set; }
    public TrackRipStatus Status { get; set; }
    public string Summary { get; set; } = "";
    public TimeSpan Elapsed { get; set; }
}

/// <summary>
/// Lettura di una traccia con correzione dell'offset.
/// Modalità: lettura veloce + verifica AccurateRip; se non coincide (o non è nel database) seconda lettura
/// completa di confronto e rilettura ostinata ("paranoia") dei soli settori che differiscono.
/// </summary>
public sealed class TrackReader
{
    readonly ISectorSource _drive;
    readonly Toc _toc;
    readonly int _offset;
    readonly int _maxRetries;
    readonly int _readableEnd;
    const int SB = Toc.BytesPerSector;

    public TrackReader(ISectorSource drive, Toc toc, int offsetSamples, int maxRetries)
    {
        _drive = drive; _toc = toc; _offset = offsetSamples;
        _maxRetries = Math.Max(2, maxRetries);
        _readableEnd = toc.AudioAreaEndLba;
    }

    static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);
    static long CeilDiv(long a, long b) => -FloorDiv(-a, b);

    /// <summary>Settori da leggere per ottenere [firstSample, firstSample+count) già corretti per offset.</summary>
    internal static (int lbaStart, int lbaEnd, int byteOffset) Plan(long driveFirstSample, long count)
    {
        long ls = FloorDiv(driveFirstSample, Toc.SamplesPerSector);
        long le = CeilDiv(driveFirstSample + count, Toc.SamplesPerSector);
        int byteOff = (int)((driveFirstSample - ls * Toc.SamplesPerSector) * Toc.BytesPerSample);
        return ((int)ls, (int)le, byteOff);
    }

    /// <summary>Legge [lba, lba+n) in dest. Settori fuori dall'area audio = silenzio. Errori annotati in errs.</summary>
    void ReadChunk(int lba, int n, Span<byte> dest, ISet<int>? errs, CancellationToken ct)
    {
        dest[..(n * SB)].Clear();
        int lo = Math.Max(lba, 0), hi = Math.Min(lba + n, _readableEnd);
        if (lo >= hi) return;
        var sub = dest.Slice((lo - lba) * SB, (hi - lo) * SB);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try { _drive.ReadSectors(lo, hi - lo, sub); return; }
            catch (IOException) { ct.ThrowIfCancellationRequested(); }
        }
        // ripiego: settore per settore
        for (int s = lo; s < hi; s++)
        {
            var one = sub.Slice((s - lo) * SB, SB);
            bool ok = false;
            for (int attempt = 0; attempt < 3 && !ok; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try { _drive.ReadSectors(s, 1, one); ok = true; }
                catch (IOException) { }
            }
            if (!ok) { one.Clear(); errs?.Add(s); }
        }
    }

    void Pass(byte[] raw, int lbaStart, int lbaEnd, ISet<int> errs, Action<double> progress, CancellationToken ct)
    {
        int total = lbaEnd - lbaStart;
        for (int lba = lbaStart; lba < lbaEnd; lba += CdDrive.MaxSectorsPerRead)
        {
            ct.ThrowIfCancellationRequested();
            int n = Math.Min(CdDrive.MaxSectorsPerRead, lbaEnd - lba);
            ReadChunk(lba, n, raw.AsSpan((lba - lbaStart) * SB, n * SB), errs, ct);
            progress((double)(lba - lbaStart + n) / total);
        }
    }

    /// <summary>Svuota la cache del lettore leggendo una zona lontana.</summary>
    void DefeatCache(int nearLba, CancellationToken ct)
    {
        if (_readableEnd < 3000) return;
        var tmp = new byte[CdDrive.MaxSectorsPerRead * SB];
        int far = nearLba > _readableEnd / 2 ? Math.Max(0, nearLba - 15000) : Math.Min(_readableEnd - 60, nearLba + 15000);
        far = Math.Clamp(far, 0, Math.Max(0, _readableEnd - 2 * CdDrive.MaxSectorsPerRead));
        try
        {
            ReadChunk(far, CdDrive.MaxSectorsPerRead, tmp, null, ct);
            ReadChunk(far + CdDrive.MaxSectorsPerRead, CdDrive.MaxSectorsPerRead, tmp, null, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    public TrackRipResult ReadTrack(TocTrack t, int audioIndex, ArDisc? ar, bool paranoia,
        Action<string, double> progress, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int audioCount = _toc.AudioTracks.Count;
        bool isFirst = audioIndex == 0, isLast = audioIndex == audioCount - 1;
        long first = (long)t.StartLba * Toc.SamplesPerSector;
        long count = t.Samples;
        var (lbaStart, lbaEnd, byteOff) = Plan(first + _offset, count);
        var raw = new byte[(lbaEnd - lbaStart) * SB];
        var res = new TrackRipResult
        {
            Track = t, AudioIndex = audioIndex, Buffer = raw, PcmOffset = byteOff, PcmLength = (int)(count * 4)
        };

        // ---- 1ª lettura (veloce)
        var errs1 = new HashSet<int>();
        Pass(raw, lbaStart, lbaEnd, errs1, f => progress("Lettura", f), ct);
        res.ReadErrorsPass1 = errs1.Count;
        Checksums(res, ar, isFirst, isLast);

        if (!paranoia && errs1.Count == 0 && res.Ar is { Found: true })
        {
            res.Status = TrackRipStatus.AccurateRip;
            res.Summary = $"AccurateRip OK (conf. {res.Ar.Confidence})";
            res.Elapsed = sw.Elapsed;
            return res;
        }

        // ---- 2ª lettura di confronto
        res.SecureRead = true;
        var versions = new Dictionary<int, List<(byte[] data, int count)>>();
        var suspects = new SortedSet<int>(errs1);
        foreach (var s in errs1) versions[s] = new();
        DefeatCache(lbaStart, ct);
        var tmp = new byte[CdDrive.MaxSectorsPerRead * SB];
        int total = lbaEnd - lbaStart;
        for (int lba = lbaStart; lba < lbaEnd; lba += CdDrive.MaxSectorsPerRead)
        {
            ct.ThrowIfCancellationRequested();
            int n = Math.Min(CdDrive.MaxSectorsPerRead, lbaEnd - lba);
            var errs2 = new HashSet<int>();
            ReadChunk(lba, n, tmp, errs2, ct);
            for (int i = 0; i < n; i++)
            {
                int s = lba + i;
                var a = raw.AsSpan((s - lbaStart) * SB, SB);
                var b = tmp.AsSpan(i * SB, SB);
                if (errs2.Contains(s))
                {
                    suspects.Add(s);
                    if (!versions.ContainsKey(s)) versions[s] = errs1.Contains(s) ? new() : new() { (a.ToArray(), 1) };
                    continue;
                }
                if (errs1.Contains(s)) { AddVersion(versions[s], b); continue; }
                if (!a.SequenceEqual(b))
                {
                    suspects.Add(s);
                    versions[s] = new() { (a.ToArray(), 1), (b.ToArray(), 1) };
                }
            }
            progress("Verifica", (double)(lba - lbaStart + n) / total);
        }

        // ---- rilettura ostinata dei soli settori sospetti
        res.SuspectSectors = suspects.Count;
        if (suspects.Count > 0)
        {
            var groups = Group(suspects);
            int g = 0;
            foreach (var (gs, gn) in groups)
            {
                g++;
                for (int attempt = 0; attempt < _maxRetries; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    bool allResolved = true;
                    for (int s = gs; s < gs + gn; s++)
                        if (suspects.Contains(s) && !Resolved(versions[s])) { allResolved = false; break; }
                    if (allResolved) break;
                    progress($"Rilettura {g}/{groups.Count}", (double)attempt / _maxRetries);
                    DefeatCache(gs, ct);
                    var errs = new HashSet<int>();
                    ReadChunk(gs, gn, tmp, errs, ct);
                    for (int s = gs; s < gs + gn; s++)
                    {
                        if (!suspects.Contains(s) || errs.Contains(s)) continue;
                        AddVersion(versions[s], tmp.AsSpan((s - gs) * SB, SB));
                    }
                }
            }
            // scelta della versione più frequente
            foreach (var s in suspects)
            {
                var v = versions[s];
                var dst = raw.AsSpan((s - lbaStart) * SB, SB);
                if (v.Count == 0) { dst.Clear(); res.UnresolvedSectors++; continue; }
                var best = v.OrderByDescending(x => x.count).First();
                best.data.CopyTo(dst);
                if (best.count < 2) res.UnresolvedSectors++;
            }
            Checksums(res, ar, isFirst, isLast);
        }

        bool inDb = ar != null;
        if (res.Ar is { Found: true })
        {
            res.Status = TrackRipStatus.AccurateRip;
            res.Summary = res.UnresolvedSectors > 0
                ? $"AccurateRip OK (conf. {res.Ar.Confidence}) — {res.UnresolvedSectors} settori riletti"
                : $"AccurateRip OK (conf. {res.Ar.Confidence}){(suspects.Count > 0 ? $" dopo rilettura di {suspects.Count} settori" : "")}";
        }
        else if (res.UnresolvedSectors > 0)
        {
            res.Status = TrackRipStatus.Errors;
            res.Summary = $"ATTENZIONE: {res.UnresolvedSectors} settori incerti";
        }
        else if (inDb && ar!.MaxConfidence(audioIndex) > 0)
        {
            res.Status = TrackRipStatus.Mismatch;
            res.Summary = suspects.Count > 0
                ? $"Letture coerenti ({suspects.Count} settori riletti), ma diversa da AccurateRip"
                : "Letture coerenti, ma diversa da AccurateRip";
        }
        else
        {
            res.Status = TrackRipStatus.Consistent;
            res.Summary = suspects.Count > 0
                ? $"Letture coerenti dopo rilettura di {suspects.Count} settori"
                : "Letture coerenti (doppia lettura identica)";
        }
        res.Elapsed = sw.Elapsed;
        return res;
    }

    static void Checksums(TrackRipResult r, ArDisc? ar, bool isFirst, bool isLast)
    {
        var pcm = r.Pcm.Span;
        r.Crc32 = System.IO.Hashing.Crc32.HashToUInt32(pcm);
        if (ar == null) { r.Ar = null; return; }
        (r.ArV1, r.ArV2) = ArChecksum.Compute(pcm, isFirst, isLast);
        r.Ar = ar.Match(r.AudioIndex, r.ArV1, r.ArV2);
    }

    static void AddVersion(List<(byte[] data, int count)> list, ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < list.Count; i++)
            if (data.SequenceEqual(list[i].data)) { list[i] = (list[i].data, list[i].count + 1); return; }
        list.Add((data.ToArray(), 1));
    }

    static bool Resolved(List<(byte[] data, int count)> v) => v.Any(x => x.count >= 2);

    static List<(int start, int count)> Group(SortedSet<int> s)
    {
        var res = new List<(int, int)>();
        int gs = -1, gn = 0;
        foreach (var x in s)
        {
            if (gs >= 0 && x < gs + CdDrive.MaxSectorsPerRead) { gn = x - gs + 1; continue; }
            if (gs >= 0) res.Add((gs, gn));
            gs = x; gn = 1;
        }
        if (gs >= 0) res.Add((gs, gn));
        return res;
    }

    // ------------------------------------------------------------------ rilevamento offset

    /// <summary>Legge una traccia con margine e cerca l'offset che fa coincidere AccurateRip.</summary>
    public static (int offset, int confidence, int trackNumber)? DetectOffset(ISectorSource drive, Toc toc, ArDisc ar,
        Action<double> progress, CancellationToken ct)
    {
        var audio = toc.AudioTracks;
        int n = audio.Count;
        // traccia centrale più corta ma di almeno 20 s (scansione completa); altrimenti la prima
        var candidates = Enumerable.Range(0, n)
            .Where(i => i > 0 && i < n - 1 && audio[i].Sectors >= 75 * 20 && ar.MaxConfidence(i) > 0)
            .OrderBy(i => audio[i].Sectors).ToList();
        if (candidates.Count == 0)
            candidates = Enumerable.Range(0, n).Where(i => ar.MaxConfidence(i) > 0).OrderBy(i => audio[i].Sectors).Take(1).ToList();
        if (candidates.Count == 0) return null;

        var reader = new TrackReader(drive, toc, 0, 2);
        foreach (var idx in candidates.Take(2))
        {
            var t = audio[idx];
            int margin = ArChecksum.DetectMargin;
            long first = (long)t.StartLba * Toc.SamplesPerSector - margin;
            long count = t.Samples + 2L * margin;
            var (ls, le, off) = Plan(first, count);
            var raw = new byte[(le - ls) * SB];
            reader.Pass(raw, ls, le, new HashSet<int>(), progress, ct);
            var span = raw.AsSpan(off, (int)(count * 4));
            var r = ArChecksum.DetectOffset(span, margin, (int)t.Samples, idx, n, ar);
            if (r != null) return (r.Value.offset, r.Value.confidence, t.Number);
        }
        return null;
    }
}
