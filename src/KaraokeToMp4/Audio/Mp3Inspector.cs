namespace KaraokeToMp4.Audio;

/// <summary>Result of walking the frames of an MP3 file.</summary>
/// <param name="Frames">Number of valid audio frames.</param>
/// <param name="Duration">Playing time implied by the frames.</param>
/// <param name="AudioBytes">Bytes between the tags (the part that should be all frames).</param>
/// <param name="DamagedBytes">Bytes inside the audio that weren't part of a valid frame.</param>
/// <param name="DamagedSections">Number of places where the frame sequence was broken and had to be re-found.</param>
/// <param name="EndsMidFrame">True when the last frame is cut off.</param>
public sealed record Mp3Report(int Frames, TimeSpan Duration, long AudioBytes, long DamagedBytes, int DamagedSections, bool EndsMidFrame)
{
    /// <summary>
    /// A description of what's wrong, or null when the audio looks intact. Small amounts of junk (a few
    /// KB, e.g. leftover tag padding) are tolerated.
    /// </summary>
    public string? Problem =>
        Frames == 0 ? "no MP3 audio frames found" :
        DamagedBytes > Math.Max(8192, AudioBytes / 200) || DamagedSections > 3
            ? $"the audio has {DamagedSections} damaged section{(DamagedSections == 1 ? "" : "s")} ({DamagedBytes:N0} bytes)"
            : null;
}

/// <summary>
/// Walks the frames of an MP3 (MPEG-1/2/2.5, layers I-III) to measure its real playing length and find
/// damage, without decoding the audio. Fast enough to run on every file in a large collection.
/// </summary>
public static class Mp3Inspector
{
    private static readonly int[,] BitratesV1 =
    {
        { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448 }, // layer I
        { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384 },     // layer II
        { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 },      // layer III
    };

    private static readonly int[,] BitratesV2 =
    {
        { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256 }, // layer I
        { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 },      // layer II
        { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 },      // layer III
    };

    private static readonly int[][] SampleRates =
    {
        new[] { 11025, 12000, 8000 },  // MPEG 2.5
        Array.Empty<int>(),            // reserved
        new[] { 22050, 24000, 16000 }, // MPEG 2
        new[] { 44100, 48000, 32000 }, // MPEG 1
    };

    private readonly record struct Frame(int Length, int Samples, int SampleRate, int Version, int Layer);

    public static Mp3Report Inspect(string path) => Inspect(File.ReadAllBytes(path));

    public static Mp3Report Inspect(ReadOnlySpan<byte> data)
    {
        int start = SkipId3v2(data);
        int end = AudioEnd(data, start);
        long audioBytes = Math.Max(0, end - start);

        // Find the first frame that is followed by a matching frame (avoids locking onto noise).
        int pos = FindSync(data, start, end, null);
        if (pos < 0) return new Mp3Report(0, TimeSpan.Zero, audioBytes, audioBytes, 0, false);

        var first = Parse(data, pos)!.Value;
        long damaged = pos - start; // junk before the first frame
        int sections = damaged > 0 ? 1 : 0;
        int frames = 0;
        double seconds = 0;
        bool endsMidFrame = false;

        while (pos < end)
        {
            var frame = end - pos >= 4 ? Parse(data, pos) : null;
            if (frame is { } f && f.Version == first.Version && f.Layer == first.Layer && f.SampleRate == first.SampleRate)
            {
                if (pos + f.Length > end)
                {
                    endsMidFrame = true;
                    damaged += end - pos;
                    break;
                }
                frames++;
                seconds += (double)f.Samples / f.SampleRate;
                pos += f.Length;
                continue;
            }

            // Lost the frame sequence: look for the next good frame.
            int next = FindSync(data, pos + 1, end, first);
            sections++;
            if (next < 0)
            {
                damaged += end - pos;
                break;
            }
            damaged += next - pos;
            pos = next;
        }

        return new Mp3Report(frames, TimeSpan.FromSeconds(seconds), audioBytes, damaged, sections, endsMidFrame);
    }

