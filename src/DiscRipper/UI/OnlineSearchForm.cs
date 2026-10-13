using DiscRipper.Core;

namespace DiscRipper.UI;

/// <summary>
/// Ricerca per nome: artista e album scritti a mano, cercati in contemporanea su più portali.
/// I risultati arrivano man mano, ordinati per quanto corrispondono al CD (numero di tracce e durate).
/// A destra le tracce del risultato scelto messe accanto a quelle del CD.
/// </summary>
public sealed class OnlineSearchForm : Form
{
    readonly HttpClient _http;
    readonly AppSettings _s;
    readonly Toc _toc;

    readonly FieldBox _artist = new();
    readonly FieldBox _album = new();
    readonly FlatButton _btnSearch = new() { Text = "Cerca", Icon = Glyph.Search, Accent = true };
    readonly Dictionary<string, ThemedCheckBox> _portalChecks = new();
    readonly Label _keysNote = new() { AutoSize = false, Dock = DockStyle.Fill, Tag = "dim", Font = Theme.Small, Margin = Ui.P(0) };
    readonly DataGridView _results = new() { Dock = DockStyle.Fill };
    readonly DataGridView _tracks = new() { Dock = DockStyle.Fill };
    readonly Label _tracksTitle = new() { AutoSize = true, Font = Theme.Bold, Text = "Tracce", Anchor = AnchorStyles.Left, Margin = Ui.P(0) };
    readonly Label _status = new() { AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, Tag = "dim", TextAlign = ContentAlignment.MiddleLeft, Margin = Ui.P(0) };
    readonly FlatButton _ok = new() { Text = "Usa questo", Accent = true, Width = Ui.S(150) };
    readonly FlatButton _cancel = new() { Text = "Annulla", DialogResult = DialogResult.Cancel, Width = Ui.S(130) };
    readonly ToolTip _tip = new();

    readonly List<SearchHit> _hits = new();
    CancellationTokenSource? _cts;
    bool _userPicked, _filling;
    string _qArtist = "", _qAlbum = "";

    /// <summary>L'album scelto, con le tracce già numerate come quelle del CD.</summary>
    public AlbumMeta? Chosen { get; private set; }

