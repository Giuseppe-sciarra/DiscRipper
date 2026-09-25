using System.Diagnostics;
using System.Runtime.InteropServices;
using DiscRipper.Core;

namespace DiscRipper.UI;

public sealed class MainForm : Form
{
    readonly AppSettings _s = AppSettings.Load();
    readonly HttpClient _http = Http.Create();

    // header
    readonly ThemedCombo _drives = new() { Width = 340 };
    readonly FlatButton _btnRead = new() { Text = "Leggi CD", Icon = Glyph.Disc };
    readonly FlatButton _btnEject = new() { Text = "Espelli", Icon = Glyph.Eject };
    readonly FlatButton _btnTheme = new() { Text = "Tema", Icon = Glyph.Theme };
    readonly FlatButton _btnSettings = new() { Text = "Impostazioni", Icon = Glyph.Settings };

    // album
    readonly CoverBox _cover = new() { Size = new Size(196, 196), Cursor = Cursors.Hand, AllowDrop = true };
    readonly ThemedCombo _candidates = new();
    readonly FlatButton _btnLookup = new() { Text = "Cerca di nuovo", Icon = Glyph.Search };
    readonly FieldBox _artist = new();
    readonly FieldBox _album = new();
    readonly FieldBox _year = new() { Width = 80 };
    readonly FieldBox _genre = new();
    readonly FieldBox _discNo = new() { Width = 52, Text = "1", TextAlign = HorizontalAlignment.Center };
    readonly FieldBox _discTot = new() { Width = 52, Text = "1", TextAlign = HorizontalAlignment.Center };
    readonly Label _info = new() { AutoSize = true, Tag = "dim", Font = Theme.Small, Anchor = AnchorStyles.Left, Margin = new Padding(0) };
    readonly Label _meta = new() { AutoSize = true, Tag = "dim", Font = Theme.Small, Anchor = AnchorStyles.Left, Margin = new Padding(0) };

    // tracce
    readonly DataGridView _grid = new() { Dock = DockStyle.Fill };

    // fondo
    readonly Dictionary<OutputFormat, ThemedCheckBox> _fmt = new();
    readonly FieldBox _outRoot = new();
    readonly FlatButton _btnBrowse = new() { Text = "Sfoglia", Icon = Glyph.Folder };
    readonly FlatButton _btnRip = new() { Text = "Estrai", Icon = Glyph.Rip, Accent = true, Font = Theme.Big };
    readonly FlatButton _btnOpen = new() { Text = "Apri cartella", Icon = Glyph.FolderOpen, Visible = false };
    readonly FlatProgress _progress = new() { Height = 10 };
    readonly Label _status = new() { AutoSize = true, Text = "Inserisci un CD audio", Tag = "dim", Anchor = AnchorStyles.Left, Margin = new Padding(0) };
    readonly RichTextBox _log = new() { Dock = DockStyle.Fill, ReadOnly = true, Font = Theme.Mono, DetectUrls = false };

    // stato
    Toc? _toc;
    CdDriveInfo? _driveInfo;
    CdTextInfo? _cdText;
    ArDisc? _ar;
    bool _arFailed;
    byte[]? _coverBytes;
    readonly Dictionary<string, byte[]?> _coverCache = new();
    CancellationTokenSource? _lookupCts, _ripCts;
    bool _busy, _ripping, _looking, _suppressCandidate, _mediaRejected;
    string? _lastAlbumDir;
    readonly System.Windows.Forms.Timer _poll = new() { Interval = 3000 };

    public MainForm()
    {
        Text = "DiscRipper";
        Font = Theme.Base;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(1000, 780);
        Size = new Size(1200, 940);
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        BuildUi();
        Theme.Changed += () => { Theme.Apply(this); RepaintStatuses(); _btnTheme.Text = ThemeLabel(); };
        Theme.Set(Args.Contains("--dark") ? ThemeMode.Scuro : Args.Contains("--light") ? ThemeMode.Chiaro : _s.Theme);

        Load += async (_, _) =>
        {
            LoadDrives();
            if (Args.Contains("--demo")) { FillDemo(); return; }
            await TryAutoRead(); _poll.Start();
        };
        if (Args.Contains("--settings")) Shown += (_, _) => BeginInvoke(OpenSettings);
        Shown += (_, _) => Theme.Apply(this); // ora gli handle esistono: scrollbar scure
        _poll.Tick += async (_, _) => await PollMedia();
        FormClosing += OnClosing;
    }

    // ------------------------------------------------------------------ layout

