using System.Diagnostics;
using System.Runtime.InteropServices;
using DiscRipper.Core;

namespace DiscRipper.UI;

public sealed class MainForm : Form
{
    readonly AppSettings _s = AppSettings.Load();
    readonly HttpClient _http = Http.Create();

    // header
    readonly ThemedCombo _drives = new() { Width = 330 };
    readonly FlatButton _btnRead = new() { Text = "Leggi CD" };
    readonly FlatButton _btnEject = new() { Text = "Espelli" };
    readonly FlatButton _btnTheme = new() { Text = "Tema" };
    readonly FlatButton _btnSettings = new() { Text = "Impostazioni" };

    // album
    readonly PictureBox _cover = new() { Size = new Size(190, 190), SizeMode = PictureBoxSizeMode.Zoom, Cursor = Cursors.Hand, AllowDrop = true };
    readonly ThemedCombo _candidates = new() { Dock = DockStyle.Fill };
    readonly FlatButton _btnLookup = new() { Text = "Cerca di nuovo" };
    readonly TextBox _artist = new() { Dock = DockStyle.Fill };
    readonly TextBox _album = new() { Dock = DockStyle.Fill };
    readonly TextBox _year = new() { Width = 70 };
    readonly TextBox _genre = new() { Dock = DockStyle.Fill };
    readonly TextBox _discNo = new() { Width = 40, Text = "1" };
    readonly TextBox _discTot = new() { Width = 40, Text = "1" };
    readonly Label _info = new() { AutoSize = true, Tag = "dim", Font = Theme.Small, Margin = new Padding(0, 6, 0, 0) };
    readonly Label _meta = new() { AutoSize = true, Tag = "dim", Font = Theme.Small };

    // tracce
    readonly DataGridView _grid = new() { Dock = DockStyle.Fill };

