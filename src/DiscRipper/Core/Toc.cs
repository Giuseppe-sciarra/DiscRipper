using System.Security.Cryptography;
using System.Text;

namespace DiscRipper.Core;

/// <summary>Una traccia della TOC. LBA "assoluti" senza i 150 settori di lead-in.</summary>
public sealed class TocTrack
{
    public int Number { get; init; }
    public int StartLba { get; init; }
    /// <summary>Primo settore NON appartenente alla traccia (esclusivo).</summary>
    public int EndLba { get; set; }
    public bool IsAudio { get; init; }
    public bool PreEmphasis { get; init; }

    public int Sectors => EndLba - StartLba;
    public long Samples => (long)Sectors * Toc.SamplesPerSector;
    public TimeSpan Duration => TimeSpan.FromSeconds(Sectors / 75.0);
}

public sealed class Toc
{
    public const int SamplesPerSector = 588;
    public const int BytesPerSector = 2352;
    public const int BytesPerSample = 4;

    public List<TocTrack> Tracks { get; } = new();
    /// <summary>Lead-out reale del disco (ultima sessione).</summary>
    public int LeadoutLba { get; set; }

    public IReadOnlyList<TocTrack> AudioTracks => Tracks.Where(t => t.IsAudio).ToList();
    public bool HasDataTrack => Tracks.Any(t => !t.IsAudio);

    /// <summary>Fine dell'area audio leggibile (per CD Extra esclude il gap di 11400 settori).</summary>
    public int AudioAreaEndLba => AudioTracks.Count == 0 ? 0 : AudioTracks[^1].EndLba;

    public TimeSpan TotalDuration => TimeSpan.FromSeconds(AudioTracks.Sum(t => t.Sectors) / 75.0);

    /// <summary>Calcola le EndLba. Da chiamare dopo aver riempito Tracks e LeadoutLba.</summary>
    public void ComputeEnds()
    {
        Tracks.Sort((a, b) => a.Number.CompareTo(b.Number));
        for (int i = 0; i < Tracks.Count; i++)
        {
            var t = Tracks[i];
            if (i + 1 < Tracks.Count)
            {
                var next = Tracks[i + 1];
                // audio seguito da traccia dati (CD Extra / Enhanced CD): gap tra sessioni di 11400 settori
                t.EndLba = (t.IsAudio && !next.IsAudio) ? next.StartLba - 11400 : next.StartLba;
            }
            else t.EndLba = LeadoutLba;
        }
    }

    public static Toc Build(IEnumerable<(int number, int startLba, bool audio)> tracks, int leadout)
    {
        var toc = new Toc { LeadoutLba = leadout };
        foreach (var (n, s, a) in tracks)
            toc.Tracks.Add(new TocTrack { Number = n, StartLba = s, IsAudio = a });
        toc.ComputeEnds();
        return toc;
    }

    // ------------------------------------------------------------------ MusicBrainz

    /// <summary>Valori per la disc ID MusicBrainz: first, last, leadout+150, offsets+150.</summary>
    public int[] MusicBrainzValues()
    {
        var audio = AudioTracks;
        if (audio.Count == 0) return Array.Empty<int>();
        int leadout;
        var last = Tracks[^1];
        if (!last.IsAudio) leadout = last.StartLba - 11400; // CD Extra: la traccia dati finale non conta
        else leadout = LeadoutLba;

        var vals = new List<int> { audio[0].Number, audio[^1].Number, leadout + 150 };
        vals.AddRange(audio.Select(t => t.StartLba + 150));
        return vals.ToArray();
    }

    public string MusicBrainzDiscId()
    {
        var v = MusicBrainzValues();
        if (v.Length == 0) return "";
        var sb = new StringBuilder();
        sb.Append(v[0].ToString("X2"));
        sb.Append(v[1].ToString("X2"));
        sb.Append(v[2].ToString("X8"));
        var offsets = new int[100];
        for (int i = 3; i < v.Length; i++)
        {
            int trackNo = v[0] + (i - 3);
            if (trackNo >= 1 && trackNo <= 99) offsets[trackNo] = v[i];
        }
        for (int i = 1; i < 100; i++) sb.Append(offsets[i].ToString("X8"));
        var hash = SHA1.HashData(Encoding.ASCII.GetBytes(sb.ToString()));
        return Convert.ToBase64String(hash).Replace('+', '.').Replace('/', '_').Replace('=', '-');
    }

    /// <summary>Stringa TOC per il lookup fuzzy MusicBrainz (?toc=).</summary>
    public string MusicBrainzTocParam()
    {
        var v = MusicBrainzValues();
        if (v.Length == 0) return "";
        // formato: first last leadout off1 off2 ... ; "last" = numero di tracce audio
        var list = new List<int> { v[0], v[1], v[2] };
        list.AddRange(v.Skip(3));
        return string.Join('+', list);
    }

    // ------------------------------------------------------------------ CDDB / GnuDB

    public uint CddbDiscId()
    {
        static int DigitSum(int n) { int s = 0; while (n > 0) { s += n % 10; n /= 10; } return s; }
        int n = 0;
        foreach (var t in Tracks) n += DigitSum((t.StartLba + 150) / 75);
        int total = (LeadoutLba + 150) / 75 - (Tracks[0].StartLba + 150) / 75;
        return (uint)(((n % 0xFF) << 24) | (total << 8) | Tracks.Count);
    }

    public string CddbQueryArgs()
    {
        // discid ntrks off1 off2 ... nsecs
        var parts = new List<string> { CddbDiscId().ToString("x8"), Tracks.Count.ToString() };
        parts.AddRange(Tracks.Select(t => (t.StartLba + 150).ToString()));
        parts.Add(((LeadoutLba + 150) / 75).ToString());
        return string.Join('+', parts);
    }

    // ------------------------------------------------------------------ AccurateRip

    public (uint id1, uint id2) AccurateRipIds()
    {
        uint id1 = 0, id2 = 0;
        foreach (var t in Tracks.Where(t => t.IsAudio))
        {
            uint off = (uint)t.StartLba;
            id1 += off;
            id2 += (off == 0 ? 1u : off) * (uint)t.Number;
        }
        uint lo = (uint)LeadoutLba; // conta anche la traccia dati per il lead-out
        id1 += lo;
        id2 += lo * (uint)(AudioTracks.Count + 1);
        return (id1, id2);
    }

    public string AccurateRipPath()
    {
        var (id1, id2) = AccurateRipIds();
        string h = id1.ToString("x8");
        return $"{h[7]}/{h[6]}/{h[5]}/dBAR-{AudioTracks.Count:D3}-{h}-{id2:x8}-{CddbDiscId():x8}.bin";
    }
}