    private static Frame? Parse(ReadOnlySpan<byte> d, int pos)
    {
        if (pos + 4 > d.Length) return null;
        if (d[pos] != 0xFF || (d[pos + 1] & 0xE0) != 0xE0) return null;

        int version = (d[pos + 1] >> 3) & 0x03;    // 0 = 2.5, 1 = reserved, 2 = 2, 3 = 1
        int layerBits = (d[pos + 1] >> 1) & 0x03;  // 1 = III, 2 = II, 3 = I
        int bitrateIndex = (d[pos + 2] >> 4) & 0x0F;
        int rateIndex = (d[pos + 2] >> 2) & 0x03;
        int padding = (d[pos + 2] >> 1) & 0x01;
        if (version == 1 || layerBits == 0 || bitrateIndex is 0 or 15 || rateIndex == 3) return null;

        int layer = 4 - layerBits; // 1, 2 or 3
        int bitrate = (version == 3 ? BitratesV1 : BitratesV2)[layer - 1, bitrateIndex] * 1000;
        int sampleRate = SampleRates[version][rateIndex];

        int length, samples;
        if (layer == 1)
        {
            length = (12 * bitrate / sampleRate + padding) * 4;
            samples = 384;
        }
        else
        {
            bool lowRateLayer3 = layer == 3 && version != 3;
            length = (lowRateLayer3 ? 72 : 144) * bitrate / sampleRate + padding;
            samples = lowRateLayer3 ? 576 : 1152;
        }
        return length < 24 ? null : new Frame(length, samples, sampleRate, version, layer);
    }

    /// <summary>Next position holding a frame that is immediately followed by another frame.</summary>
    private static int FindSync(ReadOnlySpan<byte> d, int from, int end, Frame? like)
    {
        for (int i = from; i + 4 <= end; i++)
        {
            if (d[i] != 0xFF) continue;
            var f = Parse(d, i);
            if (f is not { } frame) continue;
            if (like is { } l && (frame.Version != l.Version || frame.Layer != l.Layer || frame.SampleRate != l.SampleRate)) continue;

            int next = i + frame.Length;
            if (next == end) return i; // last frame of the file
            if (next + 4 <= end && Parse(d, next) is { } n && n.Version == frame.Version && n.Layer == frame.Layer)
                return i;
        }
        return -1;
    }

    private static int SkipId3v2(ReadOnlySpan<byte> d)
    {
        int pos = 0;
        // Some files carry more than one ID3v2 tag in a row.
        while (d.Length >= pos + 10 && d[pos] == 'I' && d[pos + 1] == 'D' && d[pos + 2] == '3')
        {
            int size = (d[pos + 6] & 0x7F) << 21 | (d[pos + 7] & 0x7F) << 14 | (d[pos + 8] & 0x7F) << 7 | (d[pos + 9] & 0x7F);
            bool footer = (d[pos + 5] & 0x10) != 0;
            pos += 10 + size + (footer ? 10 : 0);
        }
        return Math.Min(pos, d.Length);
    }

    private static int AudioEnd(ReadOnlySpan<byte> d, int start)
    {
        int end = d.Length;
        if (end - start >= 128 && d.Slice(end - 128, 3).SequenceEqual("TAG"u8)) end -= 128;

        // APEv2 tag footer: "APETAGEX", version, tag size (excluding header), item count, flags.
        if (end - start >= 32 && d.Slice(end - 32, 8).SequenceEqual("APETAGEX"u8))
        {
            int size = BitConverter.ToInt32(d.Slice(end - 32 + 12, 4));
            int flags = BitConverter.ToInt32(d.Slice(end - 32 + 20, 4));
            bool hasHeader = (flags & unchecked((int)0x80000000)) != 0;
            int tagTotal = size + (hasHeader ? 32 : 0);
            if (size > 0 && tagTotal <= end - start) end -= tagTotal;
        }

        // Lyrics3 v2: "...LYRICS200" just before the end.
        if (end - start >= 15 && d.Slice(end - 9, 9).SequenceEqual("LYRICS200"u8) &&
            int.TryParse(System.Text.Encoding.ASCII.GetString(d.Slice(end - 15, 6)), out int lyricsSize) &&
            lyricsSize + 15 <= end - start)
            end -= lyricsSize + 15;
        return end;
    }
}
