using System.Text;
using System.Threading.Channels;

namespace DiscRipper.Core;

public enum RipEventKind { Log, TrackStatus, Progress, Phase }

public sealed record RipEvent(RipEventKind Kind, string Text = "", int AudioIndex = -1,
    TrackRipStatus Status = TrackRipStatus.Pending, double Fraction = 0, bool Warning = false);

public sealed class RipJob
{
    public required CdDriveInfo DriveInfo { get; init; }
    public required Toc Toc { get; init; }
    public required AlbumMeta Meta { get; init; }
    public required List<int> SelectedAudioIndexes { get; init; }
    public required AppSettings Settings { get; init; }
    public byte[]? Cover { get; init; }
    public ArDisc? Ar { get; init; }
    public bool ArLookupFailed { get; init; }

    public string AlbumDir { get; private set; } = "";
    int _errors;
    public int ErrorCount => _errors;

    readonly StringBuilder _log = new();
    IProgress<RipEvent> _p = null!;

    void Log(string s, bool warn = false)
    {
        lock (_log) _log.AppendLine(s);
        _p.Report(new RipEvent(RipEventKind.Log, s, Warning: warn));
    }

    public async Task RunAsync(IProgress<RipEvent> progress, CancellationToken ct)
    {
        _p = progress;
        var s = Settings;
        var formats = FormatInfo.All.Where(f => s.Formats.HasFlag(f)).ToList();
        if (formats.Count == 0) throw new InvalidOperationException("Nessun formato di uscita selezionato.");
        string? ffmpeg = null;
        if (formats.Any(f => f != OutputFormat.Wav))
        {
            ffmpeg = AudioEncoder.FindFfmpeg() ?? throw new InvalidOperationException("ffmpeg.exe non trovato accanto a DiscRipper.exe.");
        }

        // ---- destinazione
        if (string.IsNullOrWhiteSpace(s.OutputRoot)) throw new InvalidOperationException("Cartella di destinazione non impostata.");
        if (!Directory.Exists(s.OutputRoot))
            throw new InvalidOperationException($"La destinazione \"{s.OutputRoot}\" non è raggiungibile (unità di rete scollegata?).");
        AlbumDir = PathBuilder.AlbumFolder(s.OutputRoot, Meta);
        Directory.CreateDirectory(AlbumDir);
        string tempDir = Path.Combine(Path.GetTempPath(), "DiscRipper", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        Log($"DiscRipper {typeof(RipJob).Assembly.GetName().Version?.ToString(3)} — {DateTime.Now:dd/MM/yyyy HH:mm}");
        Log($"Lettore: {DriveInfo}");
        Log($"Album: {Meta.Artist} — {Meta.Album}{(Meta.Year.Length > 0 ? $" ({Meta.Year})" : "")}  [fonte: {Meta.Source}]");
        Log($"MusicBrainz disc ID: {Toc.MusicBrainzDiscId()}   CDDB: {Toc.CddbDiscId():x8}");
        Log($"Destinazione: {AlbumDir}");
        Log($"Formati: {string.Join(", ", formats.Select(FormatInfo.Label))}");

        using var drive = new CdDrive(DriveInfo.Letter);

        // ---- offset del lettore
        var (offset, detected) = s.GetOffset(DriveInfo.Key);
        if (!detected && Ar != null)
        {
            _p.Report(new RipEvent(RipEventKind.Phase, "Rilevo l'offset del lettore…"));
            Log("Offset del lettore non ancora rilevato: lo cerco con AccurateRip…");
            var det = await Task.Run(() => TrackReader.DetectOffset(drive, Toc, Ar,
                f => _p.Report(new RipEvent(RipEventKind.Progress, "Rilevamento offset", Fraction: f)), ct), ct);
            if (det != null)
            {
                offset = det.Value.offset; detected = true;
                s.DriveOffsets[DriveInfo.Key] = offset; s.Save();
                Log($"Offset rilevato: {offset:+0;-0;0} campioni (traccia {det.Value.trackNumber}, confidenza {det.Value.confidence}) — salvato per questo lettore.");
            }
            else Log($"Offset non rilevato su questo disco: uso {offset:+0;-0;0} (predefinito).", true);
        }
        else Log($"Offset di lettura: {offset:+0;-0;0} campioni{(detected ? "" : " (valore predefinito, non verificato)")}");

        if (Ar != null) Log($"AccurateRip: disco presente nel database ({Ar.Pressings.Count} stampe).");
        else if (ArLookupFailed) Log("AccurateRip: database non raggiungibile — verifica con doppia lettura.", true);
        else Log("AccurateRip: disco non presente nel database — verifica con doppia lettura.");
        Log(s.ParanoiaAlways ? "Modalità: paranoia sempre attiva (doppia lettura su ogni traccia)." : "Modalità: veloce + AccurateRip, paranoia automatica se serve.");

        if (s.ReadSpeed > 0) { drive.SetSpeed(s.ReadSpeed); Log($"Velocità di lettura limitata a {s.ReadSpeed}x."); }
        else drive.SetSpeed(0);

        // ---- copertina
        if (Cover != null && s.SaveCoverJpg)
        {
            try
            {
                bool png = Cover.Length > 4 && Cover[0] == 0x89 && Cover[1] == 0x50;
                await File.WriteAllBytesAsync(Path.Combine(AlbumDir, png ? "cover.png" : "cover.jpg"), Cover, ct);
            }
            catch (Exception ex) { Log($"Copertina non salvata: {ex.Message}", true); }
        }

        Log("");
        var audio = Toc.AudioTracks;
        var reader = new TrackReader(drive, Toc, offset, s.MaxRetries);
        long totalSectors = SelectedAudioIndexes.Sum(i => (long)audio[i].Sectors);
        long doneSectors = 0;

        var channel = Channel.CreateBounded<TrackRipResult>(2);
        var encoder = Task.Run(() => EncodeLoop(channel.Reader, formats, ffmpeg, tempDir, ct), ct);

        try
        {
            foreach (var idx in SelectedAudioIndexes)
            {
                ct.ThrowIfCancellationRequested();
                var t = audio[idx];
                _p.Report(new RipEvent(RipEventKind.TrackStatus, "Lettura…", idx, TrackRipStatus.Reading));
                long before = doneSectors;
                TrackRipResult r = await Task.Run(() => reader.ReadTrack(t, idx, Ar, s.ParanoiaAlways, (phase, f) =>
                {
                    double frac = phase == "Lettura" ? f : 1.0;
                    _p.Report(new RipEvent(RipEventKind.Progress, $"Traccia {t.Number:D2}: {phase}",
                        idx, Fraction: (before + frac * t.Sectors) / Math.Max(1, totalSectors)));
                    if (phase != "Lettura")
                        _p.Report(new RipEvent(RipEventKind.TrackStatus, $"{phase} {f:P0}", idx, TrackRipStatus.Verifying));
                }, ct), ct);
                doneSectors += t.Sectors;

                string arTxt = Ar != null ? $"  AR v1 {r.ArV1:X8} v2 {r.ArV2:X8}" : "";
                Log($"Traccia {t.Number:D2}  {t.Duration:mm\\:ss}  CRC32 {r.Crc32:X8}{arTxt}  {(r.SecureRead ? "[doppia lettura] " : "")}{r.Summary}  ({r.Elapsed.TotalSeconds:0}s)",
                    r.Status is TrackRipStatus.Errors or TrackRipStatus.Mismatch);
                if (r.ReadErrorsPass1 > 0) Log($"           {r.ReadErrorsPass1} errori di lettura nella prima passata", true);
                if (t.PreEmphasis) Log("           traccia con pre-enfasi (flag nella TOC)", true);
                if (r.Status == TrackRipStatus.Errors) Interlocked.Increment(ref _errors);
                _p.Report(new RipEvent(RipEventKind.TrackStatus, r.Summary + " — codifica…", idx, r.Status));
                await channel.Writer.WriteAsync(r, ct);
            }
        }
        finally
        {
            channel.Writer.TryComplete();
        }
        _p.Report(new RipEvent(RipEventKind.Phase, "Completo la codifica…"));
        await encoder;

        Log("");
        Log(ErrorCount == 0 ? "Estrazione completata senza errori." : $"Estrazione completata con {ErrorCount} tracce problematiche.", ErrorCount > 0);

        if (s.WriteLog)
        {
            try { await File.WriteAllTextAsync(Path.Combine(AlbumDir, PathBuilder.Clean($"{Meta.Artist} - {Meta.Album}") + ".log"), _log.ToString(), Encoding.UTF8, ct); }
            catch (Exception ex) { Log($"Log non salvato: {ex.Message}", true); }
        }
        try { Directory.Delete(tempDir, true); } catch { }
        if (s.EjectWhenDone) { try { drive.Eject(); } catch { } }
    }

    async Task EncodeLoop(ChannelReader<TrackRipResult> reader, List<OutputFormat> formats, string? ffmpeg, string tempDir, CancellationToken ct)
    {
        int total = Toc.AudioTracks.Count;
        await foreach (var r in reader.ReadAllAsync(ct))
        {
            var tm = Meta.Tracks.FirstOrDefault(x => x.Number == r.Track.Number)
                     ?? new TrackMeta { Number = r.Track.Number, Title = $"Traccia {r.Track.Number:D2}", Artist = Meta.Artist };
            int index = r.AudioIndex + 1;
            string baseName = PathBuilder.TrackFileName(Meta, tm, index, total);
            _p.Report(new RipEvent(RipEventKind.TrackStatus, r.Summary + " — codifica…", r.AudioIndex, TrackRipStatus.Encoding));
            bool failed = false;
            await Parallel.ForEachAsync(formats, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount), CancellationToken = ct },
                async (f, token) =>
                {
                    string tmp = Path.Combine(tempDir, $"{index:D2}_{f}{FormatInfo.Ext(f)}");
                    string dst = Path.Combine(AlbumDir, baseName + FormatInfo.Ext(f));
                    try
                    {
                        await AudioEncoder.EncodeAsync(ffmpeg ?? "", r.Pcm, f, Settings, tmp, token);
                        try { Tagger.Write(tmp, f, Meta, tm, index, total, Cover); }
                        catch (Exception ex) { Log($"Tag non scritti su {Path.GetFileName(dst)}: {ex.Message}", true); }
                        await CopyWithRetry(tmp, dst, token);
                        File.Delete(tmp);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        failed = true;
                        Log($"Errore {FormatInfo.Label(f)} traccia {r.Track.Number:D2}: {ex.Message}", true);
                    }
                });
            if (failed) Interlocked.Increment(ref _errors);
            var final = failed ? TrackRipStatus.Failed : r.Status;
            string txt = failed ? "Errore di codifica/salvataggio" : r.Summary;
            _p.Report(new RipEvent(RipEventKind.TrackStatus, txt, r.AudioIndex, final));
            r.Buffer = Array.Empty<byte>(); // libera memoria
        }
    }

    static async Task CopyWithRetry(string src, string dst, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                string part = dst + ".part";
                await using (var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true))
                await using (var o = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true))
                    await i.CopyToAsync(o, ct);
                File.Move(part, dst, true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                await Task.Delay(1500 * attempt, ct); // rete momentaneamente non disponibile
            }
        }
    }
}
