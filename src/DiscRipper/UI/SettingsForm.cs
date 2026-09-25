using DiscRipper.Core;

namespace DiscRipper.UI;

public sealed class SettingsForm : Form
{
    readonly AppSettings _s;
    readonly CdDriveInfo? _drive;
    readonly Toc? _toc;
    readonly ArDisc? _ar;
    bool _detectedOk;

    readonly TextBox _out = new() { Dock = DockStyle.Fill };
    readonly ThemedCombo _mp3 = new() { Width = 160 };
    readonly ThemedCombo _flac = new() { Width = 160 };
    readonly ThemedCombo _aac = new() { Width = 160 };
    readonly ThemedCombo _ogg = new() { Width = 160 };
    readonly ThemedCombo _opus = new() { Width = 160 };
    readonly ThemedCombo _theme = new() { Width = 160 };
    readonly ThemedCombo _speed = new() { Width = 160 };
    readonly NumericUpDown _retries = new() { Minimum = 4, Maximum = 100, Width = 80 };
    readonly NumericUpDown _offset = new() { Minimum = -3000, Maximum = 3000, Width = 80 };
    readonly FlatButton _detect = new() { Text = "Rileva ora" };
    readonly Label _offsetInfo = new() { AutoSize = true, Tag = "dim", MaximumSize = new Size(520, 0) };
    readonly ThemedCheckBox _paranoia = new() { Text = "Paranoia sempre (doppia lettura anche se AccurateRip coincide)" };
    readonly ThemedCheckBox _auto = new() { Text = "Leggi il CD appena viene inserito" };
    readonly ThemedCheckBox _eject = new() { Text = "Espelli il CD a fine estrazione" };
    readonly ThemedCheckBox _coverJpg = new() { Text = "Salva anche cover.jpg nella cartella dell'album" };
    readonly ThemedCheckBox _log = new() { Text = "Salva il file .log dell'estrazione" };
    readonly TextBox _gnudb = new() { Width = 300 };

    static readonly string[] Mp3Opts = { "VBR V0", "VBR V2", "CBR 320", "CBR 256", "CBR 192" };
    static readonly int[] FlacOpts = { 5, 8 };
    static readonly int[] AacOpts = { 192, 256, 320 };
    static readonly int[] OggOpts = { 4, 5, 6, 7, 8 };
    static readonly int[] OpusOpts = { 96, 128, 160, 192, 256 };
    static readonly int[] SpeedOpts = { 0, 32, 24, 16, 8, 4 };

