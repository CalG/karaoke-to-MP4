using System.Net;
using System.Security.Cryptography;
using KaraokeToMp4.Midi;

namespace KaraokeToMp4.Tests;

/// <summary>
/// SoundFontLocator has static configuration, so these tests all live in one class (xUnit runs the
/// tests of a class one at a time) and restore the settings afterwards.
/// </summary>
public sealed class SoundFontLocatorTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _origFolder = SoundFontLocator.DownloadFolder;
    private readonly bool _origSystem = SoundFontLocator.SearchSystemFolders;
    private readonly SoundFontInfo _origRecommended = SoundFontLocator.Recommended;

    public SoundFontLocatorTests()
    {
        SoundFontLocator.DownloadFolder = Path.Combine(_dir.Path, "downloads");
        SoundFontLocator.SearchSystemFolders = false;
    }

    public void Dispose()
    {
        SoundFontLocator.DownloadFolder = _origFolder;
        SoundFontLocator.SearchSystemFolders = _origSystem;
        SoundFontLocator.Recommended = _origRecommended;
        SoundFontLocator.AdditionalSearchFolders.Clear();
        _dir.Dispose();
    }

    private static readonly byte[] FakeSf2 = "RIFF\0\0\0\0sfbkfake soundfont"u8.ToArray();

    private static SoundFontInfo FakeInfo(string? sha = null) => new(
        "Fake", "fake.sf2", new Uri("https://example.invalid/fake.sf2"),
        sha ?? Convert.ToHexString(SHA256.HashData(FakeSf2)).ToLowerInvariant(),
        FakeSf2.Length, "test");

    private sealed class FakeHandler(byte[] body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
        }
    }

    [Fact]
    public void Recommended_IsPinnedGeneralUserGs()
    {
        var gu = SoundFontLocator.GeneralUserGs;
        Assert.Equal("GeneralUser-GS.sf2", gu.FileName);
        Assert.Equal(64, gu.Sha256!.Length);
        Assert.Matches("/[0-9a-f]{40}/", gu.Url.AbsolutePath); // pinned to a commit, not a branch
        Assert.Equal(31, gu.SizeMegabytes);
    }

    [Fact]
    public void TryLocate_ReturnsNullWhenNothingInstalled()
    {
        Assert.Null(SoundFontLocator.TryLocate());
    }

    [Fact]
    public void TryLocate_FindsSf2InSearchFolder()
    {
        var folder = Path.Combine(_dir.Path, "extra");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "Something.sf2"), FakeSf2);
        SoundFontLocator.AdditionalSearchFolders.Add(folder);
        Assert.Equal(Path.Combine(folder, "Something.sf2"), SoundFontLocator.TryLocate());
    }

    [Fact]
    public async Task Download_VerifiesChecksumAndSaves()
    {
        var progress = new List<double>();
        using var http = new HttpClient(new FakeHandler(FakeSf2));
        var path = await SoundFontLocator.DownloadAsync(FakeInfo(), new SyncProgress<double>(progress.Add), default, http);

        Assert.Equal(Path.Combine(SoundFontLocator.DownloadFolder, "fake.sf2"), path);
        Assert.Equal(FakeSf2, File.ReadAllBytes(path));
        Assert.Equal(1.0, progress[^1]);
        Assert.False(File.Exists(path + ".part"));
    }

    [Fact]
    public async Task Download_RejectsBadChecksum()
    {
        using var http = new HttpClient(new FakeHandler(FakeSf2));
        var ex = await Assert.ThrowsAsync<KaraokeConversionException>(
            () => SoundFontLocator.DownloadAsync(FakeInfo(new string('0', 64)), null, default, http));
        Assert.Contains("checksum", ex.Message);
        Assert.Empty(Directory.GetFiles(SoundFontLocator.DownloadFolder));
    }

    [Fact]
    public async Task Download_HttpErrorIsWrapped()
    {
        using var http = new HttpClient(new FakeHandler(Array.Empty<byte>(), HttpStatusCode.NotFound));
        await Assert.ThrowsAsync<KaraokeConversionException>(
            () => SoundFontLocator.DownloadAsync(FakeInfo(), null, default, http));
    }

    [Fact]
    public async Task Ensure_DoesNotDownloadWhenDeclined()
    {
        SoundFontLocator.Recommended = FakeInfo();
        var handler = new FakeHandler(FakeSf2);
        using var http = new HttpClient(handler);
        SoundFontDownloadRequest? asked = null;

        var result = await SoundFontLocator.EnsureAsync((r, _) => { asked = r; return Task.FromResult(false); }, null, default, http);

        Assert.Null(result);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(Path.Combine(SoundFontLocator.DownloadFolder, "fake.sf2"), asked!.Destination);
    }

    [Fact]
    public async Task Ensure_DownloadsOnceWhenApproved()
    {
        SoundFontLocator.Recommended = FakeInfo();
        var handler = new FakeHandler(FakeSf2);
        using var http = new HttpClient(handler);
        int asks = 0;
        Task<bool> Yes(SoundFontDownloadRequest r, CancellationToken c) { asks++; return Task.FromResult(true); }

        var first = await SoundFontLocator.EnsureAsync(Yes, null, default, http);
        var second = await SoundFontLocator.EnsureAsync(Yes, null, default, http);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(1, asks);          // found on disk the second time
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Ensure_WithoutCallback_NeverDownloads()
    {
        Assert.Null(await SoundFontLocator.EnsureAsync(null));
    }

    [Fact]
    public async Task MidiConversion_WithoutSoundFont_ThrowsHelpfulError()
    {
        var mid = TestFiles.WriteTemp(_dir.Path, "song.mid", TestFiles.MidiWithLyrics("la"));
        var ex = await Assert.ThrowsAsync<KaraokeConversionException>(
            () => new KaraokeConverter(EndToEndTests.SmallOptions()).ConvertAsync(mid, _dir.File("x.mp4")));
        Assert.Contains("SoundFont", ex.Message);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
