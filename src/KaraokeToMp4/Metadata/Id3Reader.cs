using System.Text;

namespace KaraokeToMp4.Metadata;

/// <summary>Reads the title and artist from ID3v2 (2.2/2.3/2.4) or ID3v1 tags in an MP3.</summary>
public static class Id3Reader
{
    private const int MaxTagSize = 16 * 1024 * 1024;

    public static SongInfo Read(string path)
    {
        using var fs = File.OpenRead(path);
        return Read(fs);
    }

    /// <summary>Reads tags from a stream. ID3v1 is only checked when the stream is seekable.</summary>
    public static SongInfo Read(Stream stream)
    {
        string? title = null, artist = null, albumArtist = null;

        var header = new byte[10];
        if (ReadFully(stream, header) == 10 && header[0] == 'I' && header[1] == 'D' && header[2] == '3')
        {
            int major = header[3];
            int flags = header[5];
            int size = SyncSafe(header, 6);
            if (major is >= 2 and <= 4 && size > 0 && size <= MaxTagSize)
            {
                var tag = new byte[size];
                int read = ReadFully(stream, tag);
                var data = tag.AsSpan(0, read).ToArray();
                if ((flags & 0x80) != 0 && major < 4) data = RemoveUnsync(data);
                ParseFrames(data, major, flags, ref title, ref artist, ref albumArtist);
            }
        }

        artist ??= albumArtist;
        if ((title == null || artist == null) && stream.CanSeek && stream.Length >= 128)
        {
            stream.Seek(-128, SeekOrigin.End);
            var v1 = new byte[128];
            if (ReadFully(stream, v1) == 128 && v1[0] == 'T' && v1[1] == 'A' && v1[2] == 'G')
            {
                title ??= Clean(Encoding.Latin1.GetString(v1, 3, 30));
                artist ??= Clean(Encoding.Latin1.GetString(v1, 33, 30));
            }
        }

        return title == null && artist == null ? SongInfo.Empty : new SongInfo(title, artist, "ID3 tags");
    }

    private static void ParseFrames(byte[] data, int major, int tagFlags, ref string? title, ref string? artist, ref string? albumArtist)
    {
        int pos = 0;
        if ((tagFlags & 0x40) != 0 && major >= 3 && data.Length >= 4)
        {
            // Extended header: v2.3 size excludes its own 4 bytes, v2.4 size (syncsafe) includes them.
            pos = major == 3 ? 4 + ReadBigEndian(data, 0, 4) : SyncSafe(data, 0);
        }

        int idLen = major == 2 ? 3 : 4;
        int headerLen = major == 2 ? 6 : 10;
        while (pos + headerLen <= data.Length)
        {
            if (data[pos] == 0) break; // Padding.
            string id = Encoding.ASCII.GetString(data, pos, idLen);
            int size = major switch
            {
                2 => ReadBigEndian(data, pos + 3, 3),
                3 => ReadBigEndian(data, pos + 4, 4),
                _ => SyncSafe(data, pos + 4),
            };
            int frameFlags = major >= 3 ? (data[pos + 8] << 8) | data[pos + 9] : 0;
            pos += headerLen;
            if (size <= 0 || pos + size > data.Length) break;

            var body = data.AsSpan(pos, size).ToArray();
            pos += size;
            if (major == 4 && (frameFlags & 0x0002) != 0) body = RemoveUnsync(body);
            bool compressedOrEncrypted = major == 3 ? (frameFlags & 0x00C0) != 0 : major == 4 && (frameFlags & 0x000C) != 0;
            if (compressedOrEncrypted) continue;

            switch (id)
            {
                case "TIT2" or "TT2": title ??= DecodeText(body); break;
                case "TPE1" or "TP1": artist ??= DecodeText(body); break;
                case "TPE2" or "TP2": albumArtist ??= DecodeText(body); break;
            }
        }
    }

    private static string? DecodeText(byte[] body)
    {
        if (body.Length < 2) return null;
        byte encoding = body[0];
        var bytes = body.AsSpan(1);
        string text = encoding switch
        {
            0 => Encoding.Latin1.GetString(bytes),
            1 => DecodeUtf16WithBom(bytes),
            2 => Encoding.BigEndianUnicode.GetString(bytes),
            3 => Encoding.UTF8.GetString(bytes),
            _ => Encoding.Latin1.GetString(bytes),
        };
        // v2.4 allows several values separated by NUL; keep the first.
        int nul = text.IndexOf('\0');
        if (nul >= 0) text = text[..nul];
        return Clean(text);
    }

    private static string DecodeUtf16WithBom(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes[2..]);
        return Encoding.Unicode.GetString(bytes);
    }

    private static string? Clean(string s)
    {
        s = s.Replace("\0", "").Trim();
        return s.Length == 0 ? null : s;
    }

    private static byte[] RemoveUnsync(byte[] data)
    {
        var result = new List<byte>(data.Length);
        for (int i = 0; i < data.Length; i++)
        {
            result.Add(data[i]);
            if (data[i] == 0xFF && i + 1 < data.Length && data[i + 1] == 0x00) i++;
        }
        return result.ToArray();
    }

    private static int SyncSafe(byte[] b, int i) =>
        (b[i] & 0x7F) << 21 | (b[i + 1] & 0x7F) << 14 | (b[i + 2] & 0x7F) << 7 | (b[i + 3] & 0x7F);

    private static int ReadBigEndian(byte[] b, int i, int count)
    {
        int v = 0;
        for (int k = 0; k < count; k++) v = (v << 8) | b[i + k];
        return v;
    }

    private static int ReadFully(Stream s, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = s.Read(buffer, total, buffer.Length - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