    public SettingsForm(AppSettings s, CdDriveInfo? drive, Toc? toc, ArDisc? ar)
    {
        _s = s; _drive = drive; _toc = toc; _ar = ar;
        Text = "Impostazioni";
        Font = Theme.Base;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, 700);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(16) };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var card = new Card { Dock = DockStyle.Fill, AutoScroll = true };
        var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Tag = "surface" };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.Controls.Add(t);
        root.Controls.Add(card, 0, 0);

        int row = 0;
        void Section(string title)
        {
            var l = new Label { Text = title, Font = Theme.Big, AutoSize = true, Tag = "accent", Margin = new Padding(0, row == 0 ? 0 : 14, 0, 4) };
            t.Controls.Add(l, 0, row); t.SetColumnSpan(l, 2); row++;
        }
        void Row(string label, Control c)
        {
            var l = new Label { Text = label, AutoSize = true, Tag = "dim", Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 0) };
            c.Margin = new Padding(0, 3, 0, 3);
            t.Controls.Add(l, 0, row); t.Controls.Add(c, 1, row); row++;
        }
        void Full(Control c) { c.Margin = new Padding(0, 3, 0, 3); t.Controls.Add(c, 0, row); t.SetColumnSpan(c, 2); row++; }

        Section("Destinazione");
        var outRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0), Tag = "surface" };
        outRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var browse = new FlatButton { Text = "…", MinimumSize = new Size(40, 28), Height = 28, Margin = new Padding(6, 0, 0, 0) };
        _out.Margin = new Padding(0, 3, 0, 0);
        outRow.Controls.Add(_out, 0, 0); outRow.Controls.Add(browse, 1, 0);
        Row("Cartella", outRow);
        Full(new Label { Text = "Struttura: Artista\\Album (Anno)\\01 - Titolo  — anche percorsi di rete (\\\\server\\share o unità mappata).", AutoSize = true, Tag = "dim", MaximumSize = new Size(640, 0) });

        Section("Qualità");
        Row("MP3", _mp3);
        Row("FLAC compressione", _flac);
        Row("AAC (M4A) kbps", _aac);
        Row("OGG qualità", _ogg);
        Row("Opus kbps", _opus);

        Section("Lettura");
        Full(_paranoia);
        Row("Riletture max", _retries);
        Row("Velocità", _speed);
        var offFlow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Tag = "surface" };
        _detect.Margin = new Padding(8, 0, 0, 0);
        _detect.Height = 28; _detect.MinimumSize = new Size(100, 28);
        offFlow.Controls.Add(_offset); offFlow.Controls.Add(_detect);
        Row("Offset lettore", offFlow);
        Full(_offsetInfo);

        Section("Metadati");
        Row("Email GnuDB", _gnudb);
        Full(new Label
        {
            Text = "MusicBrainz e AccurateRip non richiedono account. GnuDB (ex freedb) viene usato come riserva solo se inserisci un'email: la chiede nel saluto del protocollo.",
            AutoSize = true, Tag = "dim", MaximumSize = new Size(640, 0)
        });

        Section("Generale");
        Row("Tema", _theme);
        Full(_auto);
        Full(_eject);
        Full(_coverJpg);
        Full(_log);

        foreach (var cb in new[] { _paranoia, _auto, _eject, _coverJpg, _log }) { cb.AutoSize = false; cb.Width = 600; }

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        var ok = new FlatButton { Text = "Salva", Accent = true, DialogResult = DialogResult.OK };
        var cancel = new FlatButton { Text = "Annulla", DialogResult = DialogResult.Cancel, Margin = new Padding(0, 0, 8, 0) };
        buttons.Controls.Add(ok); buttons.Controls.Add(cancel);
        root.Controls.Add(buttons, 0, 1);
        AcceptButton = ok; CancelButton = cancel;

        // valori
        _out.Text = s.OutputRoot;
        Fill(_mp3, Mp3Opts, s.Mp3Quality);
        Fill(_flac, FlacOpts.Select(x => x.ToString()).ToArray(), s.FlacLevel.ToString());
        Fill(_aac, AacOpts.Select(x => x.ToString()).ToArray(), s.AacBitrate.ToString());
        Fill(_ogg, OggOpts.Select(x => "q" + x).ToArray(), "q" + s.OggQuality);
        Fill(_opus, OpusOpts.Select(x => x.ToString()).ToArray(), s.OpusBitrate.ToString());
        Fill(_theme, new[] { "Sistema", "Chiaro", "Scuro" }, s.Theme.ToString());
        Fill(_speed, SpeedOpts.Select(x => x == 0 ? "Massima" : x + "x").ToArray(), s.ReadSpeed == 0 ? "Massima" : s.ReadSpeed + "x");
        _paranoia.Checked = s.ParanoiaAlways;
        _retries.Value = Math.Clamp(s.MaxRetries, 4, 100);
        _auto.Checked = s.AutoReadOnInsert;
        _eject.Checked = s.EjectWhenDone;
        _coverJpg.Checked = s.SaveCoverJpg;
        _log.Checked = s.WriteLog;
        _gnudb.Text = s.GnuDbEmail;

        if (drive != null)
        {
            var (o, known) = s.GetOffset(drive.Key);
            _offset.Value = o;
            _offsetInfo.Text = known
                ? $"{drive.Key}: offset rilevato e salvato."
                : $"{drive.Key}: offset non ancora rilevato — verrà cercato da solo alla prima estrazione di un CD presente in AccurateRip.";
        }
        else { _offset.Enabled = false; _offsetInfo.Text = "Nessun lettore selezionato."; }
        _detect.Enabled = drive != null && toc != null && ar != null;
        if (drive != null && (toc == null || ar == null))
            _offsetInfo.Text += " Per rilevarlo ora serve un CD presente in AccurateRip nel lettore.";

        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = _out.Text, UseDescriptionForTitle = true, Description = "Cartella di destinazione" };
            if (dlg.ShowDialog(this) == DialogResult.OK) _out.Text = dlg.SelectedPath;
        };
        _detect.Click += async (_, _) => await Detect();
        ok.Click += (_, _) => SaveValues();

        Theme.Apply(this);
    }

    static void Fill(ComboBox c, string[] items, string sel)
    {
        c.Items.AddRange(items);
        int i = Array.IndexOf(items, sel);
        c.SelectedIndex = i >= 0 ? i : 0;
    }

    async Task Detect()
    {
        if (_drive == null || _toc == null || _ar == null) return;
        _detect.Enabled = false;
        _offsetInfo.Text = "Rilevamento in corso (lettura di una traccia)…";
        try
        {
            var r = await Task.Run(() =>
            {
                using var cd = new CdDrive(_drive.Letter);
                return TrackReader.DetectOffset(cd, _toc, _ar, _ => { }, CancellationToken.None);
            });
            if (r != null)
            {
                _offset.Value = r.Value.offset;
                _detectedOk = true;
                _offsetInfo.Text = $"Offset trovato: {r.Value.offset:+0;-0;0} (traccia {r.Value.trackNumber}, confidenza {r.Value.confidence}). Premi Salva.";
            }
            else _offsetInfo.Text = "Nessun offset coincide con AccurateRip su questo disco. Prova con un altro CD molto diffuso.";
        }
        catch (Exception ex) { _offsetInfo.Text = "Errore: " + ex.Message; }
        finally { _detect.Enabled = true; }
    }

    void SaveValues()
    {
        _s.OutputRoot = _out.Text.Trim();
        _s.Mp3Quality = Mp3Opts[Math.Max(0, _mp3.SelectedIndex)];
        _s.FlacLevel = FlacOpts[Math.Max(0, _flac.SelectedIndex)];
        _s.AacBitrate = AacOpts[Math.Max(0, _aac.SelectedIndex)];
        _s.OggQuality = OggOpts[Math.Max(0, _ogg.SelectedIndex)];
        _s.OpusBitrate = OpusOpts[Math.Max(0, _opus.SelectedIndex)];
        _s.Theme = (ThemeMode)Math.Max(0, _theme.SelectedIndex);
        _s.ReadSpeed = SpeedOpts[Math.Max(0, _speed.SelectedIndex)];
        _s.ParanoiaAlways = _paranoia.Checked;
        _s.MaxRetries = (int)_retries.Value;
        _s.AutoReadOnInsert = _auto.Checked;
        _s.EjectWhenDone = _eject.Checked;
        _s.SaveCoverJpg = _coverJpg.Checked;
        _s.WriteLog = _log.Checked;
        _s.GnuDbEmail = _gnudb.Text.Trim();
        if (_drive != null && _offset.Enabled)
        {
            var (o, known) = _s.GetOffset(_drive.Key);
            if (known || _detectedOk || (int)_offset.Value != o) _s.DriveOffsets[_drive.Key] = (int)_offset.Value;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.TitleBar(this);
    }
}
