using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace DiscRipper.UI;

/// <summary>Pannello "card" con bordo arrotondato.</summary>
public class Card : Panel
{
    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Padding = new Padding(14);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.P.Back);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.Rounded(r, 10);
        using var b = new SolidBrush(Theme.P.Surface);
        using var pen = new Pen(Theme.P.Border);
        e.Graphics.FillPath(b, path);
        e.Graphics.DrawPath(pen, path);
    }
}

public class FlatButton : Button, IThemed
{
    [DefaultValue(false)] public bool Accent { get; set; }

    public FlatButton()
    {
        FlatStyle = FlatStyle.Flat;
        Font = Theme.Bold;
        Height = 34;
        Padding = new Padding(10, 0, 10, 0);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowOnly;
        MinimumSize = new Size(90, 34);
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
    }

    public void ApplyTheme()
    {
        var p = Theme.P;
        if (Accent)
        {
            BackColor = p.Accent; ForeColor = p.AccentText;
            FlatAppearance.BorderColor = p.Accent;
            FlatAppearance.MouseOverBackColor = p.AccentHover;
            FlatAppearance.MouseDownBackColor = p.AccentHover;
        }
        else
        {
            BackColor = p.Surface2; ForeColor = p.Text;
            FlatAppearance.BorderColor = p.Border;
            FlatAppearance.MouseOverBackColor = Theme.IsDark ? Color.FromArgb(52, 55, 62) : Color.FromArgb(226, 229, 235);
            FlatAppearance.MouseDownBackColor = p.Selection;
        }
        FlatAppearance.BorderSize = 1;
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Enabled) { base.OnPaint(e); return; }
        // stato disabilitato leggibile in entrambi i temi
        var p = Theme.P;
        e.Graphics.Clear(p.Surface2);
        using var pen = new Pen(p.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, p.TextDim,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

/// <summary>ComboBox disegnata a mano, leggibile anche in tema scuro.</summary>
public class ThemedCombo : ComboBox, IThemed
{
    public ThemedCombo()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        ItemHeight = 22;
        Font = Theme.Base;
    }

    public void ApplyTheme()
    {
        BackColor = Theme.P.Surface2;
        ForeColor = Theme.P.Text;
        Invalidate();
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        var p = Theme.P;
        bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
        using (var b = new SolidBrush(sel ? p.Selection : p.Surface2)) e.Graphics.FillRectangle(b, e.Bounds);
        if (e.Index >= 0)
        {
            var txt = GetItemText(Items[e.Index]);
            var r = e.Bounds; r.X += 4; r.Width -= 4;
            TextRenderer.DrawText(e.Graphics, txt, Font, r, Enabled ? p.Text : p.TextDim,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
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

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); FitWidth(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); FitWidth(); }

    void FitWidth()
    {
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
        int box = 16;
        var r = new Rectangle(1, (Height - box) / 2, box, box);
        using (var path = Theme.Rounded(r, 4))
        {
            if (Checked)
            {
                using var b = new SolidBrush(Enabled ? p.Accent : p.TextDim);
                g.FillPath(b, path);
                using var pen = new Pen(p.AccentText, 2f);
                g.DrawLines(pen, new[] { new Point(r.X + 4, r.Y + 8), new Point(r.X + 7, r.Y + 11), new Point(r.X + 12, r.Y + 5) });
            }
            else
            {
                using var b = new SolidBrush(p.Surface2);
                g.FillPath(b, path);
                using var pen = new Pen(p.Border, 1.4f);
                g.DrawPath(pen, path);
            }
        }
        var tr = new Rectangle(box + 8, 0, Width - box - 8, Height);
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
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Theme.Rounded(r, Height / 2 - 1))
        using (var b = new SolidBrush(p.Surface2))
            g.FillPath(b, path);
        int w = (int)((Width - 1) * _value);
        if (w >= Height)
        {
            using var path = Theme.Rounded(new Rectangle(0, 0, w, Height - 1), Height / 2 - 1);
            using var b = new SolidBrush(p.Accent);
            g.FillPath(b, path);
        }
    }
}
