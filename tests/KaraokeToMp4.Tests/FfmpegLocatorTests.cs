using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using KaraokeToMp4.Downloads;
using KaraokeToMp4.Video;

namespace KaraokeToMp4.Tests;

/// <summary>
/// Locator settings are static, so tests touching them share the "Tools" collection (run one at a time).
/// </summary>
[CollectionDefinition("Tools", DisableParallelization = true)]
public sealed class ToolsCollection { }

[Collection("Tools")]
public sealed class FfmpegLocatorTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _origFolder = FfmpegLocator.DownloadFolder;
    private readonly bool _origSearchPath = FfmpegLocator.SearchPath;
    private readonly FfmpegRelease? _origRelease = FfmpegLocator.Release;

    private static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    public FfmpegLocatorTests()
    {
        FfmpegLocator.DownloadFolder = Path.Combine(_dir.Path, "ffmpeg");
        FfmpegLocator.SearchPath = false;
    }

    public void Dispose()
    {
        FfmpegLocator.DownloadFolder = _origFolder;
        FfmpegLocator.SearchPath = _origSearchPath;
        FfmpegLocator.Release = _origRelease;
        FfmpegLocator.AdditionalSearchFolders.Clear();
        _dir.Dispose();
    }

    private static byte[] ZipWith(string entryName, string content)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var w = new StreamWriter(zip.CreateEntry(entryName).Open());
            w.Write(content);
        }
        return ms.ToArray();
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// <summary>Serves a fake ffmpeg zip and ffprobe zip by URL.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public readonly Dictionary<string, byte[]> Files = new();
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Files.TryGetValue(request.RequestUri!.AbsolutePath, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static (FfmpegRelease Release, FakeServer Server) FakeRelease(string? badProbeSha = null)
    {
        var ffmpegZip = ZipWith(Exe("ffmpeg"), "fake ffmpeg");
        var probeZip = ZipWith(Exe("ffprobe"), "fake ffprobe");
        var server = new FakeServer();
        server.Files["/ffmpeg.zip"] = ffmpegZip;
        server.Files["/ffprobe.zip"] = probeZip;
        var release = new FfmpegRelease("9.9", "test",
            new DownloadAsset(new Uri("https://example.invalid/ffmpeg.zip"), Sha(ffmpegZip), ffmpegZip.Length),
            new DownloadAsset(new Uri("https://example.invalid/ffprobe.zip"), badProbeSha ?? Sha(probeZip), probeZip.Length));
        return (release, server);
    }

    [Fact]
    public void KnownReleases_ArePinnedWithChecksums()
    {
        Assert.Equal(new[] { "linux-64", "linux-arm-64", "macos-64", "win-64" }, FfmpegLocator.KnownReleases.Keys.Order());
        foreach (var r in FfmpegLocator.KnownReleases.Values)
        {
            foreach (var a in new[] { r.Ffmpeg, r.Ffprobe })
            {
                Assert.Equal(64, a.Sha256!.Length);
                Assert.Contains("/download/v6.1/", a.Url.AbsoluteUri);
                Assert.True(a.SizeBytes > 10_000_000);
            }
        }
        // Every 64-bit desktop platform this library targets has a build.
        Assert.NotNull(_origRelease);
    }

    [Fact]
    public void TryLocate_NeedsBothFfmpegAndFfprobe()
    {
        var folder = Path.Combine(_dir.Path, "tools");
        Directory.CreateDirectory(folder);
        FfmpegLocator.AdditionalSearchFolders.Add(folder);

        File.WriteAllText(Path.Combine(folder, Exe("ffmpeg")), "x");
        Assert.Null(FfmpegLocator.TryLocate());

        File.WriteAllText(Path.Combine(folder, Exe("ffprobe")), "x");
        var tools = FfmpegLocator.TryLocate();
        Assert.Equal(Path.Combine(folder, Exe("ffmpeg")), tools!.FfmpegPath);
        Assert.Equal(Path.Combine(folder, Exe("ffprobe")), tools.FfprobePath);
    }

    [Fact]
    public async Task Download_ExtractsBothExecutables()
    {
        var (release, server) = FakeRelease();
        FfmpegLocator.Release = release;
        var progress = new List<double>();
        using var http = new HttpClient(server);

        var tools = await FfmpegLocator.DownloadAsync(new SyncProgress(progress.Add), default, http);

        Assert.Equal("fake ffmpeg", File.ReadAllText(tools.FfmpegPath));
        Assert.Equal("fake ffprobe", File.ReadAllText(tools.FfprobePath));
        Assert.Equal(1.0, progress[^1]);
        Assert.True(progress.SequenceEqual(progress.Order()), "progress should only go up");
        Assert.Equal(2, Directory.GetFiles(FfmpegLocator.DownloadFolder).Length); // zips cleaned up
        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(tools.FfmpegPath).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public async Task Download_RejectsBadChecksum()
    {
        var (release, server) = FakeRelease(badProbeSha: new string('0', 64));
        FfmpegLocator.Release = release;
        using var http = new HttpClient(server);

        var ex = await Assert.ThrowsAsync<KaraokeConversionException>(() => FfmpegLocator.DownloadAsync(null, default, http));
        Assert.Contains("checksum", ex.Message);
        Assert.Null(FfmpegLocator.TryLocate());
    }

    [Fact]
    public async Task Ensure_AsksOnceAndRespectsNo()
    {
        var (release, server) = FakeRelease();
        FfmpegLocator.Release = release;
        using var http = new HttpClient(server);

        FfmpegDownloadRequest? asked = null;
        Assert.Null(await FfmpegLocator.EnsureAsync((r, _) => { asked = r; return Task.FromResult(false); }, null, default, http));
        Assert.Equal(0, server.Calls);
        Assert.Equal(FfmpegLocator.DownloadFolder, asked!.Destination);
        Assert.Same(release, asked.Release);

        int asks = 0;
        Task<bool> Yes(FfmpegDownloadRequest r, CancellationToken c) { asks++; return Task.FromResult(true); }
        var first = await FfmpegLocator.EnsureAsync(Yes, null, default, http);
        var second = await FfmpegLocator.EnsureAsync(Yes, null, default, http);
        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(1, asks);
        Assert.Equal(2, server.Calls);
    }

    [Fact]
    public async Task Conversion_WithoutFfmpeg_ThrowsHelpfulError()
    {
        FfmpegLocator.Release = null;
        using var dir = new TempDir();
        var mp3 = TestFiles.WriteTemp(dir.Path, "Song.mp3", new byte[10]);
        TestFiles.WriteTemp(dir.Path, "Song.cdg", TestFiles.SimpleCdg(1));

        bool asked = false;
        var options = EndToEndTests.SmallOptions();
        options.ConfirmFfmpegDownload = (_, _) => { asked = true; return Task.FromResult(true); };

        var ex = await Assert.ThrowsAsync<KaraokeConversionException>(
            () => new KaraokeConverter(options).ConvertAsync(mp3, dir.File("out.mp4")));
        Assert.Contains("ffmpeg was not found", ex.Message);
        Assert.False(asked); // no build for this "platform", so nothing to offer
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
