using SkiaSharp;

namespace KaraokeToMp4;

/// <summary>
/// Settings shared by every karaoke-to-MP4 conversion.
/// </summary>
public sealed class ConversionOptions
{
    // ---- Output video -------------------------------------------------

    /// <summary>Output video width in pixels (must be even). Default 1280.</summary>
    public int Width { get; set; } = 1280;

    /// <summary>Output video height in pixels (must be even). Default 720.</summary>
    public int Height { get; set; } = 720;

    /// <summary>Output frame rate. Default 30.</summary>
    public int FrameRate { get; set; } = 30;

    // ---- Encoder ------------------------------------------------------

    /// <summary>
    /// Path to the ffmpeg executable. When null (the default), <see cref="Video.FfmpegLocator"/> looks
    /// for it (app folder, download folder, PATH) and, if it isn't found, asks
    /// <see cref="ConfirmFfmpegDownload"/> whether to download it.
    /// </summary>
    public string? FfmpegPath { get; set; }

    /// <summary>
    /// Path to the ffprobe executable. When null it is looked up next to <see cref="FfmpegPath"/>,
    /// falling back to "ffprobe" on the PATH.
    /// </summary>
    public string? FfprobePath { get; set; }

    /// <summary>
    /// Called when ffmpeg can't be found. Return true to download it (about 80-105 MB depending on the
    /// platform) into <see cref="Video.FfmpegLocator.DownloadFolder"/>. When null, no download is
    /// attempted and the conversion fails with an explanatory error.
    /// </summary>
    public Func<Video.FfmpegDownloadRequest, CancellationToken, Task<bool>>? ConfirmFfmpegDownload { get; set; }

    /// <summary>Optional progress (0..1) for an ffmpeg download.</summary>
    public IProgress<double>? FfmpegDownloadProgress { get; set; }

    /// <summary>ffmpeg video codec. Default "libx264".</summary>
    public string VideoCodec { get; set; } = "libx264";

    /// <summary>x264/x265 preset. Default "medium".</summary>
    public string VideoPreset { get; set; } = "medium";

    /// <summary>Constant rate factor (lower is higher quality). Default 20.</summary>
    public int Crf { get; set; } = 20;

    /// <summary>ffmpeg audio codec. Default "aac".</summary>
    public string AudioCodec { get; set; } = "aac";

    /// <summary>Audio bitrate in kbit/s. Default 192.</summary>
    public int AudioBitrateKbps { get; set; } = 192;

    /// <summary>Extra raw arguments inserted before the output file name (advanced).</summary>
    public string? ExtraFfmpegOutputArgs { get; set; }

    /// <summary>Overwrite the output file if it already exists. Default true.</summary>
    public bool Overwrite { get; set; } = true;

    // ---- CDG ----------------------------------------------------------

    /// <summary>
    /// ffmpeg scaler used to enlarge the 300x216 CDG picture. "neighbor" keeps the
    /// blocky pixel look; "lanczos" or "bicubic" give a smoother result. Default "neighbor".
    /// </summary>
    public string CdgScaleAlgorithm { get; set; } = "neighbor";

    /// <summary>
    /// When true the CDG border area is cropped off and only the 288x192 safe area is shown.
    /// Default false (border is kept, as on a real CD+G player).
    /// </summary>
    public bool CdgCropBorder { get; set; }

    // ---- MIDI / KAR ---------------------------------------------------

    /// <summary>
    /// SoundFont (.sf2) used to synthesise MIDI audio. When null, <see cref="Midi.SoundFontLocator"/>
    /// looks for one (download folder, app folder, system folders) and, if none is found, asks
    /// <see cref="ConfirmSoundFontDownload"/> whether to download the recommended one.
    /// </summary>
    public string? SoundFontPath { get; set; }

    /// <summary>
    /// Called when MIDI/KAR audio needs a SoundFont and none can be found. Return true to download
    /// <see cref="Midi.SoundFontLocator.Recommended"/> (GeneralUser GS, ~31 MB). When null, no download
    /// is attempted and the conversion fails with an explanatory error.
    /// </summary>
    public Func<Midi.SoundFontDownloadRequest, CancellationToken, Task<bool>>? ConfirmSoundFontDownload { get; set; }

    /// <summary>Optional progress (0..1) for a SoundFont download.</summary>
    public IProgress<double>? SoundFontDownloadProgress { get; set; }

    /// <summary>
    /// Optional pre-rendered backing track to use instead of synthesising the MIDI file.
    /// </summary>
    public string? MidiAudioOverridePath { get; set; }

    /// <summary>Sample rate used when synthesising MIDI audio. Default 44100.</summary>
    public int MidiSampleRate { get; set; } = 44100;

    /// <summary>Seconds of silence/reverb tail appended after the last MIDI event. Default 2.</summary>
    public double MidiTailSeconds { get; set; } = 2.0;

    // ---- Lyric rendering (MIDI / KAR) ------------------------------------

    /// <summary>Font family used for lyrics. Default "Arial" (falls back to the system default).</summary>
    public string FontFamily { get; set; } = "Arial";

    /// <summary>Optional path to a .ttf/.otf file; overrides <see cref="FontFamily"/>.</summary>
    public string? FontPath { get; set; }

    /// <summary>Lyric font size in pixels. 0 = automatic (Height / 12).</summary>
    public float FontSize { get; set; }

    /// <summary>Maximum number of lyric lines shown on screen at once. Default 4.</summary>
    public int LinesPerPage { get; set; } = 4;

    /// <summary>Background colour. Default near-black blue.</summary>
    public SKColor BackgroundColor { get; set; } = new(0x0A, 0x0A, 0x28);

    /// <summary>Colour of lyrics that have not been sung yet. Default white.</summary>
    public SKColor UnsungColor { get; set; } = SKColors.White;

    /// <summary>Colour of lyrics that have been sung. Default gold.</summary>
    public SKColor SungColor { get; set; } = new(0xFF, 0xC8, 0x00);

    /// <summary>Outline colour around lyric text. Default black.</summary>
    public SKColor OutlineColor { get; set; } = SKColors.Black;

    /// <summary>Title text colour on the intro screen. Default light blue.</summary>
    public SKColor TitleColor { get; set; } = new(0x80, 0xC8, 0xFF);

    /// <summary>Show a title card before the first lyric (when there is time). Default true.</summary>
    public bool ShowTitleScreen { get; set; } = true;

    /// <summary>Override the title shown on the intro screen (otherwise taken from the file).</summary>
    public string? Title { get; set; }

    /// <summary>
    /// How many seconds before the first syllable of a page that page appears when it follows an
    /// instrumental break. Default 3.
    /// </summary>
    public double PageLeadInSeconds { get; set; } = 3.0;

    internal void Validate()
    {
        if (Width <= 0 || Height <= 0 || Width % 2 != 0 || Height % 2 != 0)
            throw new ArgumentException("Width and Height must be positive even numbers.");
        if (FrameRate <= 0 || FrameRate > 240)
            throw new ArgumentException("FrameRate must be between 1 and 240.");
        if (LinesPerPage <= 0)
            throw new ArgumentException("LinesPerPage must be at least 1.");
    }
}
