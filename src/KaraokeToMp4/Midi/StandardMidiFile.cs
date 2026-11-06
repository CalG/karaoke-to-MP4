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
        while (pos + 8 <= span.Length && track < Math.Max(declaredTracks, 1) + 64)
        {
            var id = span.Slice(pos, 4);
            int len = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(span.Slice(pos + 4)), (uint)(span.Length - pos - 8));
            pos += 8;
            if (id.SequenceEqual("MTrk"u8))
            {
                ReadTrack(span.Slice(pos, len), track, events);
                track++;
            }
            pos += len;
        }
        TrackCount = track;

        // Stable sort by tick: List.Sort isn't stable, so sort with the original index as tiebreak.
        var indexed = events.Select((e, i) => (e, i)).ToList();
        indexed.Sort((a, b) => a.e.Tick != b.e.Tick ? a.e.Tick.CompareTo(b.e.Tick) : a.i.CompareTo(b.i));
        Events = indexed.Select(x => x.e).ToList();
        LastTick = Events.Count == 0 ? 0 : Events[^1].Tick;
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

    private static void ReadTrack(ReadOnlySpan<byte> s, int track, List<MidiEvent> events)
    {
        int pos = 0;
        long tick = 0;
        byte running = 0;
        while (pos < s.Length)
        {
            if (!TryReadVarLen(s, ref pos, out int delta)) break;
            tick += delta;
            if (pos >= s.Length) break;

            byte status = s[pos];
            if (status < 0x80)
            {
                // Running status: reuse the previous channel status byte.
                if (running == 0) break; // Corrupt data; give up on this track.
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
                if (type == MetaEndOfTrack) break;
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
