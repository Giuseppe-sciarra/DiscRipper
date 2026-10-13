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

// ---- CUETools DB (MusicBrainz + Discogs + freedb) e unione dei risultati
{
    int[] off = { 0, 15537, 31691, 50866, 66466, 81202, 99409, 115920, 133093, 149847, 161560, 177682, 207106 };
    var toc = Toc.Build(off.Select((o, i) => (i + 1, o, i < 12)), 210385);
    Check("CTDB toc", CueToolsDbClient.TocString(toc) == "0:15537:31691:50866:66466:81202:99409:115920:133093:149847:161560:177682:-207106:210385", CueToolsDbClient.TocString(toc));
    var res = CueToolsDbClient.Parse(File.ReadAllText(Dir("ctdb_ladyhawke.xml")), toc);
    Check("CTDB parse", res.Count >= 3 && res.All(r => r.Tracks.Count == 12) && res.Any(r => r.Source == "freedb") && res.Any(r => r.Source == "MusicBrainz"),
        string.Join(", ", res.Select(r => r.Source + ":" + r.Tracks.Count)));
    Check("CTDB titoli", res.All(r => r.Tracks[0].Title.Equals("Magic", StringComparison.OrdinalIgnoreCase) && r.Tracks[11].Title.Equals("Morning Dreams", StringComparison.OrdinalIgnoreCase)));
    Check("CTDB copertina", res.Any(r => r.CoverUrl != null));
    // doppioni: la stessa release MusicBrainz arriva sia diretta sia da CTDB
    var dup = new AlbumMeta { Source = "MusicBrainz", ProviderId = res.First(r => r.Source == "MusicBrainz").ProviderId, Artist = "Ladyhawke", Album = "Ladyhawke", ExactMatch = true };
    var merged = MetadataMerge.Merge(new[] { dup }.Concat(res));
    Check("Merge senza doppioni", merged.Count(m => m.ProviderId == dup.ProviderId) == 1 && merged[0] == dup, merged.Count.ToString());
    Check("Merge genere preso da altro risultato", merged.Where(m => m.Source == "MusicBrainz").All(m => m.Genre.Length > 0), string.Join("|", merged.Select(m => m.Source + "=" + m.Genre)));
}
// ---- impostazioni: salvataggio e rilettura
{
    var path = Path.Combine(Path.GetTempPath(), "drtest-settings", "settings.json");
    try { File.Delete(path); } catch { }
    AppSettings.PathOverride = path;
    var a = new AppSettings
    {
        OutputRoot = @"\\nas\musica", Formats = OutputFormat.Mp3 | OutputFormat.Flac, Mp3Quality = "CBR 320", FlacLevel = 5,
        AacBitrate = 192, OggQuality = 8, OpusBitrate = 96, Theme = ThemeMode.Scuro, ParanoiaAlways = true, MaxRetries = 40,
        ReadSpeed = 8, GnuDbEmail = "x@y.it", DiscogsToken = "tokDiscogs", LastFmApiKey = "keyLastfm", AutoReadOnInsert = false, EjectWhenDone = false, SaveCoverJpg = false, WriteLog = false,
        LastDrive = "E", WindowBounds = new[] { 10, 20, 1300, 900 }, WindowMaximized = true
    };
    a.DriveOffsets["TSSTcorp BDDVDW SE-506BB"] = 6;
    a.Save();
    var b = AppSettings.Load();
    bool ok = b.OutputRoot == a.OutputRoot && b.Formats == a.Formats && b.Mp3Quality == "CBR 320" && b.FlacLevel == 5 && b.AacBitrate == 192
              && b.OggQuality == 8 && b.OpusBitrate == 96 && b.Theme == ThemeMode.Scuro && b.ParanoiaAlways && b.MaxRetries == 40 && b.ReadSpeed == 8
              && b.GnuDbEmail == "x@y.it" && b.DiscogsToken == "tokDiscogs" && b.LastFmApiKey == "keyLastfm" && !b.AutoReadOnInsert && !b.EjectWhenDone && !b.SaveCoverJpg && !b.WriteLog && b.LastDrive == "E"
              && b.WindowBounds!.SequenceEqual(new[] { 10, 20, 1300, 900 }) && b.WindowMaximized
              && b.GetOffset("tsstcorp bddvdw se-506bb") == (6, true);
    Check("Impostazioni salvate e riaperte", ok, File.ReadAllText(path));
    AppSettings.PathOverride = null;
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

// ---- ricerca per nome (risposte vere di MusicBrainz, Deezer, Discogs salvate; iTunes e Last.fm nel formato documentato)
{
    int[] lo = { 0, 15537, 31691, 50866, 66466, 81202, 99409, 115920, 133093, 149847, 161560, 177682, 207106 };
    var lady = Toc.Build(lo.Select((o, i) => (i + 1, o, i < 12)), 210385);   // Ladyhawke, 12 audio + dati
    string F(string f) => File.ReadAllText(Dir(f));

    // MusicBrainz
    var mbIds = MusicBrainzClient.PickSearchResults(F("ns_mb_search.json"), 12, 6);
    using (var sd = System.Text.Json.JsonDocument.Parse(F("ns_mb_search.json")))
    {
        var byId = sd.RootElement.GetProperty("releases").EnumerateArray().ToDictionary(r => r.GetProperty("id").GetString()!, r => r);
        int Count(string id) => byId[id].GetProperty("media").EnumerateArray().Max(m => m.GetProperty("track-count").GetInt32());
        string Fmt(string id) => string.Join("+", byId[id].GetProperty("media").EnumerateArray().Select(m => m.GetProperty("format").GetString()));
        // prima le release con 12 tracce (il CD prima del digitale), una sola per tipo: le stampe uguali di altri paesi non servono
        Check("Nome · MusicBrainz sceglie prima le release da 12 tracce", mbIds.Count == 6 && Count(mbIds[0]) == 12 && Fmt(mbIds[0]) == "CD"
            && Count(mbIds[1]) == 12 && mbIds.Skip(2).All(id => Count(id) != 12), string.Join(",", mbIds.Select(id => Fmt(id) + Count(id))));
    }
    var mbRaw = MusicBrainzClient.ParseRelease(F("ns_mb_release.json"))!;
    var mbHit = NameMatcher.Build(mbRaw, lady, "ladyhawke", "ladyhawke");
    Check("Nome · MusicBrainz release", mbRaw.Tracks.Count == 12 && mbRaw.Info.Year == "2008" && mbRaw.Info.MbReleaseId != null && mbRaw.Tracks.All(t => t.LengthMs > 0),
        $"{mbRaw.Tracks.Count} {mbRaw.Info.Year}");
    Check("Nome · MusicBrainz corrisponde al CD", mbHit.Meta.ExactMatch && mbHit.Score >= 95 && mbHit.Meta.Tracks[0].Title == "Magic" && mbHit.Meta.Tracks[11].Number == 12
        && mbHit.TextSimilarity == 1.0 && mbHit.Meta.Tracks[0].MbRecordingId != null, $"{mbHit.Score} {mbHit.Match}");
    Check("Nome · query MusicBrainz", MusicBrainzClient.BuildQuery("AC/DC", "Back in Black", 10, false) == @"release:(Back in Black Black~) AND artist:(AC\/DC AC\/DC~)"
        && MusicBrainzClient.BuildQuery("Pino Daniele", "", 12, true) == "artist:(Pino Daniele Daniele~) AND tracksmedium:12",
        MusicBrainzClient.BuildQuery("AC/DC", "Back in Black", 10, false));

    // Deezer
    var dzIds = DeezerSearch.PickSearchResults(F("ns_dz_search.json"), "Ladyhawke", "Ladyhawke", 12, 6);
    Check("Nome · Deezer prima l'album giusto", dzIds.Count > 0 && dzIds[0] == 220277, string.Join(",", dzIds));
    var dzRaw = DeezerSearch.Parse(F("ns_dz_album.json"), F("ns_dz_tracks.json"))!;
    var dzHit = NameMatcher.Build(dzRaw, lady, "Ladyhawke", "Ladyhawke");
    Check("Nome · Deezer album", dzRaw.Tracks.Count == 12 && dzRaw.Info.Year == "2008" && dzRaw.Info.Genre == "Pop" && dzRaw.Info.Label.Length > 0
        && dzRaw.Info.CoverUrl!.Contains("1000x1000") && dzRaw.Info.Barcode == "602517801974", $"{dzRaw.Info.Year} {dzRaw.Info.Genre} {dzRaw.Info.CoverUrl}");
    Check("Nome · Deezer durate", dzHit.Compared == 12 && dzHit.Matching >= 9 && dzHit.Score >= 90 && dzHit.Meta.Tracks[5].Title == "Love Don't Live Here", $"{dzHit.Score} {dzHit.Match}");

    // Deezer, doppio CD: un CD con le durate del secondo disco di The Wall deve prendere il CD 2
    var wall = DeezerSearch.Parse(F("ns_dz_album_wall.json"), F("ns_dz_tracks_wall.json"))!;
    var cd2 = wall.Tracks.Where(t => t.Disc == 2).ToList();
    int lba = 0; var starts = new List<int>();
    foreach (var t in cd2) { starts.Add(lba); lba += (int)(t.LengthMs!.Value / 1000.0 * 75) + 20; }
    var wallToc = Toc.Build(starts.Select((o, i) => (i + 1, o, true)), lba);
    var wallHit = NameMatcher.Build(wall, wallToc, "Pink Floyd", "The Wall");
    Check("Nome · doppio CD: sceglie il disco giusto", wall.Tracks.Count == 26 && wallHit.Meta.DiscNumber == 2 && wallHit.Meta.DiscTotal == 2
        && wallHit.Meta.Tracks[0].Title == "Hey You" && wallHit.Meta.ExactMatch, $"{wallHit.Meta.DiscNumber}/{wallHit.Meta.DiscTotal} {wallHit.Match}");

    // Discogs
    var dcIds = DiscogsSearch.PickSearchResults(F("ns_dc_search.json"), "Ladyhawke", "Ladyhawke", 6);
    Check("Nome · Discogs prima i CD", dcIds.Count == 6 && !dcIds.Contains(13691441), string.Join(",", dcIds));
    var dcRaw = DiscogsSearch.ParseRelease(F("ns_dc_release.json"))!;
    var dcHit = NameMatcher.Build(dcRaw, lady, "Ladyhawke", "Ladyhawke");
    Check("Nome · Discogs release", dcRaw.Tracks.Count == 12 && dcRaw.Info.Year == "2008" && dcRaw.Info.Format == "CD" && dcRaw.Info.Label == "Modular Recordings"
        && dcRaw.Info.Artist == "Ladyhawke", $"{dcRaw.Info.Format} {dcRaw.Info.Label} {dcRaw.Info.Artist}");
    Check("Nome · Discogs corrisponde al CD", dcHit.Meta.ExactMatch && dcHit.Matching == 12, dcHit.Match);
    var dcWall = DiscogsSearch.ParseRelease(F("ns_dc_wall.json"))!;
    Check("Nome · Discogs doppio CD", dcWall.Tracks.Count == 26 && dcWall.Tracks.Count(t => t.Disc == 2) == 13 && dcWall.Info.Format == "2×CD", dcWall.Info.Format);
    Check("Nome · Discogs posizioni", DiscogsSearch.DiscOf("1-03") == 1 && DiscogsSearch.DiscOf("2.5") == 2 && DiscogsSearch.DiscOf("CD2-05") == 2
        && DiscogsSearch.DiscOf("A1") == 1 && DiscogsSearch.DiscOf("12") == 1 && DiscogsSearch.Duration("1:02:03") == 3723000 && DiscogsSearch.Duration("") == null
        && DiscogsSearch.CleanName("Shake (4)") == "Shake");

    // Apple Music
    var itRaw = ITunesSearch.Parse(F("ns_it_lookup.json"))!;
    var itHit = NameMatcher.Build(itRaw, lady, "Ladyhawke", "Ladyhawke");
    Check("Nome · Apple Music", itRaw.Tracks.Count == 12 && itRaw.Tracks[0].Title == "Magic" && itRaw.Tracks[3].Title == "Better Than Sunday"
        && itRaw.Info.CoverUrl!.EndsWith("/600x600bb.jpg") && itRaw.Info.Year == "2008" && itHit.Score >= 85, $"{itRaw.Info.CoverUrl} {itHit.Match}");

    // Apple Music, ricerca vera nel negozio italiano: l'album omonimo non c'è → va cercato anche nel negozio americano
    var itJson = F("ns_it_search_it.json");
    var itIds = ITunesSearch.PickSearchResults(itJson, "Ladyhawke", "Ladyhawke", 12, 6);
    long[] singoli = { 1444404216, 1845919416, 1554819686, 1490219828, 1473579249, 1123066719 };
    Check("Nome · Apple Music: niente singoli, prima i nomi simili", itIds.Count == 6 && !itIds.Any(singoli.Contains)
        && new long[] { 1090015130, 1587690241, 1443811352 }.All(itIds.Contains), string.Join(",", itIds));
    Check("Nome · Apple Music: negozio IT senza l'album → prova anche US", !ITunesSearch.HasGoodCandidate(itJson, "Ladyhawke", "Ladyhawke", 12)
        && ITunesSearch.HasGoodCandidate(itJson, "Ladyhawke", "Wild Things", 11));

    // Last.fm
    using (var ld = System.Text.Json.JsonDocument.Parse(F("ns_lastfm_album.json")))
    {
        var lfRaw = LastFmSearch.ParseAlbum(ld.RootElement)!;
        Check("Nome · Last.fm", lfRaw.Tracks.Count == 12 && lfRaw.Tracks[0].Title == "Magic" && lfRaw.Tracks[4].LengthMs == null && lfRaw.Info.Genre == "Electronic"
            && lfRaw.Info.CoverUrl != null, string.Join("|", lfRaw.Tracks.Select(t => t.Title)));
    }
    using (var ld = System.Text.Json.JsonDocument.Parse("{\"album\":{\"artist\":\"X\",\"name\":\"Y\",\"tags\":\"\",\"image\":[],\"tracks\":{\"track\":{\"name\":\"Unica\",\"duration\":180,\"@attr\":{\"rank\":\"1\"},\"artist\":{\"name\":\"X\"}}}}}"))
    {
        var one = LastFmSearch.ParseAlbum(ld.RootElement)!;
        Check("Nome · Last.fm traccia singola (oggetto, non elenco)", one.Tracks.Count == 1 && one.Tracks[0].LengthMs == 180000 && one.Info.Genre == "");
    }
    using (var ld = System.Text.Json.JsonDocument.Parse(F("ns_lastfm_search.json")))
    {
        var pairs = LastFmSearch.PickSearchResults(ld.RootElement, "Ladyhawke", 5);
        Check("Nome · Last.fm prima l'artista cercato, senza doppioni", pairs.Count == 2 && pairs[0].artist == "Ladyhawke", string.Join(",", pairs));
    }

    // allineamento: risultato con un'intro in più all'inizio e una bonus track in fondo
    var extra = new RawRelease { Info = new AlbumMeta { Source = "Prova", Artist = "A", Album = "B" } };
    extra.Tracks.Add(new RawTrack { Title = "Intro", LengthMs = 30000 });
    foreach (var t in mbRaw.Tracks) extra.Tracks.Add(new RawTrack { Title = t.Title, LengthMs = t.LengthMs });
    extra.Tracks.Add(new RawTrack { Title = "Bonus", LengthMs = 200000 });
    var exHit = NameMatcher.Build(extra, lady);
    Check("Nome · allinea saltando l'intro", exHit.Offset == 1 && exHit.Meta.Tracks[0].Title == "Magic" && exHit.Matching == 12 && !exHit.Meta.ExactMatch
        && exHit.Match.Contains("14 tracce"), exHit.Match);
    var few = new RawRelease { Info = new AlbumMeta { Source = "Prova" } };
    few.Tracks.Add(new RawTrack { Title = "Solo questa" });
    var fewHit = NameMatcher.Build(few, lady);
    Check("Nome · risultato con meno tracce", fewHit.Meta.Tracks.Count == 12 && fewHit.Meta.Tracks[1].Title == "Traccia 02" && fewHit.Score < 20, $"{fewHit.Score} {fewHit.Match}");
    Check("Nome · somiglianza testi", NameMatcher.Similarity("pino daniele", "nero a meta", "Pino Daniele", "Nero a metà") == 1.0
        && NameMatcher.Similarity("Ladyhawke", "Ladyhawke", "Crooked Colours", "Never Dance Alone (feat. Ladyhawke)") < 0.3);
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

    try
    {
        int[] o3 = { 0, 15213, 32164, 46442, 63264, 80339 };
        var t3 = Toc.Build(o3.Select((o, i) => (i + 1, o, true)), 95312);
        var ct = await new CueToolsDbClient(http).LookupAsync(t3, CancellationToken.None);
        Check("CUETools DB live", ct.Count > 0, ct.Count.ToString());
        foreach (var r in ct) Console.WriteLine("     " + r.Display + " | cover: " + (r.CoverUrl ?? "-"));
    }
    catch (Exception ex) { Console.WriteLine("     CUETools DB non raggiungibile: " + ex.Message); }

    // ricerca per nome dal vivo (Apple a volte rifiuta le richieste dai server: lì conta come avviso, non come errore)
    {
        int[] lo = { 0, 15537, 31691, 50866, 66466, 81202, 99409, 115920, 133093, 149847, 161560, 177682, 207106 };
        var lady = Toc.Build(lo.Select((o, i) => (i + 1, o, i < 12)), 210385);
        var settings = new AppSettings { LastFmApiKey = Environment.GetEnvironmentVariable("LASTFM_KEY") ?? "", DiscogsToken = Environment.GetEnvironmentVariable("DISCOGS_TOKEN") ?? "" };
        foreach (var portal in NameSearchPortals.All(http, settings))
        {
            if (!portal.Available) { Console.WriteLine($"     {portal.Name}: saltato ({portal.Hint})"); continue; }
            var hits = new List<SearchHit>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                int n = await portal.Create().SearchAsync("Ladyhawke", "Ladyhawke", 12, r => hits.Add(NameMatcher.Build(r, lady, "Ladyhawke", "Ladyhawke")), CancellationToken.None);
                var best = hits.OrderByDescending(h => h.Rank).FirstOrDefault();
                Check($"{portal.Name} ricerca per nome live", n > 0 && best != null && best.Score >= 80, best?.Match ?? "nessun risultato");
                foreach (var h in hits.OrderByDescending(h => h.Rank).Take(3))
                    Console.WriteLine($"     {h.Score,3} {h.Meta.Display} | {h.Match} | cover: {(h.Meta.CoverUrl ?? h.Meta.MbReleaseId ?? "-")}  ({sw.Elapsed.TotalSeconds:0.0}s)");
            }
            catch (PortalException ex) when (portal.Name == "Apple Music") { Console.WriteLine("     AVVISO " + ex.Message); }
            catch (Exception ex) { Check($"{portal.Name} ricerca per nome live", false, ex.Message); }
        }
    }
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
