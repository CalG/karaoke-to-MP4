using System.Buffers.Binary;

namespace KaraokeToMp4.Midi;

/// <summary>A meta or channel event with its absolute tick position.</summary>
/// <param name="Tick">Absolute time in MIDI ticks.</param>
/// <param name="Track">Index of the track the event came from.</param>
/// <param name="Status">Status byte (0xFF for meta events, 0xF0/0xF7 for SysEx).</param>
/// <param name="MetaType">Meta event type when <paramref name="Status"/> is 0xFF, otherwise 0.</param>
/// <param name="Data">Event payload (for meta events, the bytes after the length).</param>
public sealed record MidiEvent(long Tick, int Track, byte Status, byte MetaType, byte[] Data)
{
    public bool IsMeta => Status == 0xFF;
}

/// <summary>
/// Minimal Standard MIDI File (SMF) reader. It keeps every event with absolute tick times and
/// builds a tempo map so tick positions can be turned into seconds. Audio synthesis is done
/// separately (see <see cref="MidiAudioRenderer"/>); this class only exists to get at lyrics,
/// text and timing information.
/// </summary>
public sealed class StandardMidiFile
{
    public const byte MetaText = 0x01;
    public const byte MetaCopyright = 0x02;
    public const byte MetaTrackName = 0x03;
    public const byte MetaLyric = 0x05;
    public const byte MetaEndOfTrack = 0x2F;
    public const byte MetaTempo = 0x51;

    private readonly List<(long Tick, double Seconds, int MicrosPerQuarter)> _tempoMap = new();

    public int Format { get; }
    public int TrackCount { get; }

    /// <summary>Ticks per quarter note (only meaningful when <see cref="IsSmpte"/> is false).</summary>
    public int TicksPerQuarter { get; }

    /// <summary>True when the header division is SMPTE-based rather than metrical.</summary>
    public bool IsSmpte { get; }

    /// <summary>Ticks per second for SMPTE time division.</summary>
    public double SmpteTicksPerSecond { get; }

    /// <summary>All events from all tracks, sorted by tick (stable within a track).</summary>
    public IReadOnlyList<MidiEvent> Events { get; }

    /// <summary>Absolute tick of the last event in the file.</summary>
    public long LastTick { get; }

    /// <summary>
    /// Damage found while reading (truncated file, track without an end marker, garbage chunks).
    /// The parser keeps everything readable up to the damage, so such files still convert, but the
    /// song may stop early.
    /// </summary>
    public IReadOnlyList<string> Problems => _problems;

    /// <summary>True when <see cref="Problems"/> is not empty.</summary>
    public bool IsDamaged => _problems.Count > 0;

    /// <summary>
    /// Small faults that were worked around (e.g. a malformed text event that was skipped). The file
    /// should still play normally.
    /// </summary>
    public IReadOnlyList<string> Repairs => _repairs;

    private readonly List<string> _repairs = new();

    private readonly List<string> _problems = new();
    private readonly short _division;

