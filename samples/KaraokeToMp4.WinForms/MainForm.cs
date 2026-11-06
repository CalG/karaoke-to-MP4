using System.Diagnostics;
using KaraokeToMp4.Midi;
using KaraokeToMp4.Video;

namespace KaraokeToMp4.WinForms;

/// <summary>
/// Minimal test app: pick a karaoke file, press Convert, and the MP4 is written next to it.
/// </summary>
internal sealed class MainForm : Form
{
    private const string FileFilter =
        "Karaoke files (*.mp3;*.cdg;*.zip;*.kar;*.mid;*.midi)|*.mp3;*.cdg;*.zip;*.kar;*.mid;*.midi;*.ogg;*.wav;*.flac;*.m4a|" +
        "MP3 + CDG (*.mp3;*.cdg;*.zip)|*.mp3;*.cdg;*.zip|" +
        "KAR / MIDI (*.kar;*.mid;*.midi)|*.kar;*.mid;*.midi|" +
        "All files (*.*)|*.*";

    private readonly TextBox _fileBox = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly Button _browseButton = new() { Text = "Browse...", AutoSize = true };
    // Song details, one field per line (tag on the left, value on the right). Values are read-only
    // text boxes so they can be selected and copied.
    private readonly TextBox _formatValue = ValueBox();
    private readonly TextBox _titleValue = ValueBox();
    private readonly TextBox _artistValue = ValueBox();
    private readonly TextBox _sourceValue = ValueBox();
    private readonly TextBox _rawTitleValue = ValueBox();
    private readonly TextBox _rawArtistValue = ValueBox();
    private readonly TextBox _outputValue = ValueBox();
    private readonly TextBox _fileCheckValue = ValueBox();
    private readonly CheckBox _includeArtistBox = new()
    {
        Text = "Include artist in file name, MP4 tags and title screen",
        Checked = true,
        AutoSize = true,
    };
    private readonly Button _convertButton = new() { Text = "Convert to MP4", AutoSize = true, Enabled = false };
    private readonly Button _cancelButton = new() { Text = "Cancel", AutoSize = true, Enabled = false };
    private readonly Button _openFolderButton = new() { Text = "Open Folder", AutoSize = true, Enabled = false };
    private readonly ProgressBar _progressBar = new() { Dock = DockStyle.Fill, Maximum = 1000 };
    private readonly Label _statusLabel = new() { AutoSize = true, Text = "Choose a karaoke file (or drag one onto this window)." };

    private CancellationTokenSource? _cts;
    private string? _lastOutput;

    public MainForm()
    {
        Text = "Karaoke to MP4";
        Width = 760;
        Height = 500;
        MinimumSize = new Size(600, 500);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 2,
            RowCount = 6,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        layout.Controls.Add(_fileBox, 0, 0);
        layout.Controls.Add(_browseButton, 1, 0);

        var details = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(6),
        };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddField(details, "Format:", _formatValue);
        AddField(details, "Title:", _titleValue);
        AddField(details, "Artist:", _artistValue);
        AddField(details, "Found in:", _sourceValue);
        AddField(details, "Raw title:", _rawTitleValue);
        AddField(details, "Raw artist:", _rawArtistValue);
        AddField(details, "Output:", _outputValue);
        AddField(details, "File check:", _fileCheckValue);
        details.Controls.Add(_includeArtistBox, 1, details.RowCount++);

