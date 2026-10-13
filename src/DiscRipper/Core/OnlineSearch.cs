using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DiscRipper.Core;

// ═══════════════════════════════════════════════════════════════════════════════════════════
//  Ricerca per nome (artista + album) su più portali, per i CD che la ricerca automatica
//  non riconosce (tipicamente le copie masterizzate: TOC diversa dall'originale).
//
//  Ogni portale restituisce gli album trovati con TUTTE le tracce e le durate; NameMatcher
//  sceglie il disco giusto (per i doppi CD), allinea le tracce a quelle del CD e dà un
//  punteggio confrontando numero di tracce e durate.
//
//  Gratis e senza chiave: MusicBrainz, Deezer, Apple Music (iTunes), Discogs (il token è
//  facoltativo: alza il limite di richieste e aggiunge le copertine).
//  Con chiave gratuita: Last.fm.
// ═══════════════════════════════════════════════════════════════════════════════════════════

/// <summary>Un brano di un album trovato per nome, prima di allinearlo alle tracce del CD.</summary>
public sealed class RawTrack
{
    public int Disc { get; init; } = 1;
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public int? LengthMs { get; init; }
    public string? MbTrackId { get; init; }
    public string? MbRecordingId { get; init; }
    public string? MbArtistId { get; init; }
}

/// <summary>Un album trovato per nome: dati dell'album (senza tracce) + tutte le tracce di tutti i dischi.</summary>
public sealed class RawRelease
{
    public required AlbumMeta Info { get; init; }
    public List<RawTrack> Tracks { get; } = new();
}

/// <summary>Un risultato pronto da mostrare: album allineato alle tracce del CD e quanto gli corrisponde.</summary>
public sealed class SearchHit
{
    public required RawRelease Raw { get; init; }
    /// <summary>Metadati con le tracce numerate come quelle del CD (pronti per l'estrazione).</summary>
    public required AlbumMeta Meta { get; init; }
    /// <summary>Tracce del disco del risultato confrontato con il CD (per i doppi CD: il disco che corrisponde meglio).</summary>
    public required List<RawTrack> DiscTracks { get; init; }
    /// <summary>Indice in DiscTracks della traccia che corrisponde alla traccia 1 del CD.</summary>
    public int Offset { get; init; }
    /// <summary>0-100: numero di tracce e durate rispetto al CD.</summary>
    public int Score { get; init; }
    /// <summary>0-1: quanto artista e album somigliano a quello che è stato cercato.</summary>
    public double TextSimilarity { get; init; }
    /// <summary>Spiegazione breve, es. «12 tracce come il CD · durate 12/12 uguali».</summary>
    public string Match { get; init; } = "";
    public int Matching { get; init; }
    public int Compared { get; init; }

    public double Rank => Score + 12 * TextSimilarity;
}

public static class NameMatcher
{
    /// <summary>Differenza massima perché due durate contino come «uguali» (le copie e i servizi digitali arrotondano).</summary>
    public const int ToleranceMs = 3000;

    public static long[] CdLengthsMs(Toc toc) => toc.AudioTracks.Select(t => t.Sectors * 1000L / 75).ToArray();

    public static SearchHit Build(RawRelease raw, Toc toc, string queryArtist = "", string queryAlbum = "")
    {
        var audio = toc.AudioTracks;
        int n = audio.Count;
        var cd = CdLengthsMs(toc);

        // dischi del risultato: ognuno separato e, se sono più di uno, anche tutti insieme
        var groups = raw.Tracks.GroupBy(t => t.Disc).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();
        var options = groups.Select(g => (disc: g[0].Disc, list: g)).ToList();
        if (groups.Count > 1) options.Add((0, raw.Tracks.ToList()));
        if (options.Count == 0) options.Add((1, new List<RawTrack>()));

        // il disco (o l'insieme) che corrisponde meglio; a pari punteggio quello con tante tracce quante il CD
        var best = options
            .Select((o, i) =>
            {
                var (offset, matching, compared) = Align(cd, o.list);
                return (o.disc, o.list, offset, matching, compared, score: Score(n, o.list.Count, matching, compared), i);
            })
            .OrderByDescending(x => x.score).ThenByDescending(x => x.list.Count == n).ThenBy(x => x.i)
            .First();

        var info = raw.Info;
        var meta = new AlbumMeta
        {
            Source = info.Source,
            ProviderId = info.ProviderId,
            Artist = Tidy(info.Artist),
            Album = Tidy(info.Album),
            Year = info.Year,
            Genre = Tidy(info.Genre),
            Country = info.Country,
            Label = info.Label,
            Barcode = info.Barcode,
            Format = info.Format,
            CoverUrl = info.CoverUrl,
            MbReleaseId = info.MbReleaseId,
            MbReleaseGroupId = info.MbReleaseGroupId,
            MbAlbumArtistId = info.MbAlbumArtistId,
            DiscNumber = best.disc <= 0 ? 1 : best.disc,
            DiscTotal = best.disc <= 0 ? 1 : Math.Max(groups.Count, best.disc),
        };
        for (int i = 0; i < n; i++)
        {
            int j = best.offset + i;
            var rt = j < best.list.Count ? best.list[j] : null;
            string title = Tidy(rt?.Title), artist = Tidy(rt?.Artist);
            meta.Tracks.Add(new TrackMeta
            {
                Number = audio[i].Number,
                Title = title.Length > 0 ? title : $"Traccia {audio[i].Number:D2}",
                Artist = artist.Length > 0 ? artist : meta.Artist,
                MbTrackId = rt?.MbTrackId,
                MbRecordingId = rt?.MbRecordingId,
                MbArtistId = rt?.MbArtistId
            });
        }
        int m = best.list.Count;
        meta.ExactMatch = m == n && best.compared >= Math.Max(1, n - 1) && best.matching >= Math.Ceiling(n * 0.9);

        return new SearchHit
        {
            Raw = raw,
            Meta = meta,
            DiscTracks = best.list,
            Offset = best.offset,
            Score = best.score,
            Matching = best.matching,
            Compared = best.compared,
            TextSimilarity = Similarity(queryArtist, queryAlbum, info.Artist, info.Album),
            Match = Describe(n, m, best.matching, best.compared, best.offset, groups.Count > 1 && best.disc > 0 ? best.disc : 0)
        };
    }