    void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(18, 14, 18, 18) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 252));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 186));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
        Controls.Add(root);

        // ---- header: titolo | lettore + azioni | tema + impostazioni (tutto alla stessa altezza)
        var title = new Label { Text = "DiscRipper", Font = Theme.Title, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 20, 0) };
        _drives.Anchor = AnchorStyles.Left;
        var header = Ui.Row(52,
            (title, SizeType.AutoSize, 0),
            (_drives, SizeType.AutoSize, 0),
            (Spacer(8), SizeType.AutoSize, 0),
            (_btnRead, SizeType.AutoSize, 0),
            (Spacer(8), SizeType.AutoSize, 0),
            (_btnEject, SizeType.AutoSize, 0),
            (new Panel { Width = 1, Margin = new Padding(0) }, SizeType.Percent, 100),
            (_btnTheme, SizeType.AutoSize, 0),
            (Spacer(8), SizeType.AutoSize, 0),
            (_btnSettings, SizeType.AutoSize, 0));
        header.Tag = null;
        header.Margin = new Padding(0, 0, 0, 6);
        root.Controls.Add(header, 0, 0);

        // ---- album: copertina | campi
        var albumCard = new Card { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 14), Padding = new Padding(18, 18, 18, 14) };
        var albumTl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Tag = "surface", Margin = new Padding(0) };
        albumTl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        albumTl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        albumTl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _cover.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        _cover.Margin = new Padding(0);
        albumTl.Controls.Add(_cover, 0, 0);

        const int labelW = 74;
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Tag = "surface", Margin = new Padding(0) };
        for (int i = 0; i < 4; i++) fields.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.RowH));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        fields.Controls.Add(Ui.Row(Ui.RowH,
            (Ui.Caption("Trovato"), SizeType.Absolute, labelW),
            (_candidates, SizeType.Percent, 100),
            (Spacer(8), SizeType.AutoSize, 0),
            (_btnLookup, SizeType.AutoSize, 0)), 0, 0);
        fields.Controls.Add(Ui.Row(Ui.RowH,
            (Ui.Caption("Artista"), SizeType.Absolute, labelW),
            (_artist, SizeType.Percent, 100)), 0, 1);
        fields.Controls.Add(Ui.Row(Ui.RowH,
            (Ui.Caption("Album"), SizeType.Absolute, labelW),
            (_album, SizeType.Percent, 100)), 0, 2);
        _year.Anchor = _discNo.Anchor = _discTot.Anchor = AnchorStyles.Left;
        fields.Controls.Add(Ui.Row(Ui.RowH,
            (Ui.Caption("Genere"), SizeType.Absolute, labelW),
            (_genre, SizeType.Percent, 100),
            (Pad(Ui.Caption("Anno", 10), 18), SizeType.AutoSize, 0),
            (_year, SizeType.AutoSize, 0),
            (Pad(Ui.Caption("Disco", 10), 18), SizeType.AutoSize, 0),
            (_discNo, SizeType.AutoSize, 0),
            (Pad(Ui.Caption("di", 0), 8, 8), SizeType.AutoSize, 0),
            (_discTot, SizeType.AutoSize, 0)), 0, 3);

        var infoTl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Tag = "surface", Margin = new Padding(labelW, 4, 0, 0) };
        infoTl.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        infoTl.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        infoTl.Controls.Add(_info, 0, 0);
        infoTl.Controls.Add(_meta, 0, 1);
        fields.Controls.Add(infoTl, 0, 4);
        albumTl.Controls.Add(fields, 1, 0);
        albumCard.Controls.Add(albumTl);
        root.Controls.Add(albumCard, 0, 1);

        // ---- griglia tracce
        var gridCard = new Card { Dock = DockStyle.Fill, Padding = new Padding(10, 10, 10, 10), Margin = new Padding(0, 0, 0, 14) };
        SetupGrid();
        gridCard.Controls.Add(_grid);
        root.Controls.Add(gridCard, 0, 2);

        // ---- fondo: [formati / destinazione / avanzamento] | [Estrai, Apri cartella]
        var bottom = new Card { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 14), Padding = new Padding(18, 12, 18, 12) };
        var btl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Tag = "surface", Margin = new Padding(0) };
        btl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        btl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 196));
        btl.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.RowH));
        btl.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.RowH));
        btl.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.RowH));
        btl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var fmtFlow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Tag = "surface", Anchor = AnchorStyles.Left };
        foreach (var f in FormatInfo.All)
        {
            var cb = new ThemedCheckBox { Text = FormatInfo.Label(f), Checked = _s.Formats.HasFlag(f), Margin = new Padding(0, 0, 18, 0) };
            cb.CheckedChanged += (_, _) => SaveFormats();
            _fmt[f] = cb;
            fmtFlow.Controls.Add(cb);
        }
        btl.Controls.Add(Ui.Row(Ui.RowH,
            (Ui.Caption("Formati"), SizeType.Absolute, labelW),
            (fmtFlow, SizeType.Percent, 100)), 0, 0);

        _outRoot.Text = _s.OutputRoot;
        btl.Controls.Add(Ui.Row(Ui.RowH,
            (Ui.Caption("Salva in"), SizeType.Absolute, labelW),
            (_outRoot, SizeType.Percent, 100),
            (Spacer(8), SizeType.AutoSize, 0),
            (_btnBrowse, SizeType.AutoSize, 0)), 0, 1);

        _progress.Margin = new Padding(0);
        btl.Controls.Add(Ui.Row(Ui.RowH,
            (Ui.Caption("Stato"), SizeType.Absolute, labelW),
            (_progress, SizeType.Percent, 100)), 0, 2);
        _status.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        _status.Margin = new Padding(labelW, 0, 0, 0);
        btl.Controls.Add(_status, 0, 3);

        // Estrai: alto quanto le righe Formati + Salva in; Apri cartella allineato alla riga Stato
        _btnRip.Dock = DockStyle.Fill;
        _btnRip.Margin = new Padding(16, 4, 0, 4);
        btl.Controls.Add(_btnRip, 1, 0); btl.SetRowSpan(_btnRip, 2);
        _btnOpen.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _btnOpen.Margin = new Padding(16, 0, 0, 0);
        btl.Controls.Add(_btnOpen, 1, 2);
        bottom.Controls.Add(btl);
        root.Controls.Add(bottom, 0, 3);

        // ---- registro
        var logCard = new Card { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 8, 10), Margin = new Padding(0) };
        logCard.Controls.Add(_log);
        root.Controls.Add(logCard, 0, 4);

        // ---- eventi
        _btnRead.Click += async (_, _) => await ReadDisc();
        _btnEject.Click += (_, _) => Eject();
        _btnTheme.Click += (_, _) => CycleTheme();
        _btnSettings.Click += (_, _) => OpenSettings();
        _btnBrowse.Click += (_, _) => Browse();
        _btnRip.Click += async (_, _) => { if (_ripping) _ripCts?.Cancel(); else await Rip(); };
        _btnOpen.Click += (_, _) => { if (_lastAlbumDir != null && Directory.Exists(_lastAlbumDir)) Process.Start("explorer.exe", $"\"{_lastAlbumDir}\""); };
        _btnLookup.Click += async (_, _) => { if (_toc != null) await Lookup(_toc, true); };
        _candidates.SelectedIndexChanged += async (_, _) => { if (!_suppressCandidate) await ApplyCandidate(); };
        _drives.SelectedIndexChanged += async (_, _) =>
        {
            if (_drives.SelectedItem is CdDriveInfo d) { _s.LastDrive = d.Letter.ToString(); _s.Save(); }
            if (!_busy && !_ripping) { ClearDisc(); await TryAutoRead(); }
        };
        _outRoot.Leave += (_, _) => { _s.OutputRoot = _outRoot.Text.Trim(); _s.Save(); };
        _artist.TextChanged += (_, _) => SyncAlbumArtistToTracks();

        // copertina: clic = scegli file, trascina immagine, menu contestuale
        var menu = new ContextMenuStrip();
        menu.Items.Add("Scegli immagine…", null, (_, _) => PickCover());
        menu.Items.Add("Incolla dagli appunti", null, (_, _) => PasteCover());
        menu.Items.Add("Rimuovi copertina", null, (_, _) => SetCover(null));
        _cover.ContextMenuStrip = menu;
        _cover.Click += (_, e) => { if (e is MouseEventArgs m && m.Button == MouseButtons.Left) PickCover(); };
        _cover.DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        _cover.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) LoadCoverFile(files[0]);
        };
        _cover.Paint += PaintCoverPlaceholder;
        new ToolTip().SetToolTip(_cover, "Clic per scegliere la copertina, oppure trascina qui un'immagine");
    }

    string _artistPrev = "";

    static readonly string[] Args = Environment.GetCommandLineArgs();

    /// <summary>Dati finti per anteprime dell'interfaccia (avvio con --demo).</summary>
    void FillDemo()
    {
        var starts = new[] { 0, 18000, 33500, 51000, 66800, 80100, 97000, 112400, 130900, 146000, 161500, 178000 };
        var toc = Toc.Build(starts.Select((o, i) => (i + 1, o, true)), 195000);
        _toc = toc;
        _driveInfo = new CdDriveInfo('E', "TSSTcorp", "BDDVDW SE-506BB");
        FillGrid(toc);
        var m = new AlbumMeta { Source = "MusicBrainz", Artist = "Artista di prova", Album = "Album dimostrativo", Year = "1994", Genre = "Rock", Country = "IT", Format = "CD", Label = "Etichetta", ExactMatch = true };
        string[] titles = { "Primo brano", "Notte a Roma", "Il mare d'inverno", "Strada lunga", "Luce", "Canzone senza nome", "Tempo", "Via del Corso", "Ultimo treno", "Ancora", "Sotto la pioggia", "Finale" };
        for (int i = 0; i < 12; i++) m.Tracks.Add(new TrackMeta { Number = i + 1, Title = titles[i], Artist = m.Artist });
        _candidates.Items.Add(m);
        _candidates.Items.Add(AlbumMeta.Empty(toc));
        _suppressCandidate = true; _candidates.SelectedIndex = 0; _suppressCandidate = false;
        ApplyMeta(m);
        _meta.Text = "MusicBrainz: 1 risultato";
        _info.Text = "12 tracce · 43:20 · AccurateRip: nel database (4 stampe) · Offset lettore +6";
        var bmp = new Bitmap(500, 500);
        using (var g = Graphics.FromImage(bmp))
        using (var lg = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, 500, 500), Color.FromArgb(236, 72, 153), Color.FromArgb(79, 70, 229), 45f))
            g.FillRectangle(lg, 0, 0, 500, 500);
        _cover.Image = bmp;
        var st = new[] { TrackRipStatus.AccurateRip, TrackRipStatus.AccurateRip, TrackRipStatus.Mismatch, TrackRipStatus.Verifying, TrackRipStatus.Pending };
        var tx = new[] { "AccurateRip OK (conf. 42)", "AccurateRip OK (conf. 42)", "Letture coerenti, ma diversa da AccurateRip", "Rilettura 1/2 40%", "In coda" };
        for (int i = 0; i < 5; i++) { _grid.Rows[i].Tag = st[i]; _grid.Rows[i].Cells["status"].Value = tx[i]; }
        _progress.Value = 0.34;
        SetStatus("Traccia 04: Rilettura 1/2 — 34% · circa 6:12 rimanenti");
        AppendLog("DiscRipper 1.0 — prova");
        AppendLog("Traccia 01  3:59  CRC32 1A2B3C4D  AccurateRip OK (conf. 42)");
        AppendLog("Traccia 03  3:53  CRC32 5E6F7A8B  [doppia lettura] Letture coerenti, ma diversa da AccurateRip", true);
        _btnOpen.Visible = true;
        UpdateButtons();
    }

    static Control Spacer(int w) => new Panel { Width = w, Height = 1, Margin = new Padding(0) };

    static Control Pad(Control c, int left, int right = -1)
    {
        c.Margin = new Padding(left, 0, right >= 0 ? right : c.Margin.Right, 0);
        return c;
    }

    void SetupGrid()
    {
        var g = _grid;
        g.AllowUserToAddRows = false;
        g.AllowUserToDeleteRows = false;
        g.AllowUserToResizeRows = false;
        g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        g.MultiSelect = true;
        g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        g.ColumnHeadersHeight = 36;
        g.RowTemplate.Height = 34;
        g.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;

        g.Columns.Add(new DataGridViewCheckBoxColumn { Name = "sel", HeaderText = "", Width = 44, FlatStyle = FlatStyle.Flat });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "num", HeaderText = "#", Width = 44, ReadOnly = true });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "title", HeaderText = "Titolo", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 55 });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "artist", HeaderText = "Artista", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 30 });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "dur", HeaderText = "Durata", Width = 70, ReadOnly = true });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "status", HeaderText = "Stato", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 45, ReadOnly = true });
        g.Columns["num"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        g.Columns["dur"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        foreach (DataGridViewColumn c in g.Columns) c.SortMode = DataGridViewColumnSortMode.NotSortable;

        g.CellPainting += (_, e) =>
        {
            if (e.ColumnIndex != 0 || e.Graphics == null) return;
            e.PaintBackground(e.CellBounds, e.RowIndex >= 0 && g.Rows[e.RowIndex].Selected);
            var p = Theme.P;
            int box = 18;
            var r = new Rectangle(e.CellBounds.X + (e.CellBounds.Width - box) / 2, e.CellBounds.Y + (e.CellBounds.Height - box) / 2, box, box);
            bool on, disabled = false;
            if (e.RowIndex < 0) on = g.Rows.Cast<DataGridViewRow>().Any() && g.Rows.Cast<DataGridViewRow>().Where(x => !x.ReadOnly).All(x => x.Cells[0].Value is true);
            else { on = e.Value is true; disabled = g.Rows[e.RowIndex].ReadOnly; }
            var gr = e.Graphics;
            gr.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = Theme.Rounded(r, 5))
            {
                if (disabled) { }
                else if (on)
                {
                    using var b = new SolidBrush(p.Accent); gr.FillPath(b, path);
                    using var pen = new Pen(p.AccentText, 2f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
                    gr.DrawLines(pen, new[] { new Point(r.X + 4, r.Y + 9), new Point(r.X + 8, r.Y + 13), new Point(r.X + 14, r.Y + 5) });
                }
                else
                {
                    using var b = new SolidBrush(p.Surface2); gr.FillPath(b, path);
                    using var pen = new Pen(p.Border, 1.4f); gr.DrawPath(pen, path);
                }
            }
            if (e.RowIndex >= 0)
            {
                using var line = new Pen(g.GridColor);
                gr.DrawLine(line, e.CellBounds.Left, e.CellBounds.Bottom - 1, e.CellBounds.Right, e.CellBounds.Bottom - 1);
            }
            e.Handled = true;
        };
        g.CellValueChanged += (_, e) => { if (e.ColumnIndex == 0) g.InvalidateCell(0, -1); };
        g.CurrentCellDirtyStateChanged += (_, _) => { if (g.IsCurrentCellDirty && g.CurrentCell is DataGridViewCheckBoxCell) g.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        g.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || g.Columns[e.ColumnIndex].Name != "status" || e.CellStyle == null) return;
            if (g.Rows[e.RowIndex].Tag is TrackRipStatus st)
            {
                e.CellStyle.ForeColor = StatusColor(st);
                e.CellStyle.SelectionForeColor = StatusColor(st);
                e.CellStyle.Font = st is TrackRipStatus.Pending ? Theme.Base : Theme.Bold;
            }
        };
        // header checkbox: clic sull'intestazione della colonna "sel" = seleziona/deseleziona tutto
        g.ColumnHeaderMouseClick += (_, e) =>
        {
            if (e.ColumnIndex != 0 || _ripping) return;
            bool any = g.Rows.Cast<DataGridViewRow>().Any(r => r.Cells[0].ReadOnly == false && !(bool)(r.Cells[0].Value ?? false));
            foreach (DataGridViewRow r in g.Rows) if (!r.Cells[0].ReadOnly) r.Cells[0].Value = any;
        };
        g.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Space && g.CurrentCell?.ColumnIndex != 0 && !_ripping)
            {
                foreach (DataGridViewRow r in g.SelectedRows) if (!r.Cells[0].ReadOnly) r.Cells[0].Value = !(bool)(r.Cells[0].Value ?? false);
                e.Handled = true;
            }
        };
    }

    static Color StatusColor(TrackRipStatus s) => s switch
    {
        TrackRipStatus.AccurateRip or TrackRipStatus.Done => Theme.P.Success,
        TrackRipStatus.Consistent => Theme.P.Success,
        TrackRipStatus.Mismatch => Theme.P.Warning,
        TrackRipStatus.Errors or TrackRipStatus.Failed => Theme.P.Error,
        TrackRipStatus.Reading or TrackRipStatus.Verifying or TrackRipStatus.Encoding => Theme.P.Info,
        _ => Theme.P.TextDim
    };

    void RepaintStatuses() => _grid.Invalidate();

    void PaintCoverPlaceholder(object? sender, PaintEventArgs e)
    {
        if (_cover.Image != null) return;
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var p = Theme.P;
        int d = Math.Min(_cover.Width, _cover.Height) - 50;
        var r = new Rectangle((_cover.Width - d) / 2, (_cover.Height - d) / 2 - 8, d, d);
        using var pen = new Pen(p.Border, 2);
        g.DrawEllipse(pen, r);
        var inner = Rectangle.Inflate(r, -d * 35 / 100, -d * 35 / 100);
        g.DrawEllipse(pen, inner);
        TextRenderer.DrawText(g, "Nessuna copertina", Theme.Small, new Rectangle(0, _cover.Height - 26, _cover.Width, 20), p.TextDim,
            TextFormatFlags.HorizontalCenter);
    }

    // ------------------------------------------------------------------ lettori e disco

    void LoadDrives()
    {
        var list = CdDrive.Enumerate();
        _drives.Items.Clear();
        foreach (var d in list) _drives.Items.Add(d);
        if (list.Count == 0) { SetStatus("Nessun lettore CD/DVD trovato"); return; }
        var last = list.FirstOrDefault(d => d.Letter.ToString() == _s.LastDrive) ?? list[0];
        _drives.SelectedItem = last;
    }

    CdDriveInfo? CurrentDrive => _drives.SelectedItem as CdDriveInfo;

    async Task TryAutoRead()
    {
        var d = CurrentDrive;
        if (d == null || _busy || _ripping) return;
        bool present = await Task.Run(() => { try { using var cd = new CdDrive(d.Letter); return cd.IsMediaPresent(); } catch { return false; } });
        if (present && _toc == null) await ReadDisc();
    }

    async Task PollMedia()
    {
        if (_busy || _ripping || !_s.AutoReadOnInsert) return;
        var d = CurrentDrive;
        if (d == null) return;
        bool present = await Task.Run(() => { try { using var cd = new CdDrive(d.Letter); return cd.IsMediaPresent(); } catch { return false; } });
        if (_busy || _ripping) return;
        if (!present) _mediaRejected = false;
        if (present && _toc == null && !_mediaRejected) await ReadDisc();
        else if (!present && _toc != null) { ClearDisc(); SetStatus("Inserisci un CD audio"); }
    }

    const int WM_DEVICECHANGE = 0x0219, DBT_DEVICEARRIVAL = 0x8000, DBT_DEVICEREMOVECOMPLETE = 0x8004;

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != WM_DEVICECHANGE || m.LParam == IntPtr.Zero) return;
        int ev = (int)m.WParam;
        if (ev != DBT_DEVICEARRIVAL && ev != DBT_DEVICEREMOVECOMPLETE) return;
        int devType = Marshal.ReadInt32(m.LParam, 4);
        if (devType != 2) return; // DBT_DEVTYP_VOLUME
        uint mask = (uint)Marshal.ReadInt32(m.LParam, 12);
        var d = CurrentDrive;
        if (d == null || (mask & (1u << (d.Letter - 'A'))) == 0) return;
        if (ev == DBT_DEVICEARRIVAL && _s.AutoReadOnInsert && !_ripping && !_busy)
            BeginInvoke(async () => { _mediaRejected = false; await Task.Delay(1200); if (_toc == null) await ReadDisc(); });
        else if (ev == DBT_DEVICEREMOVECOMPLETE && !_ripping)
            BeginInvoke(() => { ClearDisc(); SetStatus("Inserisci un CD audio"); });
    }

    void ClearDisc()
    {
        _lookupCts?.Cancel();
        _toc = null; _cdText = null; _ar = null; _arFailed = false;
        _grid.Rows.Clear();
        _suppressCandidate = true; _candidates.Items.Clear(); _suppressCandidate = false;
        _artist.Text = _album.Text = _year.Text = _genre.Text = "";
        _discNo.Text = _discTot.Text = "1";
        _info.Text = _meta.Text = "";
        SetCover(null);
        _progress.Value = 0;
        UpdateButtons();
    }

    async Task ReadDisc()
    {
        var d = CurrentDrive;
        if (d == null || _busy || _ripping) return;
        _busy = true; UpdateButtons();
        ClearDisc();
        _busy = true; UpdateButtons();
        SetStatus("Lettura del disco…");
        try
        {
            var (toc, txt) = await Task.Run(() =>
            {
                using var cd = new CdDrive(d.Letter);
                var t = cd.ReadToc();
                CdTextInfo? ct = null;
                try { ct = cd.ReadCdText(); } catch { }
                return (t, ct);
            });
            if (toc.AudioTracks.Count == 0) { _mediaRejected = true; SetStatus("Il disco non contiene tracce audio"); return; }
            _toc = toc; _cdText = txt; _driveInfo = d;
            FillGrid(toc);
            var baseMeta = AlbumMeta.FromCdText(toc, txt) ?? AlbumMeta.Empty(toc);
            _suppressCandidate = true;
            _candidates.Items.Add(baseMeta);
            if (baseMeta.Source != "Manuale") _candidates.Items.Add(AlbumMeta.Empty(toc));
            _candidates.SelectedIndex = 0;
            _suppressCandidate = false;
            ApplyMeta(baseMeta);
            UpdateInfo();
            AppendLog($"Disco letto: {toc.AudioTracks.Count} tracce audio, {Fmt(toc.TotalDuration)}{(toc.HasDataTrack ? " + traccia dati" : "")}{(txt != null ? ", CD-Text presente" : "")}.");
            _busy = false; UpdateButtons();
            await Lookup(toc, false);
        }
        catch (Exception ex)
        {
            _mediaRejected = true;
            SetStatus("Nessun CD audio leggibile");
            AppendLog("Lettura TOC fallita: " + ex.Message, true);
        }
        finally { _busy = false; UpdateButtons(); }
    }

    void FillGrid(Toc toc)
    {
        _grid.Rows.Clear();
        foreach (var t in toc.Tracks)
        {
            int i = _grid.Rows.Add(t.IsAudio, t.Number.ToString("D2"), t.IsAudio ? $"Traccia {t.Number:D2}" : "(traccia dati)", "", Fmt(t.Duration), t.IsAudio ? "" : "non audio");
            var row = _grid.Rows[i];
            row.Tag = TrackRipStatus.Pending;
            if (!t.IsAudio) { row.ReadOnly = true; row.Cells[0].Value = false; row.DefaultCellStyle.ForeColor = Theme.P.TextDim; }
        }
    }

    DataGridViewRow? RowForAudioIndex(int audioIndex)
    {
        if (_toc == null) return null;
        var t = _toc.AudioTracks[audioIndex];
        int i = _toc.Tracks.IndexOf(t);
        return i >= 0 && i < _grid.Rows.Count ? _grid.Rows[i] : null;
    }

    static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    // ------------------------------------------------------------------ metadati

    async Task Lookup(Toc toc, bool manual)
    {
        _lookupCts?.Cancel();
        var cts = _lookupCts = new CancellationTokenSource();
        _looking = true; UpdateButtons();
        try { await LookupCore(toc, manual, cts); }
        finally { if (_lookupCts == cts) { _looking = false; UpdateButtons(); } }
    }

    async Task LookupCore(Toc toc, bool manual, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        SetStatus("Cerco il disco su MusicBrainz e AccurateRip…");
        _meta.Text = "Ricerca metadati in corso…";

        var arTask = Task.Run(async () =>
        {
            try { return (await ArDisc.FetchAsync(_http, toc, ct), false); }
            catch (OperationCanceledException) { throw; }
            catch { return ((ArDisc?)null, true); }
        }, ct);

        var found = new List<AlbumMeta>();
        string mbStatus;
        try
        {
            var mb = await new MusicBrainzClient(_http).LookupAsync(toc, ct);
            found.AddRange(mb);
            mbStatus = mb.Count == 0 ? "MusicBrainz: non trovato" : $"MusicBrainz: {mb.Count} risultat{(mb.Count == 1 ? "o" : "i")}{(mb.Any(x => x.ExactMatch) ? "" : " (approssimati)")}";
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { mbStatus = "MusicBrainz non raggiungibile"; AppendLog("MusicBrainz: " + ex.Message, true); }

        if ((found.Count == 0 || manual) && !string.IsNullOrWhiteSpace(_s.GnuDbEmail))
        {
            try
            {
                var gn = await new GnuDbClient(_http, _s.GnuDbEmail).LookupAsync(toc, ct);
                found.AddRange(gn);
                if (gn.Count > 0) mbStatus += $" · GnuDB: {gn.Count}";
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { AppendLog("GnuDB: " + ex.Message, true); }
        }

        try { (_ar, _arFailed) = await arTask; }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested || _toc != toc || _ripping) return;

        // aggiorna l'elenco dei candidati: prima quelli online, poi CD-Text / manuale
        var local = _candidates.Items.Cast<AlbumMeta>().Where(a => a.Source is "CD-Text" or "Manuale").ToList();
        _suppressCandidate = true;
        _candidates.Items.Clear();
        foreach (var a in found) _candidates.Items.Add(a);
        foreach (var a in local) _candidates.Items.Add(a);
        _candidates.SelectedIndex = 0;
        _suppressCandidate = false;
        _meta.Text = mbStatus;
        UpdateInfo();
        AppendLog(mbStatus + " · " + ArText());
        if (found.Count > 0) await ApplyCandidate();
        SetStatus(found.Count > 0 ? "Pronto: controlla i dati e premi Estrai" : "Disco non riconosciuto: inserisci i dati a mano (o usa il CD-Text)");
    }

    string ArText() => _ar != null ? $"AccurateRip: nel database ({_ar.Pressings.Count} stamp{(_ar.Pressings.Count == 1 ? "a" : "e")})"
        : _arFailed ? "AccurateRip: non raggiungibile" : "AccurateRip: non presente";

    async Task ApplyCandidate()
    {
        if (_candidates.SelectedItem is not AlbumMeta a) return;
        ApplyMeta(a);
        if (a.MbReleaseId == null) { SetCover(null); return; }
        if (_coverCache.TryGetValue(a.MbReleaseId, out var cached)) { SetCover(cached); return; }
        var ct = _lookupCts?.Token ?? CancellationToken.None;
        try
        {
            var bytes = await new MusicBrainzClient(_http).GetCoverAsync(a, ct);
            _coverCache[a.MbReleaseId] = bytes;
            if (_candidates.SelectedItem == a) SetCover(bytes);
        }
        catch { }
    }

    void ApplyMeta(AlbumMeta a)
    {
        _artistPrev = a.Artist;
        _artist.Text = a.Artist;
        _album.Text = a.Album;
        _year.Text = a.Year;
        _genre.Text = a.Genre;
        _discNo.Text = a.DiscNumber.ToString();
        _discTot.Text = a.DiscTotal.ToString();
        if (_toc == null) return;
        foreach (var tm in a.Tracks)
        {
            var t = _toc.Tracks.FirstOrDefault(x => x.Number == tm.Number);
            if (t == null) continue;
            var row = _grid.Rows[_toc.Tracks.IndexOf(t)];
            row.Cells["title"].Value = tm.Title;
            row.Cells["artist"].Value = string.IsNullOrWhiteSpace(tm.Artist) ? a.Artist : tm.Artist;
        }
    }

    /// <summary>Se l'artista dell'album cambia, aggiorna le tracce che avevano il vecchio artista.</summary>
    void SyncAlbumArtistToTracks()
    {
        string now = _artist.Text;
        foreach (DataGridViewRow r in _grid.Rows)
        {
            if (r.ReadOnly) continue;
            var v = r.Cells["artist"].Value as string ?? "";
            if (v.Length == 0 || v == _artistPrev) r.Cells["artist"].Value = now;
        }
        _artistPrev = now;
    }

    AlbumMeta BuildMetaFromUi()
    {
        var src = _candidates.SelectedItem as AlbumMeta;
        var a = new AlbumMeta
        {
            Source = src?.Source ?? "Manuale",
            Artist = _artist.Text.Trim().Length > 0 ? _artist.Text.Trim() : "Artista sconosciuto",
            Album = _album.Text.Trim().Length > 0 ? _album.Text.Trim() : "Album sconosciuto",
            Year = _year.Text.Trim(),
            Genre = _genre.Text.Trim(),
            DiscNumber = int.TryParse(_discNo.Text, out var dn) && dn > 0 ? dn : 1,
            DiscTotal = int.TryParse(_discTot.Text, out var dt) && dt > 0 ? dt : 1,
            MbReleaseId = src?.MbReleaseId,
            MbReleaseGroupId = src?.MbReleaseGroupId,
            MbAlbumArtistId = src?.MbAlbumArtistId,
            Country = src?.Country ?? "",
            ExactMatch = true
        };
        if (a.DiscNumber > a.DiscTotal) a.DiscTotal = a.DiscNumber;
        foreach (var t in _toc!.AudioTracks)
        {
            var row = _grid.Rows[_toc.Tracks.IndexOf(t)];
            var srcT = src?.Tracks.FirstOrDefault(x => x.Number == t.Number);
            string title = (row.Cells["title"].Value as string ?? "").Trim();
            string artist = (row.Cells["artist"].Value as string ?? "").Trim();
            a.Tracks.Add(new TrackMeta
            {
                Number = t.Number,
                Title = title.Length > 0 ? title : $"Traccia {t.Number:D2}",
                Artist = artist.Length > 0 ? artist : a.Artist,
                MbRecordingId = srcT?.MbRecordingId,
                MbTrackId = srcT?.MbTrackId,
                MbArtistId = srcT?.MbArtistId
            });
        }
        return a;
    }

    void UpdateInfo()
    {
        if (_toc == null) { _info.Text = ""; return; }
        var d = CurrentDrive;
        string off = "";
        if (d != null)
        {
            var (o, known) = _s.GetOffset(d.Key);
            off = known ? $" · Offset lettore {o:+0;-0;0}" : $" · Offset lettore {o:+0;-0;0} (da rilevare)";
        }
        _info.Text = $"{_toc.AudioTracks.Count} tracce · {Fmt(_toc.TotalDuration)} · {ArText()}{off}";
    }

    // ------------------------------------------------------------------ copertina

    void SetCover(byte[]? bytes)
    {
        _coverBytes = bytes;
        var old = _cover.Image;
        _cover.Image = null;
        old?.Dispose();
        if (bytes != null)
        {
            try { using var ms = new MemoryStream(bytes); _cover.Image = new Bitmap(Image.FromStream(ms)); }
            catch { _coverBytes = null; }
        }
        _cover.Invalidate();
    }

    void PickCover()
    {
        using var dlg = new OpenFileDialog { Filter = "Immagini|*.jpg;*.jpeg;*.png;*.webp;*.bmp|Tutti i file|*.*", Title = "Scegli la copertina" };
        if (dlg.ShowDialog(this) == DialogResult.OK) LoadCoverFile(dlg.FileName);
    }

    void PasteCover()
    {
        if (!Clipboard.ContainsImage()) return;
        using var img = Clipboard.GetImage();
        if (img != null) SetCover(ToJpeg(img));
    }

    void LoadCoverFile(string path)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".jpg" or ".jpeg" or ".png") SetCover(File.ReadAllBytes(path));
            else { using var img = Image.FromFile(path); SetCover(ToJpeg(img)); }
        }
        catch (Exception ex) { AppendLog("Copertina non valida: " + ex.Message, true); }
    }

    static byte[] ToJpeg(Image img)
    {
        using var ms = new MemoryStream();
        var enc = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(e => e.MimeType == "image/jpeg");
        using var ep = new System.Drawing.Imaging.EncoderParameters(1);
        ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
        img.Save(ms, enc, ep);
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ estrazione

    async Task Rip()
    {
        if (_toc == null || _driveInfo == null) return;
        _grid.EndEdit();
        _s.OutputRoot = _outRoot.Text.Trim(); _s.Save();
        var selected = new List<int>();
        var audio = _toc.AudioTracks;
        for (int i = 0; i < audio.Count; i++)
        {
            var row = RowForAudioIndex(i);
            if (row != null && row.Cells[0].Value is true) selected.Add(i);
        }
        if (selected.Count == 0) { MessageBox.Show(this, "Seleziona almeno una traccia.", "DiscRipper", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (_s.Formats == OutputFormat.None) { MessageBox.Show(this, "Seleziona almeno un formato.", "DiscRipper", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

        var meta = BuildMetaFromUi();
        var job = new RipJob
        {
            DriveInfo = _driveInfo,
            Toc = _toc,
            Meta = meta,
            SelectedAudioIndexes = selected,
            Settings = _s,
            Cover = _coverBytes,
            Ar = _ar,
            ArLookupFailed = _arFailed
        };

        _ripping = true;
        _ripCts = new CancellationTokenSource();
        _poll.Stop();
        UpdateButtons();
        _btnOpen.Visible = false;
        _log.Clear();
        foreach (var i in selected)
        {
            var r = RowForAudioIndex(i);
            if (r != null) { r.Tag = TrackRipStatus.Pending; r.Cells["status"].Value = "In coda"; }
        }
        _progress.Value = 0;
        var sw = Stopwatch.StartNew();
        var progress = new Progress<RipEvent>(e => OnRipEvent(e, sw));
        try
        {
            await job.RunAsync(progress, _ripCts.Token);
            _lastAlbumDir = job.AlbumDir;
            _btnOpen.Visible = true;
            _progress.Value = 1;
            SetStatus(job.ErrorCount == 0
                ? $"Fatto in {Fmt(sw.Elapsed)} — salvato in {job.AlbumDir}"
                : $"Terminato con {job.ErrorCount} tracce da controllare — {job.AlbumDir}");
            if (_s.EjectWhenDone) ClearDisc();
            FlashWindow();
        }
        catch (OperationCanceledException)
        {
            SetStatus("Estrazione annullata");
            AppendLog("Estrazione annullata dall'utente.", true);
            MarkCancelled();
        }
        catch (Exception ex)
        {
            SetStatus("Errore: " + ex.Message);
            AppendLog("ERRORE: " + ex.Message, true);
            MarkCancelled();
            MessageBox.Show(this, ex.Message, "DiscRipper", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _ripping = false;
            _ripCts.Dispose(); _ripCts = null;
            UpdateButtons();
            _poll.Start();
            UpdateInfo();
        }
    }

    void MarkCancelled()
    {
        foreach (DataGridViewRow r in _grid.Rows)
            if (r.Tag is TrackRipStatus.Reading or TrackRipStatus.Verifying or TrackRipStatus.Encoding
                || (r.Tag is TrackRipStatus.Pending && (r.Cells["status"].Value as string) == "In coda"))
            { r.Tag = TrackRipStatus.Cancelled; r.Cells["status"].Value = "Annullata"; }
    }

    void OnRipEvent(RipEvent e, Stopwatch sw)
    {
        switch (e.Kind)
        {
            case RipEventKind.Log: AppendLog(e.Text, e.Warning); break;
            case RipEventKind.Phase: SetStatus(e.Text); break;
            case RipEventKind.Progress:
                _progress.Value = e.Fraction;
                string eta = "";
                if (e.Fraction > 0.02 && e.AudioIndex >= 0)
                {
                    var rem = TimeSpan.FromSeconds(sw.Elapsed.TotalSeconds * (1 - e.Fraction) / e.Fraction);
                    eta = $" · circa {Fmt(rem)} rimanenti";
                }
                SetStatus($"{e.Text} — {e.Fraction:P0}{eta}");
                break;
            case RipEventKind.TrackStatus:
                var row = RowForAudioIndex(e.AudioIndex);
                if (row == null) break;
                row.Tag = e.Status;
                row.Cells["status"].Value = e.Text;
                _grid.InvalidateRow(row.Index);
                break;
        }
    }

    // ------------------------------------------------------------------ varie

    void UpdateButtons()
    {
        bool hasDisc = _toc != null;
        _btnRead.Enabled = !_busy && !_ripping && CurrentDrive != null;
        _btnEject.Enabled = !_ripping && CurrentDrive != null;
        _drives.Enabled = !_ripping;
        _btnLookup.Enabled = hasDisc && !_ripping;
        _btnRip.Enabled = (hasDisc && !_busy && !_looking) || _ripping;
        _btnRip.Text = _ripping ? "Annulla" : "Estrai";
        _btnRip.Accent = !_ripping;
        _btnRip.ApplyTheme();
        _btnSettings.Enabled = !_ripping;
        foreach (var c in new Control[] { _artist, _album, _year, _genre, _discNo, _discTot, _candidates, _outRoot, _btnBrowse })
            c.Enabled = !_ripping;
        foreach (var cb in _fmt.Values) cb.Enabled = !_ripping;
        _grid.ReadOnly = _ripping;
        if (!_ripping)
            foreach (DataGridViewRow r in _grid.Rows)
                if (_toc != null && r.Index < _toc.Tracks.Count && !_toc.Tracks[r.Index].IsAudio) r.ReadOnly = true;
    }

    void Eject()
    {
        var d = CurrentDrive;
        if (d == null) return;
        _lookupCts?.Cancel();
        Task.Run(() => { try { using var cd = new CdDrive(d.Letter); cd.Eject(); } catch { } });
        ClearDisc();
        SetStatus("Inserisci un CD audio");
    }

    void Browse()
    {
        using var dlg = new FolderBrowserDialog { Description = "Cartella principale dove salvare (anche unità di rete)", UseDescriptionForTitle = true, SelectedPath = _outRoot.Text };
        if (dlg.ShowDialog(this) == DialogResult.OK) { _outRoot.Text = dlg.SelectedPath; _s.OutputRoot = dlg.SelectedPath; _s.Save(); }
    }

    void SaveFormats()
    {
        var f = OutputFormat.None;
        foreach (var (k, cb) in _fmt) if (cb.Checked) f |= k;
        _s.Formats = f; _s.Save();
    }

    string ThemeLabel() => Theme.Mode switch { ThemeMode.Chiaro => "Tema chiaro", ThemeMode.Scuro => "Tema scuro", _ => "Tema di sistema" };

    void CycleTheme()
    {
        _s.Theme = _s.Theme switch { ThemeMode.Sistema => ThemeMode.Chiaro, ThemeMode.Chiaro => ThemeMode.Scuro, _ => ThemeMode.Sistema };
        _s.Save();
        Theme.Set(_s.Theme);
    }

    void OpenSettings()
    {
        using var f = new SettingsForm(_s, CurrentDrive, _toc, _ar);
        if (f.ShowDialog(this) == DialogResult.OK)
        {
            _s.Save();
            Theme.Set(_s.Theme);
            _outRoot.Text = _s.OutputRoot;
            UpdateInfo();
        }
    }

    void SetStatus(string s) => _status.Text = s;

    void AppendLog(string s, bool warn = false)
    {
        _log.SelectionStart = _log.TextLength;
        _log.SelectionLength = 0;
        _log.SelectionColor = warn ? Theme.P.Warning : Theme.P.Text;
        _log.AppendText(s + Environment.NewLine);
        _log.SelectionColor = Theme.P.Text;
        _log.ScrollToCaret();
    }

    [DllImport("user32.dll")] static extern bool FlashWindow(IntPtr hwnd, bool invert);
    void FlashWindow() { if (Form.ActiveForm != this) FlashWindow(Handle, true); }

    void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_ripping && MessageBox.Show(this, "Estrazione in corso: vuoi davvero uscire?", "DiscRipper",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        { e.Cancel = true; return; }
        _ripCts?.Cancel();
        _lookupCts?.Cancel();
        _s.OutputRoot = _outRoot.Text.Trim();
        _s.Save();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.TitleBar(this);
    }
}
