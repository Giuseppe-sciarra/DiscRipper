using System.Text;

namespace DiscRipper.Core;

/// <summary>CD-Text letto dal disco (blocco 0). Indice 0 = album, 1..99 = tracce.</summary>
public sealed class CdTextInfo
{
    public Dictionary<int, string> Titles { get; } = new();
    public Dictionary<int, string> Performers { get; } = new();

    public bool IsEmpty => Titles.Count == 0 && Performers.Count == 0;

    public string? AlbumTitle => Titles.TryGetValue(0, out var s) && s.Length > 0 ? s : null;
    public string? AlbumPerformer => Performers.TryGetValue(0, out var s) && s.Length > 0 ? s : null;

    /// <summary>
    /// Parser dei pack CD-Text (18 byte ciascuno). <paramref name="data"/> inizia dal primo pack
    /// (header di 4 byte già saltato).
    /// </summary>
    public static CdTextInfo? Parse(ReadOnlySpan<byte> data)
    {
        var info = new CdTextInfo();
        // Charset dal pack 0x8F (block info): 0x00 ISO-8859-1, 0x01 ASCII, 0x80 MS-JIS
        Encoding enc = Encoding.Latin1;

        var byType = new Dictionary<byte, List<(int track, byte[] text)>>();
        for (int p = 0; p + 18 <= data.Length; p += 18)
        {
            byte type = data[p];
            if (type < 0x80 || type > 0x8F) continue;
            int block = (data[p + 3] >> 4) & 0x07;
            if (block != 0) continue;
            bool dbcc = (data[p + 3] & 0x80) != 0;
            if (dbcc) continue; // testo a doppio byte: non gestito
            if (type == 0x8F && data[p + 1] == 0)
            {
                byte cs = data[p + 4];
                if (cs == 0x80) return null; // MS-JIS non supportato
            }
            if (type != 0x80 && type != 0x81) continue;
            if (!byType.TryGetValue(type, out var list)) byType[type] = list = new();
            list.Add((data[p + 1] & 0x7F, data.Slice(p + 4, 12).ToArray()));
        }

        foreach (var (type, packs) in byType)
        {
            var target = type == 0x80 ? info.Titles : info.Performers;
            if (packs.Count == 0) continue;
            int track = packs[0].track;
            var cur = new List<byte>();
            string prev = "";
            foreach (var (_, text) in packs)
            {
                foreach (var b in text)
                {
                    if (b == 0)
                    {
                        string s;
                        if (cur.Count == 1 && cur[0] == 0x09) s = prev; // TAB = uguale al precedente
                        else s = enc.GetString(cur.ToArray()).Trim();
                        if (track <= 99 && !target.ContainsKey(track)) target[track] = s;
                        prev = s;
                        cur.Clear();
                        track++;
                    }
                    else cur.Add(b);
                }
            }
        }
        return info.IsEmpty ? null : info;
    }
}
