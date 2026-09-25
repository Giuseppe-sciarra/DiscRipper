using System.Diagnostics;
using System.Text;

namespace DiscRipper.Core;

public static class PathBuilder
{
    static readonly char[] Invalid = Path.GetInvalidFileNameChars().Concat(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }).Distinct().ToArray();

    public static string Clean(string s, int max = 120)
    {
        if (string.IsNullOrWhiteSpace(s)) return "_";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c < 32) continue;
            sb.Append(c switch
            {
                '/' or '\\' or '|' => '-',
                ':' => '-',
                '"' => '\'',
                '<' => '(',
                '>' => ')',
                '*' or '?' => '_',
                _ => Invalid.Contains(c) ? '_' : c
            });
        }
        var r = sb.ToString().Trim().TrimEnd('.', ' ');
        if (r.Length > max) r = r[..max].TrimEnd('.', ' ');
        string[] reserved = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "LPT1", "LPT2", "LPT3" };
        if (reserved.Contains(r.ToUpperInvariant())) r = "_" + r;
        return r.Length == 0 ? "_" : r;
    }

    /// <summary>Artista\Album (Anno)[\CD n]</summary>
    public static string AlbumFolder(string root, AlbumMeta a)
    {
        string album = a.Year.Length >= 4 ? $"{a.Album} ({a.Year})" : a.Album;
        string dir = Path.Combine(root, Clean(a.Artist), Clean(album));
        if (a.DiscTotal > 1) dir = Path.Combine(dir, $"CD {a.DiscNumber}");
        return dir;
    }

    /// <summary>01 - Titolo  (compilation: 01 - Artista - Titolo)</summary>
    public static string TrackFileName(AlbumMeta a, TrackMeta t, int index1Based, int total)
    {
        string num = index1Based.ToString(total >= 100 ? "D3" : "D2");
        bool showArtist = a.IsCompilation && !string.IsNullOrWhiteSpace(t.Artist)
                          && !t.Artist.Equals(a.Artist, StringComparison.OrdinalIgnoreCase);
        string name = showArtist ? $"{num} - {t.Artist} - {t.Title}" : $"{num} - {t.Title}";
        return Clean(name, 150);
    }
}

public static class FormatInfo
{
    public static readonly OutputFormat[] All =
        { OutputFormat.Mp3, OutputFormat.Flac, OutputFormat.Wav, OutputFormat.M4a, OutputFormat.Ogg, OutputFormat.Opus };

    public static string Ext(OutputFormat f) => f switch
    {
        OutputFormat.Mp3 => ".mp3",
        OutputFormat.Flac => ".flac",
        OutputFormat.Wav => ".wav",
        OutputFormat.M4a => ".m4a",
        OutputFormat.Ogg => ".ogg",
        OutputFormat.Opus => ".opus",
        _ => ".bin"
    };

    public static string Label(OutputFormat f) => f switch
    {
        OutputFormat.M4a => "AAC (M4A)",
        OutputFormat.Ogg => "OGG Vorbis",
        _ => f.ToString().ToUpperInvariant()
    };

    public static string FfmpegArgs(OutputFormat f, AppSettings s) => f switch
    {
        OutputFormat.Mp3 => s.Mp3Quality switch
        {
            "VBR V2" => "-c:a libmp3lame -q:a 2 -id3v2_version 0 -write_id3v1 0",
            "CBR 320" => "-c:a libmp3lame -b:a 320k -id3v2_version 0 -write_id3v1 0",
            "CBR 256" => "-c:a libmp3lame -b:a 256k -id3v2_version 0 -write_id3v1 0",
            "CBR 192" => "-c:a libmp3lame -b:a 192k -id3v2_version 0 -write_id3v1 0",
            _ => "-c:a libmp3lame -q:a 0 -id3v2_version 0 -write_id3v1 0",
        },
        OutputFormat.Flac => $"-c:a flac -compression_level {Math.Clamp(s.FlacLevel, 0, 12)}",
        OutputFormat.M4a => $"-c:a aac -b:a {s.AacBitrate}k -movflags +faststart",
        OutputFormat.Ogg => $"-c:a libvorbis -q:a {Math.Clamp(s.OggQuality, 0, 10)}",
        OutputFormat.Opus => $"-c:a libopus -b:a {s.OpusBitrate}k",
        _ => throw new NotSupportedException()
    };
}

