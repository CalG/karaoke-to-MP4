using System.Diagnostics;
using System.IO.Compression;

namespace KaraokeToMp4.Tests;

/// <summary>
/// Full conversions through ffmpeg. Skipped automatically (they pass trivially) when ffmpeg isn't
/// installed; the MIDI test also needs a SoundFont, found via KARAOKE_TEST_SOUNDFONT or common paths.
/// </summary>
public class EndToEndTests
{
    private static readonly bool HasFfmpeg = CanRun("ffmpeg", "-version") && CanRun("ffprobe", "-version");

    private static string? SoundFont =>
        new[]
        {
            Environment.GetEnvironmentVariable("KARAOKE_TEST_SOUNDFONT"),
            "/usr/share/sounds/sf2/TimGM6mb.sf2",
            "/usr/share/sounds/sf2/FluidR3_GM.sf2",
            "/usr/share/sounds/sf2/default-GM.sf2",
        }.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));

    internal static ConversionOptions SmallOptions() => new()
    {
        Width = 320,
        Height = 240,
        FrameRate = 10,
        VideoPreset = "ultrafast",
    };

    [Fact]
    public async Task Mp3Cdg_ProducesMp4WithVideoAndAudio()
    {
        if (!HasFfmpeg) return;
        using var dir = new TempDir();
        var mp3 = dir.File("Song.mp3");
        MakeTone(mp3, 3);
        TestFiles.WriteTemp(dir.Path, "Song.cdg", TestFiles.SimpleCdg(3));
        var output = dir.File("out.mp4");

        var reports = new List<ConversionProgress>();
        await new KaraokeConverter(SmallOptions()).ConvertAsync(mp3, output, new SyncProgress(reports.Add));

        AssertValidMp4(output, 320, 240, expectedSeconds: 3);
        Assert.Equal(ConversionStage.Completed, reports[^1].Stage);
    }

    [Fact]
    public async Task ZipBundle_IsConverted()
    {
        if (!HasFfmpeg) return;
        using var dir = new TempDir();
        var mp3 = dir.File("t.mp3");
        MakeTone(mp3, 2);
        var zipPath = dir.File("bundle.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(mp3, "Artist - Title.mp3");
            var e = zip.CreateEntry("Artist - Title.cdg");
            using var s = e.Open();
            s.Write(TestFiles.SimpleCdg(2));
        }

        var output = dir.File("zip.mp4");
        await new KaraokeConverter(SmallOptions()).ConvertAsync(zipPath, output);
        AssertValidMp4(output, 320, 240, expectedSeconds: 2);
    }

    [Fact]
    public async Task Kar_ProducesMp4()
    {
        if (!HasFfmpeg || SoundFont is null) return;
        using var dir = new TempDir();
        var kar = TestFiles.WriteTemp(dir.Path, "song.kar",
            TestFiles.Kar("\\Hel", "lo ", "world", "/This ", "is ", "a ", "test"));
        var output = dir.File("kar.mp4");

        var options = SmallOptions();
        options.SoundFontPath = SoundFont;
        await new KaraokeConverter(options).ConvertAsync(kar, output);
        AssertValidMp4(output, 320, 240, expectedSeconds: 6);
    }

    [Fact]
    public async Task MidiWithLyrics_UsingAudioOverride_ProducesMp4()
    {
        if (!HasFfmpeg) return;
        using var dir = new TempDir();
        var mid = TestFiles.WriteTemp(dir.Path, "song.mid", TestFiles.MidiWithLyrics("One ", "two\r", "three ", "four\r"));
        var wav = dir.File("backing.wav");
        MakeTone(wav, 4);
        var output = dir.File("mid.mp4");

        var options = SmallOptions();
        options.MidiAudioOverridePath = wav;
        await new KaraokeConverter(options).ConvertAsync(mid, output);
        AssertValidMp4(output, 320, 240, expectedSeconds: 4);
    }

    [Fact]
    public async Task Cancellation_StopsConversion()
    {
        if (!HasFfmpeg) return;
        using var dir = new TempDir();
        var mp3 = dir.File("Song.mp3");
        MakeTone(mp3, 5);
        TestFiles.WriteTemp(dir.Path, "Song.cdg", TestFiles.SimpleCdg(5));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new KaraokeConverter(SmallOptions()).ConvertAsync(mp3, dir.File("c.mp4"), null, cts.Token));
    }

    // ---- helpers ----

    private sealed class SyncProgress(Action<ConversionProgress> report) : IProgress<ConversionProgress>
    {
        public void Report(ConversionProgress value) => report(value);
    }

    private static void MakeTone(string path, int seconds)
    {
        var (code, _) = Run("ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi",
            "-i", $"sine=frequency=440:duration={seconds}", path);
        Assert.Equal(0, code);
    }

    private static void AssertValidMp4(string path, int width, int height, double expectedSeconds)
    {
        Assert.True(File.Exists(path), "output file missing");
        var (code, output) = Run("ffprobe", "-v", "error",
            "-show_entries", "stream=codec_type,width,height:format=duration", "-of", "default=nw=1", path);
        Assert.Equal(0, code);
        Assert.Contains("codec_type=video", output);
        Assert.Contains("codec_type=audio", output);
        Assert.Contains($"width={width}", output);
        Assert.Contains($"height={height}", output);

        var durLine = output.Split('\n').First(l => l.StartsWith("duration="));
        double duration = double.Parse(durLine["duration=".Length..], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(duration, expectedSeconds - 0.5, expectedSeconds + 1.0);
    }

    private static bool CanRun(string exe, string arg)
    {
        try { return Run(exe, arg).Code == 0; }
        catch { return false; }
    }

    private static (int Code, string Output) Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, stdout.Result + stderr.Result);
    }
}
