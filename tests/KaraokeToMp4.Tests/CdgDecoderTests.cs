using KaraokeToMp4.Cdg;

namespace KaraokeToMp4.Tests;

public class CdgDecoderTests
{
    private static uint PixelBgra(byte[] frame, int x, int y, int width = CdgDecoder.Width)
    {
        int i = (y * width + x) * 4;
        return (uint)(frame[i + 2] << 16 | frame[i + 1] << 8 | frame[i]);
    }

    [Fact]
    public void Duration_IsPacketCountOver300()
    {
        var d = new CdgDecoder(new byte[24 * 600]);
        Assert.Equal(600, d.PacketCount);
        Assert.Equal(TimeSpan.FromSeconds(2), d.Duration);
    }

    [Fact]
    public void LoadColorTable_DecodesFourBitRgb()
    {
        var d = new CdgDecoder(TestFiles.LoadColors(true, (15, 0, 0), (0, 15, 0), (0, 0, 15), (8, 4, 2)));
        d.AdvanceToPacket(1);
        Assert.Equal(0xFFFF0000u, d.Palette[0]);
        Assert.Equal(0xFF00FF00u, d.Palette[1]);
        Assert.Equal(0xFF0000FFu, d.Palette[2]);
        Assert.Equal(0xFF000000u | (8 * 17u) << 16 | (4 * 17u) << 8 | 2 * 17u, d.Palette[3]);
    }

    [Fact]
    public void LoadColorTableHigh_FillsUpperEight()
    {
        var d = new CdgDecoder(TestFiles.LoadColors(false, (15, 15, 15)));
        d.AdvanceToPacket(1);
        Assert.Equal(0xFFFFFFFFu, d.Palette[8]);
    }

    [Fact]
    public void MemoryPreset_AndTile_DrawExpectedPixels()
    {
        var data = TestFiles.Concat(new[]
        {
            TestFiles.LoadColors(true, (0, 0, 0), (15, 15, 15), (15, 0, 0)),
            TestFiles.MemoryPreset(2),
            // Tile at row 1, col 1 (pixels 6..11, 12..23): left 3 pixels colour 1, right 3 colour 0.
            TestFiles.Tile(0, 1, 1, 1, 0b111000),
        });
        var d = new CdgDecoder(data);
        d.AdvanceToPacket(3);

        var frame = new byte[CdgDecoder.Width * CdgDecoder.Height * 4];
        d.RenderBgra(frame);

        Assert.Equal(0xFF0000u, PixelBgra(frame, 100, 100)); // memory preset (red)
        Assert.Equal(0xFFFFFFu, PixelBgra(frame, 6, 12));    // tile, bit set -> white
        Assert.Equal(0xFFFFFFu, PixelBgra(frame, 8, 23));
        Assert.Equal(0x000000u, PixelBgra(frame, 9, 12));    // tile, bit clear -> black
        Assert.Equal(0x000000u, PixelBgra(frame, 11, 23));
        Assert.Equal(0xFF0000u, PixelBgra(frame, 12, 12));   // next tile untouched
    }

    [Fact]
    public void XorTile_TogglesColours()
    {
        var data = TestFiles.Concat(new[]
        {
            TestFiles.MemoryPreset(0),
            TestFiles.Tile(0, 5, 2, 2, 0b111111),
            TestFiles.Tile(0, 3, 2, 2, 0b111111, xor: true),
        });
        var d = new CdgDecoder(data);
        d.AdvanceToPacket(3);
        int x = 2 * 6, y = 2 * 12;
        Assert.Equal(5 ^ 3, d.Pixels[y * CdgDecoder.Width + x]);
    }

    [Fact]
    public void BorderPreset_ColoursBorderOnly()
    {
        var data = TestFiles.Concat(new[]
        {
            TestFiles.LoadColors(true, (0, 0, 0), (0, 15, 0)),
            TestFiles.MemoryPreset(0),
            TestFiles.BorderPreset(1),
        });
        var d = new CdgDecoder(data);
        d.AdvanceToPacket(3);
        var frame = new byte[CdgDecoder.Width * CdgDecoder.Height * 4];
        d.RenderBgra(frame);
        Assert.Equal(0x00FF00u, PixelBgra(frame, 0, 0));
        Assert.Equal(0x00FF00u, PixelBgra(frame, 299, 215));
        Assert.Equal(0x000000u, PixelBgra(frame, 150, 100));
    }

    [Fact]
    public void CropBorder_OutputsSafeArea()
    {
        var d = new CdgDecoder(TestFiles.Concat(new[] { TestFiles.MemoryPreset(0) }));
        d.AdvanceToPacket(1);
        var frame = new byte[288 * 192 * 4];
        d.RenderBgra(frame, cropBorder: true);
        Assert.Throws<ArgumentException>(() => d.RenderBgra(new byte[10], cropBorder: true));
    }

    [Fact]
    public void ScrollPreset_ShiftsPictureRight()
    {
        var data = TestFiles.Concat(new[]
        {
            TestFiles.MemoryPreset(0),
            TestFiles.Tile(0, 7, 3, 3, 0b111111),
            TestFiles.CdgPacket(20, 4, 0x10, 0), // scroll right by 6 pixels, fill colour 4
        });
        var d = new CdgDecoder(data);
        d.AdvanceToPacket(3);
        int y = 3 * 12;
        Assert.Equal(7, d.Pixels[y * CdgDecoder.Width + 4 * 6]);
        Assert.Equal(0, d.Pixels[y * CdgDecoder.Width + 3 * 6]);
        Assert.Equal(4, d.Pixels[y * CdgDecoder.Width + 0]); // new column filled with preset colour
    }

    [Fact]
    public void SeekingBackwards_ReplaysFromStart()
    {
        var data = TestFiles.Concat(new[] { TestFiles.MemoryPreset(1), TestFiles.MemoryPreset(2) });
        var d = new CdgDecoder(data);
        d.AdvanceToPacket(2);
        Assert.Equal(2, d.Pixels[0]);
        d.AdvanceToPacket(1);
        Assert.Equal(1, d.Pixels[0]);
    }

    [Fact]
    public void NonGraphicsPackets_AreIgnored()
    {
        var p = TestFiles.MemoryPreset(5);
        p[0] = 0x08;
        var d = new CdgDecoder(p);
        d.AdvanceToPacket(1);
        Assert.Equal(0, d.Pixels[0]);
    }
}
