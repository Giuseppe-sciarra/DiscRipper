using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Drawing.Drawing2D;

namespace DiscRipper.UI;

/// <summary>Altezze standard: tutti i controlli di una riga sono alti uguali e centrati.</summary>
public static class Ui
{
    public const int H = 34;       // campi, combo, pulsanti
    public const int RowH = 42;    // altezza riga dei form
    public const int Radius = 10;     // pulsanti e campi
    public const int CardRadius = 16; // card

    /// <summary>Riga a colonne con altezza fissa: ogni controllo viene centrato verticalmente.</summary>
    public static TableLayoutPanel Row(int height, params (Control c, SizeType type, float size)[] cols)
    {
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = cols.Length, RowCount = 1, Margin = new Padding(0), Padding = new Padding(0),
            Height = height, Tag = "surface"
        };
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        for (int i = 0; i < cols.Length; i++)
        {
            t.ColumnStyles.Add(new ColumnStyle(cols[i].type, cols[i].size));
            var c = cols[i].c;
            if (c.Dock == DockStyle.None)
                c.Anchor = cols[i].type == SizeType.AutoSize ? AnchorStyles.Left : AnchorStyles.Left | AnchorStyles.Right;
            t.Controls.Add(c, i, 0);
        }
        return t;
    }

    public static Label Caption(string text, int rightMargin = 10) => new()
    {
        Text = text, AutoSize = true, Tag = "dim", Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, rightMargin, 0)
    };
}

/// <summary>Pannello "card" con bordo arrotondato.</summary>
public class Card : Panel
{
    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Padding = new Padding(16);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.P.Back);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.Rounded(r, Ui.CardRadius);
        using var b = new SolidBrush(Theme.P.Surface);
        using var pen = new Pen(Theme.P.Border);
        e.Graphics.FillPath(b, path);
        e.Graphics.DrawPath(pen, path);
    }
}

public enum Glyph { None, Disc, Eject, Settings, Theme, Search, Folder, Rip, Stop, FolderOpen }

/// <summary>Pulsante piatto disegnato a mano: altezza fissa, icona vettoriale, testo centrato.</summary>
public class FlatButton : Button, IThemed
{
    [DefaultValue(false)] public bool Accent { get; set; }
    Glyph _icon;
    [DefaultValue(Glyph.None)] public Glyph Icon { get => _icon; set { _icon = value; FitWidth(); Invalidate(); } }
    bool _hover, _down;

