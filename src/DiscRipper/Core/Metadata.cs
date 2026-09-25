namespace DiscRipper.Core;

public sealed class TrackMeta
{
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string? MbRecordingId { get; set; }
    public string? MbTrackId { get; set; }
    public string? MbArtistId { get; set; }
}

/// <summary>Un candidato di metadati (una release MusicBrainz, una voce GnuDB, il CD-Text…).</summary>
public sealed class AlbumMeta
{
    public string Source { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string Year { get; set; } = "";
    public string Genre { get; set; } = "";
    public string Country { get; set; } = "";
    public string Label { get; set; } = "";
    public string Barcode { get; set; } = "";
    public string Format { get; set; } = "";
    public int DiscNumber { get; set; } = 1;
    public int DiscTotal { get; set; } = 1;
    public bool ExactMatch { get; set; }
    public string? MbReleaseId { get; set; }
    public string? MbReleaseGroupId { get; set; }
    public string? MbAlbumArtistId { get; set; }
    public List<TrackMeta> Tracks { get; } = new();

    public bool IsCompilation =>
        Tracks.Select(t => t.Artist).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1
        || Artist.Equals("Various Artists", StringComparison.OrdinalIgnoreCase)
        || Artist.Equals("Artisti vari", StringComparison.OrdinalIgnoreCase);

    public string Display
    {
        get
        {
            var extra = new List<string>();
            if (Year.Length > 0) extra.Add(Year);
            if (Country.Length > 0) extra.Add(Country);
            if (Format.Length > 0) extra.Add(Format);
            if (Label.Length > 0) extra.Add(Label);
            if (DiscTotal > 1) extra.Add($"CD {DiscNumber}/{DiscTotal}");
            string e = extra.Count > 0 ? $"  ({string.Join(", ", extra)})" : "";
            string fuzzy = ExactMatch ? "" : "  ≈";
            return $"[{Source}] {Artist} — {Album}{e}{fuzzy}";
        }
    }

    public override string ToString() => Display;

    public static AlbumMeta Empty(Toc toc, string source = "Manuale")
    {
        var m = new AlbumMeta { Source = source, Artist = "Artista sconosciuto", Album = "Album sconosciuto", ExactMatch = true };
        foreach (var t in toc.AudioTracks)
            m.Tracks.Add(new TrackMeta { Number = t.Number, Title = $"Traccia {t.Number:D2}", Artist = "" });
        return m;
    }

    public static AlbumMeta? FromCdText(Toc toc, CdTextInfo? txt)
    {
        if (txt == null) return null;
        var m = new AlbumMeta
        {
            Source = "CD-Text",
            Artist = txt.AlbumPerformer ?? "Artista sconosciuto",
            Album = txt.AlbumTitle ?? "Album sconosciuto",
            ExactMatch = true
        };
        foreach (var t in toc.AudioTracks)
        {
            txt.Titles.TryGetValue(t.Number, out var title);
            txt.Performers.TryGetValue(t.Number, out var perf);
            m.Tracks.Add(new TrackMeta
            {
                Number = t.Number,
                Title = string.IsNullOrWhiteSpace(title) ? $"Traccia {t.Number:D2}" : title!,
                Artist = string.IsNullOrWhiteSpace(perf) ? m.Artist : perf!
            });
        }
        return m;
    }
}
