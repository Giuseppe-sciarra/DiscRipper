using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiscRipper.Core;

public sealed record CdDriveInfo(char Letter, string Vendor, string Model)
{
    public string Key => $"{Vendor} {Model}".Trim();
    public override string ToString() => string.IsNullOrWhiteSpace(Key) ? $"{Letter}:" : $"{Letter}:  {Key}";
}

public interface ISectorSource
{
    void ReadSectors(int lba, int count, Span<byte> dest);
}

/// <summary>Accesso diretto al lettore CD tramite IOCTL (non servono privilegi di amministratore).</summary>
public sealed class CdDrive : IDisposable, ISectorSource
{
    public const int MaxSectorsPerRead = 26; // 26 * 2352 = 61152 byte (< 64 KB)

    const uint GENERIC_READ = 0x80000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    const uint OPEN_EXISTING = 3;

    const uint IOCTL_CDROM_READ_TOC_EX = 0x00024054;
    const uint IOCTL_CDROM_RAW_READ = 0x0002403E;
    const uint IOCTL_CDROM_SET_SPEED = 0x00024060;
    const uint IOCTL_STORAGE_EJECT_MEDIA = 0x002D4808;
    const uint IOCTL_STORAGE_LOAD_MEDIA = 0x002D480C;
    const uint IOCTL_STORAGE_CHECK_VERIFY2 = 0x002D0800;
    const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern unsafe bool DeviceIoControl(SafeFileHandle h, uint code, void* inBuf, int inSize, void* outBuf, int outSize, out int returned, IntPtr ovl);

    readonly SafeFileHandle _h;
    public char Letter { get; }

    public CdDrive(char letter)
    {
        Letter = char.ToUpperInvariant(letter);
        _h = CreateFile($@"\\.\{Letter}:", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (_h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Impossibile aprire il lettore {Letter}:");
    }

    public void Dispose() => _h.Dispose();

    public static List<CdDriveInfo> Enumerate()
    {
        var list = new List<CdDriveInfo>();
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.CDRom) continue;
            char l = d.Name[0];
            string vendor = "", model = "";
            try { using var cd = new CdDrive(l); (vendor, model) = cd.QueryIdentity(); } catch { }
            list.Add(new CdDriveInfo(l, vendor, model));
        }
        return list;
    }

    unsafe bool Ioctl(uint code, void* inBuf, int inSize, void* outBuf, int outSize, out int ret)
        => DeviceIoControl(_h, code, inBuf, inSize, outBuf, outSize, out ret, IntPtr.Zero);

    public unsafe bool IsMediaPresent()
    {
        return Ioctl(IOCTL_STORAGE_CHECK_VERIFY2, null, 0, null, 0, out _);
    }

    public unsafe (string vendor, string model) QueryIdentity()
    {
        int* query = stackalloc int[3]; // PropertyId=StorageDeviceProperty(0), QueryType=Standard(0)
        query[0] = 0; query[1] = 0; query[2] = 0;
        var buf = new byte[1024];
        fixed (byte* b = buf)
        {
            if (!Ioctl(IOCTL_STORAGE_QUERY_PROPERTY, query, 12, b, buf.Length, out int ret) || ret < 24) return ("", "");
            int vOff = BitConverter.ToInt32(buf, 12), pOff = BitConverter.ToInt32(buf, 16);
            return (ReadAnsi(buf, vOff), ReadAnsi(buf, pOff));
        }
    }

    static string ReadAnsi(byte[] b, int off)
    {
        if (off <= 0 || off >= b.Length) return "";
        int end = off; while (end < b.Length && b[end] != 0) end++;
        return Encoding.ASCII.GetString(b, off, end - off).Trim();
    }