    public FlatButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = Theme.Bold;
        AutoSize = false;
        Height = Ui.H;
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
        Margin = new Padding(0);
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); FitWidth(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); FitWidth(); }

    /// <summary>Larghezza calcolata dal testo (se non è ancorato/dockato in larghezza).</summary>
    public void FitWidth()
    {
        if (Dock is DockStyle.Fill or DockStyle.Top or DockStyle.Bottom) return;
        int w = TextRenderer.MeasureText(Text, Font).Width + 28;
        if (Icon != Glyph.None) w += Text.Length > 0 ? 24 : 12;
        Width = Math.Max(w, Text.Length == 0 ? Height : 92);
    }

    public void ApplyTheme() => Invalidate();

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? p.Back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color back, fore, border;
        if (!Enabled) { back = p.Surface2; fore = p.TextDim; border = p.Border; }
        else if (Accent)
        {
            back = _down || _hover ? p.AccentHover : p.Accent; fore = p.AccentText; border = back;
        }
        else
        {
            back = _down ? p.Selection : _hover ? (Theme.IsDark ? Color.FromArgb(52, 55, 62) : Color.FromArgb(226, 229, 235)) : p.Surface2;
            fore = p.Text; border = _hover ? p.Accent : p.Border;
        }
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Theme.Rounded(r, Ui.Radius))
        {
            using var b = new SolidBrush(back); g.FillPath(b, path);
            using var pen = new Pen(border); g.DrawPath(pen, path);
        }
        if (Focused && ShowFocusCues && Enabled)
        {
            using var path = Theme.Rounded(Rectangle.Inflate(r, -2, -2), Ui.Radius - 2);
            using var pen = new Pen(Color.FromArgb(120, Accent ? p.AccentText : p.Accent)) { DashStyle = DashStyle.Dot };
            g.DrawPath(pen, path);
        }

        var textSize = TextRenderer.MeasureText(Text, Font);
        int iconW = Icon == Glyph.None ? 0 : 16;
        int gap = Icon != Glyph.None && Text.Length > 0 ? 8 : 0;
        int total = iconW + gap + (Text.Length > 0 ? textSize.Width : 0);
        int x = (Width - total) / 2;
        if (Icon != Glyph.None)
        {
            DrawGlyph(g, Icon, new Rectangle(x, (Height - 16) / 2, 16, 16), fore);
            x += iconW + gap;
        }
        if (Text.Length > 0)
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x - 2, 0, textSize.Width + 4, Height), fore,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    public static void DrawGlyph(Graphics g, Glyph glyph, Rectangle r, Color c)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(c, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var br = new SolidBrush(c);
        float x = r.X, y = r.Y, w = r.Width, h = r.Height;
        float cx = x + w / 2, cy = y + h / 2;
        switch (glyph)
        {
            case Glyph.Disc:
                g.DrawEllipse(pen, x + 1, y + 1, w - 2, h - 2);
                g.DrawEllipse(pen, cx - 2.5f, cy - 2.5f, 5, 5);
                g.DrawArc(pen, x + 4, y + 4, w - 8, h - 8, 200, 70);
                break;
            case Glyph.Eject:
                g.FillPolygon(br, new[] { new PointF(cx, y + 2), new PointF(x + w - 2, y + 10), new PointF(x + 2, y + 10) });
                g.FillRectangle(br, x + 2, y + 12.5f, w - 4, 2.2f);
                break;
            case Glyph.Settings:
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4;
                    g.DrawLine(pen, cx + (float)Math.Cos(a) * 5.2f, cy + (float)Math.Sin(a) * 5.2f,
                                    cx + (float)Math.Cos(a) * 7.3f, cy + (float)Math.Sin(a) * 7.3f);
                }
                g.DrawEllipse(pen, cx - 5, cy - 5, 10, 10);
                g.DrawEllipse(pen, cx - 1.8f, cy - 1.8f, 3.6f, 3.6f);
                break;
            case Glyph.Theme:
                g.DrawEllipse(pen, x + 1.5f, y + 1.5f, w - 3, h - 3);
                g.FillPie(br, x + 1.5f, y + 1.5f, w - 3, h - 3, 90, 180);
                break;
            case Glyph.Search:
                g.DrawEllipse(pen, x + 1.5f, y + 1.5f, 9.5f, 9.5f);
                g.DrawLine(pen, x + 9.8f, y + 9.8f, x + w - 1.5f, y + h - 1.5f);
                break;
            case Glyph.Folder:
            case Glyph.FolderOpen:
                using (var path = new GraphicsPath())
                {
                    path.AddLines(new[]
                    {
                        new PointF(x + 1, y + 3), new PointF(x + 6, y + 3), new PointF(x + 8, y + 5),
                        new PointF(x + w - 1, y + 5), new PointF(x + w - 1, y + h - 2), new PointF(x + 1, y + h - 2)
                    });
                    path.CloseFigure();
                    g.DrawPath(pen, path);
                }
                if (glyph == Glyph.FolderOpen) g.DrawLine(pen, x + 1, y + 8, x + w - 1, y + 8);
                break;
            case Glyph.Rip:
                g.DrawLine(pen, cx, y + 1, cx, y + 10);
                g.DrawLines(pen, new[] { new PointF(cx - 4, y + 6.5f), new PointF(cx, y + 10.5f), new PointF(cx + 4, y + 6.5f) });
                g.DrawLines(pen, new[] { new PointF(x + 1.5f, y + 10), new PointF(x + 1.5f, y + h - 1.5f), new PointF(x + w - 1.5f, y + h - 1.5f), new PointF(x + w - 1.5f, y + 10) });
                break;
            case Glyph.Stop:
                using (var path = Theme.Rounded(new Rectangle((int)x + 3, (int)y + 3, (int)w - 6, (int)h - 6), 2))
                    g.FillPath(br, path);
                break;
        }
    }
}

/// <summary>Campo di testo alto come i pulsanti, con bordo arrotondato ed evidenziazione al focus.</summary>
[DefaultEvent(nameof(TextChanged))]
public class FieldBox : UserControl, IThemed
{
    public readonly TextBox Box = new() { BorderStyle = BorderStyle.None };
    bool _focus;

