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

    /// <summary>
    /// True when the rip carries only the short "Q" check data (protecting each packet's command and
    /// instruction) but not the full "P" data. Instructions can then be verified and repaired; the picture
    /// data can't.
    /// </summary>
    public bool HasQParityOnly { get; init; }

    /// <summary>Instruction bytes repaired with the Q check data (see <see cref="HasQParityOnly"/>).</summary>
    public int QRepaired { get; init; }

    /// <summary>Packets whose command/instruction the Q check data shows to be damaged beyond repair.</summary>
    public int QUnrepairable { get; init; }

    /// <summary>
    /// In a rip whose check bytes were zeroed, graphics packets where a check byte is not zero. Each one is a
    /// bit error that happened to land on a check byte; others have probably landed on picture data, where
    /// they can't be seen.
    /// </summary>
    public int StrayCheckBits { get; init; }

    /// <summary>Damaged packets that error correction can repair.</summary>
    public int Repairable { get; init; }

    /// <summary>
    /// Graphics packets too damaged to repair or recover; they are left blank (a small gap in the graphics).
    /// </summary>
    public int Unrepairable { get; init; }

    /// <summary>
    /// Graphics packets beyond normal repair that were rebuilt anyway: the damage was located using the
    /// error pattern of neighbouring packets, and the result was confirmed by an identical "twin" packet
    /// elsewhere in the song (the same tile drawn at the same position).
    /// </summary>
    public int Recovered { get; init; }

    /// <summary>
    /// Damaged packets that carried no graphics at all (an empty packet plus a little noise); they are cleared,
    /// which restores them exactly.
    /// </summary>
    public int EmptyPacketsCleared { get; init; }

    /// <summary>Graphics packets with an instruction code that doesn't exist (after repair).</summary>
    public int UnknownInstructions { get; init; }

    /// <summary>
    /// Filler packets: an unused instruction code with every data symbol the same (e.g. Music Maestro
    /// discs fill unused graphics slots with packets whose symbols are all 0x09). Ignored when rendering.
    /// </summary>
    public int FillerPackets { get; init; }

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
                           QRepaired == 0 && QUnrepairable == 0 && StrayCheckBits == 0 &&
                           (HasParity || (UnknownInstructions == 0 && OffScreenTiles == 0));

    /// <summary>One-line, human readable summary.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (!HasParity)
                parts.Add(HasQParityOnly
                    ? "only partial error-correction data (Q) in this rip: instructions are checked, picture damage can't be detected"
                    : "no error-correction data in this rip, so damage can't be detected");
            if (QRepaired > 0) parts.Add($"{QRepaired} damaged instruction{(QRepaired == 1 ? "" : "s")} (repaired with the Q check data)");
            if (QUnrepairable > 0) parts.Add($"{QUnrepairable} packet{(QUnrepairable == 1 ? "" : "s")} with a damaged instruction that can't be repaired");
            if (StrayCheckBits > 0) parts.Add($"{StrayCheckBits} packet{(StrayCheckBits == 1 ? " shows" : "s show")} bit errors in the empty check bytes (more may be hidden in the graphics)");
            if (Repairable > 0) parts.Add($"{Repairable} damaged packet{(Repairable == 1 ? "" : "s")} (repaired automatically)");
            if (Recovered > 0) parts.Add($"{Recovered} badly damaged packet{(Recovered == 1 ? "" : "s")} rebuilt from twin packets");
            if (Unrepairable > 0) parts.Add($"{Unrepairable} graphics packet{(Unrepairable == 1 ? "" : "s")} too damaged to repair (left blank)");
            var ignored = HasParity ? " (not damage; ignored as on a real player)" : " (possible damage)";
            if (FillerPackets > 0) parts.Add($"{FillerPackets} filler packet{(FillerPackets == 1 ? "" : "s")} (normal for some producers, e.g. Music Maestro; ignored)");
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

        int repaired = 0;
        var times = new List<TimeSpan>();
        var lost = new List<int>();
        var errorPositions = new Dictionary<int, int[]>();

        // Pass 1: ordinary error correction (up to two bad symbols per packet).
        if (hasParity)
        {
            Span<byte> before = stackalloc byte[CdgDecoder.PacketSize];
            for (int i = 0; i < count; i++)
            {
                var packet = data.AsSpan(i * CdgDecoder.PacketSize, CdgDecoder.PacketSize);
                packet.CopyTo(before);
                var status = CdgParity.Repair(packet);
                if (status == CdgPacketStatus.Ok) continue;
                times.Add(TimeSpan.FromSeconds((double)i / CdgDecoder.PacketsPerSecond));
                if (status == CdgPacketStatus.Repaired)
                {
                    repaired++;
                    var positions = new List<int>(2);
                    for (int j = 0; j < CdgDecoder.PacketSize; j++) if (before[j] != packet[j]) positions.Add(j);
                    errorPositions[i] = positions.ToArray();
                }
                else
                {
                    lost.Add(i);
                }
            }
        }

        // Without full check data: use partial (Q) check data if present, else look for stray bits.
        int qRepaired = 0, qUnrepairable = 0, strayBits = 0;
        bool hasQ = false;
        if (!hasParity)
        {
            int graphicsPackets = 0, qValid = 0, checkBytesZero = 0;
            for (int i = 0; i < count; i++)
            {
                var packet = data.AsSpan(i * CdgDecoder.PacketSize, CdgDecoder.PacketSize);
                if ((packet[0] & 0x3F) != 0x09) continue;
                graphicsPackets++;
                if (CdgParity.QValid(packet)) qValid++;
                if (CheckBytesZero(packet)) checkBytesZero++;
            }
            hasQ = graphicsPackets > 0 && qValid * 2 > graphicsPackets;
            bool zeroedRip = graphicsPackets > 0 && checkBytesZero >= graphicsPackets * 0.9;

            for (int i = 0; i < count; i++)
            {
                var packet = data.AsSpan(i * CdgDecoder.PacketSize, CdgDecoder.PacketSize);
                if ((packet[0] & 0x3F) != 0x09) continue;
                if (hasQ)
                {
                    var status = CdgParity.RepairQ(packet);
                    if (status == CdgPacketStatus.Ok) continue;
                    times.Add(TimeSpan.FromSeconds((double)i / CdgDecoder.PacketsPerSecond));
                    if (status == CdgPacketStatus.Repaired) qRepaired++;
                    else { qUnrepairable++; packet.Clear(); } // a wrong instruction would draw garbage
                }
                else if (zeroedRip && !CheckBytesZero(packet))
                {
                    strayBits++;
                    times.Add(TimeSpan.FromSeconds((double)i / CdgDecoder.PacketsPerSecond));
                }
            }
        }

        // Pass 2: packets beyond ordinary repair.
        int recovered = 0, emptyCleared = 0, unrepairable = 0;
        if (lost.Count > 0)
        {
            var twins = IndexTiles(data, count, lost);
            foreach (int i in lost)
            {
                var packet = data.AsSpan(i * CdgDecoder.PacketSize, CdgDecoder.PacketSize);
                if (IsNoiseInEmptyPacket(packet))
                {
                    emptyCleared++;
                    packet.Clear();
                }
                else if (TryRecover(packet, i, errorPositions, twins))
                {
                    recovered++;
                }
                else
                {
                    unrepairable++;
                    packet.Clear(); // a blank packet draws nothing, which is better than drawing garbage
                }
            }
        }

        // Pass 3: statistics on the (repaired) graphics.
        int graphics = 0, unknown = 0, offscreen = 0, filler = 0;
        for (int i = 0; i < count; i++)
        {
            var packet = data.AsSpan(i * CdgDecoder.PacketSize, CdgDecoder.PacketSize);
            if ((packet[0] & 0x3F) != 0x09) continue;
            graphics++;
            int instruction = packet[1] & 0x3F;
            if (!KnownInstructions.Contains(instruction))
            {
                if (IsFiller(packet)) filler++; else unknown++;
                continue;
            }
            if (instruction is 6 or 38 && ((packet[6] & 0x1F) >= CdgDecoder.Rows || (packet[7] & 0x3F) >= CdgDecoder.Columns))
                offscreen++;
        }

        return new CdgReport
        {
            Packets = count,
            GraphicsPackets = graphics,
            Duration = TimeSpan.FromSeconds((double)count / CdgDecoder.PacketsPerSecond),
            HasParity = hasParity,
            Repairable = repaired + emptyCleared,
            Recovered = recovered,
            EmptyPacketsCleared = emptyCleared,
            Unrepairable = unrepairable,
            HasQParityOnly = hasQ,
            QRepaired = qRepaired,
            QUnrepairable = qUnrepairable,
            StrayCheckBits = strayBits,
            UnknownInstructions = unknown,
            FillerPackets = filler,
            OffScreenTiles = offscreen,
            TrailingBytes = data.Length % CdgDecoder.PacketSize,
            DamageTimes = times,
        };
    }

    /// <summary>True when the packet's check bytes (Q: 2-3, P: 20-23) are all zero, as rippers that drop them leave them.</summary>
    private static bool CheckBytesZero(ReadOnlySpan<byte> packet) =>
        (packet[2] & 0x3F) == 0 && (packet[3] & 0x3F) == 0 &&
        (packet[20] & 0x3F) == 0 && (packet[21] & 0x3F) == 0 && (packet[22] & 0x3F) == 0 && (packet[23] & 0x3F) == 0;

    /// <summary>
    /// A damaged packet with at most 4 non-zero symbols holds no graphics: it is an empty packet with noise.
    /// (A real graphics packet has a command, an instruction, colours, a position and pixel rows.)
    /// </summary>
    private static bool IsNoiseInEmptyPacket(ReadOnlySpan<byte> packet)
    {
        int nonZero = 0;
        foreach (var b in packet) if ((b & 0x3F) != 0) nonZero++;
        return nonZero <= 4;
    }

    /// <summary>Key for "the same tile at the same place": row, column and the 12 pixel rows (colours excluded).</summary>
    private static string TileKey(ReadOnlySpan<byte> packet)
    {
        Span<char> key = stackalloc char[14];
        key[0] = (char)(packet[6] & 0x1F);
        key[1] = (char)(packet[7] & 0x3F);
        for (int i = 0; i < 12; i++) key[2 + i] = (char)(packet[8 + i] & 0x3F);
        return new string(key);
    }

    private static bool IsTile(ReadOnlySpan<byte> packet) =>
        (packet[0] & 0x3F) == 0x09 && (packet[1] & 0x3F) is 6 or 38 &&
        (packet[6] & 0x1F) < CdgDecoder.Rows && (packet[7] & 0x3F) < CdgDecoder.Columns;

    /// <summary>All intact tile packets (lost ones excluded), keyed by position and pixels.</summary>
    private static HashSet<string> IndexTiles(byte[] data, int count, List<int> lost)
    {
        var lostSet = new HashSet<int>(lost);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            if (lostSet.Contains(i)) continue;
            var packet = data.AsSpan(i * CdgDecoder.PacketSize, CdgDecoder.PacketSize);
            if (IsTile(packet)) keys.Add(TileKey(packet));
        }
        return keys;
    }

    /// <summary>
    /// Tries to rebuild a packet with 3-4 damaged symbols. The CD's subcode interleaving spreads one scratch
    /// over neighbouring packets at 8-symbol spacing, so the positions repaired in nearby packets predict
    /// where this one is damaged. A candidate is accepted only if it is a valid tile that exactly matches a
    /// twin tile elsewhere in the song, and only if exactly one candidate does.
    /// </summary>
    private static bool TryRecover(Span<byte> packet, int index, Dictionary<int, int[]> errorPositions, HashSet<string> twins)
    {
        Span<int> syndromes = stackalloc int[4];
        CdgParity.Syndromes(packet, syndromes);

        // Likely damaged positions: those repaired within 8 packets either side, and the same positions
        // shifted by the 8-symbol interleave step.
        var weight = new Dictionary<int, int>();
        for (int j = index - 8; j <= index + 8; j++)
        {
            if (!errorPositions.TryGetValue(j, out var positions)) continue;
            foreach (int p in positions)
                for (int shift = -16; shift <= 16; shift += 8)
                    if (p + shift is >= 0 and < CdgDecoder.PacketSize)
                        weight[p + shift] = weight.GetValueOrDefault(p + shift) + 1;
        }
        var likely = weight.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(10).Select(kv => kv.Key).ToArray();

        var patterns = new List<int[]>();
        foreach (var combo in Combinations(Enumerable.Range(0, CdgDecoder.PacketSize).ToArray(), 3)) patterns.Add(combo);
        foreach (var combo in Combinations(likely, 4)) patterns.Add(combo);

        Span<byte> candidate = stackalloc byte[CdgDecoder.PacketSize];
        Span<int> values = stackalloc int[4];
        byte[]? match = null;
        foreach (var positions in patterns)
        {
            if (!CdgParity.TrySolveErasures(syndromes, positions, values[..positions.Length])) continue;
            bool allNonZero = true;
            for (int k = 0; k < positions.Length; k++) allNonZero &= values[k] != 0;
            if (!allNonZero) continue;

            packet.CopyTo(candidate);
            for (int k = 0; k < positions.Length; k++)
                candidate[positions[k]] = (byte)((candidate[positions[k]] & 0xC0) | ((candidate[positions[k]] ^ values[k]) & 0x3F));

            if (!CdgParity.QValid(candidate) || !IsTile(candidate) || !twins.Contains(TileKey(candidate))) continue;
            if (match != null && !candidate.SequenceEqual(match)) return false; // two different answers: don't guess
            match ??= candidate.ToArray();
        }

        if (match == null) return false;
        match.CopyTo(packet);
        return true;
    }

    private static IEnumerable<int[]> Combinations(int[] items, int k)
    {
        if (items.Length < k) yield break;
        var idx = Enumerable.Range(0, k).ToArray();
        while (true)
        {
            yield return idx.Select(i => items[i]).ToArray();
            int pos = k - 1;
            while (pos >= 0 && idx[pos] == items.Length - k + pos) pos--;
            if (pos < 0) yield break;
            idx[pos]++;
            for (int j = pos + 1; j < k; j++) idx[j] = idx[j - 1] + 1;
        }
    }

    private static bool IsFiller(ReadOnlySpan<byte> packet)
    {
        int first = packet[4] & 0x3F;
        for (int i = 5; i < 20; i++)
            if ((packet[i] & 0x3F) != first) return false;
        return true;
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
