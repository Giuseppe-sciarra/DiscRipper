using DiscRipper.Core;

namespace DiscRipper.UI;

public sealed class SettingsForm : Form
{
    readonly AppSettings _s;
    readonly CdDriveInfo? _drive;
    readonly Toc? _toc;
    readonly ArDisc? _ar;
    bool _detectedOk;

    readonly FieldBox _out = new();
    readonly ThemedCombo _mp3 = new();
    readonly ThemedCombo _flac = new();
    readonly ThemedCombo _aac = new();
    readonly ThemedCombo _ogg = new();
    readonly ThemedCombo _opus = new();
    readonly ThemedCombo _theme = new();
    readonly ThemedCombo _speed = new();
    readonly ThemedCombo _retries = new();
    readonly FieldBox _offset = new() { Width = Ui.S(90), TextAlign = HorizontalAlignment.Center };
    readonly FlatButton _detect = new() { Text = "Rileva ora", Icon = Glyph.Search };
    readonly Label _offsetInfo = new() { AutoSize = true, Tag = "dim", Font = Theme.Small };
    readonly ThemedCheckBox _paranoia = new() { Text = "Paranoia sempre (doppia lettura di ogni traccia)", KeepWidth = true };
    readonly ThemedCheckBox _auto = new() { Text = "Leggi il CD appena viene inserito", KeepWidth = true };
    readonly ThemedCheckBox _eject = new() { Text = "Espelli il CD a fine estrazione", KeepWidth = true };
    readonly ThemedCheckBox _coverJpg = new() { Text = "Salva anche cover.jpg nella cartella dell'album", KeepWidth = true };
    readonly ThemedCheckBox _log = new() { Text = "Salva il file .log dell'estrazione (checksum di ogni traccia)", KeepWidth = true };
    readonly FieldBox _gnudb = new();

    // valore salvato  →  testo mostrato
    static readonly (string v, string label)[] Mp3Opts =
    {
        ("VBR V0", "VBR V0  ·  ~245 kbps  ·  qualità massima (consigliato)"),
        ("VBR V2", "VBR V2  ·  ~190 kbps  ·  ottima, file più piccoli"),
        ("CBR 320", "CBR 320 kbps  ·  bitrate fisso massimo"),
        ("CBR 256", "CBR 256 kbps  ·  bitrate fisso"),
        ("CBR 192", "CBR 192 kbps  ·  bitrate fisso, file piccoli"),
    };
    static readonly (int v, string label)[] FlacOpts =
    {
        (8, "Livello 8  ·  file più piccoli (consigliato)"),
        (5, "Livello 5  ·  standard, codifica più veloce"),
    };
    static readonly (int v, string label)[] AacOpts =
    {
        (320, "320 kbps  ·  massima"),
        (256, "256 kbps  ·  ottima (consigliato)"),
        (192, "192 kbps  ·  buona, file più piccoli"),
    };
    static readonly (int v, string label)[] OggOpts =
    {
        (8, "q8  ·  ~256 kbps"),
        (7, "q7  ·  ~224 kbps"),
        (6, "q6  ·  ~192 kbps (consigliato)"),
        (5, "q5  ·  ~160 kbps"),
        (4, "q4  ·  ~128 kbps"),
    };
    static readonly (int v, string label)[] OpusOpts =
    {
        (256, "256 kbps  ·  massima"),
        (192, "192 kbps  ·  ottima"),
        (160, "160 kbps  ·  trasparente (consigliato)"),
        (128, "128 kbps  ·  molto buona"),
        (96, "96 kbps  ·  buona, file piccoli"),
    };
    static readonly (int v, string label)[] SpeedOpts =
    {
        (0, "Massima"), (32, "32x"), (24, "24x"), (16, "16x"), (8, "8x  ·  CD rovinati"), (4, "4x  ·  CD molto rovinati"),
    };
    static readonly (int v, string label)[] RetryOpts =
    {
        (10, "10  ·  veloce"), (20, "20  ·  normale (consigliato)"), (40, "40  ·  ostinato"), (80, "80  ·  molto ostinato"),
    };
    static readonly (ThemeMode v, string label)[] ThemeOpts =
    {
        (ThemeMode.Sistema, "Come il sistema"), (ThemeMode.Chiaro, "Chiaro"), (ThemeMode.Scuro, "Scuro"),
    };

