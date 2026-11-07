using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using KaraokeToMp4.Health;

namespace KaraokeSongChecker;

/// <summary>
/// Scans karaoke folders and sorts every song into "No issues", "Bad but repairable" and "Cannot repair".
/// Repairable songs can be fixed in place (a .bak copy of the original is kept by default).
/// </summary>
internal sealed class MainForm : Form
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KaraokeTools", "song-checker.json");

    private static readonly (SongHealthStatus Status, string Title)[] Lists =
    {
        (SongHealthStatus.NoIssues, "No issues detected"),
        (SongHealthStatus.CannotVerify, "Can't verify"),
        (SongHealthStatus.Repairable, "Bad but repairable"),
        (SongHealthStatus.CannotRepair, "Cannot repair"),
        (SongHealthStatus.OnlyMusic, "Only music"),
        (SongHealthStatus.OnlyLyrics, "Only lyrics"),
    };

    private readonly ListBox _folderList = new()
    {
        Dock = DockStyle.Fill,
        IntegralHeight = false,
        SelectionMode = SelectionMode.MultiExtended,
        HorizontalScrollbar = true,
    };
    private readonly Button _addFolder = Btn("Add Folder...");
    private readonly Button _removeFolder = Btn("Remove");
    private readonly CheckBox _subfolders = new() { Text = "Include subfolders", Checked = true, AutoSize = true };
    private readonly CheckBox _keepBackups = new()
    {
        Text = "Keep a backup of each file before fixing it (.bak)",
        Checked = true,
        AutoSize = true,
    };

    private readonly Button _scan = Btn("Scan");
    private readonly Button _cancel = Btn("Cancel");
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Margin = new Padding(6, 8, 6, 8) };
    private readonly Label _status = new() { AutoSize = true, Anchor = AnchorStyles.Left, Text = "Add your karaoke folders, then press Scan." };

    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<SongHealthStatus, ListView> _views = new();
    private readonly Dictionary<SongHealthStatus, TabPage> _pages = new();
    private readonly TextBox _details = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        BorderStyle = BorderStyle.FixedSingle,
    };

    private readonly Button _fixChecked = Btn("Fix Checked");
    private readonly Button _fixAll = Btn("Fix All Repairable");
    private readonly Button _export = Btn("Export Report...");
    private readonly Button _organize = Btn("Organize Files...");

    private CancellationTokenSource? _cts;
    private readonly ConcurrentQueue<SongHealth> _incoming = new();
    private readonly System.Windows.Forms.Timer _drainTimer = new() { Interval = 500 };
    private readonly Dictionary<SongHealthStatus, ItemSorter> _sorters = new();

    /// <summary>
    /// Items whose songs were moved (Organize) or modified (Fix). They stay listed, greyed out, but are
    /// skipped by Fix and Organize until the next scan.
    /// </summary>
    private readonly HashSet<ListViewItem> _handled = new();
    private int _found;
    private int _checked;

    public MainForm()
    {
        Text = "Karaoke Song Checker";
        Width = 1100;
        Height = 760;
        MinimumSize = new Size(800, 560);
        StartPosition = FormStartPosition.CenterScreen;

        // ---- folders ----
        var folderButtons = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill };
        folderButtons.Controls.AddRange(new Control[] { _addFolder, _removeFolder });
        var folderOptions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        folderOptions.Controls.AddRange(new Control[] { _subfolders, _keepBackups });

        var folderLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        folderLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folderLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        folderLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        folderLayout.Controls.Add(_folderList, 0, 0);
        folderLayout.Controls.Add(folderButtons, 1, 0);
        folderLayout.Controls.Add(folderOptions, 0, 1);
        folderLayout.SetColumnSpan(folderOptions, 2);
        var folderGroup = new GroupBox { Text = "Karaoke folders", Dock = DockStyle.Fill, Padding = new Padding(8) };
        folderGroup.Controls.Add(folderLayout);

        // ---- scan row ----
        var scanRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, AutoSize = true };
        scanRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        scanRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        scanRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        scanRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scanRow.Controls.Add(_scan, 0, 0);
        scanRow.Controls.Add(_cancel, 1, 0);
        scanRow.Controls.Add(_progress, 2, 0);
        scanRow.Controls.Add(_status, 3, 0);

        // ---- result lists ----
        foreach (var (status, title) in Lists)
        {
            var view = CreateListView(checkBoxes: status == SongHealthStatus.Repairable);
            _views[status] = view;
            _sorters[status] = (ItemSorter)view.ListViewItemSorter!;
            var page = new TabPage(title) { Padding = new Padding(4) };
            page.Controls.Add(view);
            _pages[status] = page;
            _tabs.TabPages.Add(page);
        }

        var fixRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        fixRow.Controls.AddRange(new Control[] { _fixChecked, _fixAll, _export, _organize });

        // ---- main layout ----
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(folderGroup, 0, 0);
        layout.Controls.Add(scanRow, 0, 1);
        layout.Controls.Add(_tabs, 0, 2);
        layout.Controls.Add(_details, 0, 3);
        layout.Controls.Add(fixRow, 0, 4);
        Controls.Add(layout);

        // ---- events ----
        _addFolder.Click += (_, _) => AddFolder();
        _removeFolder.Click += (_, _) => RemoveSelectedFolders();
        _scan.Click += async (_, _) => await ScanAsync();
        _cancel.Click += (_, _) => _cts?.Cancel();
        _fixChecked.Click += async (_, _) => await FixAsync(onlyChecked: true);
        _fixAll.Click += async (_, _) => await FixAsync(onlyChecked: false);
        _export.Click += (_, _) => ExportReport();
        _organize.Click += (_, _) => Organize();
        _drainTimer.Tick += (_, _) => DrainResults();
        _folderList.AllowDrop = true;
        _folderList.DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        _folderList.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths)
                foreach (var p in paths.Where(Directory.Exists)) AddFolderPath(p);
        };
        FormClosing += (_, e) =>
        {
            if (_cts != null)
            {
                if (MessageBox.Show(this, "A scan or repair is running. Stop it and close?", Text,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                _cts.Cancel();
            }
            SaveSettings();
        };

        LoadSettings();
        UpdateTabTitles();
        SetBusy(false);
    }

    private static Button Btn(string text) => new() { Text = text, AutoSize = true, MinimumSize = new Size(90, 28) };

    // ---------------- folders ----------------

    private void AddFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose a folder that contains karaoke songs", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) AddFolderPath(dialog.SelectedPath);
    }

    private void AddFolderPath(string path)
    {
        if (!_folderList.Items.Cast<string>().Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
            _folderList.Items.Add(path);
        SetBusy(false);
    }

    private void RemoveSelectedFolders()
    {
        foreach (var item in _folderList.SelectedItems.Cast<object>().ToList()) _folderList.Items.Remove(item);
        SetBusy(false);
    }

    // ---------------- scanning ----------------

    private async Task ScanAsync()
    {
        var folders = _folderList.Items.Cast<string>().ToList();
        if (folders.Count == 0)
        {
            SetStatus("Add at least one folder first.");
            return;
        }

        foreach (var view in _views.Values)
        {
            // A list with a sorter re-sorts (and repaints) on every added item; sort once at the end instead.
            view.ListViewItemSorter = null;
            view.Items.Clear();  // the only place results are cleared: a new scan (or restarting the program)
        }
        _details.Clear();
        _handled.Clear();
        _found = 0;
        _checked = 0;
        UpdateTabTitles();

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        bool subfolders = _subfolders.Checked;
        SetBusy(true);
        SetStatus("Looking for songs...");
        _progress.Style = ProgressBarStyle.Marquee;
        _drainTimer.Start();
        var stopwatch = Stopwatch.StartNew();
        bool cancelled = false;

        try
        {
            await Task.Run(() =>
            {
                var songs = SongHealthChecker.FindSongs(folders, subfolders).ToList();
                token.ThrowIfCancellationRequested();
                _found = songs.Count;
                var options = new ParallelOptions
                {
                    CancellationToken = token,
                    MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 4),
                };
                Parallel.ForEach(songs, options, path =>
                {
                    SongHealth result;
                    try
                    {
                        result = SongHealthChecker.Check(path);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Belt and braces: one unreadable file must never stop the whole scan.
                        result = new SongHealth(path, Path.GetExtension(path), SongHealthStatus.CannotRepair,
                            "Could not check the file: " + ex.Message, Array.Empty<string>());
                    }
                    _incoming.Enqueue(result);
                    Interlocked.Increment(ref _checked);
                });
            }, token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Scan failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _drainTimer.Stop();
            DrainResults();
            foreach (var (status, view) in _views)
            {
                view.BeginUpdate();
                view.ListViewItemSorter = _sorters[status]; // assigning the sorter sorts the list once
                view.EndUpdate();
            }
            _cts.Dispose();
            _cts = null;
            _progress.Style = ProgressBarStyle.Continuous;
            SetBusy(false);
        }

        int bad = _views[SongHealthStatus.Repairable].Items.Count;
        int dead = _views[SongHealthStatus.CannotRepair].Items.Count;
        int music = _views[SongHealthStatus.OnlyMusic].Items.Count;
        int lyrics = _views[SongHealthStatus.OnlyLyrics].Items.Count;
        int unverified = _views[SongHealthStatus.CannotVerify].Items.Count;
        SetStatus((cancelled ? "Scan cancelled. " : "Scan finished. ") +
                  $"{_checked:N0} songs checked in {stopwatch.Elapsed:m\\:ss}: " +
                  $"{_views[SongHealthStatus.NoIssues].Items.Count:N0} OK, {unverified:N0} can't be verified, {bad:N0} repairable, {dead:N0} cannot be repaired, " +
                  $"{music:N0} only music, {lyrics:N0} only lyrics.");
        if (bad > 0) _tabs.SelectedTab = _pages[SongHealthStatus.Repairable];
        else if (dead > 0) _tabs.SelectedTab = _pages[SongHealthStatus.CannotRepair];
    }

    /// <summary>Moves finished results from the worker threads into the lists (on the UI thread).</summary>
    private void DrainResults()
    {
        if (_incoming.IsEmpty && _cts == null) return;

        // Add each batch in one go, and only to the lists that actually got new songs: touching a
        // list (BeginUpdate/EndUpdate) repaints it, which made every tab flicker on every tick.
        var batches = new Dictionary<SongHealthStatus, List<ListViewItem>>();
        while (_incoming.TryDequeue(out var result))
        {
            if (!batches.TryGetValue(result.Status, out var batch)) batches[result.Status] = batch = new List<ListViewItem>();
            batch.Add(CreateItem(result));
        }
        foreach (var (status, items) in batches)
        {
            var view = _views[status];
            view.BeginUpdate();
            view.Items.AddRange(items.ToArray());
            view.EndUpdate();
            view.Items[^1].EnsureVisible(); // follow the scan: keep the newest song in view
        }

        if (_found > 0)
        {
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Maximum = _found;
            _progress.Value = Math.Min(_checked, _found);
            SetStatus($"Checked {_checked:N0} of {_found:N0} songs...");
        }
        UpdateTabTitles();
    }

    private static ListViewItem CreateItem(SongHealth result) =>
        new(new[] { result.FileName, result.Kind, result.Summary, result.Folder })
        {
            Tag = result,
            Checked = result.Status == SongHealthStatus.Repairable,
            ToolTipText = result.Path,
        };

    /// <summary>Greys out an item whose song was moved or modified, and shows what happened.</summary>
    private void MarkHandled(ListViewItem item, string outcome)
    {
        item.SubItems[2].Text = outcome;
        item.ForeColor = SystemColors.GrayText;
        item.Checked = false;
        _handled.Add(item);
        if (item.ListView?.SelectedItems.Contains(item) == true) ShowDetails(item.ListView);
    }

    // ---------------- repairing ----------------

    private async Task FixAsync(bool onlyChecked)
    {
        var view = _views[SongHealthStatus.Repairable];
        var items = view.Items.Cast<ListViewItem>().Where(i => !_handled.Contains(i) && (!onlyChecked || i.Checked)).ToList();
        if (items.Count == 0)
        {
            SetStatus(onlyChecked ? "Tick the songs you want to fix first." : "There is nothing to fix.");
            return;
        }

        bool keepBackups = _keepBackups.Checked;
        var question = $"Fix {items.Count:N0} song{(items.Count == 1 ? "" : "s")}?\n\n" +
                       (keepBackups
                           ? "Each original file is kept next to it as a .bak copy."
                           : "Backups are turned off: the original files will be overwritten.");
        if (MessageBox.Show(this, question, "Fix songs", MessageBoxButtons.YesNo,
                keepBackups ? MessageBoxIcon.Question : MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        _cts = new CancellationTokenSource();
        SetBusy(true);
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Maximum = items.Count;
        _progress.Value = 0;
        int repaired = 0, failed = 0;

        try
        {
            foreach (var item in items)
            {
                if (_cts.IsCancellationRequested) break;
                var before = (SongHealth)item.Tag!;
                SetStatus($"Fixing {before.FileName}...");

                // The song stays in this list (only Scan clears the lists). Once its files have been changed it
                // is greyed out and shows the outcome.
                try
                {
                    // Usually one result; a zip holding several songs is split into one zip per song.
                    var after = await Task.Run(() => SongHealthChecker.RepairAll(before.Path, keepBackups));
                    string outcome;
                    if (after.Count > 1)
                    {
                        outcome = $"Split into {after.Count} files: " + string.Join(", ", after.Select(r => r.FileName)) +
                                  (after.All(r => r.Status == SongHealthStatus.NoIssues) ? "" :
                                      " (" + string.Join("; ", after.Where(r => r.Status != SongHealthStatus.NoIssues).Select(r => $"{r.FileName}: {r.Summary}")) + ")");
                    }
                    else
                    {
                        outcome = after[0].Status == SongHealthStatus.NoIssues
                            ? "Repaired - " + after[0].Summary
                            : $"Repair attempted, now: {after[0].Summary}";
                        item.Tag = after[0];
                    }
                    if (after.All(r => r.Status != SongHealthStatus.Repairable)) repaired++; else failed++;
                    MarkHandled(item, outcome);
                }
                catch (Exception ex)
                {
                    failed++;
                    item.SubItems[2].Text = "Repair failed (nothing changed): " + ex.Message;
                    item.Checked = false;
                }
                _progress.Value++;
            }
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }

        SetStatus($"Repaired {repaired:N0} song{(repaired == 1 ? "" : "s")}" +
                  (failed > 0 ? $"; {failed:N0} could not be repaired (see the lists)" : "") + ".");
    }

    // ---------------- organizing ----------------

    private void Organize()
    {
        // Songs moved by an earlier Organize stay in the lists (greyed out) but aren't offered again.
        var songs = Lists.ToDictionary(
            l => l.Status,
            l => (IReadOnlyList<SongHealth>)_views[l.Status].Items.Cast<ListViewItem>()
                .Where(i => !_handled.Contains(i))
                .Select(i => (SongHealth)i.Tag!).ToList());
        if (songs.Values.All(l => l.Count == 0))
        {
            SetStatus("Scan first: there are no songs to organize.");
            return;
        }

        // Suggest the scanned folder as the source root when there is exactly one.
        var folders = _folderList.Items.Cast<string>().ToList();
        using var dialog = new OrganizeForm(songs, folders.Count == 1 ? folders[0] : null);
        dialog.ShowDialog(this);

        if (dialog.MovedSongs.Count > 0)
        {
            // Keep moved songs in their lists (only Scan clears the lists), marked with where they went.
            foreach (var view in _views.Values)
            {
                view.BeginUpdate();
                foreach (ListViewItem item in view.Items)
                {
                    var song = (SongHealth)item.Tag!;
                    if (!dialog.MovedSongs.TryGetValue(song.Path, out var newPath)) continue;
                    var moved = song with { Path = newPath };
                    item.Tag = moved;
                    item.SubItems[3].Text = moved.Folder;
                    item.ToolTipText = newPath;
                    MarkHandled(item, "Moved to " + moved.Folder + " - " + song.Summary);
                }
                view.EndUpdate();
            }
        }
        foreach (var view in _views.Values)
        {
            foreach (ListViewItem item in view.Items)
            {
                // Copies leave the original untouched, so the song stays active; just note where the copy went.
                var song = (SongHealth)item.Tag!;
                if (dialog.CopiedSongs.TryGetValue(song.Path, out var copy) && !item.SubItems[2].Text.StartsWith("Copied to"))
                    item.SubItems[2].Text = $"Copied to {Path.GetDirectoryName(copy)} - {item.SubItems[2].Text}";
            }
        }
        if (dialog.Summary != null) SetStatus(dialog.Summary);
    }

    // ---------------- report ----------------

    private void ExportReport()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Save scan report",
            Filter = "CSV file (*.csv)|*.csv",
            FileName = $"karaoke-scan-{DateTime.Now:yyyy-MM-dd}.csv",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var sb = new StringBuilder();
        sb.AppendLine("Status,Type,File,Folder,Problem,Details");
        foreach (var (status, title) in Lists)
        {
            foreach (ListViewItem item in _views[status].Items)
            {
                var r = (SongHealth)item.Tag!;
                sb.AppendLine(string.Join(",", Csv(title), Csv(r.Kind), Csv(r.FileName), Csv(r.Folder), Csv(r.Summary),
                    Csv(string.Join(" | ", r.Details))));
            }
        }
        File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        SetStatus("Report saved: " + dialog.FileName);
    }

    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    // ---------------- list views ----------------

    private ListView CreateListView(bool checkBoxes)
    {
        var view = new BufferedListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            CheckBoxes = checkBoxes,
            ShowItemToolTips = true,
            HideSelection = false,
        };
        view.Columns.Add("Song", 320);
        view.Columns.Add("Type", 85);
        view.Columns.Add("Problem", 380);
        view.Columns.Add("Folder", 300);
        var sorter = new ItemSorter();
        view.ListViewItemSorter = sorter;
        view.ColumnClick += (_, e) =>
        {
            sorter.Toggle(e.Column);
            if (view.ListViewItemSorter != null) view.Sort(); // during a scan the order is applied at the end
        };
        view.SelectedIndexChanged += (_, _) => ShowDetails(view);
        view.DoubleClick += (_, _) => OpenInExplorer(view);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show in Explorer", null, (_, _) => OpenInExplorer(view));
        menu.Items.Add("Copy path", null, (_, _) =>
        {
            if (view.SelectedItems.Count > 0) Clipboard.SetText(((SongHealth)view.SelectedItems[0].Tag!).Path);
        });
        if (checkBoxes)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Tick all", null, (_, _) => { foreach (ListViewItem i in view.Items) i.Checked = true; });
            menu.Items.Add("Untick all", null, (_, _) => { foreach (ListViewItem i in view.Items) i.Checked = false; });
        }
        view.ContextMenuStrip = menu;
        return view;
    }

    private void ShowDetails(ListView view)
    {
        if (view.SelectedItems.Count == 0) return;
        var r = (SongHealth)view.SelectedItems[0].Tag!;
        _details.Text = string.Join(Environment.NewLine,
            new[] { r.Path, r.Summary }.Concat(r.Details));
    }

    private static void OpenInExplorer(ListView view)
    {
        if (view.SelectedItems.Count == 0) return;
        var path = ((SongHealth)view.SelectedItems[0].Tag!).Path;
        if (File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    private void UpdateTabTitles()
    {
        foreach (var (status, title) in Lists)
        {
            var text = $"{title} ({_views[status].Items.Count:N0})";
            if (_pages[status].Text != text) _pages[status].Text = text; // setting it redraws the tab strip
        }
    }

    // ---------------- state ----------------

    private void SetBusy(bool busy)
    {
        bool hasFolders = _folderList.Items.Count > 0;
        _scan.Enabled = !busy && hasFolders;
        _cancel.Enabled = busy;
        _addFolder.Enabled = !busy;
        _removeFolder.Enabled = !busy && hasFolders;
        _subfolders.Enabled = !busy;
        _keepBackups.Enabled = !busy;
        _fixChecked.Enabled = !busy;
        _fixAll.Enabled = !busy;
        _export.Enabled = !busy;
        _organize.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private void SetStatus(string text) => _status.Text = text;

    private sealed record Settings(List<string> Folders, bool IncludeSubfolders, bool KeepBackups);

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
            if (s == null) return;
            foreach (var f in s.Folders) _folderList.Items.Add(f);
            _subfolders.Checked = s.IncludeSubfolders;
            _keepBackups.Checked = s.KeepBackups;
        }
        catch
        {
            // A damaged settings file just means starting with an empty folder list.
        }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var s = new Settings(_folderList.Items.Cast<string>().ToList(), _subfolders.Checked, _keepBackups.Checked);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Not being able to remember folders isn't worth interrupting the user for.
        }
    }

    /// <summary>Sorts a list by the clicked column; clicking again reverses the order.</summary>
    private sealed class ItemSorter : System.Collections.IComparer
    {
        private int _column;
        private bool _descending;

        public void Toggle(int column)
        {
            _descending = column == _column && !_descending;
            _column = column;
        }

        public int Compare(object? x, object? y)
        {
            var a = (x as ListViewItem)?.SubItems[_column].Text ?? "";
            var b = (y as ListViewItem)?.SubItems[_column].Text ?? "";
            int result = string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
            return _descending ? -result : result;
        }
    }
}