    // fondo
    readonly Dictionary<OutputFormat, ThemedCheckBox> _fmt = new();
    readonly TextBox _outRoot = new() { Dock = DockStyle.Fill };
    readonly FlatButton _btnBrowse = new() { Text = "…", MinimumSize = new Size(40, 30) };
    readonly FlatButton _btnRip = new() { Text = "Estrai", Accent = true, MinimumSize = new Size(140, 40), Font = Theme.Big };
    readonly FlatButton _btnOpen = new() { Text = "Apri cartella", Visible = false };
    readonly FlatProgress _progress = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 8) };
    readonly Label _status = new() { AutoSize = true, Text = "Inserisci un CD audio", Tag = "dim" };
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
        MinimumSize = new Size(980, 700);
        Size = new Size(1180, 860);
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        BuildUi();
        Theme.Changed += () => { Theme.Apply(this); RepaintStatuses(); _btnTheme.Text = ThemeLabel(); };
        Theme.Set(_s.Theme);

        Load += async (_, _) => { LoadDrives(); await TryAutoRead(); _poll.Start(); };
        Shown += (_, _) => Theme.Apply(this); // ora gli handle esistono: scrollbar scure
        _poll.Tick += async (_, _) => await PollMedia();
        FormClosing += OnClosing;
    }

    // ------------------------------------------------------------------ layout

    void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(16, 12, 16, 16) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        Controls.Add(root);

        // ---- header
        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 0, 0, 10) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var title = new Label { Text = "DiscRipper", Font = Theme.Title, AutoSize = true, Margin = new Padding(0, 2, 18, 0) };
        var left = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0) };
        _drives.Margin = new Padding(0, 6, 8, 0);
        _btnRead.Margin = _btnEject.Margin = new Padding(0, 2, 8, 0);
        left.Controls.AddRange(new Control[] { _drives, _btnRead, _btnEject });
        var right = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        _btnTheme.Margin = _btnSettings.Margin = new Padding(8, 2, 0, 0);
        right.Controls.AddRange(new Control[] { _btnTheme, _btnSettings });
        header.Controls.Add(title, 0, 0);
        header.Controls.Add(left, 1, 0);
        header.Controls.Add(right, 2, 0);
        root.Controls.Add(header, 0, 0);

        // ---- album card
        var albumCard = new Card { Dock = DockStyle.Top, Height = 226, Margin = new Padding(0, 0, 0, 12) };
        var albumTl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Tag = "surface" };
        albumTl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        albumTl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _cover.Margin = new Padding(0, 0, 16, 0);
        albumTl.Controls.Add(_cover, 0, 0);

        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 6, Tag = "surface", Margin = new Padding(0) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        for (int i = 0; i < 5; i++) fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        Label L(string t) => new() { Text = t, AutoSize = true, Tag = "dim", Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 6, 0) };

        var candRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0), Tag = "surface" };
        candRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        candRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _candidates.Margin = new Padding(0, 4, 8, 0);
        _btnLookup.Margin = new Padding(0, 0, 0, 0);
        _btnLookup.MinimumSize = new Size(90, 30); _btnLookup.Height = 30;
        candRow.Controls.Add(_candidates, 0, 0);
        candRow.Controls.Add(_btnLookup, 1, 0);

        fields.Controls.Add(L("Trovato"), 0, 0);
        fields.Controls.Add(candRow, 1, 0); fields.SetColumnSpan(candRow, 3);
        fields.Controls.Add(L("Artista"), 0, 1);
        fields.Controls.Add(_artist, 1, 1); fields.SetColumnSpan(_artist, 3);
        fields.Controls.Add(L("Album"), 0, 2);
        fields.Controls.Add(_album, 1, 2); fields.SetColumnSpan(_album, 3);
        fields.Controls.Add(L("Genere"), 0, 3);
        fields.Controls.Add(_genre, 1, 3);
        fields.Controls.Add(L("Anno"), 2, 3);
        fields.Controls.Add(_year, 3, 3);
        var discFlow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Tag = "surface" };
        discFlow.Controls.AddRange(new Control[] { _discNo, new Label { Text = "di", AutoSize = true, Tag = "dim", Margin = new Padding(6, 6, 6, 0) }, _discTot });
        fields.Controls.Add(L("Disco"), 0, 4);
        fields.Controls.Add(discFlow, 1, 4);
        var infoFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0), Tag = "surface" };
        infoFlow.Controls.Add(_info);
        infoFlow.Controls.Add(_meta);
        fields.Controls.Add(infoFlow, 1, 5); fields.SetColumnSpan(infoFlow, 3);
        foreach (var tb in new[] { _artist, _album, _genre, _year, _discNo, _discTot }) tb.Margin = new Padding(0, 4, 0, 0);
        albumTl.Controls.Add(fields, 1, 0);
        albumCard.Controls.Add(albumTl);
        root.Controls.Add(albumCard, 0, 1);

        // ---- griglia tracce
        var gridCard = new Card { Dock = DockStyle.Fill, Padding = new Padding(2, 8, 2, 8), Margin = new Padding(0, 0, 0, 12) };
        SetupGrid();
        gridCard.Controls.Add(_grid);
        root.Controls.Add(gridCard, 0, 2);

        // ---- fondo: formati, destinazione, estrai
        var bottom = new Card { Dock = DockStyle.Top, Height = 144, Margin = new Padding(0, 0, 0, 12) };
        var btl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3, Tag = "surface" };
        btl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        btl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        btl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        btl.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        btl.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        btl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var fmtFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0), Tag = "surface" };
        foreach (var f in FormatInfo.All)
        {
            var cb = new ThemedCheckBox { Text = FormatInfo.Label(f), Checked = _s.Formats.HasFlag(f), Margin = new Padding(0, 4, 14, 0) };
            cb.CheckedChanged += (_, _) => SaveFormats();
            _fmt[f] = cb;
            fmtFlow.Controls.Add(cb);
        }
        btl.Controls.Add(L("Formati"), 0, 0);
        btl.Controls.Add(fmtFlow, 1, 0);

        var destRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0), Tag = "surface" };
        destRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        destRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _outRoot.Text = _s.OutputRoot;
        _outRoot.Margin = new Padding(0, 5, 6, 0);
        _btnBrowse.Margin = new Padding(0, 1, 0, 0); _btnBrowse.Height = 30;
        destRow.Controls.Add(_outRoot, 0, 0);
        destRow.Controls.Add(_btnBrowse, 1, 0);
        btl.Controls.Add(L("Salva in"), 0, 1);
        btl.Controls.Add(destRow, 1, 1);

        var ripFlow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(16, 0, 0, 0), Tag = "surface" };
        _btnRip.Margin = new Padding(0, 0, 0, 6);
        _btnOpen.Margin = new Padding(0);
        _btnOpen.MinimumSize = new Size(140, 30);
        ripFlow.Controls.Add(_btnRip);
        ripFlow.Controls.Add(_btnOpen);
        btl.Controls.Add(ripFlow, 2, 0); btl.SetRowSpan(ripFlow, 3);

        var progTl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0), Tag = "surface" };
        progTl.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        progTl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        progTl.Controls.Add(_progress, 0, 0);
        progTl.Controls.Add(_status, 0, 1);
        btl.Controls.Add(progTl, 0, 2); btl.SetColumnSpan(progTl, 2);
        bottom.Controls.Add(btl);
        root.Controls.Add(bottom, 0, 3);

        // ---- log
        var logCard = new Card { Dock = DockStyle.Fill, Padding = new Padding(10, 8, 6, 8), Margin = new Padding(0) };
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
        _artistPrev = "";

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
        g.ColumnHeadersHeight = 32;
        g.RowTemplate.Height = 30;
        g.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;

        g.Columns.Add(new DataGridViewCheckBoxColumn { Name = "sel", HeaderText = "", Width = 34, FlatStyle = FlatStyle.Flat });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "num", HeaderText = "#", Width = 44, ReadOnly = true });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "title", HeaderText = "Titolo", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 55 });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "artist", HeaderText = "Artista", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 30 });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "dur", HeaderText = "Durata", Width = 70, ReadOnly = true });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "status", HeaderText = "Stato", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 45, ReadOnly = true });
        g.Columns["num"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        g.Columns["dur"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        foreach (DataGridViewColumn c in g.Columns) c.SortMode = DataGridViewColumnSortMode.NotSortable;

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

    string ThemeLabel() => _s.Theme switch { ThemeMode.Chiaro => "☀ Chiaro", ThemeMode.Scuro => "☾ Scuro", _ => "◐ Sistema" };

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