    public FieldBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = Ui.H;
        Margin = new Padding(0);
        Padding = new Padding(10, 0, 10, 0);
        Cursor = Cursors.IBeam;
        Box.Font = Theme.Base;
        Controls.Add(Box);
        Box.TextChanged += (_, e) => OnTextChanged(e);
        Box.GotFocus += (_, _) => { _focus = true; Invalidate(); };
        Box.LostFocus += (_, _) => { _focus = false; Invalidate(); };
        Click += (_, _) => Box.Focus();
    }

    [Browsable(true), EditorBrowsable(EditorBrowsableState.Always), DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
    [AllowNull]
    public override string Text { get => Box.Text; set => Box.Text = value ?? ""; }

    public bool ReadOnly { get => Box.ReadOnly; set => Box.ReadOnly = value; }
    public HorizontalAlignment TextAlign { get => Box.TextAlign; set => Box.TextAlign = value; }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        Box.Width = Width - Padding.Horizontal;
        Box.Location = new Point(Padding.Left, (Height - Box.Height) / 2);
    }

    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); ApplyTheme(); }

    public void ApplyTheme()
    {
        var p = Theme.P;
        Box.BackColor = p.Surface2;
        Box.ForeColor = Enabled ? p.Text : p.TextDim;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? p.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.Rounded(r, Ui.Radius);
        using var b = new SolidBrush(p.Surface2);
        g.FillPath(b, path);
        using var pen = new Pen(_focus ? p.Accent : p.Border, _focus ? 1.6f : 1f);
        g.DrawPath(pen, path);
    }
}

/// <summary>ComboBox disegnata a mano, alta come i pulsanti, con bordo arrotondato.</summary>
public class ThemedCombo : ComboBox, IThemed
{
    bool _hover;

