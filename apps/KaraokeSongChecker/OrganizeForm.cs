using System.Diagnostics;
using System.Text;
using System.Text.Json;
using KaraokeToMp4.Health;

namespace KaraokeSongChecker;

/// <summary>
/// Moves (or, for repairable songs, copies) the songs of each result list to its own root folder,
/// keeping each song's path relative to the source root.
/// </summary>
internal sealed class OrganizeForm : Form
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KaraokeTools", "song-checker-organize.json");

    private static readonly string LogFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KaraokeTools", "logs");

    /// <summary>The lists in the order shown, with what happens to their songs.</summary>
    private static readonly (SongHealthStatus Status, string Title, RelocateAction Action, bool DefaultOn)[] Categories =
    {
        (SongHealthStatus.OnlyLyrics, "Only lyrics", RelocateAction.Move, true),
        (SongHealthStatus.OnlyMusic, "Only music", RelocateAction.Move, true),
        (SongHealthStatus.CannotRepair, "Cannot repair", RelocateAction.Move, true),
        (SongHealthStatus.NoIssues, "No issues detected", RelocateAction.Move, false),
        (SongHealthStatus.CannotVerify, "Can't verify", RelocateAction.Move, false),
        (SongHealthStatus.Repairable, "Bad but repairable", RelocateAction.Copy, false),
    };

    private readonly IReadOnlyDictionary<SongHealthStatus, IReadOnlyList<SongHealth>> _songs;
    private readonly TextBox _sourceRoot = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<SongHealthStatus, CheckBox> _include = new();
    private readonly Dictionary<SongHealthStatus, TextBox> _destination = new();
    private readonly CheckBox _removeEmpty = new() { Text = "Remove folders left empty under the source root", Checked = true, AutoSize = true };
    private readonly Button _start = new() { Text = "Start", AutoSize = true, MinimumSize = new Size(100, 30) };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, MinimumSize = new Size(100, 30), Enabled = false };
    private readonly Button _close = new() { Text = "Close", AutoSize = true, MinimumSize = new Size(100, 30) };
    private readonly LinkLabel _openLog = new() { Text = "Open log", AutoSize = true, Visible = false };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new() { AutoSize = true, Text = "" };
    private CancellationTokenSource? _cts;
    private string? _logPath;

    /// <summary>Songs that were moved: original path of the song's main file → its new path.</summary>
    public Dictionary<string, string> MovedSongs { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Songs that were copied: path of the song's main file → the copy's path.</summary>
    public Dictionary<string, string> CopiedSongs { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Summary of the last run, for the caller's status line.</summary>
    public string? Summary { get; private set; }

    public OrganizeForm(IReadOnlyDictionary<SongHealthStatus, IReadOnlyList<SongHealth>> songs, string? suggestedSourceRoot)
    {
        _songs = songs;
        Text = "Organize Files";
        Width = 900;
        Height = 510;
        MinimumSize = new Size(700, 510);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, AutoSize = true, Padding = new Padding(10) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var intro = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(850, 0),
            Text = "Songs keep their path relative to the source root, e.g. <source root>\\CDG\\e\\Song.zip goes to " +
                   "<destination>\\CDG\\e\\Song.zip. A song's files move together (.cdg with its audio, and .bak copies). " +
                   "Existing files are never overwritten. Repairable songs are copied; all others are moved.",
            Margin = new Padding(3, 3, 3, 12),
        };
        grid.Controls.Add(intro, 0, 0);
        grid.SetColumnSpan(intro, 4);

        grid.Controls.Add(Bold("Source root"), 0, 1);
        grid.Controls.Add(new Label { Text = "", AutoSize = true }, 1, 1);
        grid.Controls.Add(_sourceRoot, 2, 1);
        grid.Controls.Add(BrowseButton(_sourceRoot), 3, 1);

        int row = 2;
        foreach (var (status, title, action, _) in Categories)
        {
            int count = songs.TryGetValue(status, out var list) ? list.Count : 0;
            var include = new CheckBox { Text = $"{title} ({count:N0})", AutoSize = true, Anchor = AnchorStyles.Left };
            var dest = new TextBox { Dock = DockStyle.Fill };
            _include[status] = include;
            _destination[status] = dest;
            grid.Controls.Add(include, 0, row);
            grid.Controls.Add(new Label { Text = action == RelocateAction.Copy ? "copy to" : "move to", AutoSize = true, Anchor = AnchorStyles.Left }, 1, row);
            grid.Controls.Add(dest, 2, row);
            grid.Controls.Add(BrowseButton(dest), 3, row);
            row++;
        }
        grid.Controls.Add(_removeEmpty, 0, row);
        grid.SetColumnSpan(_removeEmpty, 4);
        row++;

        var progressRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        progressRow.Controls.Add(_progress, 0, 0);
        progressRow.Controls.Add(_openLog, 1, 0);
        progressRow.Controls.Add(_status, 0, 1);
        progressRow.SetColumnSpan(_status, 2);
        grid.Controls.Add(progressRow, 0, row);
        grid.SetColumnSpan(progressRow, 4);
        row++;

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.AddRange(new Control[] { _close, _cancel, _start });
        grid.Controls.Add(buttons, 0, row);
        grid.SetColumnSpan(buttons, 4);

        Controls.Add(grid);

        _start.Click += async (_, _) => await RunAsync();
        _cancel.Click += (_, _) => _cts?.Cancel();
        _close.Click += (_, _) => Close();
        _openLog.LinkClicked += (_, _) =>
        {
            if (_logPath != null && File.Exists(_logPath))
                Process.Start(new ProcessStartInfo(_logPath) { UseShellExecute = true });
        };
        FormClosing += (_, e) =>
        {
            if (_cts != null) { e.Cancel = true; return; } // finish or cancel the run first
            SaveSettings();
        };

        LoadSettings(suggestedSourceRoot);
    }

    private static Label Bold(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Font = new Font(SystemFonts.MessageBoxFont ?? DefaultFont, FontStyle.Bold),
    };

    private Button BrowseButton(TextBox target)
    {
        var button = new Button { Text = "Browse...", AutoSize = true };
        button.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { UseDescriptionForTitle = true, Description = "Choose a folder" };
            if (Directory.Exists(target.Text)) dialog.InitialDirectory = target.Text;
            if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.SelectedPath;
        };
        return button;
    }

    private async Task RunAsync()
    {
        var sourceRoot = _sourceRoot.Text.Trim();
        var jobs = new List<(SongHealthStatus Status, string Title, RelocateAction Action, string Destination, IReadOnlyList<SongHealth> Songs)>();
        foreach (var (status, title, action, _) in Categories)
        {
            if (!_include[status].Checked) continue;
            var dest = _destination[status].Text.Trim();
            var error = SongOrganizer.ValidateRoots(sourceRoot, dest);
            if (error != null)
            {
                MessageBox.Show(this, $"{title}: {error}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var list = _songs.TryGetValue(status, out var l) ? l : Array.Empty<SongHealth>();
            if (list.Count > 0) jobs.Add((status, title, action, dest, list));
        }
        if (jobs.Count == 0)
        {
            _status.Text = "Tick at least one list that has songs in it.";
            return;
        }
        if (!Directory.Exists(sourceRoot))
        {
            MessageBox.Show(this, "The source root folder doesn't exist.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var confirm = new StringBuilder("This will:\n\n");
        foreach (var job in jobs)
            confirm.AppendLine($"• {(job.Action == RelocateAction.Copy ? "Copy" : "Move")} {job.Songs.Count:N0} song{(job.Songs.Count == 1 ? "" : "s")} ({job.Title}) to\n    {job.Destination}");
        confirm.AppendLine($"\nfrom under {sourceRoot}.\n\nContinue?");
        if (MessageBox.Show(this, confirm.ToString(), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        SaveSettings();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SetBusy(true);
        int total = jobs.Sum(j => j.Songs.Count);
        _progress.Maximum = Math.Max(1, total);
        _progress.Value = 0;

        Directory.CreateDirectory(LogFolder);
        _logPath = Path.Combine(LogFolder, $"organize-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        int done = 0, ok = 0, conflicts = 0, failed = 0, skipped = 0, foldersRemoved = 0;
        bool removeEmpty = _removeEmpty.Checked;
        var progress = new Progress<string>(name =>
        {
            _progress.Value = Math.Min(done, _progress.Maximum);
            _status.Text = $"{done:N0} of {total:N0}: {name}";
        });

        try
        {
            await Task.Run(() =>
            {
                using var log = new StreamWriter(_logPath, false, new UTF8Encoding(true));
                log.WriteLine("Time,List,Action,Outcome,Source,Destination,Message");
                var emptiedFolders = new List<string>();

                foreach (var job in jobs)
                {
                    foreach (var song in job.Songs)
                    {
                        token.ThrowIfCancellationRequested();
                        ((IProgress<string>)progress).Report(song.FileName);

                        var result = SongOrganizer.Relocate(song, sourceRoot, job.Destination, job.Action);
                        foreach (var f in result.Files)
                        {
                            log.WriteLine(string.Join(",", Csv(DateTime.Now.ToString("s")), Csv(job.Title), Csv(job.Action.ToString()),
                                Csv(f.Outcome.ToString()), Csv(f.Source), Csv(f.Destination), Csv(f.Message ?? "")));
                        }
                        log.Flush(); // the log is complete up to here even if something goes wrong

                        if (result.Succeeded)
                        {
                            ok++;
                            var newPath = result.Files.FirstOrDefault(f => string.Equals(f.Source, song.Path, StringComparison.OrdinalIgnoreCase))?.Destination;
                            if (job.Action == RelocateAction.Move)
                            {
                                if (newPath != null) lock (MovedSongs) MovedSongs[song.Path] = newPath;
                                emptiedFolders.Add(song.Folder);
                            }
                            else if (newPath != null)
                            {
                                lock (CopiedSongs) CopiedSongs[song.Path] = newPath;
                            }
                        }
                        else if (result.Files.Any(f => f.Outcome == FileOutcome.Conflict)) conflicts++;
                        else if (result.Files.Any(f => f.Outcome == FileOutcome.Failed)) failed++;
                        else skipped++;
                        Interlocked.Increment(ref done);
                    }
                }

                if (removeEmpty) foldersRemoved = SongOrganizer.RemoveEmptyFolders(sourceRoot, emptiedFolders);
            }, token);
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Cancelled. ";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Organize failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }

        _progress.Value = Math.Min(done, _progress.Maximum);
        Summary = $"Organized {ok:N0} of {total:N0} songs" +
                  (conflicts > 0 ? $"; {conflicts:N0} left in place because a different file already exists at the destination" : "") +
                  (failed > 0 ? $"; {failed:N0} failed" : "") +
                  (skipped > 0 ? $"; {skipped:N0} skipped (not under the source root or missing)" : "") +
                  (foldersRemoved > 0 ? $"; {foldersRemoved:N0} empty folders removed" : "") + ".";
        _status.Text = (_status.Text.StartsWith("Cancelled") ? "Cancelled. " : "") + Summary + " Details are in the log.";
        _openLog.Visible = true;
    }

    private void SetBusy(bool busy)
    {
        _start.Enabled = !busy;
        _cancel.Enabled = busy;
        _close.Enabled = !busy;
        _sourceRoot.Enabled = !busy;
        foreach (var c in _include.Values) c.Enabled = !busy;
        foreach (var t in _destination.Values) t.Enabled = !busy;
        _removeEmpty.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    // ---------------- settings ----------------

    private sealed class OrganizeSettings
    {
        public string? SourceRoot { get; set; }
        public Dictionary<string, string> Destinations { get; set; } = new();
        public Dictionary<string, bool> Included { get; set; } = new();
        public bool RemoveEmptyFolders { get; set; } = true;
    }

    private void LoadSettings(string? suggestedSourceRoot)
    {
        OrganizeSettings? s = null;
        try
        {
            if (File.Exists(SettingsPath)) s = JsonSerializer.Deserialize<OrganizeSettings>(File.ReadAllText(SettingsPath));
        }
        catch
        {
            // Start with defaults if the settings file is damaged.
        }

        _sourceRoot.Text = s?.SourceRoot ?? suggestedSourceRoot ?? "";
        foreach (var (status, _, _, defaultOn) in Categories)
        {
            var key = status.ToString();
            _destination[status].Text = s?.Destinations.GetValueOrDefault(key) ?? "";
            _include[status].Checked = s?.Included.TryGetValue(key, out var on) == true ? on : defaultOn;
        }
        if (s != null) _removeEmpty.Checked = s.RemoveEmptyFolders;
    }

    private void SaveSettings()
    {
        try
        {
            var s = new OrganizeSettings { SourceRoot = _sourceRoot.Text.Trim(), RemoveEmptyFolders = _removeEmpty.Checked };
            foreach (var (status, _, _, _) in Categories)
            {
                s.Destinations[status.ToString()] = _destination[status].Text.Trim();
                s.Included[status.ToString()] = _include[status].Checked;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Not being able to remember the folders isn't worth interrupting the user for.
        }
    }
}