    const int LabelW = 170;

    public SettingsForm(AppSettings s, CdDriveInfo? drive, Toc? toc, ArDisc? ar)
    {
        _s = s; _drive = drive; _toc = toc; _ar = ar;
        Text = "Impostazioni";
        Font = Theme.Base;
        AutoScaleMode = AutoScaleMode.None; // misure già scalate a mano (Ui.S)
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        ClientSize = new Size(Math.Min(Ui.S(760), wa.Width - 40), Math.Min(Ui.S(820), wa.Height - 80));

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = Ui.P(18) };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.S(58)));
        Controls.Add(root);

        // la card resta ferma, scorre solo il pannello interno (niente bordi "fantasma" durante lo scroll)
        var card = new Card { Dock = DockStyle.Fill, Padding = Ui.P(6, 12, 4, 12), Margin = new Padding(0) };
        var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Tag = "surface", Margin = new Padding(0), Padding = Ui.P(16, 4, 16, 4) };
        var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Tag = "surface", Margin = new Padding(0) };
        scroller.Controls.Add(t);
        card.Controls.Add(scroller);
        root.Controls.Add(card, 0, 0);

        void Add(Control c, int h)
        {
            c.Dock = DockStyle.Fill;
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.S(h)));
            t.Controls.Add(c, 0, t.RowCount++);
        }
        void Section(string title, bool first = false)
        {
            var l = new Label { Text = title, Font = Theme.Big, AutoSize = true, Tag = "accent", Margin = Ui.P(0) };
            var holder = new Panel { Tag = "surface", Margin = Ui.P(0) };
            l.Location = new Point(0, first ? 0 : Ui.S(18));
            holder.Controls.Add(l);
            Add(holder, first ? 32 : 46);
        }
        void Field(string label, Control c, Control? extra = null)
        {
            var cols = new List<(Control, SizeType, float)> { (Ui.Caption(label), SizeType.Absolute, LabelW), (c, SizeType.Percent, 100) };
            if (extra != null) { cols.Add((Spacer(8), SizeType.AutoSize, 0)); cols.Add((extra, SizeType.AutoSize, 0)); }
            Add(Ui.Row(Ui.RowH, cols.ToArray()), Ui.RowH);
        }
        const int NoteW = 480; // larghezza usata per calcolare l'altezza (il testo va a capo da solo)
        Label Note(string text)
        {
            var l = new Label { Text = text, AutoSize = false, Dock = DockStyle.Fill, Tag = "dim", Font = Theme.Small, Margin = Ui.P(0, 0, 0, 0) };
            int px = TextRenderer.MeasureText(text, Theme.Small, new Size(Ui.S(NoteW), 0), TextFormatFlags.WordBreak).Height;
            int h = (int)Math.Ceiling(px / Ui.Scale) + 8; // misura a 100%, Ui.Row la riscala
            var row = Ui.Row(h, (Spacer(LabelW), SizeType.Absolute, LabelW), (l, SizeType.Percent, 100));
            Add(row, h);
            return l;
        }
        void Check(ThemedCheckBox cb)
        {
            cb.Width = Ui.S(500);
            Add(Ui.Row(34, (Spacer(LabelW), SizeType.Absolute, LabelW), (cb, SizeType.Percent, 100)), 34);
        }

        var browse = new FlatButton { Text = "Sfoglia", Icon = Glyph.Folder };

        Section("Destinazione", true);
        Field("Cartella", _out, browse);
        Note("Struttura: Artista\\Album (Anno)\\01 - Titolo. Va bene anche un percorso di rete (\\\\server\\share o un'unità mappata).");

        Section("Qualità dei formati");
        Field("MP3", _mp3);
        Note("VBR = bitrate variabile: più kbps nei passaggi complessi, meno nei silenzi. V0 suona come il CD e pesa meno di un 320 fisso. Il CBR serve solo per vecchi lettori che non digeriscono il VBR.");
        Field("FLAC", _flac);
        Note("FLAC è senza perdita: la qualità è identica al CD a qualsiasi livello, cambia solo quanto pesa il file.");
        Field("AAC (M4A)", _aac);
        Field("OGG Vorbis", _ogg);
        Field("Opus", _opus);

        Section("Lettura del CD");
        Check(_paranoia);
        Note("Normalmente rilegge solo se AccurateRip non coincide o il disco non è nel database. Attivala per i CD graffiati.");
        Field("Riletture massime", _retries);
        Note("Quante volte rileggere un punto del disco che dà risultati diversi prima di arrendersi.");
        Field("Velocità di lettura", _speed);
        _offset.Anchor = AnchorStyles.Left;
        var offRow = Ui.Row(Ui.RowH, (_offset, SizeType.AutoSize, 0), (Spacer(8), SizeType.AutoSize, 0), (_detect, SizeType.AutoSize, 0), (new Panel { Margin = Ui.P(0) }, SizeType.Percent, 100));
        Field("Offset del lettore", offRow);
        _offsetInfo.AutoSize = false; _offsetInfo.Dock = DockStyle.Fill; _offsetInfo.Margin = Ui.P(0);
        Add(Ui.Row(44, (Spacer(LabelW), SizeType.Absolute, LabelW), (_offsetInfo, SizeType.Percent, 100)), 44);

        Section("Riconoscimento del disco");
        Field("Email per GnuDB", _gnudb);
        Note("Il disco viene cercato sempre, in contemporanea, su MusicBrainz, Discogs e freedb (tramite CUETools DB) più AccurateRip: tutto gratis e senza account. GnuDB è un'aggiunta facoltativa: si attiva solo se inserisci un'email, perché la chiede nel saluto del protocollo.");

        Section("Generale");
        Field("Tema", _theme);
        Check(_auto);
        Check(_eject);
        Check(_coverJpg);
        Check(_log);
        Add(new Panel { Tag = "surface" }, 8);

        var ok = new FlatButton { Text = "Salva", Accent = true, DialogResult = DialogResult.OK, Width = Ui.S(130) };
        var cancel = new FlatButton { Text = "Annulla", DialogResult = DialogResult.Cancel, Width = Ui.S(130) };
        var buttons = Ui.Row(58, (new Panel { Margin = Ui.P(0) }, SizeType.Percent, 100), (cancel, SizeType.AutoSize, 0), (Spacer(10), SizeType.AutoSize, 0), (ok, SizeType.AutoSize, 0));
        buttons.Tag = null;
        root.Controls.Add(buttons, 0, 1);
        AcceptButton = ok; CancelButton = cancel;

        // ---- valori
        _out.Text = s.OutputRoot;
        Fill(_mp3, Mp3Opts.Select(x => x.label), Array.FindIndex(Mp3Opts, x => x.v == s.Mp3Quality));
        Fill(_flac, FlacOpts.Select(x => x.label), Array.FindIndex(FlacOpts, x => x.v == s.FlacLevel));
        Fill(_aac, AacOpts.Select(x => x.label), Array.FindIndex(AacOpts, x => x.v == s.AacBitrate));
        Fill(_ogg, OggOpts.Select(x => x.label), Array.FindIndex(OggOpts, x => x.v == s.OggQuality));
        Fill(_opus, OpusOpts.Select(x => x.label), Array.FindIndex(OpusOpts, x => x.v == s.OpusBitrate));
        Fill(_speed, SpeedOpts.Select(x => x.label), Array.FindIndex(SpeedOpts, x => x.v == s.ReadSpeed));
        Fill(_retries, RetryOpts.Select(x => x.label), Nearest(RetryOpts.Select(x => x.v).ToArray(), s.MaxRetries));
        Fill(_theme, ThemeOpts.Select(x => x.label), Array.FindIndex(ThemeOpts, x => x.v == s.Theme));
        _paranoia.Checked = s.ParanoiaAlways;
        _auto.Checked = s.AutoReadOnInsert;
        _eject.Checked = s.EjectWhenDone;
        _coverJpg.Checked = s.SaveCoverJpg;
        _log.Checked = s.WriteLog;
        _gnudb.Text = s.GnuDbEmail;

        if (drive != null)
        {
            var (o, known) = s.GetOffset(drive.Key);
            _offset.Text = o.ToString("+0;-0;0");
            _offsetInfo.Text = known
                ? $"{drive.Key}: offset rilevato e salvato."
                : $"{drive.Key}: non ancora rilevato — lo cerca da solo alla prima estrazione di un CD presente in AccurateRip.";
        }
        else { _offset.Enabled = false; _offsetInfo.Text = "Nessun lettore selezionato."; }
        _detect.Enabled = drive != null && toc != null && ar != null;
        if (drive != null && (toc == null || ar == null))
            _offsetInfo.Text += " Per rilevarlo adesso inserisci un CD presente in AccurateRip.";

        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = _out.Text, UseDescriptionForTitle = true, Description = "Cartella di destinazione" };
            if (dlg.ShowDialog(this) == DialogResult.OK) _out.Text = dlg.SelectedPath;
        };
        _detect.Click += async (_, _) => await Detect();
        ok.Click += (_, _) => SaveValues();

        Theme.Apply(this);
    }

    static Control Spacer(int w) => new Panel { Width = Ui.S(w), Height = 1, Margin = new Padding(0) };

    static int Nearest(int[] values, int v)
    {
        int best = 0;
        for (int i = 1; i < values.Length; i++) if (Math.Abs(values[i] - v) < Math.Abs(values[best] - v)) best = i;
        return best;
    }

    static void Fill(ComboBox c, IEnumerable<string> items, int sel)
    {
        c.Items.AddRange(items.Cast<object>().ToArray());
        c.SelectedIndex = sel >= 0 && sel < c.Items.Count ? sel : 0;
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
                _offset.Text = r.Value.offset.ToString("+0;-0;0");
                _detectedOk = true;
                _offsetInfo.Text = $"Offset trovato: {r.Value.offset:+0;-0;0} (traccia {r.Value.trackNumber}, confidenza {r.Value.confidence}). Premi Salva.";
            }
            else _offsetInfo.Text = "Nessun offset coincide con AccurateRip su questo disco. Prova con un CD molto diffuso.";
        }
        catch (Exception ex) { _offsetInfo.Text = "Errore: " + ex.Message; }
        finally { _detect.Enabled = true; }
    }

    void SaveValues()
    {
        _s.OutputRoot = _out.Text.Trim();
        _s.Mp3Quality = Mp3Opts[Math.Max(0, _mp3.SelectedIndex)].v;
        _s.FlacLevel = FlacOpts[Math.Max(0, _flac.SelectedIndex)].v;
        _s.AacBitrate = AacOpts[Math.Max(0, _aac.SelectedIndex)].v;
        _s.OggQuality = OggOpts[Math.Max(0, _ogg.SelectedIndex)].v;
        _s.OpusBitrate = OpusOpts[Math.Max(0, _opus.SelectedIndex)].v;
        _s.Theme = ThemeOpts[Math.Max(0, _theme.SelectedIndex)].v;
        _s.ReadSpeed = SpeedOpts[Math.Max(0, _speed.SelectedIndex)].v;
        _s.MaxRetries = RetryOpts[Math.Max(0, _retries.SelectedIndex)].v;
        _s.ParanoiaAlways = _paranoia.Checked;
        _s.AutoReadOnInsert = _auto.Checked;
        _s.EjectWhenDone = _eject.Checked;
        _s.SaveCoverJpg = _coverJpg.Checked;
        _s.WriteLog = _log.Checked;
        _s.GnuDbEmail = _gnudb.Text.Trim();
        if (_drive != null && _offset.Enabled && int.TryParse(_offset.Text.Trim().TrimStart('+'), out var val))
        {
            var (o, known) = _s.GetOffset(_drive.Key);
            if (known || _detectedOk || val != o) _s.DriveOffsets[_drive.Key] = Math.Clamp(val, -3000, 3000);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.TitleBar(this);
    }
}