    /// <summary>Spazi doppi, tabulazioni e spazi in fondo (Discogs ne lascia) → un solo spazio.</summary>
    public static string Tidy(string? s) => string.IsNullOrEmpty(s) ? "" : Regex.Replace(s, @"\s+", " ").Trim();

    /// <summary>
    /// Allinea le tracce del risultato a quelle del CD. Se il risultato ne ha di più (bonus track,
    /// intro…) prova tutte le posizioni e tiene quella con più durate uguali.
    /// </summary>
    public static (int offset, int matching, int compared) Align(long[] cd, List<RawTrack> list)
    {
        int n = cd.Length, m = list.Count;
        int maxK = Math.Max(0, m - n);
        (int k, int matching, int compared, double avg) best = (0, 0, 0, double.MaxValue);
        for (int k = 0; k <= maxK; k++)
        {
            int matching = 0, compared = 0; double sum = 0;
            for (int i = 0; i < n && k + i < m; i++)
            {
                var len = list[k + i].LengthMs;
                if (len is not > 0) continue;
                long d = Math.Abs(cd[i] - len.Value);
                compared++; sum += d;
                if (d <= ToleranceMs) matching++;
            }
            double avg = compared > 0 ? sum / compared : double.MaxValue;
            if (k == 0 || matching > best.matching || (matching == best.matching && avg < best.avg))
                best = (k, matching, compared, avg);
        }
        return (best.k, best.matching, best.compared);
    }

    public static int Score(int n, int m, int matching, int compared)
    {
        if (m == 0 || n == 0) return 0;
        double s = m == n ? 60 : Math.Max(0, 40 - 8 * Math.Abs(m - n));
        if (compared > 0) s += 40.0 * matching / n;
        else s += 15; // durate non disponibili: né premio né penalità piena
        return (int)Math.Round(Math.Clamp(s, 0, 100));
    }

    static string Describe(int n, int m, int matching, int compared, int offset, int disc)
    {
        var sb = new StringBuilder();
        if (disc > 0) sb.Append($"CD {disc}: ");
        if (m == n) sb.Append($"{m} tracce come il CD");
        else if (m > n) sb.Append($"{m} tracce (il CD ne ha {n}{(offset > 0 ? $", allineate dalla {offset + 1}ª" : "")})");
        else sb.Append($"solo {m} tracce (il CD ne ha {n})");
        if (compared > 0) sb.Append($" · durate {matching}/{compared} uguali");
        else sb.Append(" · durate non disponibili");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ somiglianza dei testi

    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    static HashSet<string> Words(string s) => Normalize(s).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    static double Jaccard(string a, string b)
    {
        var A = Words(a); var B = Words(b);
        if (A.Count == 0 || B.Count == 0) return 0;
        int inter = A.Count(B.Contains);
        return (double)inter / (A.Count + B.Count - inter);
    }

    /// <summary>
    /// Per scegliere quali risultati della ricerca leggere per intero: 3 = album e artista somigliano a
    /// quelli cercati, 2 = solo l'album, 1 = solo l'artista, 0 = nessuno dei due.
    /// </summary>
    public static int Relevance(string qArtist, string qAlbum, string artist, string album)
    {
        int r = 0;
        if (!string.IsNullOrWhiteSpace(qAlbum) && Jaccard(qAlbum, album) >= 0.5) r += 2;
        if (!string.IsNullOrWhiteSpace(qArtist) && Jaccard(qArtist, artist) >= 0.5) r += 1;
        return r;
    }

    /// <summary>0-1: media della somiglianza di artista e album rispetto a quello cercato (solo i campi compilati).</summary>
    public static double Similarity(string qArtist, string qAlbum, string artist, string album)
    {
        var parts = new List<double>();
        if (!string.IsNullOrWhiteSpace(qArtist)) parts.Add(Jaccard(qArtist, artist));
        if (!string.IsNullOrWhiteSpace(qAlbum)) parts.Add(Jaccard(qAlbum, album));
        return parts.Count == 0 ? 0 : parts.Average();
    }
}

/// <summary>Un portale su cui cercare un album per nome.</summary>
public interface INameSearch
{
    string Name { get; }
    /// <summary>Cerca e chiama <paramref name="found"/> per ogni album (con le tracce) appena è pronto. Restituisce quanti ne ha trovati.</summary>
    Task<int> SearchAsync(string artist, string album, int cdTracks, Action<RawRelease> found, CancellationToken ct);
}

/// <summary>Errore di un portale da mostrare così com'è (già spiegato in italiano).</summary>
public sealed class PortalException(string message) : Exception(message);

static class J
{
    public static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Number => v.GetRawText(),
                _ => ""
            }
            : "";

    public static int Int(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var j)) return j;
        return 0;
    }

    public static long Long(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var j)) return j;
        return 0;
    }

    public static JsonElement Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    /// <summary>Array anche quando l'API restituisce un oggetto singolo (Last.fm lo fa con un solo elemento).</summary>
    public static IEnumerable<JsonElement> Arr(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return Enumerable.Empty<JsonElement>();
        return v.ValueKind switch
        {
            JsonValueKind.Array => v.EnumerateArray().ToList(),
            JsonValueKind.Object => new[] { v },
            _ => Enumerable.Empty<JsonElement>()
        };
    }

    public static string Year(string date) => date.Length >= 4 && date[..4].All(char.IsDigit) && date[..4] != "0000" ? date[..4] : "";

    public static string Cap(string s) => s.Length > 0 ? char.ToUpper(s[0], CultureInfo.InvariantCulture) + s[1..] : s;

    public static async Task<string> GetAsync(HttpClient http, string url, string portal, CancellationToken ct, Action<HttpRequestMessage>? prepare = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/json");
        prepare?.Invoke(req);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == (HttpStatusCode)429)
            throw new PortalException($"{portal}: troppe richieste ravvicinate, riprova tra un minuto.");
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new PortalException($"{portal}: accesso rifiutato ({(int)resp.StatusCode}).");
        if (!resp.IsSuccessStatusCode)
            throw new PortalException($"{portal}: errore {(int)resp.StatusCode}.");
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }
}