    public unsafe Toc ReadToc()
    {
        byte* inBuf = stackalloc byte[4];
        inBuf[0] = 0; // Format = TOC, Msf = 0 (indirizzi LBA)
        inBuf[1] = 1; // SessionTrack: dalla traccia 1
        inBuf[2] = 0; inBuf[3] = 0;
        var buf = new byte[4 + 100 * 8];
        fixed (byte* b = buf)
        {
            if (!Ioctl(IOCTL_CDROM_READ_TOC_EX, inBuf, 4, b, buf.Length, out int ret))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Nessun CD leggibile nel lettore");
            int len = (buf[0] << 8) | buf[1];
            int entries = Math.Min((len - 2) / 8, 100);
            var toc = new Toc { LeadoutLba = -1 };
            for (int i = 0; i < entries; i++)
            {
                int p = 4 + i * 8;
                byte ctrl = (byte)(buf[p + 1] & 0x0F); // Control nei 4 bit bassi (bitfield MSVC)
                int num = buf[p + 2];
                int lba = (buf[p + 4] << 24) | (buf[p + 5] << 16) | (buf[p + 6] << 8) | buf[p + 7];
                if (num == 0xAA) { toc.LeadoutLba = lba; continue; }
                if (num < 1 || num > 99) continue;
                toc.Tracks.Add(new TocTrack
                {
                    Number = num,
                    StartLba = lba,
                    IsAudio = (ctrl & 0x04) == 0,   // bit 2: traccia dati
                    PreEmphasis = (ctrl & 0x01) != 0 // bit 0: pre-enfasi
                });
            }
            if (toc.Tracks.Count == 0 || toc.LeadoutLba <= 0) throw new InvalidOperationException("TOC non valida");
            toc.ComputeEnds();
            return toc;
        }
    }

    public unsafe CdTextInfo? ReadCdText()
    {
        byte* inBuf = stackalloc byte[4];
        inBuf[0] = 5; // CDROM_READ_TOC_EX_FORMAT_CDTEXT
        inBuf[1] = 0; inBuf[2] = 0; inBuf[3] = 0;
        var buf = new byte[4 + 8 * 256 * 18];
        fixed (byte* b = buf)
        {
            if (!Ioctl(IOCTL_CDROM_READ_TOC_EX, inBuf, 4, b, buf.Length, out int ret) || ret < 4 + 18) return null;
            int len = (buf[0] << 8) | buf[1];
            int dataLen = Math.Min(len - 2, ret - 4);
            if (dataLen < 18) return null;
            return CdTextInfo.Parse(buf.AsSpan(4, dataLen));
        }
    }

    /// <summary>Legge settori CDDA grezzi (2352 byte ciascuno). Lancia eccezione in caso di errore.</summary>
    public unsafe void ReadSectors(int lba, int count, Span<byte> dest)
    {
        if (count <= 0) return;
        if (count > MaxSectorsPerRead) throw new ArgumentOutOfRangeException(nameof(count));
        if (dest.Length < count * Toc.BytesPerSector) throw new ArgumentException("buffer troppo piccolo");
        byte* info = stackalloc byte[16];
        *(long*)info = (long)lba * 2048;       // DiskOffset in "settori da 2048"
        *(uint*)(info + 8) = (uint)count;      // SectorCount
        *(int*)(info + 12) = 2;                // TrackMode = CDDA
        fixed (byte* d = dest)
        {
            if (!Ioctl(IOCTL_CDROM_RAW_READ, info, 16, d, count * Toc.BytesPerSector, out int ret))
                throw new IOException($"Errore di lettura al settore {lba}", Marshal.GetLastWin32Error());
            if (ret < count * Toc.BytesPerSector)
                throw new IOException($"Lettura incompleta al settore {lba}");
        }
    }

    /// <summary>Imposta la velocità di lettura (x). 0 = massima.</summary>
    public unsafe bool SetSpeed(int x)
    {
        byte* req = stackalloc byte[12];
        *(int*)req = 0;                                           // CdromSetSpeed
        *(ushort*)(req + 4) = x <= 0 ? (ushort)0xFFFF : (ushort)Math.Min(0xFFFE, x * 176); // KB/s
        *(ushort*)(req + 6) = 0xFFFF;                             // scrittura: max
        *(int*)(req + 8) = 0;                                     // CdromDefaultRotation
        return Ioctl(IOCTL_CDROM_SET_SPEED, req, 12, null, 0, out _);
    }

    public unsafe bool Eject() => Ioctl(IOCTL_STORAGE_EJECT_MEDIA, null, 0, null, 0, out _);
    public unsafe bool Load() => Ioctl(IOCTL_STORAGE_LOAD_MEDIA, null, 0, null, 0, out _);
}
