using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;

namespace DiscRipper.Core;

public sealed class ArPressing
{
    public uint[] Crc { get; init; } = Array.Empty<uint>();
    public int[] Confidence { get; init; } = Array.Empty<int>();
    public uint[] Crc450 { get; init; } = Array.Empty<uint>();
}

public sealed class ArMatch
{
    public bool Found { get; init; }
    public int Confidence { get; init; }      // confidenza della stampa che coincide
    public int TotalConfidence { get; init; } // somma delle confidenze di tutte le stampe per la traccia
    public string Version { get; init; } = "";
}

/// <summary>Voci AccurateRip scaricate per un disco.</summary>
public sealed class ArDisc
{
    public const string BaseUrl = "http://www.accuraterip.com/accuraterip/";
    public List<ArPressing> Pressings { get; } = new();
    public int TrackCount { get; init; }

    public static ArDisc? Parse(ReadOnlySpan<byte> raw, int expectedTracks)
    {
        var disc = new ArDisc { TrackCount = expectedTracks };
        int pos = 0;
        while (pos + 13 <= raw.Length)
        {
            int n = raw[pos];
            int size = 13 + n * 9;
            if (n == 0 || pos + size > raw.Length) break;
            var crc = new uint[n]; var conf = new int[n]; var c450 = new uint[n];
            int p = pos + 13;
            for (int i = 0; i < n; i++, p += 9)
            {
                conf[i] = raw[p];
                crc[i] = BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(p + 1, 4));
                c450[i] = BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(p + 5, 4));
            }
            if (n == expectedTracks)
                disc.Pressings.Add(new ArPressing { Crc = crc, Confidence = conf, Crc450 = c450 });
            pos += size;
        }
        return disc.Pressings.Count > 0 ? disc : null;
    }

    public static async Task<ArDisc?> FetchAsync(HttpClient http, Toc toc, CancellationToken ct)
    {
        var url = BaseUrl + toc.AccurateRipPath();
        using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return Parse(bytes, toc.AudioTracks.Count);
    }

    /// <summary>Confronta i CRC calcolati con il database (indice traccia audio 0-based).</summary>
    public ArMatch Match(int audioIndex, uint v1, uint v2)
    {
        int total = 0, best = -1; string ver = "";
        foreach (var p in Pressings)
        {
            if (audioIndex >= p.Crc.Length) continue;
            total += p.Confidence[audioIndex];
            if (p.Crc[audioIndex] == v2 && p.Confidence[audioIndex] > best) { best = p.Confidence[audioIndex]; ver = "v2"; }
            else if (p.Crc[audioIndex] == v1 && p.Confidence[audioIndex] > best) { best = p.Confidence[audioIndex]; ver = "v1"; }
        }
        return new ArMatch { Found = best >= 0, Confidence = Math.Max(best, 0), TotalConfidence = total, Version = ver };
    }

    public int MaxConfidence(int audioIndex) =>
        Pressings.Where(p => audioIndex < p.Confidence.Length).Select(p => p.Confidence[audioIndex]).DefaultIfEmpty(0).Max();
}

public static class ArChecksum
{
    public const int SkipSamples = 5 * Toc.SamplesPerSector; // 2940

    /// <summary>CRC AccurateRip v1 e v2 sui campioni (PCM 16 bit stereo LE, 4 byte/campione).</summary>
    public static (uint v1, uint v2) Compute(ReadOnlySpan<byte> pcm, bool isFirst, bool isLast)
    {
        var samples = MemoryMarshal.Cast<byte, uint>(pcm);
        uint count = (uint)samples.Length;
        uint from = isFirst ? (uint)SkipSamples : 0u;
        uint to = isLast ? count - (uint)SkipSamples : count;
        uint v1 = 0, v2 = 0;
        uint mult = 1;
        for (int i = 0; i < samples.Length; i++, mult++)
        {
            if (mult >= from && mult <= to)
            {
                uint s = samples[i];
                v1 += mult * s;
                ulong prod = (ulong)s * mult;
                v2 += (uint)(prod & 0xFFFFFFFF) + (uint)(prod >> 32);
            }
        }
        return (v1, v2);
    }

    /// <summary>Offset più comuni dei lettori (fonte: AccurateRip / whipper).</summary>
    public static readonly int[] CommonOffsets =
    {
        6, 667, 48, 102, 30, 12, 103, 618, 96, 738, 594, 98, -472, 733, 696, 116, 120, 691, 685,
        99, 702, 97, 600, 676, 690, 1292, 686, 697, -24, 704, 572, 1182, 688, -491, 91, 145, 689,
        86, 355, 708, 79, 564, -496, 679, -1164, 0, 1160, -436, 684, 694, 1194, 94, 106, 681,
        678, 117, 692, 943, 92, 680, 682, 1268, 1279, 1473, -54, 1263, -582, 674, 687, 1272, 1508,
        -489, 740, 675, 534, 122, 974, 976, 1303, 111, 108, 1130, 975, 87, 739, 732, -589, -495,
        -494, -12, 961, 935, 699, 668, 234, 1776, 138, 1364, 1336, 1262, 1161, 1127
    };

    public const int DetectMargin = 1800; // campioni cercati in ciascuna direzione con la scansione completa

    /// <summary>
    /// Cerca l'offset di lettura. <paramref name="raw"/> contiene i campioni grezzi (offset 0) della traccia
    /// a partire da (inizio traccia - margin) per (lunghezza traccia + 2*margin) campioni.
    /// Restituisce l'offset con la confidenza più alta, o null.
    /// </summary>
    public static (int offset, int confidence)? DetectOffset(ReadOnlySpan<byte> rawBytes, int margin, int trackSamples,
        int audioIndex, int audioCount, ArDisc ar)
    {
        var raw = MemoryMarshal.Cast<byte, uint>(rawBytes);
        bool isFirst = audioIndex == 0, isLast = audioIndex == audioCount - 1;

        var wanted = new Dictionary<uint, int>();
        foreach (var p in ar.Pressings)
        {
            if (audioIndex >= p.Crc.Length || p.Confidence[audioIndex] == 0) continue;
            uint c = p.Crc[audioIndex];
            wanted[c] = Math.Max(wanted.TryGetValue(c, out var old) ? old : 0, p.Confidence[audioIndex]);
        }
        if (wanted.Count == 0) return null;

        (int offset, int confidence)? best = null;
        void Consider(int off, uint crc)
        {
            if (wanted.TryGetValue(crc, out var conf) && (best == null || conf > best.Value.confidence
                || (conf == best.Value.confidence && Math.Abs(off) < Math.Abs(best.Value.offset))))
                best = (off, conf);
        }

        int n = trackSamples;
        if (!isFirst && !isLast)
        {
            // Scansione completa v1 con finestra scorrevole: O(1) per ogni offset.
            uint crc = 0, sum = 0;
            for (int i = 0; i < n; i++) { uint s = raw[i]; crc += (uint)(i + 1) * s; sum += s; }
            for (int off = -margin; ; off++)
            {
                Consider(off, crc);
                int start = off + margin; // indice nel buffer
                if (off == margin) break;
                uint outS = raw[start], inS = raw[start + n];
                crc = crc - sum + (uint)n * inS;
                sum = sum - outS + inS;
            }
        }

        // v2 (e v1 per tracce iniziali/finali) sugli offset comuni
        foreach (var off in CommonOffsets)
        {
            if (off < -margin || off > margin) continue;
            var slice = rawBytes.Slice((off + margin) * 4, n * 4);
            var (v1, v2) = Compute(slice, isFirst, isLast);
            Consider(off, v2);
            Consider(off, v1);
        }
        return best;
    }
}