// ═══════════════════════════════════════════════════════════════════ MusicBrainz

public sealed partial class MusicBrainzClient : INameSearch
{
    public string Name => "MusicBrainz";

    /// <summary>Quanti album leggere per intero (MusicBrainz: max 1 richiesta al secondo).</summary>
    public const int MaxDetails = 6;

    static string Esc(string s) => Regex.Replace(s, @"([+\-&|!(){}\[\]^""~*?:\\/])", @"\$1");

    /// <summary>Parole separate, quelle lunghe anche "sfumate" (~) per sopportare un errore di battitura.</summary>
    static string Terms(string s) => string.Join(' ',
        s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
         .Select(Esc).Where(w => w.Length > 0).Select(w => w.Length >= 5 ? $"{w} {w}~" : w));

    public static string BuildQuery(string artist, string album, int cdTracks, bool withTrackFilter)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(album)) parts.Add($"release:({Terms(album)})");
        if (!string.IsNullOrWhiteSpace(artist)) parts.Add($"artist:({Terms(artist)})");
        // solo l'artista: troppi album, tengo quelli con un disco di quante tracce ha il CD
        if (withTrackFilter && cdTracks > 0) parts.Add($"tracksmedium:{cdTracks}");
        return string.Join(" AND ", parts);
    }

    public async Task<int> SearchAsync(string artist, string album, int cdTracks, Action<RawRelease> found, CancellationToken ct)
    {
        bool onlyArtist = string.IsNullOrWhiteSpace(album);
        string q = BuildQuery(artist, album, cdTracks, onlyArtist);
        string? json = await GetAsync($"https://musicbrainz.org/ws/2/release/?query={Uri.EscapeDataString(q)}&fmt=json&limit=50", ct).ConfigureAwait(false);
        if (onlyArtist && (json == null || CountOf(json) == 0))
        {
            q = BuildQuery(artist, album, cdTracks, false);
            json = await GetAsync($"https://musicbrainz.org/ws/2/release/?query={Uri.EscapeDataString(q)}&fmt=json&limit=50", ct).ConfigureAwait(false);
        }
        if (string.IsNullOrEmpty(json)) return 0;

        var ids = PickSearchResults(json, cdTracks, MaxDetails);
        int count = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            string? rel = await GetAsync($"https://musicbrainz.org/ws/2/release/{id}?inc=recordings+artist-credits+release-groups+labels+genres&fmt=json", ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(rel)) continue;
            var raw = ParseRelease(rel);
            if (raw == null || raw.Tracks.Count == 0) continue;
            found(raw); count++;
        }
        return count;
    }

    static int CountOf(string json)
    {
        try { using var d = JsonDocument.Parse(json); return J.Arr(d.RootElement, "releases").Count(); }
        catch { return 0; }
    }

    /// <summary>
    /// Dai risultati della ricerca sceglie quali leggere per intero: prima quelli con un disco di
    /// tante tracce quante il CD (meglio se CD), poi gli altri nell'ordine di pertinenza.
    /// Salta i doppioni (stesso titolo, artista, numero di tracce e formato).
    /// </summary>
    public static List<string> PickSearchResults(string json, int cdTracks, int max)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<(string id, int order, bool sameCount, bool cd, string key)>();
        int order = 0;
        foreach (var r in J.Arr(doc.RootElement, "releases"))
        {
            var media = J.Arr(r, "media").ToList();
            bool same = media.Any(m => J.Int(m, "track-count") == cdTracks);
            bool cd = media.Any(m => J.Str(m, "format").Contains("CD"));
            string key = NameMatcher.Normalize(J.Str(r, "title")) + "|" + NameMatcher.Normalize(Credit(r)) + "|"
                         + string.Join(",", media.Select(m => J.Str(m, "format") + J.Int(m, "track-count")));
            list.Add((J.Str(r, "id"), order++, same, cd, key));
        }
        var seen = new HashSet<string>();
        return list.OrderByDescending(x => x.sameCount).ThenByDescending(x => x.sameCount && x.cd).ThenBy(x => x.order)
                   .Where(x => x.id.Length > 0 && seen.Add(x.key)).Take(max).Select(x => x.id).ToList();
    }

    /// <summary>Una release MusicBrainz (con inc=recordings…) → album + tutte le tracce di tutti i dischi.</summary>
    public static RawRelease? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var rel = doc.RootElement;
        if (rel.ValueKind != JsonValueKind.Object || J.Str(rel, "id").Length == 0) return null;
        var media = J.Arr(rel, "media").ToList();
        var info = new AlbumMeta
        {
            Source = "MusicBrainz",
            MbReleaseId = J.Str(rel, "id"),
            ProviderId = J.Str(rel, "id"),
            Album = J.Str(rel, "title"),
            Artist = Credit(rel),
            MbAlbumArtistId = FirstArtistId(rel),
            Year = J.Year(J.Str(rel, "date")),
            Country = J.Str(rel, "country"),
            Barcode = J.Str(rel, "barcode"),
            Format = string.Join(" + ", media.Select(m => J.Str(m, "format")).Where(f => f.Length > 0).GroupBy(f => f)
                                            .Select(g => g.Count() > 1 ? $"{g.Count()}×{g.Key}" : g.Key)),
        };
        if (rel.TryGetProperty("release-group", out var rg) && rg.ValueKind == JsonValueKind.Object)
        {
            info.MbReleaseGroupId = J.Str(rg, "id");
            if (info.Year.Length == 0) info.Year = J.Year(J.Str(rg, "first-release-date"));
            info.Genre = TopGenre(rg);
        }
        if (info.Genre.Length == 0) info.Genre = TopGenre(rel);
        info.Label = J.Arr(rel, "label-info").Select(x => J.Str(J.Obj(x, "label"), "name")).FirstOrDefault(s => s.Length > 0) ?? "";

        var raw = new RawRelease { Info = info };
        int pos = 0;
        foreach (var m in media)
        {
            pos++;
            int disc = J.Int(m, "position") is > 0 and var p ? p : pos;
            foreach (var t in J.Arr(m, "tracks"))
            {
                var rec = J.Obj(t, "recording");
                string title = J.Str(t, "title");
                if (title.Length == 0) title = J.Str(rec, "title");
                string artist = t.TryGetProperty("artist-credit", out _) ? Credit(t) : rec.ValueKind == JsonValueKind.Object ? Credit(rec) : "";
                long len = J.Long(t, "length");
                if (len <= 0) len = J.Long(rec, "length");
                raw.Tracks.Add(new RawTrack
                {
                    Disc = disc,
                    Title = title,
                    Artist = artist.Length > 0 ? artist : info.Artist,
                    LengthMs = len > 0 ? (int)len : null,
                    MbTrackId = J.Str(t, "id") is { Length: > 0 } tid ? tid : null,
                    MbRecordingId = J.Str(rec, "id") is { Length: > 0 } rid ? rid : null,
                    MbArtistId = FirstArtistId(t) ?? (rec.ValueKind == JsonValueKind.Object ? FirstArtistId(rec) : null)
                });
            }
        }
        return raw;
    }
}