        var group = new GroupBox { Text = "Song details", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(6) };
        group.Controls.Add(details);
        layout.Controls.Add(group, 0, 1);
        layout.SetColumnSpan(group, 2);
        ClearDetails();

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 8) };
        buttons.Controls.AddRange(new Control[] { _convertButton, _cancelButton, _openFolderButton });
        layout.Controls.Add(buttons, 0, 3);
        layout.SetColumnSpan(buttons, 2);

        layout.Controls.Add(_progressBar, 0, 4);
        layout.SetColumnSpan(_progressBar, 2);
        layout.Controls.Add(_statusLabel, 0, 5);
        layout.SetColumnSpan(_statusLabel, 2);
        Controls.Add(layout);

        _browseButton.Click += (_, _) => Browse();
        _convertButton.Click += async (_, _) => await ConvertAsync();
        _cancelButton.Click += (_, _) => _cts?.Cancel();
        _openFolderButton.Click += (_, _) => OpenOutputFolder();
        _includeArtistBox.CheckedChanged += (_, _) => ShowSongInfo();

        DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true && _cts == null) e.Effect = DragDropEffects.Copy;
        };
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) SelectFile(files[0]);
        };
        FormClosing += (_, e) =>
        {
            if (_cts == null) return;
            if (MessageBox.Show(this, "A conversion is running. Cancel it and close?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                _cts.Cancel();
            else
                e.Cancel = true;
        };
    }

    /// <summary>Everything read from the file, including the artist.</summary>
    private SongInfo _fileInfo = SongInfo.Empty;

    /// <summary>What will actually be used, honouring the "Include artist" box.</summary>
    private SongInfo _songInfo => _includeArtistBox.Checked ? _fileInfo : _fileInfo.WithoutArtist();

    private static TextBox ValueBox() => new()
    {
        ReadOnly = true,
        BorderStyle = BorderStyle.None,
        Dock = DockStyle.Fill,
        TabStop = false,
        Margin = new Padding(3, 4, 3, 4),
    };

    private static void AddField(TableLayoutPanel table, string tag, TextBox value)
    {
        int row = table.RowCount++;
        table.Controls.Add(new Label
        {
            Text = tag,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Font = new Font(SystemFonts.MessageBoxFont ?? DefaultFont, FontStyle.Bold),
            Margin = new Padding(3, 4, 12, 4),
        }, 0, row);
        value.BackColor = SystemColors.Control;
        table.Controls.Add(value, 1, row);
    }

    private void ClearDetails()
    {
        foreach (var box in new[] { _formatValue, _titleValue, _artistValue, _sourceValue, _rawTitleValue, _rawArtistValue, _outputValue, _fileCheckValue })
            box.Text = "-";
    }

    /// <summary>Fills the Title/Artist/Found in/Raw/Output fields from the current file.</summary>
    private void ShowSongInfo()
    {
        var path = _fileBox.Text;
        if (string.IsNullOrEmpty(path) || KaraokeConverter.DetectFormat(path) == KaraokeFormat.Unknown) return;

        var info = _songInfo;
        _titleValue.Text = info.Title ?? "(not found)";
        _artistValue.Text = info.Artist
                            ?? (!_includeArtistBox.Checked && _fileInfo.HasArtist ? $"(not used: {_fileInfo.Artist})" : "(not found)");
        _sourceValue.Text = _fileInfo.Source ?? "-";
        _rawTitleValue.Text = _fileInfo.RawTitle ?? _fileInfo.Title ?? "-";
        _rawArtistValue.Text = _fileInfo.RawArtist ?? _fileInfo.Artist ?? "-";
        _outputValue.Text = info.SuggestOutputPath(path);
    }

    private void Browse()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose a karaoke file",
            Filter = FileFilter,
            CheckFileExists = true,
        };
        if (!string.IsNullOrEmpty(_fileBox.Text))
            dialog.InitialDirectory = Path.GetDirectoryName(_fileBox.Text);
        if (dialog.ShowDialog(this) == DialogResult.OK)
            SelectFile(dialog.FileName);
    }

    private void SelectFile(string path)
    {
        _fileBox.Text = path;
        _openFolderButton.Enabled = false;
        _progressBar.Value = 0;

        var format = KaraokeConverter.DetectFormat(path);
        ClearDetails();
        _formatValue.Text = format switch
        {
            KaraokeFormat.Mp3Cdg => "MP3 + CDG",
            KaraokeFormat.Kar => "KAR (karaoke MIDI)",
            KaraokeFormat.MidiWithLyrics => "MIDI with lyrics",
            _ => KaraokeConverter.DescribeUnsupported(path),
        };
        bool ok = format != KaraokeFormat.Unknown;
        // Title/artist come from the file's own data (KAR @T, MIDI text, ID3 tags). They're appended to
        // the output name when missing from it, and written into the MP4's metadata.
        _fileInfo = ok ? ReadSongInfo(path) : SongInfo.Empty;
        ShowSongInfo();
        _fileCheckValue.Text = ok ? CheckFile(path, format) : "-";
        _fileCheckValue.ForeColor = _fileCheckValue.Text.StartsWith("OK") ? SystemColors.ControlText : Color.DarkRed;
        _convertButton.Enabled = ok;
        SetStatus(ok ? "Ready." : "This file can't be converted.");
    }

    private async Task ConvertAsync()
    {
        var input = _fileBox.Text;
        if (!File.Exists(input)) { SetStatus("File not found."); return; }
        var output = _songInfo.SuggestOutputPath(input);
        var songInfo = _songInfo;

        if (File.Exists(output) &&
            MessageBox.Show(this, $"{Path.GetFileName(output)} already exists. Replace it?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        var options = new ConversionOptions
        {
            IncludeArtist = _includeArtistBox.Checked,
            ConfirmFfmpegDownload = (req, _) => AskAsync(
                "Converting to MP4 needs ffmpeg, which wasn't found.\n\n" +
                $"Download it now (about {req.Release.SizeMegabytes} MB) into:\n{req.Destination}\n\n" +
                "This only happens once.",
                "Download ffmpeg?"),
            FfmpegDownloadProgress = new Progress<double>(f => ShowProgress(f, $"Downloading ffmpeg... {f:P0}")),
            ConfirmSoundFontDownload = (req, _) => AskAsync(
                "KAR/MIDI songs need a SoundFont to play the music, and none was found.\n\n" +
                $"Download {req.SoundFont.Name} (about {req.SoundFont.SizeMegabytes} MB) into:\n{req.Destination}\n\n" +
                $"Licence: {req.SoundFont.License}\n\nThis only happens once.",
                "Download SoundFont?"),
            SoundFontDownloadProgress = new Progress<double>(f => ShowProgress(f, $"Downloading SoundFont... {f:P0}")),
        };

        var progress = new Progress<ConversionProgress>(p =>
        {
            string what = p.Stage switch
            {
                ConversionStage.Preparing => "Preparing...",
                ConversionStage.SynthesizingAudio => "Synthesising MIDI audio...",
                ConversionStage.EncodingVideo => "Encoding video...",
                ConversionStage.Completed => "Finishing...",
                _ => p.Stage.ToString(),
            };
            ShowProgress(p.Fraction, $"{what} {p.Position:mm\\:ss} / {p.Duration:mm\\:ss}");
        });

        _cts = new CancellationTokenSource();
        SetBusy(true);
        var stopwatch = Stopwatch.StartNew();
        var startedUtc = DateTime.UtcNow;
        try
        {
            var token = _cts.Token;
            // Run off the UI thread so the window stays responsive while frames are rendered.
            await Task.Run(() => new KaraokeConverter(options).ConvertAsync(input, output, progress, token, songInfo), token);
            _lastOutput = output;
            _openFolderButton.Enabled = true;
            ShowProgress(1, $"Done in {stopwatch.Elapsed:m\\:ss}: {output}");
        }
        catch (OperationCanceledException)
        {
            DeleteIfWrittenSince(output, startedUtc);
            ShowProgress(0, "Cancelled.");
        }
        catch (Exception ex)
        {
            DeleteIfWrittenSince(output, startedUtc);
            ShowProgress(0, "Conversion failed.");
            MessageBox.Show(this, ex.Message, "Conversion failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    /// <summary>Reports damaged/truncated MIDI files, which still convert but may stop early.</summary>
    private static string CheckFile(string path, KaraokeFormat format)
    {
        if (format == KaraokeFormat.Mp3Cdg)
        {
            var cdg = path.EndsWith(".cdg", StringComparison.OrdinalIgnoreCase) ? path : KaraokeToMp4.Cdg.CdgConverter.FindCdgForAudio(path);
            if (cdg == null) return "OK"; // .zip bundles are checked during conversion
            try { return KaraokeToMp4.Cdg.CdgInspector.Inspect(cdg).Summary; }
            catch (Exception ex) { return "Unreadable - " + ex.Message; }
        }
        if (format is not (KaraokeFormat.Kar or KaraokeFormat.MidiWithLyrics)) return "OK";
        try
        {
            var midi = KaraokeToMp4.Midi.StandardMidiFile.FromFile(path);
            return midi.IsDamaged ? "Damaged - " + string.Join(" ", midi.Problems) + " It will convert, but may stop early." : "OK";
        }
        catch (Exception ex)
        {
            return "Unreadable - " + ex.Message;
        }
    }

    private static SongInfo ReadSongInfo(string path)
    {
        try
        {
            // Reading tags is quick (no ffmpeg download is ever triggered here).
            return Task.Run(() => new KaraokeConverter().ReadSongInfoAsync(path)).GetAwaiter().GetResult();
        }
        catch
        {
            return SongInfo.Empty;
        }
    }

    /// <summary>Shows a Yes/No question on the UI thread (the library may call from a worker thread).</summary>
    private Task<bool> AskAsync(string message, string caption)
    {
        bool Ask() => MessageBox.Show(this, message, caption, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        return Task.FromResult(InvokeRequired ? Invoke<bool>(Ask) : Ask());
    }

    private void ShowProgress(double fraction, string status)
    {
        _progressBar.Value = (int)Math.Round(Math.Clamp(fraction, 0, 1) * _progressBar.Maximum);
        SetStatus(status);
    }

    private void SetStatus(string text) => _statusLabel.Text = text;

    private void SetBusy(bool busy)
    {
        _browseButton.Enabled = !busy;
        _includeArtistBox.Enabled = !busy;
        _convertButton.Enabled = !busy && KaraokeConverter.DetectFormat(_fileBox.Text) != KaraokeFormat.Unknown;
        _cancelButton.Enabled = busy;
        UseWaitCursor = busy;
        if (busy) _openFolderButton.Enabled = false;
    }

    private void OpenOutputFolder()
    {
        if (_lastOutput == null || !File.Exists(_lastOutput)) return;
        // Opens Explorer with the new MP4 selected.
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastOutput}\"") { UseShellExecute = true });
    }

    private static void DeleteIfWrittenSince(string path, DateTime startedUtc)
    {
        // A cancelled/failed ffmpeg run can leave a truncated file behind. Only remove it if this run
        // wrote it, so an earlier good MP4 survives a failure that happened before ffmpeg started.
        try
        {
            if (File.Exists(path) && File.GetLastWriteTimeUtc(path) >= startedUtc.AddSeconds(-2))
                File.Delete(path);
        }
        catch { /* best effort */ }
    }
}