    public OnlineSearchForm(HttpClient http, AppSettings s, Toc toc, string artist, string album, bool notFound)
    {
        _http = http; _s = s; _toc = toc;
        Text = "Cerca il disco online per nome";
        Font = Theme.Base;
        AutoScaleMode = AutoScaleMode.None; // misure già scalate a mano (Ui.S)
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true; MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        ClientSize = new Size(Math.Min(Ui.S(1180), wa.Width - 40), Math.Min(Ui.S(760), wa.Height - 60));
        MinimumSize = new Size(Math.Min(Ui.S(900), wa.Width - 20), Math.Min(Ui.S(600), wa.Height - 20));

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = Ui.P(18, 14, 18, 12) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.S(180)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.S(54)));
        Controls.Add(root);

        // ---- cosa cercare e dove
        const int labelW = 70;
        var top = new Card { Dock = DockStyle.Fill, Margin = Ui.P(0, 0, 0, 12), Padding = Ui.P(16, 10, 16, 8) };
        var topTl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Tag = "surface", Margin = new Padding(0) };
        topTl.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.S(26)));
        topTl.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.S(Ui.RowH)));
        topTl.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.S(36)));
        topTl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var intro = new Label
        {
            AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, Margin = Ui.P(0), Tag = notFound ? null : "dim",
            Text = notFound
                ? "Il CD non è nei database dei dischi (succede con le copie masterizzate): scrivi artista e titolo e scegli l'album giusto."
                : "Scrivi artista e titolo dell'album: i risultati sono ordinati per quanto corrispondono al CD (numero di tracce e durate)."
        };
        topTl.Controls.Add(intro, 0, 0);

        _artist.Text = artist; _album.Text = album;
        topTl.Controls.Add(Ui.Row(Ui.RowH,
            (Ui.Caption("Artista"), SizeType.Absolute, labelW),
            (_artist, SizeType.Percent, 42),
            (Pad(Ui.Caption("Album", 0), 16, 10), SizeType.AutoSize, 0),
            (_album, SizeType.Percent, 58),
            (Spacer(10), SizeType.AutoSize, 0),
            (_btnSearch, SizeType.AutoSize, 0)), 0, 1);

        var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Tag = "surface", Anchor = AnchorStyles.Left };
        foreach (var p in NameSearchPortals.All(_http, _s))
        {
            var cb = new ThemedCheckBox { Text = p.Name, Checked = p.Available, Enabled = p.Available, Margin = Ui.P(0, 0, 18, 0) };
            _tip.SetToolTip(cb, $"{p.Name}: {p.Hint}");
            _portalChecks[p.Name] = cb;
            flow.Controls.Add(cb);
        }
        topTl.Controls.Add(Ui.Row(36, (Ui.Caption("Dove"), SizeType.Absolute, labelW), (flow, SizeType.Percent, 100)), 0, 2);

        bool noToken = string.IsNullOrWhiteSpace(_s.DiscogsToken), noKey = string.IsNullOrWhiteSpace(_s.LastFmApiKey);
        _keysNote.Text = noKey || noToken
            ? "MusicBrainz, Deezer, Apple Music e Discogs sono gratis e senza account. "
              + (noKey ? "Last.fm si attiva con una API key gratuita" : "")
              + (noKey && noToken ? "; " : "")
              + (noToken ? "il token Discogs (facoltativo) permette più ricerche al minuto" : "")
              + ": si inseriscono in Impostazioni → Riconoscimento del disco."
            : "Tutti i portali attivi (token Discogs e API key Last.fm impostati).";
        topTl.Controls.Add(Ui.Row(36, (Spacer(labelW), SizeType.Absolute, labelW), (_keysNote, SizeType.Percent, 100)), 0, 3);
        top.Controls.Add(topTl);
        root.Controls.Add(top, 0, 0);

        // ---- risultati | tracce
        var mid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Ui.P(0, 0, 0, 10) };
        mid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        mid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        mid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        mid.Controls.Add(GridCard("Album trovati", null, _results, Ui.P(0, 0, 6, 0)), 0, 0);
        mid.Controls.Add(GridCard(null, _tracksTitle, _tracks, Ui.P(6, 0, 0, 0)), 1, 0);
        root.Controls.Add(mid, 0, 1);
        SetupResultsGrid();
        SetupTracksGrid();

        // ---- stato e pulsanti
        var bottom = Ui.Row(54,
            (_status, SizeType.Percent, 100),
            (Spacer(12), SizeType.AutoSize, 0),
            (_cancel, SizeType.AutoSize, 0),
            (Spacer(10), SizeType.AutoSize, 0),
            (_ok, SizeType.AutoSize, 0));
        bottom.Tag = null;
        root.Controls.Add(bottom, 0, 2);
        _status.Text = "Scrivi almeno l'artista o il titolo dell'album e premi Cerca.";

        AcceptButton = _btnSearch;   // Invio nei campi = cerca
        CancelButton = _cancel;
        _btnSearch.Click += async (_, _) => await RunSearch();
        _ok.Click += (_, _) => Accept();
        _results.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Accept(); };
        _results.SelectionChanged += (_, _) => { if (!_filling) { _userPicked = _results.Focused || _userPicked; ShowTracks(); UpdateOk(); } };
        _results.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && Current() != null) { e.Handled = true; Accept(); } };
        FormClosing += (_, _) => _cts?.Cancel();
        Shown += async (_, _) =>
        {
            Theme.Apply(this); // ora gli handle esistono: scrollbar scure
            if (_artist.Text.Trim().Length > 0 && _album.Text.Trim().Length > 0) await RunSearch();
            else (_artist.Text.Trim().Length == 0 ? _artist : _album).Box.Focus();
        };
        UpdateOk();
        ShowTracks();
        Theme.Apply(this);
    }

    static Control Spacer(int w) => new Panel { Width = Ui.S(w), Height = 1, Margin = new Padding(0) };

    static Control Pad(Control c, int left, int right)
    {
        c.Margin = new Padding(Ui.S(left), 0, Ui.S(right), 0);
        return c;
    }

    static Card GridCard(string? title, Label? titleLabel, DataGridView grid, Padding margin)
    {
        var card = new Card { Dock = DockStyle.Fill, Margin = margin, Padding = Ui.P(12, 8, 12, 12) };
        var tl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Tag = "surface", Margin = new Padding(0) };
        tl.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.S(32)));
        tl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var lbl = titleLabel ?? new Label { Text = title, AutoSize = true, Font = Theme.Bold, Anchor = AnchorStyles.Left, Margin = Ui.P(0) };
        tl.Controls.Add(lbl, 0, 0);
        var holder = new Panel { Dock = DockStyle.Fill, Tag = "surface", Margin = Ui.P(0, 2, 0, 0) };
        holder.Controls.Add(grid);
        tl.Controls.Add(holder, 0, 1);
        card.Controls.Add(tl);
        return card;
    }

    // ------------------------------------------------------------------ tabelle

    static void BaseGrid(DataGridView g)
    {
        g.AllowUserToAddRows = false;
        g.AllowUserToDeleteRows = false;
        g.AllowUserToResizeRows = false;
        g.ReadOnly = true;
        g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        g.MultiSelect = false;
        g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        g.ColumnHeadersHeight = Ui.S(32);
        g.RowTemplate.Height = Ui.S(30);
        g.StandardTab = true;
        // intestazioni disegnate a mano come nella tabella delle tracce: niente righine native di Windows
        g.CellPainting += (_, e) =>
        {
            if (e.Graphics == null || e.RowIndex >= 0 || e.ColumnIndex < 0) return;
            var p = Theme.P;
            using (var bg = new SolidBrush(p.Surface)) e.Graphics.FillRectangle(bg, e.CellBounds);
            using (var line = new Pen(p.Border)) e.Graphics.DrawLine(line, e.CellBounds.Left, e.CellBounds.Bottom - 1, e.CellBounds.Right, e.CellBounds.Bottom - 1);
            var col = g.Columns[e.ColumnIndex];
            var align = col.DefaultCellStyle.Alignment == DataGridViewContentAlignment.MiddleRight ? TextFormatFlags.Right : TextFormatFlags.Left;
            TextRenderer.DrawText(e.Graphics, col.HeaderText, Theme.Bold, Rectangle.Inflate(e.CellBounds, -Ui.S(6), 0), p.TextDim,
                align | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            e.Handled = true;
        };
    }

    void SetupResultsGrid()
    {
        var g = _results;
        BaseGrid(g);
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "src", HeaderText = "Fonte", Width = Ui.S(96) });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "artist", HeaderText = "Artista", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 32 });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "album", HeaderText = "Album", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 44 });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "year", HeaderText = "Anno", Width = Ui.S(52) });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "score", HeaderText = "Affinità", Width = Ui.S(72) });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "match", HeaderText = "Rispetto al CD", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 60 });
        g.Columns["score"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        foreach (DataGridViewColumn c in g.Columns) c.SortMode = DataGridViewColumnSortMode.NotSortable;
        g.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _hits.Count || e.CellStyle == null) return;
            string name = g.Columns[e.ColumnIndex].Name;
            if (name is not ("score" or "match")) return;
            var c = ScoreColor(_hits[e.RowIndex].Score);
            e.CellStyle.ForeColor = c; e.CellStyle.SelectionForeColor = c;
            if (name == "score") e.CellStyle.Font = Theme.Bold;
        };
        g.CellToolTipTextNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _hits.Count) return;
            var h = _hits[e.RowIndex];
            e.ToolTipText = $"{h.Meta.Display}\n{h.Match}";
        };
    }

    void SetupTracksGrid()
    {
        var g = _tracks;
        BaseGrid(g);
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "num", HeaderText = "#", Width = Ui.S(38) });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "title", HeaderText = "Titolo", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 100 });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "len", HeaderText = "Album", Width = Ui.S(58) });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "cd", HeaderText = "CD", Width = Ui.S(58) });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "chk", HeaderText = "", Width = Ui.S(64) });
        g.Columns["num"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        g.Columns["len"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        g.Columns["cd"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        foreach (DataGridViewColumn c in g.Columns) c.SortMode = DataGridViewColumnSortMode.NotSortable;
        g.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.CellStyle == null) return;
            var kind = g.Rows[e.RowIndex].Tag as string;
            Color? c = kind switch
            {
                "ok" when g.Columns[e.ColumnIndex].Name == "chk" => Theme.P.Success,
                "diff" when g.Columns[e.ColumnIndex].Name == "chk" => Theme.P.Warning,
                "unused" => Theme.P.TextDim,
                _ => null
            };
            if (c != null) { e.CellStyle.ForeColor = c.Value; e.CellStyle.SelectionForeColor = c.Value; }
        };
    }

    static Color ScoreColor(int score) => score >= 85 ? Theme.P.Success : score >= 55 ? Theme.P.Info : Theme.P.Warning;

    static string Fmt(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    SearchHit? Current() =>
        _results.CurrentRow is { Index: >= 0 } r && r.Index < _hits.Count && _results.SelectedRows.Count > 0 ? _hits[r.Index] : null;

    void FillResults()
    {
        var keep = Current();
        _filling = true;
        try
        {
            _hits.Sort((a, b) => b.Rank.CompareTo(a.Rank));
            _results.Rows.Clear();
            foreach (var h in _hits)
            {
                var m = h.Meta;
                string extra = m.DiscTotal > 1 ? $"  (CD {m.DiscNumber}/{m.DiscTotal})" : "";
                _results.Rows.Add(m.Source, m.Artist, m.Album + extra, m.Year, $"{h.Score}%", h.Match);
            }
            // tengo selezionato quello scelto dall'utente; altrimenti il migliore
            int idx = _userPicked && keep != null ? _hits.IndexOf(keep) : 0;
            if (idx >= 0 && idx < _results.Rows.Count)
            {
                _results.CurrentCell = _results.Rows[idx].Cells[1];
                _results.Rows[idx].Selected = true;
            }
        }
        finally { _filling = false; }
        ShowTracks();
        UpdateOk();
    }

    void ShowTracks()
    {
        _tracks.Rows.Clear();
        var h = Current();
        if (h == null) { _tracksTitle.Text = "Tracce: scegli un album a sinistra"; return; }
        var cd = NameMatcher.CdLengthsMs(_toc);
        var audio = _toc.AudioTracks;
        _tracksTitle.Text = $"Tracce: {h.Meta.Artist} — {h.Meta.Album}";
        for (int i = 0; i < audio.Count; i++)
        {
            int j = h.Offset + i;
            var rt = j < h.DiscTracks.Count ? h.DiscTracks[j] : null;
            string len = rt?.LengthMs is > 0 ? Fmt(rt.LengthMs.Value) : "—";
            string chk; string tag;
            if (rt == null) { chk = "manca"; tag = "diff"; }
            else if (rt.LengthMs is not > 0) { chk = ""; tag = ""; }
            else
            {
                long d = rt.LengthMs.Value - cd[i];
                if (Math.Abs(d) <= NameMatcher.ToleranceMs) { chk = "✓ uguale"; tag = "ok"; }
                else { chk = $"{(d > 0 ? "+" : "−")}{Math.Abs(d) / 1000} s"; tag = "diff"; }
            }
            int r = _tracks.Rows.Add(audio[i].Number.ToString("D2"), h.Meta.Tracks[i].Title, len, Fmt(cd[i]), chk);
            _tracks.Rows[r].Tag = tag;
        }
        // tracce del risultato che non finiscono sul CD (intro, bonus track…)
        for (int j = 0; j < h.DiscTracks.Count; j++)
        {
            if (j >= h.Offset && j < h.Offset + audio.Count) continue;
            var rt = h.DiscTracks[j];
            int r = _tracks.Rows.Add("", rt.Title, rt.LengthMs is > 0 ? Fmt(rt.LengthMs.Value) : "—", "", "non sul CD");
            _tracks.Rows[r].Tag = "unused";
        }
    }

    void UpdateOk() => _ok.Enabled = Current() != null;

    void Accept()
    {
        var h = Current();
        if (h == null) return;
        _cts?.Cancel();
        Chosen = h.Meta;
        DialogResult = DialogResult.OK;
        Close();
    }

    // ------------------------------------------------------------------ ricerca

    async Task RunSearch()
    {
        string ar = _artist.Text.Trim(), al = _album.Text.Trim();
        if (ar.Length == 0 && al.Length == 0) { _status.Text = "Scrivi almeno l'artista o il titolo dell'album."; _artist.Box.Focus(); return; }
        var portals = NameSearchPortals.All(_http, _s).Where(p => p.Available && _portalChecks.TryGetValue(p.Name, out var cb) && cb.Checked).ToList();
        if (portals.Count == 0) { _status.Text = "Spunta almeno un portale su cui cercare."; return; }

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _qArtist = ar; _qAlbum = al;
        _hits.Clear(); _userPicked = false;
        FillResults();

        var pending = new List<string>(portals.Select(p => p.Name));
        var done = new List<string>();
        var errors = new List<string>();
        void Status()
        {
            if (cts != _cts || IsDisposed) return;
            string found = _hits.Count == 0 ? "nessun album finora" : $"{_hits.Count} album trovat{(_hits.Count == 1 ? "o" : "i")}";
            string text = pending.Count > 0
                ? $"Cerco su {string.Join(", ", pending)}… · {found}"
                : _hits.Count == 0
                    ? "Nessun album trovato. Prova a scrivere solo il titolo o solo l'artista, o controlla come sono scritti."
                    : $"{_hits.Count} album trovat{(_hits.Count == 1 ? "o" : "i")} ({string.Join(" · ", done)}). Scegli quello giusto e premi «Usa questo».";
            if (errors.Count > 0) text += "   ⚠ " + string.Join("  ", errors);
            _status.Text = text;
            _tip.SetToolTip(_status, text);
            _status.ForeColor = errors.Count > 0 && pending.Count == 0 && _hits.Count == 0 ? Theme.P.Warning : Theme.P.TextDim;
        }
        Status();

        void Found(RawRelease raw)
        {
            // arriva da un thread di rete: passo alla finestra
            if (IsDisposed || !IsHandleCreated || cts.IsCancellationRequested) return;
            try
            {
                BeginInvoke(() =>
                {
                    if (cts != _cts || IsDisposed) return;
                    _hits.Add(NameMatcher.Build(raw, _toc, _qArtist, _qAlbum));
                    FillResults();
                    Status();
                });
            }
            catch (InvalidOperationException) { } // finestra chiusa nel frattempo
        }

        int cdTracks = _toc.AudioTracks.Count;
        var tasks = portals.Select(async p =>
        {
            int n = 0; string? err = null;
            try { n = await Task.Run(() => p.Create().SearchAsync(ar, al, cdTracks, Found, cts.Token), cts.Token); }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { return; }
            catch (OperationCanceledException) { err = $"{p.Name}: non risponde (tempo scaduto)."; }
            catch (PortalException ex) { err = ex.Message; }
            catch (HttpRequestException) { err = $"{p.Name}: non raggiungibile (connessione?)."; }
            catch (Exception ex) { err = $"{p.Name}: {ex.Message}"; }
            if (cts != _cts || IsDisposed) return;
            pending.Remove(p.Name);
            if (err != null) errors.Add(err);
            else done.Add($"{p.Name} {(n == 0 ? "niente" : n.ToString())}");
            Status();
        }).ToList();
        await Task.WhenAll(tasks);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.TitleBar(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _cts?.Cancel(); _tip.Dispose(); }
        base.Dispose(disposing);
    }
}