// ═══════════════════════════════════════════════════════════════════ Deezer

/// <summary>Deezer: API pubblica, gratis e senza chiave. Copertine fino a 1000×1000.</summary>
public sealed class DeezerSearch(HttpClient http) : INameSearch
{
    public string Name => "Deezer";
    public const int MaxDetails = 6;

    public static string StrictQuery(string artist, string album)
    {
        static string Q(string s) => s.Replace("\"", " ").Trim();
        var p = new List<string>();
        if (!string.IsNullOrWhiteSpace(artist)) p.Add($"artist:\"{Q(artist)}\"");
        if (!string.IsNullOrWhiteSpace(album)) p.Add($"album:\"{Q(album)}\"");
        return string.Join(' ', p);
    }

    async Task<string> Get(string url, CancellationToken ct)
    {
        string json = await J.GetAsync(http, url, Name, ct).ConfigureAwait(false);
        using var d = JsonDocument.Parse(json);
        if (d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("error", out var err))
        {
            int code = J.Int(err, "code");
            throw new PortalException(code == 4 ? "Deezer: troppe richieste ravvicinate, riprova tra qualche secondo." : $"Deezer: {J.Str(err, "message")}");
        }
        return json;
    }

    public async Task<int> SearchAsync(string artist, string album, int cdTracks, Action<RawRelease> found, CancellationToken ct)
    {
        string json = await Get($"https://api.deezer.com/search/album?q={Uri.EscapeDataString(StrictQuery(artist, album))}&limit=25", ct).ConfigureAwait(false);
        var ids = PickSearchResults(json, artist, album, cdTracks, MaxDetails);
        // niente? riprovo senza i campi (tollera meglio gli errori), poi col solo titolo dell'album
        var fallbacks = new List<string> { $"{artist} {album}".Trim() };
        if (!string.IsNullOrWhiteSpace(album) && !string.IsNullOrWhiteSpace(artist)) fallbacks.Add(StrictQuery("", album));
        foreach (var q in fallbacks)
        {
            if (ids.Count > 0) break;
            json = await Get($"https://api.deezer.com/search/album?q={Uri.EscapeDataString(q)}&limit=25", ct).ConfigureAwait(false);
            ids = PickSearchResults(json, artist, album, cdTracks, MaxDetails);
        }
        int count = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            var albumTask = Get($"https://api.deezer.com/album/{id}", ct);
            var tracksTask = Get($"https://api.deezer.com/album/{id}/tracks?limit=300", ct);
            var raw = Parse(await albumTask.ConfigureAwait(false), await tracksTask.ConfigureAwait(false));
            if (raw == null || raw.Tracks.Count == 0) continue;
            found(raw); count++;
        }
        return count;
    }

    /// <summary>
    /// Prima gli album con titolo e artista simili a quelli cercati, poi per numero di tracce vicino
    /// a quello del CD. I singoli (1-2 brani) non servono per un CD intero.
    /// </summary>
    public static List<long> PickSearchResults(string json, string artist, string album, int cdTracks, int max)
    {
        using var doc = JsonDocument.Parse(json);
        return J.Arr(doc.RootElement, "data")
            .Select((a, i) => (id: J.Long(a, "id"), nb: J.Int(a, "nb_tracks"),
                               rel: NameMatcher.Relevance(artist, album, J.Str(J.Obj(a, "artist"), "name"), J.Str(a, "title")), i))
            .Where(x => x.id > 0 && !(cdTracks > 4 && x.nb is > 0 and <= 2))
            .OrderByDescending(x => x.rel)
            .ThenBy(x => Math.Abs(x.nb - cdTracks))
            .ThenBy(x => x.i)
            .Take(max).Select(x => x.id).ToList();
    }

    public static RawRelease? Parse(string albumJson, string tracksJson)
    {
        using var ad = JsonDocument.Parse(albumJson);
        using var td = JsonDocument.Parse(tracksJson);
        var a = ad.RootElement;
        if (J.Long(a, "id") <= 0) return null;
        string type = J.Str(a, "record_type");
        var info = new AlbumMeta
        {
            Source = "Deezer",
            ProviderId = J.Long(a, "id").ToString(CultureInfo.InvariantCulture),
            Artist = J.Str(J.Obj(a, "artist"), "name"),
            Album = J.Str(a, "title"),
            Year = J.Year(J.Str(a, "release_date")),
            Genre = J.Arr(J.Obj(a, "genres"), "data").Select(g => J.Str(g, "name")).FirstOrDefault(s => s.Length > 0) ?? "",
            Label = J.Str(a, "label"),
            Barcode = J.Str(a, "upc"),
            Format = type switch { "album" => "Album", "compile" => "Raccolta", "single" => "Singolo", "ep" => "EP", _ => "" },
            CoverUrl = J.Str(a, "cover_xl") is { Length: > 0 } c ? c : J.Str(a, "cover_big") is { Length: > 0 } c2 ? c2 : null,
        };
        var raw = new RawRelease { Info = info };
        var list = J.Arr(td.RootElement, "data").ToList();
        if (list.Count == 0) list = J.Arr(J.Obj(a, "tracks"), "data").ToList(); // riserva: elenco dentro l'album (senza n. disco)
        foreach (var t in list.Select((t, i) => (t, i))
                              .OrderBy(x => J.Int(x.t, "disk_number") is > 0 and var dn ? dn : 1)
                              .ThenBy(x => J.Int(x.t, "track_position") is > 0 and var tp ? tp : x.i + 1)
                              .Select(x => x.t))
        {
            int dur = J.Int(t, "duration");
            raw.Tracks.Add(new RawTrack
            {
                Disc = J.Int(t, "disk_number") is > 0 and var d ? d : 1,
                Title = J.Str(t, "title"),
                Artist = J.Str(J.Obj(t, "artist"), "name") is { Length: > 0 } ar ? ar : info.Artist,
                LengthMs = dur > 0 ? dur * 1000 : null
            });
        }
        return raw;
    }
}

