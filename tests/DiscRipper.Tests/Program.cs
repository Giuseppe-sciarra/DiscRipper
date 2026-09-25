using System.Runtime.InteropServices;
using System.Text;
using DiscRipper.Core;

int fails = 0;
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {name}{(ok || detail.Length == 0 ? "" : "  -> " + detail)}");
    if (!ok) fails++;
}
string Dir(string f) => Path.Combine(AppContext.BaseDirectory, f);

// ---- TOC: Ladyhawke (12 audio + dati) — valori verificati da whipper/EAC/freedb
{
    int[] off = { 0, 15537, 31691, 50866, 66466, 81202, 99409, 115920, 133093, 149847, 161560, 177682, 207106 };
    var toc = Toc.Build(off.Select((o, i) => (i + 1, o, i < 12)), 210385);
    Check("CDDB Ladyhawke", toc.CddbDiscId().ToString("x8") == "c60af50d", toc.CddbDiscId().ToString("x8"));
    Check("MusicBrainz Ladyhawke", toc.MusicBrainzDiscId() == "KnpGsLhvH.lPrNc1PBL21lb9Bg4-", toc.MusicBrainzDiscId());
    Check("MB toc param", toc.MusicBrainzTocParam() == "1+12+195856+150+15687+31841+51016+66616+81352+99559+116070+133243+149997+161710+177832", toc.MusicBrainzTocParam());
    var (a, b) = toc.AccurateRipIds();
    Check("AR ids", a.ToString("x8") == "0013bd5a" && b.ToString("x8") == "00b8d489", $"{a:x8} {b:x8}");
    Check("AR path", toc.AccurateRipPath() == "a/5/d/dBAR-012-0013bd5a-00b8d489-c60af50d.bin", toc.AccurateRipPath());
    Check("CD Extra: fine ultima audio = dati - 11400", toc.AudioTracks[^1].EndLba == 207106 - 11400);
}
// ---- TOC: Ettella Diamant (esempio ufficiale MusicBrainz)
{
    int[] off = { 0, 15213, 32164, 46442, 63264, 80339 };
    var toc = Toc.Build(off.Select((o, i) => (i + 1, o, true)), 95312);
    Check("MusicBrainz Ettella Diamant", toc.MusicBrainzDiscId() == "49HHV7Eb8UKF3aQiNmu1GR8vKTY-", toc.MusicBrainzDiscId());
}
// ---- AccurateRip: parsing risposta
{
    var raw = File.ReadAllBytes(Dir("dBAR-002-0000f21c-00027ef8-05021002.bin"));
    var ar = ArDisc.Parse(raw, 2)!;
    Check("AR parse stampe", ar.Pressings.Count == 2);
    Check("AR parse valori", ar.Pressings[0].Confidence[0] == 12 && ar.Pressings[0].Confidence[1] == 20
        && ar.Pressings[0].Crc[0] == 0x284fc705 && ar.Pressings[1].Crc[1] == 0xdd97d2c3);
    var m = ar.Match(1, 0x9cc1f32e, 0);
    Check("AR match v1", m.Found && m.Confidence == 20 && m.TotalConfidence == 27);
}
void CrcTest()
{
    var bytes = File.ReadAllBytes(Dir("ref_samples.bin"));
    foreach (var line in File.ReadAllLines(Dir("ref_crc.txt")))
    {
        var p = line.Split(' ');
        var (v1, v2) = ArChecksum.Compute(bytes, p[0] == "1", p[1] == "1");
        Check($"AR CRC first={p[0]} last={p[1]}", v1.ToString("x8") == p[2] && v2.ToString("x8") == p[3], $"{v1:x8} {v2:x8}");
    }
}
CrcTest();
// ---- Rilevamento offset: disco sintetico
void OffsetTest()
{
    var rnd = new Random(7);
    int trackSamples = 588 * 75 * 25; // 25 s
    int margin = ArChecksum.DetectMargin;
    var disc = new uint[trackSamples * 3 + 20000];
    for (int i = 0; i < disc.Length; i++) disc[i] = (uint)rnd.NextInt64(0, uint.MaxValue);
    int trackStart = trackSamples + 5000;
    var correct = disc.AsSpan(trackStart, trackSamples);
    var (v1, v2) = ArChecksum.Compute(MemoryMarshal.AsBytes(correct), false, false);

    foreach (var (drvOff, useV2) in new[] { (667, false), (-472, false), (6, true), (1234, false) })
    {
        // il lettore con offset +X restituisce al campione k il dato reale k - X
        // => i dati grezzi letti da (start - margin) valgono disc[start - margin - X + i]
        var raw = new uint[trackSamples + 2 * margin];
        for (int i = 0; i < raw.Length; i++) raw[i] = disc[trackStart - margin - drvOff + i];
        var ar = new ArDisc { TrackCount = 3 };
        ar.Pressings.Add(new ArPressing { Crc = new[] { 1u, useV2 ? v2 : v1, 3u }, Confidence = new[] { 5, 9, 5 }, Crc450 = new uint[3] });
        var r = ArChecksum.DetectOffset(MemoryMarshal.AsBytes(raw.AsSpan()), margin, trackSamples, 1, 3, ar);
        // convenzione AccurateRip/EAC: per avere il campione k si legge il campione k + offset
        Check($"Offset {(useV2 ? "v2" : "v1")} {drvOff:+0;-0}", r != null && r.Value.offset == drvOff, r?.offset.ToString() ?? "null");
    }
}
OffsetTest();
// ---- Plan: correzione offset
{
    var (ls, le, bo) = TrackReader.Plan(100 * 588 + 6, 588 * 10);
    Check("Plan +6", ls == 100 && le == 111 && bo == 24, $"{ls} {le} {bo}");
    var (ls2, le2, bo2) = TrackReader.Plan(0 - 30, 588 * 2);
    Check("Plan negativo", ls2 == -1 && le2 == 2 && bo2 == (588 - 30) * 4, $"{ls2} {le2} {bo2}");
}

