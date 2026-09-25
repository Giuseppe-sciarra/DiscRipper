using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscRipper.Core;

public enum ThemeMode { Sistema, Chiaro, Scuro }

[Flags]
public enum OutputFormat
{
    None = 0,
    Mp3 = 1,
    Flac = 2,
    Wav = 4,
    M4a = 8,
    Ogg = 16,
    Opus = 32
}

public sealed class AppSettings
{
    public string OutputRoot { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
    public OutputFormat Formats { get; set; } = OutputFormat.Mp3;

    public string Mp3Quality { get; set; } = "VBR V0";      // VBR V0, VBR V2, CBR 320, CBR 256, CBR 192
    public int FlacLevel { get; set; } = 8;
    public int AacBitrate { get; set; } = 256;
    public int OggQuality { get; set; } = 6;
    public int OpusBitrate { get; set; } = 160;

    public ThemeMode Theme { get; set; } = ThemeMode.Sistema;

    /// <summary>Rilettura sicura di ogni traccia anche quando AccurateRip coincide.</summary>
    public bool ParanoiaAlways { get; set; }
    public int MaxRetries { get; set; } = 20;
    public int ReadSpeed { get; set; } // 0 = max

    public string GnuDbEmail { get; set; } = "";
    public bool AutoReadOnInsert { get; set; } = true;
    public bool EjectWhenDone { get; set; } = true;
    public bool SaveCoverJpg { get; set; } = true;
    public bool WriteLog { get; set; } = true;
    public string LastDrive { get; set; } = "";

    /// <summary>Offset di lettura per modello di lettore (chiave: "Vendor Model").</summary>
    public Dictionary<string, int> DriveOffsets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiscRipper", "settings.json");

    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Opts);
                if (s != null)
                {
                    s.DriveOffsets = new Dictionary<string, int>(s.DriveOffsets ?? new(), StringComparer.OrdinalIgnoreCase);
                    return s;
                }
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
        }
        catch { }
    }

    /// <summary>Offset noti di fabbrica, usati se l'offset non è ancora stato rilevato.</summary>
    static readonly (string match, int offset)[] KnownOffsets =
    {
        ("TSSTcorp BDDVDW SE-506", 6),
        ("TSSTcorp CDDVDW SE-208", 6),
        ("TSSTcorp CDDVDW SE-218", 6),
        ("TSSTcorp CDDVDW SE-S084", 6),
        ("TSSTcorp CDDVDW SH-224", 6),
        ("ASUS SDRW-08D2S-U", 6),
        ("ASUS SDRW-08U7M-U", 6),
        ("HL-DT-ST DVDRAM GP65NB60", 6),
        ("HL-DT-ST DVDRAM GP57EB40", 6),
        ("HL-DT-ST DVDRAM GP60NB50", 6),
        ("HL-DT-ST DVDRAM GH24NSD1", 6),
        ("HL-DT-ST BD-RE  WH16NS60", 6),
        ("PIONEER DVD-RW  DVR-221L", 667),
        ("PIONEER BD-RW   BDR-XD07", 667),
        ("PIONEER BD-RW   BDR-XD05", 667),
        ("Optiarc DVD RW AD-7290H", 48),
        ("LITE-ON DVDRW SHW-160P6S", 6),
    };

    /// <summary>Restituisce (offset, rilevato?) per il lettore.</summary>
    public (int offset, bool known) GetOffset(string driveKey)
    {
        if (DriveOffsets.TryGetValue(driveKey, out var o)) return (o, true);
        string norm = Normalize(driveKey);
        foreach (var (m, off) in KnownOffsets)
            if (norm.StartsWith(Normalize(m), StringComparison.OrdinalIgnoreCase)) return (off, false);
        return (0, false);
    }

    static string Normalize(string s) => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