// ═══════════════════════════════════════════════════════════════════ Apple Music (iTunes Search API)

/// <summary>Apple Music / iTunes Search API: gratis, senza chiave (circa 20 richieste al minuto).</summary>
public sealed class ITunesSearch(HttpClient http, string country = "IT") : INameSearch
{
    public string Name => "Apple Music";
    public const int MaxDetails = 6;

    public async Task<int> SearchAsync(string artist, string album, int cdTracks, Action<RawRelease> found, CancellationToken ct)
    {
        string term = Uri.EscapeDataString($"{artist} {album}".Trim());
        string json = await Get($"https://itunes.apple.com/search?term={term}&entity=album&limit=25&country={country}", ct).ConfigureAwait(false);
        var picks = PickSearchResults(json, artist, album, cdTracks, MaxDetails).Select(id => (id, country)).ToList();
        // ogni negozio ha un catalogo diverso: se in quello italiano non c'è un album adatto provo anche quello americano
        if (!HasGoodCandidate(json, artist, album, cdTracks) && !country.Equals("US", StringComparison.OrdinalIgnoreCase))
        {
            string us = await Get($"https://itunes.apple.com/search?term={term}&entity=album&limit=25&country=US", ct).ConfigureAwait(false);
            var usPicks = PickSearchResults(us, artist, album, cdTracks, MaxDetails);
            picks = usPicks.Select(id => (id, "US")).Concat(picks).GroupBy(p => p.id).Select(g => g.First()).Take(MaxDetails).ToList();
        }
        // niente di niente (errore di battitura nell'artista?): riprovo col solo titolo dell'album
        if (picks.Count == 0 && !string.IsNullOrWhiteSpace(album) && !string.IsNullOrWhiteSpace(artist))
        {
            json = await Get($"https://itunes.apple.com/search?term={Uri.EscapeDataString(album.Trim())}&entity=album&limit=25&country={country}", ct).ConfigureAwait(false);
            picks = PickSearchResults(json, artist, album, cdTracks, MaxDetails).Select(id => (id, country)).ToList();
        }
        int count = 0;
        foreach (var (id, store) in picks)
        {
            ct.ThrowIfCancellationRequested();
            var raw = Parse(await Get($"https://itunes.apple.com/lookup?id={id}&entity=song&limit=200&country={store}", ct).ConfigureAwait(false));
            if (raw == null || raw.Tracks.Count == 0) continue;
            found(raw); count++;
        }
        return count;
    }

    async Task<string> Get(string url, CancellationToken ct)
    {
        try { return await J.GetAsync(http, url, Name, ct).ConfigureAwait(false); }
        catch (PortalException ex) when (ex.Message.Contains("(403)"))
        {
            throw new PortalException("Apple Music: richiesta rifiutata (troppe ricerche in poco tempo), riprova tra un minuto.");
        }
    }

    static IEnumerable<(long id, int nb, int rel, int i)> Candidates(string json, string artist, string album, int cdTracks)
    {
        using var doc = JsonDocument.Parse(json);
        return J.Arr(doc.RootElement, "results")
            .Where(a => J.Str(a, "wrapperType") == "collection")
            .Select((a, i) => (id: J.Long(a, "collectionId"), nb: J.Int(a, "trackCount"),
                               rel: NameMatcher.Relevance(artist, album, J.Str(a, "artistName"), J.Str(a, "collectionName")), i))
            .Where(x => x.id > 0 && !(cdTracks > 4 && x.nb is > 0 and <= 2))
            .ToList();
    }

    /// <summary>Prima gli album con titolo e artista simili a quelli cercati, poi per numero di tracce vicino a quello del CD.</summary>
    public static List<long> PickSearchResults(string json, string artist, string album, int cdTracks, int max) =>
        Candidates(json, artist, album, cdTracks)
            .OrderByDescending(x => x.rel).ThenBy(x => Math.Abs(x.nb - cdTracks)).ThenBy(x => x.i)
            .Take(max).Select(x => x.id).ToList();

