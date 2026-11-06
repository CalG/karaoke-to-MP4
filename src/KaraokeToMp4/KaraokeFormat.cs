namespace KaraokeToMp4;

/// <summary>Karaoke input formats understood by the library.</summary>
public enum KaraokeFormat
{
    Unknown,

    /// <summary>An audio file (usually MP3) paired with a CD+Graphics (.cdg) file, or a .zip holding both.</summary>
    Mp3Cdg,

    /// <summary>A .kar Soft-Karaoke MIDI file (lyrics stored as text events).</summary>
    Kar,

    /// <summary>A standard MIDI file with lyric meta events.</summary>
    MidiWithLyrics,
}