// ---- TrackReader con lettore simulato (offset, jitter, errori, rilevamento offset)
void ReaderTest()
{
    const int X = 6;
    int[] starts = { 0, 750, 1500 };
    int leadout = 2250;
    var toc = Toc.Build(starts.Select((o, i) => (i + 1, o, true)), leadout);
    var rnd = new Random(3);
    var truth = new uint[leadout * 588];
    for (int i = 0; i < truth.Length; i++) truth[i] = (uint)rnd.NextInt64(0, uint.MaxValue);

    var arDisc = new ArDisc { TrackCount = 3 };
    var crcs = new uint[3];
    for (int t = 0; t < 3; t++)
    {
        var tr = toc.AudioTracks[t];
        crcs[t] = ArChecksum.Compute(MemoryMarshal.AsBytes(truth.AsSpan(tr.StartLba * 588, (int)tr.Samples)), t == 0, t == 2).v2;
    }
    arDisc.Pressings.Add(new ArPressing { Crc = crcs, Confidence = new[] { 10, 10, 10 }, Crc450 = new uint[3] });

    bool Same(TrackRipResult r, TocTrack tr, bool isFirst, bool isLast)
    {
        var got = MemoryMarshal.Cast<byte, uint>(r.Pcm.Span);
        var exp = truth.AsSpan(tr.StartLba * 588, (int)tr.Samples);
        // i primi/ultimi X campioni del disco non sono leggibili (niente overread): li escludo
        int from = isFirst ? X : 0, to = isLast ? exp.Length - X : exp.Length;
        return got.Slice(from, to - from).SequenceEqual(exp.Slice(from, to - from));
    }

    var d = new FakeDrive(truth, X, leadout);
    var reader = new TrackReader(d, toc, X, 20);
    var r1 = reader.ReadTrack(toc.AudioTracks[1], 1, arDisc, false, (_, _) => { }, CancellationToken.None);
    Check("Lettura pulita + AR", r1.Status == TrackRipStatus.AccurateRip && !r1.SecureRead && Same(r1, toc.AudioTracks[1], false, false), r1.Summary);

    d = new FakeDrive(truth, X, leadout);
    d.Jitter[900] = 1; d.Jitter[1100] = 3;
    reader = new TrackReader(d, toc, X, 20);
    var r2 = reader.ReadTrack(toc.AudioTracks[1], 1, arDisc, false, (_, _) => { }, CancellationToken.None);
    Check("Jitter risolto con rilettura", r2.Status == TrackRipStatus.AccurateRip && r2.SecureRead && r2.UnresolvedSectors == 0 && Same(r2, toc.AudioTracks[1], false, false), r2.Summary);

    d = new FakeDrive(truth, X, leadout);
    d.IoErrors[1600] = 3;
    reader = new TrackReader(d, toc, X, 20);
    var r3 = reader.ReadTrack(toc.AudioTracks[2], 2, null, false, (_, _) => { }, CancellationToken.None);
    Check("Errore I/O recuperato senza AR", r3.Status == TrackRipStatus.Consistent && Same(r3, toc.AudioTracks[2], false, true), r3.Summary);

    d = new FakeDrive(truth, X, leadout);
    d.Jitter[200] = int.MaxValue;
    reader = new TrackReader(d, toc, X, 10);
    var r4 = reader.ReadTrack(toc.AudioTracks[0], 0, arDisc, false, (_, _) => { }, CancellationToken.None);
    Check("Settore illeggibile segnalato", r4.Status == TrackRipStatus.Errors && r4.UnresolvedSectors > 0, r4.Summary);

    d = new FakeDrive(truth, X, leadout);
    var det = TrackReader.DetectOffset(d, toc, arDisc, _ => { }, CancellationToken.None);
    Check("Rilevamento offset da lettore", det?.offset == X, det?.offset.ToString() ?? "null");

    // prima traccia / ultima con offset negativo
    d = new FakeDrive(truth, -30, leadout);
    reader = new TrackReader(d, toc, -30, 20);
    var r5 = reader.ReadTrack(toc.AudioTracks[0], 0, arDisc, true, (_, _) => { }, CancellationToken.None);
    Check("Offset negativo + paranoia", r5.Status == TrackRipStatus.AccurateRip && r5.SecureRead, r5.Summary);
}
ReaderTest();