    /// <summary>C'è almeno un album col nome giusto e un numero di tracce vicino a quello del CD?</summary>
    public static bool HasGoodCandidate(string json, string artist, string album, int cdTracks)
    {
        int want = (string.IsNullOrWhiteSpace(album) ? 0 : 2) + (string.IsNullOrWhiteSpace(artist) ? 0 : 1);
        return Candidates(json, artist, album, cdTracks).Any(x => x.rel >= want && Math.Abs(x.nb - cdTracks) <= 3);
    }

    public static RawRelease? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var results = J.Arr(doc.RootElement, "results").ToList();
        var col = results.FirstOrDefault(r => J.Str(r, "wrapperType") == "collection");
        if (col.ValueKind != JsonValueKind.Object) return null;
        string art = J.Str(col, "artworkUrl100");
        var info = new AlbumMeta
        {
            Source = "Apple Music",
            ProviderId = J.Long(col, "collectionId").ToString(CultureInfo.InvariantCulture),
            Artist = J.Str(col, "artistName"),
            Album = J.Str(col, "collectionName"),
            Year = J.Year(J.Str(col, "releaseDate")),
            Genre = J.Str(col, "primaryGenreName"),
            CoverUrl = art.Length > 0 ? Regex.Replace(art, @"/\d+x\d+bb\.(jpg|png)$", "/600x600bb.$1") : null,
        };
        var raw = new RawRelease { Info = info };
        foreach (var t in results.Where(r => J.Str(r, "wrapperType") == "track" && J.Str(r, "kind") is "song" or "")
                                 .OrderBy(r => J.Int(r, "discNumber")).ThenBy(r => J.Int(r, "trackNumber")))
        {
            long ms = J.Long(t, "trackTimeMillis");
            raw.Tracks.Add(new RawTrack
            {
                Disc = J.Int(t, "discNumber") is > 0 and var d ? d : 1,
                Title = J.Str(t, "trackName"),
                Artist = J.Str(t, "artistName") is { Length: > 0 } a ? a : info.Artist,
                LengthMs = ms > 0 ? (int)ms : null
            });
        }
        return raw;
    }
}

// ═══════════════════════════════════════════════════════════════════ Discogs

/// <summary>
/// Discogs: la ricerca funziona anche senza account (25 richieste al minuto, niente copertine).
/// Con il token personale (gratuito) il limite sale a 60 al minuto e arrivano le copertine.
/// </summary>
public sealed class DiscogsSearch(HttpClient http, string token) : INameSearch
{
    public string Name => "Discogs";
    public const int MaxDetails = 6;

    Task<string> Get(string url, CancellationToken ct) => J.GetAsync(http, url, Name, ct, req =>
    {
        if (!string.IsNullOrWhiteSpace(token)) req.Headers.TryAddWithoutValidation("Authorization", $"Discogs token={token.Trim()}");
    });

