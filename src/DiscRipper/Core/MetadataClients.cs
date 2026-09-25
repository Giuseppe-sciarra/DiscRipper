using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace DiscRipper.Core;

public static class Http
{
    public const string UserAgent = "DiscRipper/1.0 ( https://github.com/Giuseppe-TD/DiscRipper )";

    public static HttpClient Create()
    {
        var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.All })
        { Timeout = TimeSpan.FromSeconds(25) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return h;
    }
}

/// <summary>Lookup gratuito su MusicBrainz tramite disc ID (nessun account richiesto).</summary>
public sealed class MusicBrainzClient
{
    readonly HttpClient _http;
    static readonly SemaphoreSlim Gate = new(1, 1);
    static DateTime _last = DateTime.MinValue;

    public MusicBrainzClient(HttpClient http) => _http = http;

    async Task<string?> GetAsync(string url, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _last.AddMilliseconds(1100) - DateTime.UtcNow; // max 1 richiesta/s
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            _last = DateTime.UtcNow;
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            if (resp.StatusCode == HttpStatusCode.BadRequest) return "";
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    public async Task<List<AlbumMeta>> LookupAsync(Toc toc, CancellationToken ct)
    {
        string discId = toc.MusicBrainzDiscId();
        string baseUrl = $"https://musicbrainz.org/ws/2/discid/{discId}?toc={toc.MusicBrainzTocParam()}&cdstubs=no&fmt=json&inc=";
        string? json = await GetAsync(baseUrl + "recordings+artist-credits+release-groups+labels+genres", ct).ConfigureAwait(false);
        if (json == "") json = await GetAsync(baseUrl + "recordings+artist-credits+release-groups+labels", ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(json)) return new();
        return Parse(json, toc, discId);
    }

    public static List<AlbumMeta> Parse(string json, Toc toc, string discId)
    {
        var result = new List<AlbumMeta>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        bool exact = root.TryGetProperty("id", out var idEl) && idEl.GetString() == discId;
        if (!root.TryGetProperty("releases", out var releases)) return result;
        int audioCount = toc.AudioTracks.Count;

        foreach (var rel in releases.EnumerateArray())
        {
            if (!rel.TryGetProperty("media", out var media)) continue;
            var mediaList = media.EnumerateArray().ToList();
            JsonElement? medium = null;
            bool mediumExact = false;
            foreach (var m in mediaList)
            {
                if (m.TryGetProperty("discs", out var discs) &&
                    discs.EnumerateArray().Any(d => Str(d, "id") == discId)) { medium = m; mediumExact = true; break; }
            }
            medium ??= mediaList.FirstOrDefault(m => Int(m, "track-count") == audioCount
                                                    && (Str(m, "format").Contains("CD") || Str(m, "format") == ""));
            if (medium == null) continue;
            var med = medium.Value;

            var a = new AlbumMeta
            {
                Source = "MusicBrainz",
                MbReleaseId = Str(rel, "id"),
                ProviderId = Str(rel, "id"),
                Album = Str(rel, "title"),
                Artist = Credit(rel),
                MbAlbumArtistId = FirstArtistId(rel),
                Year = Str(rel, "date") is { Length: >= 4 } d ? d[..4] : "",
                Country = Str(rel, "country"),
                Barcode = Str(rel, "barcode"),
                Format = Str(med, "format"),
                DiscNumber = Math.Max(1, Int(med, "position")),
                DiscTotal = Math.Max(1, mediaList.Count(m => Str(m, "format").Contains("CD") || Str(m, "format") == "")),
                ExactMatch = exact && mediumExact
            };
            if (a.DiscNumber > a.DiscTotal) a.DiscTotal = a.DiscNumber;

            if (rel.TryGetProperty("release-group", out var rg))
            {
                a.MbReleaseGroupId = Str(rg, "id");
                if (a.Year.Length == 0 && Str(rg, "first-release-date") is { Length: >= 4 } fd) a.Year = fd[..4];
                a.Genre = TopGenre(rg);
            }
            if (a.Genre.Length == 0) a.Genre = TopGenre(rel);
            if (rel.TryGetProperty("label-info", out var li))
                a.Label = li.EnumerateArray().Select(x => x.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.Object ? Str(l, "name") : "")
                            .FirstOrDefault(s => s.Length > 0) ?? "";

            if (med.TryGetProperty("tracks", out var tracks))
            {
                var audio = toc.AudioTracks;
                int i = 0;
                foreach (var t in tracks.EnumerateArray())
                {
                    if (i >= audio.Count) break;
                    var rec = t.TryGetProperty("recording", out var r) ? r : default;
                    string title = Str(t, "title");
                    if (title.Length == 0 && rec.ValueKind == JsonValueKind.Object) title = Str(rec, "title");
                    string artist = t.TryGetProperty("artist-credit", out _) ? Credit(t)
                                  : rec.ValueKind == JsonValueKind.Object ? Credit(rec) : a.Artist;
                    a.Tracks.Add(new TrackMeta
                    {
                        Number = audio[i].Number,
                        Title = title,
                        Artist = artist.Length > 0 ? artist : a.Artist,
                        MbTrackId = Str(t, "id"),
                        MbRecordingId = rec.ValueKind == JsonValueKind.Object ? Str(rec, "id") : null,
                        MbArtistId = FirstArtistId(t)
                    });
                    i++;
                }
                for (; i < audio.Count; i++)
                    a.Tracks.Add(new TrackMeta { Number = audio[i].Number, Title = $"Traccia {audio[i].Number:D2}", Artist = a.Artist });
            }
            result.Add(a);
        }
        // prima le corrispondenze esatte, poi CD, poi con anno
        return result.OrderByDescending(r => r.ExactMatch)
                     .ThenByDescending(r => r.Format.Contains("CD"))
                     .ToList();
    }

    static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    static string Credit(JsonElement e)
    {
        if (!e.TryGetProperty("artist-credit", out var ac) || ac.ValueKind != JsonValueKind.Array) return "";
        var sb = new StringBuilder();
        foreach (var c in ac.EnumerateArray()) { sb.Append(Str(c, "name")); sb.Append(Str(c, "joinphrase")); }
        return sb.ToString().Trim();
    }

    static string? FirstArtistId(JsonElement e)
    {
        if (!e.TryGetProperty("artist-credit", out var ac) || ac.ValueKind != JsonValueKind.Array) return null;
        foreach (var c in ac.EnumerateArray())
            if (c.TryGetProperty("artist", out var art)) return Str(art, "id");
        return null;
    }

    static string TopGenre(JsonElement e)
    {
        if (!e.TryGetProperty("genres", out var g) || g.ValueKind != JsonValueKind.Array) return "";
        var best = g.EnumerateArray().OrderByDescending(x => Int(x, "count")).FirstOrDefault();
        if (best.ValueKind != JsonValueKind.Object) return "";
        var name = Str(best, "name");
        return name.Length > 0 ? char.ToUpper(name[0]) + name[1..] : "";
    }

    /// <summary>Copertina da Cover Art Archive (release, poi release group).</summary>
    public async Task<byte[]?> GetCoverAsync(AlbumMeta a, CancellationToken ct)
    {
        foreach (var url in new[]
                 {
                     a.CoverUrl,
                     a.MbReleaseId != null ? $"https://coverartarchive.org/release/{a.MbReleaseId}/front-500" : null,
                     a.MbReleaseGroupId != null ? $"https://coverartarchive.org/release-group/{a.MbReleaseGroupId}/front-500" : null
                 })
        {
            if (url == null) continue;
            try
            {
                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;
                var b = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (b.Length > 1000) return b;
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        return null;
    }
}

/// <summary>Lookup su GnuDB (ex freedb) via protocollo CDDB su HTTP. Richiede un'email nel "hello".</summary>
public sealed class GnuDbClient
{
    readonly HttpClient _http;
    readonly string _user, _host;

    public GnuDbClient(HttpClient http, string email)
    {
        _http = http;
        var parts = email.Split('@', 2);
        _user = Uri.EscapeDataString(parts[0]);
        _host = Uri.EscapeDataString(parts.Length > 1 ? parts[1] : "localhost");
    }

    string Url(string cmd) =>
        $"https://gnudb.gnudb.org/~cddb/cddb.cgi?cmd={cmd}&hello={_user}+{_host}+DiscRipper+1.0&proto=6";

    public async Task<List<AlbumMeta>> LookupAsync(Toc toc, CancellationToken ct)
    {
        var result = new List<AlbumMeta>();
        string q = await _http.GetStringAsync(Url("cddb+query+" + toc.CddbQueryArgs()), ct).ConfigureAwait(false);
        var lines = q.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return result;
        var matches = new List<(string cat, string id, bool exact)>();
        string code = lines[0].Length >= 3 ? lines[0][..3] : "";
        if (code == "200")
        {
            var p = lines[0].Split(' ', 4);
            if (p.Length >= 3) matches.Add((p[1], p[2], true));
        }
        else if (code == "210" || code == "211")
        {
            foreach (var l in lines.Skip(1))
            {
                if (l == ".") break;
                var p = l.Split(' ', 3);
                if (p.Length >= 2) matches.Add((p[0], p[1], code == "210"));
            }
        }
        foreach (var (cat, id, exact) in matches.Take(5))
        {
            string r = await _http.GetStringAsync(Url($"cddb+read+{cat}+{id}"), ct).ConfigureAwait(false);
            var meta = ParseXmcd(r, toc);
            if (meta != null) { meta.ExactMatch = exact; if (meta.Genre.Length == 0) meta.Genre = cat; result.Add(meta); }
        }
        return result;
    }

    public static AlbumMeta? ParseXmcd(string text, Toc toc)
    {
        var kv = new Dictionary<string, StringBuilder>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            if (raw.StartsWith('#') || !raw.Contains('=')) continue;
            int eq = raw.IndexOf('=');
            string k = raw[..eq].Trim(), v = raw[(eq + 1)..];
            if (!kv.TryGetValue(k, out var sb)) kv[k] = sb = new StringBuilder();
            sb.Append(v);
        }
        string Get(string k) => kv.TryGetValue(k, out var s) ? s.ToString().Replace("\\n", " ").Replace("\\t", " ").Trim() : "";
        string dtitle = Get("DTITLE");
        if (dtitle.Length == 0) return null;
        string artist = dtitle, album = dtitle;
        int sep = dtitle.IndexOf(" / ", StringComparison.Ordinal);
        if (sep >= 0) { artist = dtitle[..sep].Trim(); album = dtitle[(sep + 3)..].Trim(); }
        var m = new AlbumMeta { Source = "GnuDB", Artist = artist, Album = album, Year = Get("DYEAR"), Genre = Get("DGENRE") };
        var audio = toc.AudioTracks;
        for (int i = 0; i < audio.Count; i++)
        {
            // TTITLEn è indicizzato su tutte le tracce della TOC (0-based)
            int tocIdx = toc.Tracks.IndexOf(audio[i]);
            string tt = Get($"TTITLE{tocIdx}");
            string tArtist = artist, tTitle = tt;
            int s2 = tt.IndexOf(" / ", StringComparison.Ordinal);
            if (s2 >= 0) { tArtist = tt[..s2].Trim(); tTitle = tt[(s2 + 3)..].Trim(); }
            m.Tracks.Add(new TrackMeta
            {
                Number = audio[i].Number,
                Title = tTitle.Length > 0 ? tTitle : $"Traccia {audio[i].Number:D2}",
                Artist = tArtist
            });
        }
        return m;
    }
}

/// <summary>
/// CUETools DB (db.cuetools.net): gratuito, senza account. Con una sola richiesta interroga
/// MusicBrainz, Discogs e freedb e restituisce anche le copertine.
/// </summary>
public sealed class CueToolsDbClient
{
    readonly HttpClient _http;
    static readonly XNamespace Ns = "http://db.cuetools.net/ns/mmd-1.0#";

    public CueToolsDbClient(HttpClient http) => _http = http;

    public static string TocString(Toc toc) =>
        string.Join(':', toc.Tracks.Select(t => (t.IsAudio ? "" : "-") + t.StartLba)) + ":" + toc.LeadoutLba;

    public async Task<List<AlbumMeta>> LookupAsync(Toc toc, CancellationToken ct)
    {
        string url = $"http://db.cuetools.net/lookup2.php?version=3&ctdb=0&fuzzy=1&metadata=extensive&toc={TocString(toc)}";
        string xml = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
        return Parse(xml, toc);
    }

    public static List<AlbumMeta> Parse(string xml, Toc toc)
    {
        var result = new List<AlbumMeta>();
        if (string.IsNullOrWhiteSpace(xml)) return result;
        var doc = XDocument.Parse(xml);
        var audio = toc.AudioTracks;
        foreach (var m in doc.Root?.Elements(Ns + "metadata") ?? Enumerable.Empty<XElement>())
        {
            string A(string n) => ((string?)m.Attribute(n) ?? "").Trim();
            string src = A("source").ToLowerInvariant();
            var meta = new AlbumMeta
            {
                Source = src switch { "musicbrainz" => "MusicBrainz", "discogs" => "Discogs", "freedb" => "freedb", _ => src.Length > 0 ? src : "CTDB" },
                ProviderId = A("id"),
                Artist = A("artist"),
                Album = A("album"),
                Year = A("year") is { Length: >= 4 } y ? y[..4] : "",
                Genre = A("genre"),
                Barcode = A("barcode"),
                Format = "CD",
                DiscNumber = int.TryParse(A("discnumber"), out var dn) && dn > 0 ? dn : 1,
                DiscTotal = int.TryParse(A("disccount"), out var dc) && dc > 0 ? dc : 1,
                ExactMatch = !int.TryParse(A("relevance"), out var rel) || rel >= 100
            };
            if (meta.DiscNumber > meta.DiscTotal) meta.DiscTotal = meta.DiscNumber;
            if (src == "musicbrainz" && Guid.TryParse(meta.ProviderId, out _)) meta.MbReleaseId = meta.ProviderId;
            var release = m.Element(Ns + "release");
            if (release != null)
            {
                meta.Country = ((string?)release.Attribute("country") ?? "").Trim();
                if (meta.Year.Length == 0 && ((string?)release.Attribute("date") ?? "") is { Length: >= 4 } d) meta.Year = d[..4];
            }
            var label = m.Element(Ns + "label");
            if (label != null) meta.Label = ((string?)label.Attribute("name") ?? "").Trim();
            var cover = m.Elements(Ns + "coverart").OrderByDescending(c => (string?)c.Attribute("primary") == "1").FirstOrDefault();
            if (cover != null) meta.CoverUrl = (string?)cover.Attribute("uri");

            var tracks = m.Elements(Ns + "track").ToList();
            // se l'elenco comprende anche la traccia dati lo allineo alla TOC completa
            bool fullToc = tracks.Count == toc.Tracks.Count && tracks.Count != audio.Count;
            for (int i = 0; i < audio.Count; i++)
            {
                int idx = fullToc ? toc.Tracks.IndexOf(audio[i]) : i;
                var te = idx < tracks.Count ? tracks[idx] : null;
                string name = ((string?)te?.Attribute("name") ?? "").Trim();
                string artist = ((string?)te?.Attribute("artist") ?? "").Trim();
                meta.Tracks.Add(new TrackMeta
                {
                    Number = audio[i].Number,
                    Title = name.Length > 0 ? name : $"Traccia {audio[i].Number:D2}",
                    Artist = artist.Length > 0 ? artist : meta.Artist
                });
            }
            if (meta.Album.Length > 0 || meta.Artist.Length > 0) result.Add(meta);
        }
        return result;
    }
}

public static class MetadataMerge
{
    /// <summary>
    /// Unisce i risultati di tutti i provider: niente doppioni (stessa fonte + stesso id),
    /// prima le corrispondenze esatte, poi MusicBrainz, Discogs, freedb/GnuDB.
    /// Genere e copertina mancanti vengono presi da un altro risultato dello stesso album.
    /// </summary>
    public static List<AlbumMeta> Merge(IEnumerable<AlbumMeta> all)
    {
        var list = new List<AlbumMeta>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in all)
        {
            string key = a.Source + "|" + (a.ProviderId.Length > 0 ? a.ProviderId : a.Artist + "|" + a.Album);
            if (a.Source == "freedb" || a.Source == "GnuDB")
                key = "cddb|" + Norm(a.Artist) + "|" + Norm(a.Album) + "|" + string.Join("|", a.Tracks.Select(t => Norm(t.Title)));
            if (!seen.Add(key)) continue;
            list.Add(a);
        }
        static int Rank(string s) => s switch { "MusicBrainz" => 0, "Discogs" => 1, "freedb" => 2, "GnuDB" => 3, _ => 4 };
        list = list.OrderByDescending(a => a.ExactMatch).ThenBy(a => Rank(a.Source)).ToList();

        foreach (var a in list)
        {
            var same = list.Where(o => o != a && Norm(o.Album) == Norm(a.Album) && Norm(o.Artist) == Norm(a.Artist)).ToList();
            if (a.Genre.Length == 0)
                a.Genre = same.OrderBy(o => Rank(o.Source)).Select(o => o.Genre).FirstOrDefault(g => g.Length > 0) ?? "";
            if (a.Year.Length == 0)
                a.Year = same.Select(o => o.Year).FirstOrDefault(y => y.Length > 0) ?? "";
            if (a.CoverUrl == null && a.MbReleaseId == null)
                a.CoverUrl = same.Select(o => o.CoverUrl).FirstOrDefault(u => u != null);
        }
        return list;
    }

    static string Norm(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
