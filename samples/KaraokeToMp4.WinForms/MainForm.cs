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
    private readonly Label _formatLabel = new() { AutoSize = true, Text = "Format: -" };
    private readonly Label _outputLabel = new() { AutoSize = true, Text = "Output: -" };
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
        Width = 720;
        Height = 260;
        MinimumSize = new Size(560, 260);
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
        layout.Controls.Add(_formatLabel, 0, 1);
        layout.SetColumnSpan(_formatLabel, 2);
        layout.Controls.Add(_outputLabel, 0, 2);
        layout.SetColumnSpan(_outputLabel, 2);

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

    private static string OutputPathFor(string input) =>
        Path.Combine(Path.GetDirectoryName(input)!, Path.GetFileNameWithoutExtension(input) + ".mp4");

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
        _formatLabel.Text = "Format: " + format switch
        {
            KaraokeFormat.Mp3Cdg => "MP3 + CDG",
            KaraokeFormat.Kar => "KAR (karaoke MIDI)",
            KaraokeFormat.MidiWithLyrics => "MIDI with lyrics",
            _ => "not recognised (needs MP3+CDG, KAR or MIDI with lyrics)",
        };
        bool ok = format != KaraokeFormat.Unknown;
        _outputLabel.Text = ok ? "Output: " + OutputPathFor(path) : "Output: -";
        _convertButton.Enabled = ok;
        SetStatus(ok ? "Ready." : "This file can't be converted.");
    }

    private async Task ConvertAsync()
    {
        var input = _fileBox.Text;
        if (!File.Exists(input)) { SetStatus("File not found."); return; }
        var output = OutputPathFor(input);

        if (File.Exists(output) &&
            MessageBox.Show(this, $"{Path.GetFileName(output)} already exists. Replace it?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        var options = new ConversionOptions
        {
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
            await Task.Run(() => new KaraokeConverter(options).ConvertAsync(input, output, progress, token), token);
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