    public async Task<int> SearchAsync(string artist, string album, int cdTracks, Action<RawRelease> found, CancellationToken ct)
    {
        var qs = new List<string> { "type=release", "per_page=25" };
        if (!string.IsNullOrWhiteSpace(artist)) qs.Add("artist=" + Uri.EscapeDataString(artist.Trim()));
        if (!string.IsNullOrWhiteSpace(album)) qs.Add("release_title=" + Uri.EscapeDataString(album.Trim()));
        string json;
        try { json = await Get("https://api.discogs.com/database/search?" + string.Join('&', qs), ct).ConfigureAwait(false); }
        catch (PortalException ex) when (ex.Message.Contains("(401)") || ex.Message.Contains("(403)"))
        {
            throw new PortalException(string.IsNullOrWhiteSpace(token)
                ? "Discogs: la ricerca chiede il token. Inseriscilo in Impostazioni → Riconoscimento del disco."
                : "Discogs: token non valido. Controllalo in Impostazioni → Riconoscimento del disco.");
        }
        var ids = PickSearchResults(json, artist, album, MaxDetails);
        // Discogs non perdona gli errori di battitura: riprovo con tutto in un campo solo, poi col solo titolo
        var fallbacks = new List<string> { $"{artist} {album}".Trim() };
        if (!string.IsNullOrWhiteSpace(album) && !string.IsNullOrWhiteSpace(artist)) fallbacks.Add(album.Trim());
        foreach (var q in fallbacks)
        {
            if (ids.Count > 0) break;
            json = await Get($"https://api.discogs.com/database/search?type=release&per_page=25&q={Uri.EscapeDataString(q)}", ct).ConfigureAwait(false);
            ids = PickSearchResults(json, artist, album, MaxDetails);
        }
        int count = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            var raw = ParseRelease(await Get($"https://api.discogs.com/releases/{id}", ct).ConfigureAwait(false));
            if (raw == null || raw.Tracks.Count == 0) continue;
            found(raw); count++;
        }
        return count;
    }

    /// <summary>
    /// Prima i titoli simili a quelli cercati, tra questi prima i CD; una sola stampa per
    /// combinazione titolo/formato/anno/paese.
    /// </summary>
    public static List<long> PickSearchResults(string json, string artist, string album, int max)
    {
        using var doc = JsonDocument.Parse(json);
        var seen = new HashSet<string>();
        return J.Arr(doc.RootElement, "results")
            .Select((r, i) =>
            {
                var fmts = J.Arr(r, "format").Select(f => f.ValueKind == JsonValueKind.String ? f.GetString() ?? "" : "").ToList();
                bool cd = fmts.Any(f => f is "CD" or "CDr" or "SACD" or "Hybrid");
                string title = J.Str(r, "title");                    // "Artista - Album"
                int sep = title.IndexOf(" - ", StringComparison.Ordinal);
                string tArtist = sep > 0 ? title[..sep] : "", tAlbum = sep > 0 ? title[(sep + 3)..] : title;
                int rel = NameMatcher.Relevance(artist, album, CleanName(tArtist), tAlbum);
                string key = NameMatcher.Normalize(title) + "|" + string.Join(",", fmts) + "|" + J.Str(r, "year") + "|" + J.Str(r, "country");
                return (id: J.Long(r, "id"), cd, rel, key, i);
            })
            .Where(x => x.id > 0)
            .OrderByDescending(x => x.rel).ThenByDescending(x => x.cd).ThenBy(x => x.i)
            .Where(x => seen.Add(x.key))
            .Take(max).Select(x => x.id).ToList();
    }

    /// <summary>"Pino Daniele (2)" → "Pino Daniele" (Discogs numera gli omonimi).</summary>
    public static string CleanName(string s) => Regex.Replace(s ?? "", @"\s+\(\d+\)$", "").Trim();

    static string Artists(JsonElement parent)
    {
        var sb = new StringBuilder();
        var list = J.Arr(parent, "artists").ToList();
        for (int i = 0; i < list.Count; i++)
        {
            var a = list[i];
            string name = J.Str(a, "anv") is { Length: > 0 } anv ? anv : CleanName(J.Str(a, "name"));
            sb.Append(name);
            string join = J.Str(a, "join").Trim();
            if (i < list.Count - 1) sb.Append(join.Length == 0 || join == "," ? ", " : $" {join} ");
        }
        return sb.ToString().Trim();
    }

    /// <summary>"1-03", "1.3", "CD2-05", "2-5" → disco; "3", "A1", "B2" → disco 1.</summary>
    public static int DiscOf(string position)
    {
        var m = Regex.Match(position ?? "", @"^(?:CD|Disc|Disk|DVD)?\s*(\d+)\s*[\.\-]\s*\d+", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var d) && d > 0 ? d : 1;
    }

    /// <summary>"4:12", "1:02:33" → millisecondi.</summary>
    public static int? Duration(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Trim().Split(':');
        int total = 0;
        foreach (var p in parts)
        {
            if (!int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v < 0) return null;
            total = total * 60 + v;
        }
        return total > 0 ? total * 1000 : null;
    }

    public static RawRelease? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (J.Long(r, "id") <= 0) return null;
        var fmt = J.Arr(r, "formats").FirstOrDefault();
        string format = "";
        if (fmt.ValueKind == JsonValueKind.Object)
        {
            int qty = J.Int(fmt, "qty");
            format = (qty > 1 ? $"{qty}×" : "") + J.Str(fmt, "name");
        }
        string year = J.Int(r, "year") is > 0 and var y ? y.ToString(CultureInfo.InvariantCulture) : J.Year(J.Str(r, "released"));
        var img = J.Arr(r, "images").OrderByDescending(i => J.Str(i, "type") == "primary").FirstOrDefault();
        var info = new AlbumMeta
        {
            Source = "Discogs",
            ProviderId = J.Long(r, "id").ToString(CultureInfo.InvariantCulture),
            Artist = Artists(r),
            Album = J.Str(r, "title"),
            Year = year,
            Genre = J.Arr(r, "genres").Select(g => g.ValueKind == JsonValueKind.String ? g.GetString() ?? "" : "").FirstOrDefault(s => s.Length > 0) ?? "",
            Country = J.Str(r, "country"),
            Label = CleanName(J.Arr(r, "labels").Select(l => J.Str(l, "name")).FirstOrDefault(s => s.Length > 0) ?? ""),
            Barcode = J.Arr(r, "identifiers").Where(i => J.Str(i, "type") == "Barcode").Select(i => Regex.Replace(J.Str(i, "value"), @"\D", ""))
                       .FirstOrDefault(v => v.Length >= 8) ?? "",
            Format = format,
            CoverUrl = img.ValueKind == JsonValueKind.Object && J.Str(img, "uri") is { Length: > 0 } u ? u : null,
        };
        var raw = new RawRelease { Info = info };
        foreach (var t in J.Arr(r, "tracklist"))
        {
            string type = J.Str(t, "type_");
            if (type is not ("track" or "index" or "")) continue; // "heading": titoli di sezione, non brani
            string trackArtists = Artists(t);
            raw.Tracks.Add(new RawTrack
            {
                Disc = DiscOf(J.Str(t, "position")),
                Title = J.Str(t, "title"),
                Artist = trackArtists.Length > 0 ? trackArtists : info.Artist,
                LengthMs = Duration(J.Str(t, "duration"))
            });
        }
        return raw;
    }
}

// ═══════════════════════════════════════════════════════════════════ Last.fm

/// <summary>Last.fm: serve una API key gratuita (last.fm/api/account/create).</summary>
public sealed class LastFmSearch(HttpClient http, string apiKey) : INameSearch
{
    public string Name => "Last.fm";
    public const int MaxDetails = 5;

    string Url(string method, string args) =>
        $"https://ws.audioscrobbler.com/2.0/?method={method}&{args}&api_key={Uri.EscapeDataString(apiKey.Trim())}&format=json";

    async Task<JsonDocument> Get(string url, CancellationToken ct)
    {
        string json = await J.GetAsync(http, url, Name, ct).ConfigureAwait(false);
        var d = JsonDocument.Parse(json);
        if (d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("error", out _))
        {
            int code = J.Int(d.RootElement, "error");
            string msg = J.Str(d.RootElement, "message");
            d.Dispose();
            throw new PortalException(code switch
            {
                10 or 26 => "Last.fm: API key non valida. Controllala in Impostazioni → Riconoscimento del disco.",
                29 => "Last.fm: troppe richieste ravvicinate, riprova tra un minuto.",
                6 => "Last.fm: album non trovato.",
                _ => $"Last.fm: {msg}"
            });
        }
        return d;
    }

