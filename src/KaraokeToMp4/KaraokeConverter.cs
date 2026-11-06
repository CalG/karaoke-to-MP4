using System.IO.Compression;
using KaraokeToMp4.Cdg;
using KaraokeToMp4.Midi;

namespace KaraokeToMp4;

/// <summary>
/// Entry point: detects the karaoke format of an input file and converts it to MP4.
/// </summary>
/// <example>
/// <code>
/// var converter = new KaraokeConverter(new ConversionOptions { SoundFontPath = "GeneralUser.sf2" });
/// await converter.ConvertAsync("song.mp3", "song.mp4");   // finds song.cdg automatically
/// await converter.ConvertAsync("song.kar", "song2.mp4");
/// </code>
/// </example>
public sealed class KaraokeConverter
{
    private readonly ConversionOptions _options;

    public KaraokeConverter(ConversionOptions? options = null)
    {
        _options = options ?? new ConversionOptions();
    }

    public ConversionOptions Options => _options;

    /// <summary>Works out what kind of karaoke file <paramref name="path"/> is.</summary>
    /// <remarks>
    /// .cdg files and audio files with a sibling .cdg (or a .zip containing a .cdg) are <see cref="KaraokeFormat.Mp3Cdg"/>.
    /// .kar files are <see cref="KaraokeFormat.Kar"/>. .mid/.midi files are classified by content:
    /// files marked "@KMIDI" are KAR, files with lyric events are <see cref="KaraokeFormat.MidiWithLyrics"/>.
    /// </remarks>
    public static KaraokeFormat DetectFormat(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        switch (ext)
        {
            case ".cdg":
                return KaraokeFormat.Mp3Cdg;
            case ".kar":
                return KaraokeFormat.Kar;
            case ".zip":
                try
                {
                    using var zip = ZipFile.OpenRead(path);
                    return zip.Entries.Any(e => e.Name.EndsWith(".cdg", StringComparison.OrdinalIgnoreCase))
                        ? KaraokeFormat.Mp3Cdg
                        : KaraokeFormat.Unknown;
                }
                catch (InvalidDataException) { return KaraokeFormat.Unknown; }
            case ".mid":
            case ".midi":
            case ".rmi":
                try
                {
                    var midi = StandardMidiFile.FromFile(path);
                    bool isKar = midi.Events.Any(e => e.IsMeta && e.MetaType == StandardMidiFile.MetaText &&
                                                      MidiLyricExtractor.DecodeText(e.Data).StartsWith("@K", StringComparison.OrdinalIgnoreCase));
                    if (isKar) return KaraokeFormat.Kar;
                    return MidiLyricExtractor.Extract(midi).Lines.Count > 0 ? KaraokeFormat.MidiWithLyrics : KaraokeFormat.Unknown;
                }
                catch (InvalidDataException) { return KaraokeFormat.Unknown; }
        }

        if (CdgConverter.AudioExtensions.Contains(ext) && CdgConverter.FindCdgForAudio(path) != null)
            return KaraokeFormat.Mp3Cdg;
        return KaraokeFormat.Unknown;
    }

    /// <summary>
    /// Converts any supported karaoke file to MP4.
    /// </summary>
    /// <param name="inputPath">
    /// A .cdg file, an audio file (e.g. .mp3) with a matching .cdg next to it, a .zip holding both,
    /// a .kar file, or a .mid/.midi file with lyrics.
    /// </param>
    /// <param name="outputPath">Where to write the .mp4.</param>
    public Task ConvertAsync(
        string inputPath,
        string outputPath,
        IProgress<ConversionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        if (!File.Exists(inputPath)) throw new FileNotFoundException("Input file not found.", inputPath);

        var format = DetectFormat(inputPath);
        var ext = Path.GetExtension(inputPath).ToLowerInvariant();
        switch (format)
        {
            case KaraokeFormat.Mp3Cdg when ext == ".zip":
                return new CdgConverter(_options).ConvertZipAsync(inputPath, outputPath, progress, cancellationToken);

            case KaraokeFormat.Mp3Cdg when ext == ".cdg":
                var audio = CdgConverter.FindAudioForCdg(inputPath)
                    ?? throw new KaraokeConversionException($"No audio file found next to '{inputPath}'.");
                return new CdgConverter(_options).ConvertAsync(audio, inputPath, outputPath, progress, cancellationToken);

            case KaraokeFormat.Mp3Cdg:
                var cdg = CdgConverter.FindCdgForAudio(inputPath)!;
                return new CdgConverter(_options).ConvertAsync(inputPath, cdg, outputPath, progress, cancellationToken);

            case KaraokeFormat.Kar:
            case KaraokeFormat.MidiWithLyrics:
                return new MidiKaraokeConverter(_options).ConvertAsync(inputPath, outputPath, progress, cancellationToken);

            default:
                throw new KaraokeConversionException($"'{inputPath}' is not a supported karaoke file (MP3+CDG, KAR or MIDI with lyrics).");
        }
    }

    /// <summary>Converts an explicit audio + CDG pair.</summary>
    public Task ConvertCdgAsync(string audioPath, string cdgPath, string outputPath,
        IProgress<ConversionProgress>? progress = null, CancellationToken cancellationToken = default)
        => new CdgConverter(_options).ConvertAsync(audioPath, cdgPath, outputPath, progress, cancellationToken);

    /// <summary>Converts a .kar file or a MIDI file with lyrics.</summary>
    public Task ConvertMidiAsync(string midiPath, string outputPath,
        IProgress<ConversionProgress>? progress = null, CancellationToken cancellationToken = default)
        => new MidiKaraokeConverter(_options).ConvertAsync(midiPath, outputPath, progress, cancellationToken);
}