// ---- CD-Text
{
    var packs = new List<byte>();
    int seq = 0;
    void AddPacks(byte type, string[] strings)
    {
        var bytes = new List<byte>();
        foreach (var s in strings) { bytes.AddRange(Encoding.Latin1.GetBytes(s)); bytes.Add(0); }
        int track = 0, charPos = 0;
        for (int i = 0; i < bytes.Count; i += 12)
        {
            var p = new byte[18];
            p[0] = type; p[1] = (byte)track; p[2] = (byte)seq++; p[3] = (byte)Math.Min(charPos, 15);
            for (int j = 0; j < 12; j++) p[4 + j] = i + j < bytes.Count ? bytes[i + j] : (byte)0;
            // aggiorna track del pack successivo: conta gli zeri
            for (int j = 0; j < 12 && i + j < bytes.Count; j++) if (bytes[i + j] == 0) track++;
            packs.AddRange(p);
        }
    }
    AddPacks(0x80, new[] { "Album di prova", "Primo brano", "Città", "Terzo con un titolo molto lungo" });
    AddPacks(0x81, new[] { "Artista", "Artista", "\t", "Ospite" });
    var info = CdTextInfo.Parse(packs.ToArray())!;
    Check("CD-Text album", info.AlbumTitle == "Album di prova" && info.AlbumPerformer == "Artista");
    Check("CD-Text tracce", info.Titles[2] == "Città" && info.Titles[3] == "Terzo con un titolo molto lungo" && info.Performers[2] == "Artista" && info.Performers[3] == "Ospite",
        string.Join("|", info.Titles.Values) + " / " + string.Join("|", info.Performers.Values));
}
// ---- GnuDB xmcd
{
    var toc = Toc.Build(new[] { (1, 0, true), (2, 20000, true) }, 40000);
    var x = "# xmcd\nDISCID=1234\nDTITLE=Pino Daniele / Nero a metà\nDYEAR=1980\nDGENRE=Blues\nTTITLE0=I say i' sto cca'\nTTITLE1=Ospite / Quanno chiove\n.";
    var m = GnuDbClient.ParseXmcd(x, toc)!;
    Check("xmcd", m.Artist == "Pino Daniele" && m.Album == "Nero a metà" && m.Year == "1980" && m.Tracks[1].Artist == "Ospite" && m.Tracks[1].Title == "Quanno chiove");
}
// ---- nomi file
{
    Check("Clean", PathBuilder.Clean("AC/DC: Live? <1992>") == "AC-DC- Live_ (1992)", PathBuilder.Clean("AC/DC: Live? <1992>"));
    var a = new AlbumMeta { Artist = "AC/DC", Album = "Back in Black", Year = "1980" };
    a.Tracks.Add(new TrackMeta { Number = 1, Title = "Hells Bells", Artist = "AC/DC" });
    var dir = PathBuilder.AlbumFolder(@"Y:\Musica", a);
    Check("Cartella", dir.Replace('\\', '/').EndsWith("AC-DC/Back in Black (1980)"), dir);
    Check("File", PathBuilder.TrackFileName(a, a.Tracks[0], 1, 10) == "01 - Hells Bells");
}