    public async Task<int> SearchAsync(string artist, string album, int cdTracks, Action<RawRelease> found, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new PortalException("Last.fm: manca la API key (Impostazioni → Riconoscimento del disco).");
        var pairs = new List<(string artist, string album)>();
        if (!string.IsNullOrWhiteSpace(album))
        {
            using var d = await Get(Url("album.search", $"album={Uri.EscapeDataString(album.Trim())}&limit=20"), ct).ConfigureAwait(false);
            pairs = PickSearchResults(d.RootElement, artist, MaxDetails);
        }
        else
        {
            using var d = await Get(Url("artist.gettopalbums", $"artist={Uri.EscapeDataString(artist.Trim())}&autocorrect=1&limit=15"), ct).ConfigureAwait(false);
            pairs = J.Arr(J.Obj(d.RootElement, "topalbums"), "album")
                     .Select(a => (J.Str(J.Obj(a, "artist"), "name"), J.Str(a, "name")))
                     .Where(p => p.Item1.Length > 0 && p.Item2.Length > 0 && p.Item2 != "(null)").Take(MaxDetails + 3).ToList();
        }
        int count = 0;
        foreach (var (ar, al) in pairs)
        {
            ct.ThrowIfCancellationRequested();
            RawRelease? raw;
            try
            {
                using var d = await Get(Url("album.getinfo", $"artist={Uri.EscapeDataString(ar)}&album={Uri.EscapeDataString(al)}&autocorrect=1"), ct).ConfigureAwait(false);
                raw = ParseAlbum(d.RootElement);
            }
            catch (PortalException ex) when (ex.Message.StartsWith("Last.fm: album non trovato")) { continue; }
            if (raw == null || raw.Tracks.Count == 0) continue; // senza tracce non serve
            found(raw); count++;
            if (count >= MaxDetails) break;
        }
        return count;
    }

    /// <summary>Album trovati da album.search, prima quelli dell'artista cercato.</summary>
    public static List<(string artist, string album)> PickSearchResults(JsonElement root, string artist, int max)
    {
        var list = J.Arr(J.Obj(J.Obj(root, "results"), "albummatches"), "album")
            .Select((a, i) => (artist: J.Str(a, "artist"), album: J.Str(a, "name"), i))
            .Where(x => x.artist.Length > 0 && x.album.Length > 0)
            .ToList();
        if (!string.IsNullOrWhiteSpace(artist))
            list = list.OrderByDescending(x => NameMatcher.Similarity(artist, "", x.artist, "") >= 0.5).ThenBy(x => x.i).ToList();
        var seen = new HashSet<string>();
        return list.Where(x => seen.Add(NameMatcher.Normalize(x.artist) + "|" + NameMatcher.Normalize(x.album)))
                   .Take(max + 3).Select(x => (x.artist, x.album)).ToList();
    }

    public static RawRelease? ParseAlbum(JsonElement root)
    {
        var a = J.Obj(root, "album");
        if (a.ValueKind != JsonValueKind.Object) return null;
        var imgs = J.Arr(a, "image").ToList();
        string cover = imgs.Where(i => J.Str(i, "size") is "mega" or "extralarge").Select(i => J.Str(i, "#text")).LastOrDefault(s => s.Length > 0) ?? "";
        string artist = J.Str(a, "artist");
        var tags = J.Obj(a, "tags");
        var info = new AlbumMeta
        {
            Source = "Last.fm",
            ProviderId = J.Str(a, "mbid") is { Length: > 0 } mb ? mb : NameMatcher.Normalize(artist) + "|" + NameMatcher.Normalize(J.Str(a, "name")),
            Artist = artist,
            Album = J.Str(a, "name"),
            Genre = J.Cap(J.Arr(tags, "tag").Select(t => J.Str(t, "name")).FirstOrDefault(s => s.Length > 0) ?? ""),
            CoverUrl = cover.Length > 0 ? cover : null,
        };
        var raw = new RawRelease { Info = info };
        foreach (var t in J.Arr(J.Obj(a, "tracks"), "track")
                           .Select((t, i) => (t, rank: J.Int(J.Obj(t, "@attr"), "rank") is > 0 and var r ? r : i + 1))
                           .OrderBy(x => x.rank).Select(x => x.t))
        {
            int sec = J.Int(t, "duration");
            var ta = J.Obj(t, "artist");
            raw.Tracks.Add(new RawTrack
            {
                Title = J.Str(t, "name"),
                Artist = ta.ValueKind == JsonValueKind.Object ? J.Str(ta, "name") : J.Str(t, "artist") is { Length: > 0 } s ? s : artist,
                LengthMs = sec > 0 ? sec * 1000 : null
            });
        }
        return raw;
    }
}

/// <summary>I portali da usare, in base alle impostazioni.</summary>
public static class NameSearchPortals
{
    public sealed record Portal(string Name, string Hint, bool Available, Func<INameSearch> Create);

    public static List<Portal> All(HttpClient http, AppSettings s) => new()
    {
        new("MusicBrainz", "gratis, senza chiave", true, () => new MusicBrainzClient(http)),
        new("Deezer", "gratis, senza chiave", true, () => new DeezerSearch(http)),
        new("Apple Music", "gratis, senza chiave", true, () => new ITunesSearch(http)),
        new("Discogs", string.IsNullOrWhiteSpace(s.DiscogsToken) ? "senza token: più lento e senza copertine" : "con il tuo token",
            true, () => new DiscogsSearch(http, s.DiscogsToken)),
        new("Last.fm", string.IsNullOrWhiteSpace(s.LastFmApiKey) ? "serve la API key (Impostazioni)" : "con la tua API key",
            !string.IsNullOrWhiteSpace(s.LastFmApiKey), () => new LastFmSearch(http, s.LastFmApiKey)),
    };
}
