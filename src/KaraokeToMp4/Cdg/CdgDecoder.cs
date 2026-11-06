namespace KaraokeToMp4.Cdg;

/// <summary>
/// Decodes a CD+Graphics (CD+G) subcode stream into 300x216 indexed-colour frames.
/// </summary>
/// <remarks>
/// A .cdg file is a sequence of 24-byte packets played back at 300 packets per second
/// (4 packs per sector, 75 sectors per second). Only packets whose command is 0x09 (TV Graphics)
/// carry drawing instructions. The visible screen is 300x216 pixels of which the outer
/// 6-pixel/12-pixel frame is the "border" and the inner 288x192 area holds the tiles.
/// </remarks>
public sealed class CdgDecoder
{
    public const int PacketSize = 24;
    public const int PacketsPerSecond = 300;
    public const int Width = 300;
    public const int Height = 216;
    public const int BorderX = 6;
    public const int BorderY = 12;
    public const int TileWidth = 6;
    public const int TileHeight = 12;
    public const int Columns = 50;
    public const int Rows = 18;

    private const byte CdgCommand = 0x09;
    private const byte MemoryPreset = 1;
    private const byte BorderPreset = 2;
    private const byte TileBlockNormal = 6;
    private const byte ScrollPreset = 20;
    private const byte ScrollCopy = 24;
    private const byte DefineTransparent = 28;
    private const byte LoadColorTableLow = 30;
    private const byte LoadColorTableHigh = 31;
    private const byte TileBlockXor = 38;

    private readonly byte[] _data;
    private readonly byte[] _pixels = new byte[Width * Height];
    private readonly byte[] _scratch = new byte[Width * Height];
    private readonly uint[] _palette = new uint[16];
    private int _packetIndex;
    private int _hOffset;
    private int _vOffset;
    private int _borderColor;

    public CdgDecoder(byte[] cdgData)
    {
        _data = cdgData ?? throw new ArgumentNullException(nameof(cdgData));
        Reset();
    }

    public static CdgDecoder FromFile(string path) => new(File.ReadAllBytes(path));

    /// <summary>Total number of packets in the stream.</summary>
    public int PacketCount => _data.Length / PacketSize;

    /// <summary>Playback length implied by the packet count.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)PacketCount / PacketsPerSecond);

    /// <summary>Index of the next packet that will be decoded.</summary>
    public int PacketIndex => _packetIndex;

    /// <summary>Current colour palette as 0xAARRGGBB values.</summary>
    public ReadOnlySpan<uint> Palette => _palette;

    /// <summary>Raw 300x216 indexed pixel buffer (before fine-scroll offsets are applied).</summary>
    public ReadOnlySpan<byte> Pixels => _pixels;

    public void Reset()
    {
        Array.Clear(_pixels);
        for (int i = 0; i < 16; i++) _palette[i] = 0xFF000000;
        _packetIndex = 0;
        _hOffset = _vOffset = 0;
        _borderColor = 0;
    }

    /// <summary>Decodes packets until the stream position reaches <paramref name="time"/>.</summary>
    public void AdvanceTo(TimeSpan time)
    {
        long target = (long)Math.Floor(time.TotalSeconds * PacketsPerSecond);
        AdvanceToPacket((int)Math.Min(target, int.MaxValue));
    }

    /// <summary>Decodes packets until <see cref="PacketIndex"/> == <paramref name="packet"/>.</summary>
    public void AdvanceToPacket(int packet)
    {
        if (packet < _packetIndex)
            Reset();
        int end = Math.Min(packet, PacketCount);
        while (_packetIndex < end)
        {
            ProcessPacket(_data.AsSpan(_packetIndex * PacketSize, PacketSize));
            _packetIndex++;
        }
    }

    /// <summary>
    /// Writes the current screen as 32-bit BGRA pixels (byte order B,G,R,A) into <paramref name="destination"/>.
    /// </summary>
    /// <param name="cropBorder">When true only the inner 288x192 area is written.</param>
    public void RenderBgra(Span<byte> destination, bool cropBorder = false)
    {
        int outW = cropBorder ? Width - 2 * BorderX : Width;
        int outH = cropBorder ? Height - 2 * BorderY : Height;
        if (destination.Length < outW * outH * 4)
            throw new ArgumentException("Destination buffer too small.", nameof(destination));

        int x0 = cropBorder ? BorderX : 0;
        int y0 = cropBorder ? BorderY : 0;
        int di = 0;
        for (int y = 0; y < outH; y++)
        {
            int sy = y + y0;
            for (int x = 0; x < outW; x++)
            {
                int sx = x + x0;
                int index;
                bool inBorder = sx < BorderX || sx >= Width - BorderX || sy < BorderY || sy >= Height - BorderY;
                if (inBorder)
                {
                    index = _borderColor;
                }
                else
                {
                    // Fine scroll offsets shift the inner picture by up to 5 / 11 pixels.
                    int px = sx + _hOffset;
                    int py = sy + _vOffset;
                    index = px < Width && py < Height ? _pixels[py * Width + px] : _borderColor;
                }
                uint c = _palette[index & 0x0F];
                destination[di++] = (byte)c;          // B
                destination[di++] = (byte)(c >> 8);   // G
                destination[di++] = (byte)(c >> 16);  // R
                destination[di++] = 0xFF;             // A
            }
        }
    }