// ---- codifica + tag (richiede ffmpeg nel PATH)
if (args.Contains("--encode"))
{
    var pcm = new byte[44100 * 4 * 3];
    for (int i = 0; i < pcm.Length / 4; i++)
    {
        short v = (short)(Math.Sin(i * 2 * Math.PI * 440 / 44100) * 12000);
        BitConverter.TryWriteBytes(pcm.AsSpan(i * 4), v); BitConverter.TryWriteBytes(pcm.AsSpan(i * 4 + 2), v);
    }
    var cover = File.Exists(Dir("cover.jpg")) ? File.ReadAllBytes(Dir("cover.jpg")) : null;
    var a = new AlbumMeta { Artist = "Artista Àccentato", Album = "Album prova", Year = "1999", Genre = "Rock", DiscNumber = 1, DiscTotal = 2, MbReleaseId = "11111111-2222-3333-4444-555555555555" };
    var tm = new TrackMeta { Number = 3, Title = "Titolo è bello", Artist = "Ospite" };
    var s = new AppSettings();
    var tmp = Path.Combine(Path.GetTempPath(), "drtest"); Directory.CreateDirectory(tmp);
    foreach (var f in FormatInfo.All)
    {
        var path = Path.Combine(tmp, "t" + FormatInfo.Ext(f));
        try
        {
            await AudioEncoder.EncodeAsync("ffmpeg", pcm, f, s, path, CancellationToken.None);
            Tagger.Write(path, f, a, tm, 3, 12, cover);
            using var file = TagLib.File.Create(path);
            var t = file.Tag;
            bool ok = t.Title == tm.Title && t.FirstPerformer == "Ospite" && t.FirstAlbumArtist == a.Artist && t.Album == a.Album
                      && t.Year == 1999 && t.Track == 3 && t.TrackCount == 12 && t.Disc == 1 && t.DiscCount == 2
                      && (f == OutputFormat.Wav || cover == null || t.Pictures.Length == 1);
            Check($"Codifica+tag {f}", ok && file.Properties.Duration.TotalSeconds > 2.5,
                $"{t.Title}|{t.FirstPerformer}|{t.FirstAlbumArtist}|{t.Year}|{t.Track}/{t.TrackCount}|{t.Disc}/{t.DiscCount}|pic {t.Pictures.Length}|{file.Properties.Duration}");
        }
        catch (Exception ex) { Check($"Codifica+tag {f}", false, ex.Message); }
    }
}

// ---- MusicBrainz live (se c'è rete)
if (args.Contains("--online"))
{
    using var http = Http.Create();
    int[] off = { 0, 15213, 32164, 46442, 63264, 80339 };
    var toc = Toc.Build(off.Select((o, i) => (i + 1, o, true)), 95312);
    try
    {
        var res = await new MusicBrainzClient(http).LookupAsync(toc, CancellationToken.None);
        Check("MusicBrainz live", res.Count > 0 && res[0].Tracks.Count == 6, res.Count.ToString());
        foreach (var r in res.Take(3)) Console.WriteLine("     " + r.Display + " | " + string.Join(" / ", r.Tracks.Select(t => t.Title)) + " | genere: " + r.Genre);
        if (res.Count > 0)
        {
            var cover = await new MusicBrainzClient(http).GetCoverAsync(res[0], CancellationToken.None);
            Console.WriteLine($"     copertina: {cover?.Length ?? 0} byte");
        }
    }
    catch (Exception ex) { Console.WriteLine("     MusicBrainz non raggiungibile: " + ex.Message); }
    try
    {
        int[] o2 = { 0, 15537, 31691, 50866, 66466, 81202, 99409, 115920, 133093, 149847, 161560, 177682, 207106 };
        var t2 = Toc.Build(o2.Select((o, i) => (i + 1, o, i < 12)), 210385);
        var ar = await ArDisc.FetchAsync(http, t2, CancellationToken.None);
        Console.WriteLine($"     AccurateRip Ladyhawke: {(ar == null ? "non trovato" : ar.Pressings.Count + " stampe")}");
    }
    catch (Exception ex) { Console.WriteLine("     AccurateRip non raggiungibile: " + ex.Message); }
}

Console.WriteLine(fails == 0 ? "\nTUTTI I TEST OK" : $"\n{fails} TEST FALLITI");
return fails == 0 ? 0 : 1;


sealed class FakeDrive : ISectorSource
{
    readonly uint[] _truth; readonly int _x; readonly int _leadout;
    public Dictionary<int, int> Jitter = new();   // settore -> quante letture sbagliate
    public Dictionary<int, int> IoErrors = new(); // settore -> quanti errori I/O
    readonly Random _r = new(99);
    public FakeDrive(uint[] truth, int x, int leadout) { _truth = truth; _x = x; _leadout = leadout; }
    public void ReadSectors(int lba, int count, Span<byte> dest)
    {
        for (int s = lba; s < lba + count; s++)
            if (IoErrors.TryGetValue(s, out var n) && n > 0) { IoErrors[s] = n - 1; throw new IOException("sim"); }
        var outp = MemoryMarshal.Cast<byte, uint>(dest[..(count * 2352)]);
        for (int i = 0; i < count * 588; i++)
        {
            long j = (long)lba * 588 + i;   // campione del lettore
            long k = j - _x;                // campione reale
            outp[i] = k >= 0 && k < _truth.Length ? _truth[k] : 0;
        }
        for (int s = lba; s < lba + count; s++)
            if (Jitter.TryGetValue(s, out var n) && n > 0)
            {
                if (n != int.MaxValue) Jitter[s] = n - 1;
                outp[(s - lba) * 588 + _r.Next(588)] ^= (uint)_r.Next(1, int.MaxValue);
            }
    }
}