    public ThemedCombo()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        Font = Theme.Base;
        ItemHeight = Ui.H - 6;
        Margin = new Padding(0);
        Cursor = Cursors.Hand;
    }

    public void ApplyTheme()
    {
        BackColor = Theme.P.Surface2;
        ForeColor = Theme.P.Text;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    // ritaglia il controllo sulla forma arrotondata: niente spigoli del bordo nativo
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); UpdateRegion(); }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UpdateRegion(); }
    void UpdateRegion()
    {
        if (Width < 10 || Height < 10) return;
        using var path = Theme.Rounded(new Rectangle(0, 0, Width, Height), Ui.Radius);
        var old = Region;
        Region = new Region(path);
        old?.Dispose();
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        var p = Theme.P;
        bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
        bool sel = (e.State & DrawItemState.Selected) != 0 && !edit;
        using (var b = new SolidBrush(sel ? p.Selection : p.Surface2)) e.Graphics.FillRectangle(b, e.Bounds);
        if (e.Index >= 0)
        {
            var txt = GetItemText(Items[e.Index]);
            var r = e.Bounds; r.X += edit ? 8 : 10; r.Width -= edit ? 36 : 12;
            TextRenderer.DrawText(e.Graphics, txt, Font, r, Enabled ? p.Text : p.TextDim,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == 0x000F /* WM_PAINT */ || m.Msg == 0x0085 /* WM_NCPAINT */) PaintFrame();
    }

    /// <summary>Ridisegna bordo arrotondato e freccia sopra la combo nativa.</summary>
    void PaintFrame()
    {
        if (!IsHandleCreated || Width < 10) return;
        var p = Theme.P;
        using var g = Graphics.FromHwnd(Handle);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.Rounded(r, Ui.Radius);

        // angoli fuori dal bordo = colore del contenitore
        using (var outside = new Region(new Rectangle(0, 0, Width, Height)))
        {
            outside.Exclude(path);
            using var bg = new SolidBrush(Parent?.BackColor ?? p.Surface);
            g.FillRegion(bg, outside);
        }
        // zona freccia (copre il pulsante nativo, ritagliata dentro il bordo arrotondato)
        var arrow = new Rectangle(Width - 32, 0, 32, Height);
        g.SetClip(path);
        using (var ab = new SolidBrush(p.Surface2)) g.FillRectangle(ab, arrow);
        g.ResetClip();
        float cx = arrow.X + arrow.Width / 2f, cy = Height / 2f;
        using (var ap = new Pen(Enabled ? p.TextDim : p.Border, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawLines(ap, new[] { new PointF(cx - 4.5f, cy - 2), new PointF(cx, cy + 2.5f), new PointF(cx + 4.5f, cy - 2) });
        // bordo
        bool active = Focused || DroppedDown;
        using var pen = new Pen(active ? p.Accent : _hover && Enabled ? p.TextDim : p.Border, active ? 1.6f : 1f);
        g.DrawPath(pen, path);
    }
}

/// <summary>Riquadro copertina con angoli arrotondati.</summary>
public class CoverBox : Control, IThemed
{
    Image? _image;
    public Image? Image { get => _image; set { _image = value; Invalidate(); } }

    public CoverBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void ApplyTheme() => Invalidate();

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? p.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.Rounded(r, 12);
        if (_image != null)
        {
            // adatta mantenendo le proporzioni (riempie il riquadro)
            float scale = Math.Max((float)Width / _image.Width, (float)Height / _image.Height);
            float w = _image.Width * scale, h = _image.Height * scale;
            using var bmp = new Bitmap(Width, Height);
            using (var bg = Graphics.FromImage(bmp))
            {
                bg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                bg.DrawImage(_image, (Width - w) / 2, (Height - h) / 2, w, h);
            }
            using var tb = new TextureBrush(bmp);
            g.FillPath(tb, path);
            using var pen = new Pen(Color.FromArgb(40, p.Text));
            g.DrawPath(pen, path);
        }
        else
        {
            using var b = new SolidBrush(p.Surface2);
            g.FillPath(b, path);
            using var pen = new Pen(p.Border);
            g.DrawPath(pen, path);
            base.OnPaint(e); // evento Paint per il segnaposto
        }
    }
}

/// <summary>CheckBox disegnata a mano.</summary>
public class ThemedCheckBox : CheckBox, IThemed
{
    public ThemedCheckBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Font = Theme.Base;
        AutoSize = false;
        Height = 26;
        Cursor = Cursors.Hand;
        Padding = new Padding(0);
    }

    [DefaultValue(false)] public bool KeepWidth { get; set; }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); FitWidth(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); FitWidth(); }

    void FitWidth()
    {
        if (KeepWidth) return;
        var sz = TextRenderer.MeasureText(Text, Font);
        Width = sz.Width + 30;
    }

    public void ApplyTheme() { BackColor = Color.Transparent; ForeColor = Theme.P.Text; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? p.Back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int box = 18;
        var r = new Rectangle(1, (Height - box) / 2, box, box);
        using (var path = Theme.Rounded(r, 5))
        {
            if (Checked)
            {
                using var b = new SolidBrush(Enabled ? p.Accent : p.TextDim);
                g.FillPath(b, path);
                using var pen = new Pen(p.AccentText, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLines(pen, new[] { new Point(r.X + 4, r.Y + 9), new Point(r.X + 8, r.Y + 13), new Point(r.X + 14, r.Y + 5) });
            }
            else
            {
                using var b = new SolidBrush(p.Surface2);
                g.FillPath(b, path);
                using var pen = new Pen(p.Border, 1.4f);
                g.DrawPath(pen, path);
            }
        }
        var tr = new Rectangle(box + 9, 0, Width - box - 9, Height);
        TextRenderer.DrawText(g, Text, Font, tr, Enabled ? p.Text : p.TextDim,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Barra di avanzamento piatta.</summary>
public class FlatProgress : Control, IThemed
{
    double _value;
    public double Value { get => _value; set { _value = Math.Clamp(value, 0, 1); Invalidate(); } }

    public FlatProgress()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 8;
    }

    public void ApplyTheme() => Invalidate();

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? p.Back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int y = (Height - 8) / 2;
        var r = new Rectangle(0, y, Width - 1, 7);
        using (var path = Theme.Rounded(r, 4))
        using (var b = new SolidBrush(p.Surface2))
            g.FillPath(b, path);
        int w = (int)((Width - 1) * _value);
        if (w >= 8)
        {
            using var path = Theme.Rounded(new Rectangle(0, y, w, 7), 4);
            using var b = new SolidBrush(p.Accent);
            g.FillPath(b, path);
        }
    }
}