    private void ProcessPacket(ReadOnlySpan<byte> packet)
    {
        if ((packet[0] & 0x3F) != CdgCommand) return;
        byte instruction = (byte)(packet[1] & 0x3F);
        // Bytes 2-3 are parity Q, 4-19 are data, 20-23 are parity P.
        Span<byte> data = stackalloc byte[16];
        for (int i = 0; i < 16; i++) data[i] = (byte)(packet[4 + i] & 0x3F);

        switch (instruction)
        {
            case MemoryPreset: DoMemoryPreset(data); break;
            case BorderPreset: DoBorderPreset(data); break;
            case TileBlockNormal: DoTileBlock(data, xor: false); break;
            case TileBlockXor: DoTileBlock(data, xor: true); break;
            case ScrollPreset: DoScroll(data, copy: false); break;
            case ScrollCopy: DoScroll(data, copy: true); break;
            case LoadColorTableLow: DoLoadColors(data, 0); break;
            case LoadColorTableHigh: DoLoadColors(data, 8); break;
            case DefineTransparent: break; // Only relevant when overlaying on live video.
        }
    }

    private void DoMemoryPreset(ReadOnlySpan<byte> data)
    {
        // Memory preset is normally sent ~16 times in a row for error resilience; applying
        // every copy is harmless.
        byte color = (byte)(data[0] & 0x0F);
        Array.Fill(_pixels, color);
        _borderColor = color;
    }

    private void DoBorderPreset(ReadOnlySpan<byte> data)
    {
        byte color = (byte)(data[0] & 0x0F);
        _borderColor = color;
        for (int y = 0; y < Height; y++)
        {
            bool fullRow = y < BorderY || y >= Height - BorderY;
            int row = y * Width;
            if (fullRow)
            {
                _pixels.AsSpan(row, Width).Fill(color);
            }
            else
            {
                _pixels.AsSpan(row, BorderX).Fill(color);
                _pixels.AsSpan(row + Width - BorderX, BorderX).Fill(color);
            }
        }
    }

    private void DoTileBlock(ReadOnlySpan<byte> data, bool xor)
    {
        byte color0 = (byte)(data[0] & 0x0F);
        byte color1 = (byte)(data[1] & 0x0F);
        int row = data[2] & 0x1F;
        int col = data[3] & 0x3F;
        if (row >= Rows || col >= Columns) return;

        int x0 = col * TileWidth;
        int y0 = row * TileHeight;
        for (int y = 0; y < TileHeight; y++)
        {
            byte bits = data[4 + y];
            int rowStart = (y0 + y) * Width + x0;
            for (int x = 0; x < TileWidth; x++)
            {
                bool on = (bits & (0x20 >> x)) != 0;
                byte c = on ? color1 : color0;
                int idx = rowStart + x;
                _pixels[idx] = xor ? (byte)((_pixels[idx] ^ c) & 0x0F) : c;
            }
        }
    }

    private void DoScroll(ReadOnlySpan<byte> data, bool copy)
    {
        byte color = (byte)(data[0] & 0x0F);
        int hScroll = data[1];
        int vScroll = data[2];

        int hCmd = (hScroll & 0x30) >> 4;
        int vCmd = (vScroll & 0x30) >> 4;
        _hOffset = Math.Min(hScroll & 0x07, TileWidth - 1);
        _vOffset = Math.Min(vScroll & 0x0F, TileHeight - 1);

        int dx = hCmd switch { 1 => TileWidth, 2 => -TileWidth, _ => 0 };
        int dy = vCmd switch { 1 => TileHeight, 2 => -TileHeight, _ => 0 };
        if (dx == 0 && dy == 0) return;

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int sx = x - dx;
                int sy = y - dy;
                byte value;
                if (copy)
                {
                    sx = (sx % Width + Width) % Width;
                    sy = (sy % Height + Height) % Height;
                    value = _pixels[sy * Width + sx];
                }
                else
                {
                    value = sx >= 0 && sx < Width && sy >= 0 && sy < Height ? _pixels[sy * Width + sx] : color;
                }
                _scratch[y * Width + x] = value;
            }
        }
        Buffer.BlockCopy(_scratch, 0, _pixels, 0, _pixels.Length);
    }

    private void DoLoadColors(ReadOnlySpan<byte> data, int baseIndex)
    {
        for (int i = 0; i < 8; i++)
        {
            int high = data[2 * i];
            int low = data[2 * i + 1];
            int r = (high & 0x3C) >> 2;
            int g = ((high & 0x03) << 2) | ((low & 0x30) >> 4);
            int b = low & 0x0F;
            _palette[baseIndex + i] = 0xFF000000u | (uint)(r * 17) << 16 | (uint)(g * 17) << 8 | (uint)(b * 17);
        }
    }
}
