using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using DiscRipper.Core;
using Microsoft.Win32;

namespace DiscRipper.UI;

public sealed class Palette
{
    public Color Back, Surface, Surface2, Border, Text, TextDim, Accent, AccentText, AccentHover, Selection;
    public Color Success, Warning, Error, Info;

    public static readonly Palette Light = new()
    {
        Back = Color.FromArgb(243, 244, 247),
        Surface = Color.White,
        Surface2 = Color.FromArgb(236, 238, 242),
        Border = Color.FromArgb(214, 218, 225),
        Text = Color.FromArgb(28, 31, 36),
        TextDim = Color.FromArgb(104, 112, 125),
        Accent = Color.FromArgb(79, 70, 229),
        AccentHover = Color.FromArgb(67, 56, 202),
        AccentText = Color.White,
        Selection = Color.FromArgb(224, 226, 252),
        Success = Color.FromArgb(22, 139, 70),
        Warning = Color.FromArgb(190, 110, 0),
        Error = Color.FromArgb(205, 38, 38),
        Info = Color.FromArgb(37, 99, 235),
    };

    public static readonly Palette Dark = new()
    {
        Back = Color.FromArgb(22, 23, 27),
        Surface = Color.FromArgb(31, 33, 38),
        Surface2 = Color.FromArgb(42, 45, 51),
        Border = Color.FromArgb(55, 58, 66),
        Text = Color.FromArgb(232, 234, 237),
        TextDim = Color.FromArgb(154, 160, 170),
        Accent = Color.FromArgb(124, 128, 255),
        AccentHover = Color.FromArgb(145, 150, 255),
        AccentText = Color.FromArgb(18, 18, 30),
        Selection = Color.FromArgb(52, 54, 90),
        Success = Color.FromArgb(74, 222, 128),
        Warning = Color.FromArgb(251, 191, 36),
        Error = Color.FromArgb(248, 113, 113),
        Info = Color.FromArgb(125, 170, 255),
    };
}

public static class Theme
{
    public static Palette P { get; private set; } = Palette.Light;
    public static bool IsDark { get; private set; }
    public static ThemeMode Mode { get; private set; } = ThemeMode.Sistema;
    public static event Action? Changed;

    public static readonly Font Base = new("Segoe UI", 9.75f);
    public static readonly Font Bold = new("Segoe UI Semibold", 9.75f);
    public static readonly Font Title = new("Segoe UI Semibold", 15f);
    public static readonly Font Big = new("Segoe UI Semibold", 11f);
    public static readonly Font Small = new("Segoe UI", 8.75f);
    public static readonly Font Mono = new("Consolas", 9f);

    static Theme()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (Mode == ThemeMode.Sistema && e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
                Set(ThemeMode.Sistema);
        };
    }

    public static bool SystemIsDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public static void Set(ThemeMode mode)
    {
        Mode = mode;
        bool dark = mode == ThemeMode.Scuro || (mode == ThemeMode.Sistema && SystemIsDark());
        IsDark = dark;
        P = dark ? Palette.Dark : Palette.Light;
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ Win32

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);

    public static void TitleBar(Form f)
    {
        if (!f.IsHandleCreated) return;
        int v = IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(f.Handle, 20, ref v, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref v, 4);
        // forza il ridisegno della barra del titolo
        if (f.Visible) { f.Width += 1; f.Width -= 1; }
    }

    static void ScrollTheme(Control c)
    {
        if (!c.IsHandleCreated) return;
        try { SetWindowTheme(c.Handle, IsDark ? "DarkMode_Explorer" : "Explorer", null); } catch { }
    }

    // ------------------------------------------------------------------ applicazione ricorsiva

    public static void Apply(Control root)
    {
        if (root is Form f) { f.BackColor = P.Back; f.ForeColor = P.Text; TitleBar(f); }
        ApplyTo(root);
        if (root is FieldBox) { root.Invalidate(); return; } // il TextBox interno lo gestisce FieldBox
        foreach (Control c in root.Controls) Apply(c);
        root.Invalidate(true);
    }

    static void ApplyTo(Control c)
    {
        switch (c)
        {
            case IThemed t: t.ApplyTheme(); break;
            case DataGridView g: StyleGrid(g); break;
            case RichTextBox r:
                r.BackColor = P.Surface; r.ForeColor = P.Text; r.BorderStyle = BorderStyle.None; ScrollTheme(r); break;
            case TextBox tb:
                tb.BackColor = P.Surface2; tb.ForeColor = P.Text; tb.BorderStyle = BorderStyle.FixedSingle; break;
            case NumericUpDown nu:
                nu.BackColor = P.Surface2; nu.ForeColor = P.Text; nu.BorderStyle = BorderStyle.FixedSingle; break;
            case LinkLabel ll:
                ll.LinkColor = P.Accent; ll.ActiveLinkColor = P.AccentHover; ll.VisitedLinkColor = P.Accent; ll.BackColor = Color.Transparent; break;
            case Label l:
                if (l.Tag as string == "dim") l.ForeColor = P.TextDim;
                else if (l.Tag as string == "accent") l.ForeColor = P.Accent;
                else l.ForeColor = P.Text;
                l.BackColor = Color.Transparent; break;
            case Card card: card.BackColor = P.Surface; break;
            case TableLayoutPanel or FlowLayoutPanel or Panel:
                if (c.Tag as string == "surface") c.BackColor = P.Surface;
                else if (c.Parent is Card || c.Parent?.Tag as string == "surface" || IsInsideSurface(c)) c.BackColor = P.Surface;
                else c.BackColor = P.Back;
                c.ForeColor = P.Text;
                if (c is ScrollableControl { AutoScroll: true }) ScrollTheme(c);
                break;
        }
    }

    static bool IsInsideSurface(Control c)
    {
        for (var p = c.Parent; p != null; p = p.Parent)
            if (p is Card || p.Tag as string == "surface") return true;
        return false;
    }

    public static void StyleGrid(DataGridView g)
    {
        g.EnableHeadersVisualStyles = false;
        g.BackgroundColor = P.Surface;
        g.GridColor = P.Border;
        g.BorderStyle = BorderStyle.None;
        g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        g.RowHeadersVisible = false;
        g.DefaultCellStyle.BackColor = P.Surface;
        g.DefaultCellStyle.ForeColor = P.Text;
        g.DefaultCellStyle.SelectionBackColor = P.Selection;
        g.DefaultCellStyle.SelectionForeColor = P.Text;
        g.DefaultCellStyle.Font = Base;
        g.DefaultCellStyle.Padding = Ui.P(4, 0, 4, 0);
        g.AlternatingRowsDefaultCellStyle.BackColor = IsDark ? Color.FromArgb(35, 37, 43) : Color.FromArgb(249, 250, 252);
        g.ColumnHeadersDefaultCellStyle.BackColor = P.Surface;
        g.ColumnHeadersDefaultCellStyle.ForeColor = P.TextDim;
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = P.Surface;
        g.ColumnHeadersDefaultCellStyle.Font = Bold;
        g.ColumnHeadersDefaultCellStyle.Padding = Ui.P(4, 0, 4, 0);
        ScrollTheme(g);
        foreach (Control sc in g.Controls) ScrollTheme(sc);
    }

    public static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        if (radius <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

public interface IThemed { void ApplyTheme(); }
