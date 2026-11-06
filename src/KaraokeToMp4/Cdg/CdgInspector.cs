namespace KaraokeToMp4.Cdg;

/// <summary>Health report for a .cdg file (see <see cref="CdgInspector.Inspect(byte[])"/>).</summary>
public sealed record CdgReport
{
    /// <summary>Total 24-byte packets in the file.</summary>
    public int Packets { get; init; }

    /// <summary>Packets that carry graphics instructions.</summary>
    public int GraphicsPackets { get; init; }

    /// <summary>Playback length implied by the packet count.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// False when the ripper zeroed/stripped the error-correction bytes, so damage can't be detected.
    /// </summary>
    public bool HasParity { get; init; }

    /// <summary>Damaged packets that error correction can repair.</summary>
    public int Repairable { get; init; }

    /// <summary>Packets too damaged to repair (they are skipped when converting).</summary>
    public int Unrepairable { get; init; }

    /// <summary>Graphics packets with an instruction code that doesn't exist (after repair).</summary>
    public int UnknownInstructions { get; init; }

    /// <summary>Tile packets aimed outside the screen (after repair).</summary>
    public int OffScreenTiles { get; init; }

    /// <summary>Leftover bytes that don't make up a whole packet (the file was cut off mid-packet).</summary>
    public int TrailingBytes { get; init; }

    /// <summary>Times of the damaged packets (for locating visual glitches).</summary>
    public IReadOnlyList<TimeSpan> DamageTimes { get; init; } = Array.Empty<TimeSpan>();

    /// <summary>
    /// True when nothing is wrong (or nothing could be checked, see <see cref="HasParity"/>). On rips with
    /// error-correction data, unknown instructions and off-screen tiles that pass the check are deliberate
    /// (some producers use vendor-specific codes) and are ignored, just as a real player ignores them.
    /// </summary>
    public bool IsClean => Repairable == 0 && Unrepairable == 0 && TrailingBytes == 0 &&
                           (HasParity || (UnknownInstructions == 0 && OffScreenTiles == 0));

    /// <summary>One-line, human readable summary.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (!HasParity) parts.Add("no error-correction data in this rip, so damage can't be detected");
            if (Repairable > 0) parts.Add($"{Repairable} damaged packet{(Repairable == 1 ? "" : "s")} (repaired automatically)");
            if (Unrepairable > 0) parts.Add($"{Unrepairable} packet{(Unrepairable == 1 ? "" : "s")} too damaged to repair (skipped)");
            var ignored = HasParity ? " (not damage; ignored as on a real player)" : " (possible damage)";
            if (UnknownInstructions > 0) parts.Add($"{UnknownInstructions} packet{(UnknownInstructions == 1 ? "" : "s")} with a non-standard instruction{ignored}");
            if (OffScreenTiles > 0) parts.Add($"{OffScreenTiles} off-screen tile{(OffScreenTiles == 1 ? "" : "s")}{ignored}");
            if (TrailingBytes > 0) parts.Add($"file ends mid-packet ({TrailingBytes} extra bytes)");
            if (parts.Count == 0) return "OK - no damage found";
            var prefix = IsClean ? "OK - " : "";
            return prefix + string.Join("; ", parts) + ".";
        }
    }
}

/// <summary>Checks CD+G files for damage and repairs what can be repaired.</summary>
public static class CdgInspector
{
    private static readonly HashSet<int> KnownInstructions = new() { 1, 2, 6, 20, 24, 28, 30, 31, 38 };

    public static CdgReport Inspect(string path) => Inspect(File.ReadAllBytes(path));

    /// <summary>Analyses <paramref name="data"/> without modifying it.</summary>
    public static CdgReport Inspect(byte[] data)
    {
        var copy = (byte[])data.Clone();
        return RepairInPlace(copy);
    }

    /// <summary>
    /// Repairs damaged packets in <paramref name="data"/> in place. Packets that can't be repaired are
    /// blanked so they don't draw garbage. Files without parity data are left untouched.
    /// </summary>
    public static CdgReport RepairInPlace(byte[] data)
    {
        int count = data.Length / CdgDecoder.PacketSize;
        bool hasParity = DetectParity(data, count);

        int graphics = 0, repaired = 0, unrepairable = 0, unknown = 0, offscreen = 0;
        var times = new List<TimeSpan>();

        for (int i = 0; i < count; i++)
        {
            var packet = data.AsSpan(i * CdgDecoder.PacketSize, CdgDecoder.PacketSize);
            if (hasParity)
            {
                var status = CdgParity.Repair(packet);
                if (status != CdgPacketStatus.Ok)
                {
                    times.Add(TimeSpan.FromSeconds((double)i / CdgDecoder.PacketsPerSecond));
                    if (status == CdgPacketStatus.Repaired)
                    {
                        repaired++;
                    }
                    else
                    {
                        unrepairable++;
                        packet.Clear();
                        continue;
                    }
                }
            }

            if ((packet[0] & 0x3F) != 0x09) continue;
            graphics++;
            int instruction = packet[1] & 0x3F;
            if (!KnownInstructions.Contains(instruction)) { unknown++; continue; }
            if (instruction is 6 or 38 && ((packet[6] & 0x1F) >= CdgDecoder.Rows || (packet[7] & 0x3F) >= CdgDecoder.Columns))
                offscreen++;
        }

        return new CdgReport
        {
            Packets = count,
            GraphicsPackets = graphics,
            Duration = TimeSpan.FromSeconds((double)count / CdgDecoder.PacketsPerSecond),
            HasParity = hasParity,
            Repairable = repaired,
            Unrepairable = unrepairable,
            UnknownInstructions = unknown,
            OffScreenTiles = offscreen,
            TrailingBytes = data.Length % CdgDecoder.PacketSize,
            DamageTimes = times,
        };
    }

    /// <summary>
    /// A rip "has parity" when most graphics packets pass the parity check. Rippers that strip the
    /// parity bytes leave them all zero, which would otherwise look like every packet is damaged.
    /// </summary>
    private static bool DetectParity(byte[] data, int count)
    {
        int graphics = 0, valid = 0;
        for (int i = 0; i < count; i++)
        {
            var packet = data.AsSpan(i * CdgDecoder.PacketSize, CdgDecoder.PacketSize);
            if ((packet[0] & 0x3F) != 0x09) continue;
            graphics++;
            if (CdgParity.IsValid(packet)) valid++;
        }
        return graphics > 0 && valid * 2 > graphics;
    }
}