    public StandardMidiFile(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var span = data.AsSpan();
        int pos = 0;

        // Some files are wrapped in a RIFF 'RMID' container.
        if (span.Length >= 12 && span[..4].SequenceEqual("RIFF"u8) && span.Slice(8, 4).SequenceEqual("RMID"u8))
        {
            int idx = data.AsSpan().IndexOf("MThd"u8);
            if (idx < 0) throw new InvalidDataException("RMID file contains no MThd chunk.");
            pos = idx;
        }

        if (span.Length < pos + 14 || !span.Slice(pos, 4).SequenceEqual("MThd"u8))
            throw new InvalidDataException("Not a standard MIDI file (missing MThd header).");

        int headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(span.Slice(pos + 4));
        Format = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(pos + 8));
        int declaredTracks = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(pos + 10));
        short division = BinaryPrimitives.ReadInt16BigEndian(span.Slice(pos + 12));
        _division = division;
        if (division < 0)
        {
            IsSmpte = true;
            int fps = -(sbyte)(division >> 8);
            int ticksPerFrame = division & 0xFF;
            SmpteTicksPerSecond = (fps == 29 ? 29.97 : fps) * ticksPerFrame;
        }
        else
        {
            TicksPerQuarter = division == 0 ? 480 : division;
        }
        pos += 8 + headerLen;

        var events = new List<MidiEvent>();
        int track = 0;
        // Stop once every track the header declares has been read; some files carry a few stray bytes at the end.
        while (pos + 8 <= span.Length && track < Math.Max(declaredTracks, 1))
        {
            var id = span.Slice(pos, 4);
            uint declared = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(pos + 4));
            int len = (int)Math.Min(declared, (uint)(span.Length - pos - 8));
            bool isTrack = id.SequenceEqual("MTrk"u8);
            if (declared > (uint)len)
            {
                _problems.Add(isTrack
                    ? $"Track {track + 1} needs {declared:N0} bytes but the file ends after {len:N0}: the file is truncated ({span.Length:N0} bytes)."
                    : $"Unexpected data at byte {pos:N0}: the file is damaged from there on.");
            }
            pos += 8;
            if (isTrack)
            {
                var trackEvents = new List<MidiEvent>();
                bool complete = ReadTrack(span.Slice(pos, len), track, trackEvents, _repairs);
                if (!complete && declared <= (uint)len)
                    _problems.Add($"Track {track + 1} has no End-of-Track marker (damaged track).");
                if (!complete || declared > (uint)len)
                    trackEvents = DropGarbageTail(trackEvents, division);
                events.AddRange(trackEvents);
                track++;
            }
            pos += len;
        }
        TrackCount = track;
        if (declaredTracks > track)
            _problems.Add($"The header lists {declaredTracks} tracks but only {track} were found.");

        // Stable sort by tick: List.Sort isn't stable, so sort with the original index as tiebreak.
        var indexed = events.Select((e, i) => (e, i)).ToList();
        indexed.Sort((a, b) => a.e.Tick != b.e.Tick ? a.e.Tick.CompareTo(b.e.Tick) : a.i.CompareTo(b.i));
        Events = indexed.Select(x => x.e).ToList();
        // Ignore End-of-Track markers: some files park one far past the music (e.g. 18 minutes into a
        // 4-minute song), which would otherwise pad the video with silence.
        LastTick = Events.Where(e => !(e.IsMeta && e.MetaType == MetaEndOfTrack)).Select(e => e.Tick).DefaultIfEmpty(0).Max();
        BuildTempoMap();
    }

    public static StandardMidiFile FromFile(string path) => new(File.ReadAllBytes(path));

    /// <summary>Total length of the file in seconds.</summary>
    public double DurationSeconds => TicksToSeconds(LastTick);

    /// <summary>Converts an absolute tick position into seconds using the tempo map.</summary>
    public double TicksToSeconds(long tick)
    {
        if (IsSmpte) return tick / SmpteTicksPerSecond;

        // Binary search for the last tempo change at or before tick.
        int lo = 0, hi = _tempoMap.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_tempoMap[mid].Tick <= tick) lo = mid; else hi = mid - 1;
        }
        var t = _tempoMap[lo];
        return t.Seconds + (tick - t.Tick) * t.MicrosPerQuarter / 1_000_000.0 / TicksPerQuarter;
    }

    private void BuildTempoMap()
    {
        _tempoMap.Add((0, 0, 500_000)); // Default 120 BPM.
        if (IsSmpte) return;
        foreach (var e in Events)
        {
            if (!e.IsMeta || e.MetaType != MetaTempo || e.Data.Length < 3) continue;
            int micros = (e.Data[0] << 16) | (e.Data[1] << 8) | e.Data[2];
            if (micros <= 0) continue;
            double seconds = TicksToSeconds(e.Tick);
            if (_tempoMap[^1].Tick == e.Tick)
                _tempoMap[^1] = (e.Tick, seconds, micros);
            else
                _tempoMap.Add((e.Tick, seconds, micros));
        }
    }

    /// <summary>
    /// In a damaged track the bytes after the damage decode as nonsense, typically one enormous time gap
    /// that would make the song last for hours. Keep events only up to the first gap longer than 64 beats.
    /// </summary>
    private static List<MidiEvent> DropGarbageTail(List<MidiEvent> trackEvents, short division)
    {
        long maxGap = division > 0 ? 64L * division : 30L * 25 * 40; // 64 beats, or ~30 s for SMPTE timing
        long prev = 0;
        for (int i = 0; i < trackEvents.Count; i++)
        {
            if (trackEvents[i].Tick - prev > maxGap) return trackEvents.GetRange(0, i);
            prev = trackEvents[i].Tick;
        }
        return trackEvents;
    }

    /// <summary>Finds the next "delta 0, meta event" (00 FF type) at or after <paramref name="from"/>.</summary>
    private static int FindNextMeta(ReadOnlySpan<byte> s, int from)
    {
        for (int i = from; i + 2 < s.Length; i++)
        {
            if (s[i] == 0x00 && s[i + 1] == 0xFF && s[i + 2] < 0x80) return i;
        }
        return -1;
    }

    /// <returns>True if the track ended with an End-of-Track event.</returns>
    private static bool ReadTrack(ReadOnlySpan<byte> s, int track, List<MidiEvent> events, List<string> repairs)
    {
        int pos = 0;
        long tick = 0;
        byte running = 0;
        while (pos < s.Length)
        {
            int eventStart = pos;
            if (!TryReadVarLen(s, ref pos, out int delta)) break;
            tick += delta;
            if (pos >= s.Length) break;

            byte status = s[pos];
            if (status < 0x80)
            {
                // Running status: reuse the previous channel status byte.
                if (running == 0)
                {
                    // Malformed data (e.g. a text event whose length byte is too small, leaving its
                    // remaining characters in the stream). Skip ahead to the next meta event and carry on.
                    int next = FindNextMeta(s, eventStart + 1);
                    if (next < 0) break;
                    repairs.Add($"Track {track + 1}: skipped {next - eventStart} bytes of malformed data at byte {eventStart:N0} of the track.");
                    tick -= delta;
                    pos = next;
                    continue;
                }
                status = running;
            }
            else
            {
                pos++;
            }

            if (status == 0xFF)
            {
                if (pos >= s.Length) break;
                byte type = s[pos++];
                if (!TryReadVarLen(s, ref pos, out int len)) break;
                len = Math.Min(len, s.Length - pos);
                events.Add(new MidiEvent(tick, track, status, type, s.Slice(pos, len).ToArray()));
                pos += len;
                if (type == MetaEndOfTrack) return true;
            }
            else if (status == 0xF0 || status == 0xF7)
            {
                if (!TryReadVarLen(s, ref pos, out int len)) break;
                len = Math.Min(len, s.Length - pos);
                events.Add(new MidiEvent(tick, track, status, 0, s.Slice(pos, len).ToArray()));
                pos += len;
            }
            else
            {
                running = status;
                int kind = status & 0xF0;
                int dataLen = kind is 0xC0 or 0xD0 ? 1 : 2;
                if (pos + dataLen > s.Length) break;
                events.Add(new MidiEvent(tick, track, status, 0, s.Slice(pos, dataLen).ToArray()));
                pos += dataLen;
            }
        }
        return false;
    }

    /// <summary>
    /// Writes the events back out as a clean, well-formed format-1 MIDI file (no running status, every
    /// track closed with End-of-Track). Used to hand damaged files to the synthesiser.
    /// </summary>
    public byte[] ToBytes()
    {
        using var ms = new MemoryStream();
        void U32(uint v) { ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        void U16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }

        int tracks = Math.Max(TrackCount, 1);
        ms.Write("MThd"u8);
        U32(6);
        U16(tracks == 1 && Format == 0 ? 0 : 1);
        U16(tracks);
        U16(_division);

        var byTrack = Events.GroupBy(e => e.Track).ToDictionary(g => g.Key, g => g.ToList());
        for (int t = 0; t < tracks; t++)
        {
            var body = new List<byte>();
            long last = 0;
            long endTick = 0;
            foreach (var e in byTrack.GetValueOrDefault(t) ?? new List<MidiEvent>())
            {
                if (e.IsMeta && e.MetaType == MetaEndOfTrack) continue; // re-added below, right after the last event
                endTick = Math.Max(endTick, e.Tick);
                bool system = e.Status >= 0xF0 && !e.IsMeta && e.Status is not (0xF0 or 0xF7);
                if (system) continue; // stray system real-time/common bytes from damaged data

                WriteVarLen(body, (int)Math.Min(e.Tick - last, 0x0FFFFFFF));
                last = e.Tick;
                body.Add(e.Status);
                if (e.IsMeta)
                {
                    body.Add(e.MetaType);
                    WriteVarLen(body, e.Data.Length);
                    body.AddRange(e.Data);
                }
                else if (e.Status is 0xF0 or 0xF7)
                {
                    WriteVarLen(body, e.Data.Length);
                    body.AddRange(e.Data);
                }
                else
                {
                    foreach (var b in e.Data) body.Add((byte)(b & 0x7F));
                }
            }
            WriteVarLen(body, (int)Math.Min(endTick - last, 0x0FFFFFFF));
            body.AddRange(new byte[] { 0xFF, MetaEndOfTrack, 0x00 });

            ms.Write("MTrk"u8);
            U32((uint)body.Count);
            ms.Write(body.ToArray());
        }
        return ms.ToArray();
    }

    private static void WriteVarLen(List<byte> b, int value)
    {
        var tmp = new Stack<byte>();
        tmp.Push((byte)(value & 0x7F));
        while ((value >>= 7) > 0) tmp.Push((byte)((value & 0x7F) | 0x80));
        b.AddRange(tmp);
    }

    private static bool TryReadVarLen(ReadOnlySpan<byte> s, ref int pos, out int value)
    {
        value = 0;
        for (int i = 0; i < 4; i++)
        {
            if (pos >= s.Length) return false;
            byte b = s[pos++];
            value = (value << 7) | (b & 0x7F);
            if ((b & 0x80) == 0) return true;
        }
        return true;
    }
}