public static class AudioEncoder
{
    public static string? FindFfmpeg()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var p in new[] { Path.Combine(baseDir, "ffmpeg.exe"), Path.Combine(baseDir, "tools", "ffmpeg.exe") })
            if (File.Exists(p)) return p;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { var p = Path.Combine(dir.Trim(), "ffmpeg.exe"); if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    public static void WriteWav(string path, ReadOnlySpan<byte> pcm)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var bw = new BinaryWriter(fs);
        bw.Write("RIFF"u8); bw.Write(36 + pcm.Length); bw.Write("WAVE"u8);
        bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)2);
        bw.Write(44100); bw.Write(44100 * 4); bw.Write((short)4); bw.Write((short)16);
        bw.Write("data"u8); bw.Write(pcm.Length);
        bw.Flush();
        fs.Write(pcm);
    }

    /// <summary>Codifica PCM grezzo (s16le 44.1 kHz stereo) con ffmpeg via stdin.</summary>
    public static async Task EncodeAsync(string ffmpeg, ReadOnlyMemory<byte> pcm, OutputFormat f, AppSettings s, string outPath, CancellationToken ct)
    {
        if (f == OutputFormat.Wav) { WriteWav(outPath, pcm.Span); return; }
        var psi = new ProcessStartInfo(ffmpeg,
            $"-hide_banner -loglevel error -y -f s16le -ar 44100 -ac 2 -i pipe:0 -map_metadata -1 {FormatInfo.FfmpegArgs(f, s)} \"{outPath}\"")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg non avviato");
        var errTask = p.StandardError.ReadToEndAsync();
        var outTask = p.StandardOutput.ReadToEndAsync();
        try
        {
            var stdin = p.StandardInput.BaseStream;
            const int chunk = 1 << 20;
            for (int off = 0; off < pcm.Length; off += chunk)
            {
                ct.ThrowIfCancellationRequested();
                int n = Math.Min(chunk, pcm.Length - off);
                await stdin.WriteAsync(pcm.Slice(off, n), ct).ConfigureAwait(false);
            }
            stdin.Close();
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            try { if (!p.HasExited) p.Kill(true); } catch { }
            throw;
        }
        string err = await errTask.ConfigureAwait(false);
        await outTask.ConfigureAwait(false);
        if (p.ExitCode != 0) throw new InvalidOperationException($"ffmpeg ({f}) errore {p.ExitCode}: {err.Trim()}");
    }
}

public static class Tagger
{
    static Tagger()
    {
        TagLib.Id3v2.Tag.DefaultVersion = 3;       // ID3v2.3: massima compatibilità (Esplora risorse, autoradio)
        TagLib.Id3v2.Tag.ForceDefaultVersion = true;
    }

    public static void Write(string path, OutputFormat f, AlbumMeta a, TrackMeta t, int trackIndex, int trackTotal, byte[]? cover)
    {
        using var file = TagLib.File.Create(path);
        if (f == OutputFormat.Wav)
        {
            file.GetTag(TagLib.TagTypes.Id3v2, true);
            file.GetTag(TagLib.TagTypes.RiffInfo, true);
        }
        else if (f == OutputFormat.Mp3) file.GetTag(TagLib.TagTypes.Id3v2, true);

        var tag = file.Tag;
        string trackArtist = string.IsNullOrWhiteSpace(t.Artist) ? a.Artist : t.Artist;
        tag.Title = t.Title;
        tag.Performers = new[] { trackArtist };
        tag.AlbumArtists = new[] { a.Artist };
        tag.Album = a.Album;
        if (uint.TryParse(a.Year, out var y)) tag.Year = y;
        tag.Track = (uint)trackIndex;
        tag.TrackCount = (uint)trackTotal;
        tag.Disc = (uint)Math.Max(1, a.DiscNumber);
        tag.DiscCount = (uint)Math.Max(1, a.DiscTotal);
        if (!string.IsNullOrWhiteSpace(a.Genre)) tag.Genres = new[] { a.Genre };
        tag.Comment = "DiscRipper";
        try
        {
            if (a.MbReleaseId != null) tag.MusicBrainzReleaseId = a.MbReleaseId;
            if (a.MbAlbumArtistId != null) tag.MusicBrainzReleaseArtistId = a.MbAlbumArtistId;
            if (t.MbRecordingId != null) tag.MusicBrainzTrackId = t.MbRecordingId;
            if (t.MbArtistId != null) tag.MusicBrainzArtistId = t.MbArtistId;
            if (a.Country.Length > 0) tag.MusicBrainzReleaseCountry = a.Country;
        }
        catch { }
        if (cover != null && cover.Length > 0 && f != OutputFormat.Wav)
        {
            var pic = new TagLib.Picture(new TagLib.ByteVector(cover))
            {
                Type = TagLib.PictureType.FrontCover,
                MimeType = IsPng(cover) ? "image/png" : "image/jpeg",
                Description = "Front"
            };
            tag.Pictures = new TagLib.IPicture[] { pic };
        }
        file.Save();
    }

    static bool IsPng(byte[] b) => b.Length > 4 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;
}
