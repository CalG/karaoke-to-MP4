using System.Globalization;
using KaraokeToMp4;

// karaoke2mp4 <input> [output.mp4] [options]
// Converts MP3+CDG (.cdg/.mp3/.zip), .kar and .mid-with-lyrics files to MP4.

if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
{
    Console.WriteLine("""
        Usage: karaoke2mp4 <input> [output.mp4] [options]

        Input may be a .cdg, an audio file with a matching .cdg, a .zip (MP3+G), a .kar, or a .mid with lyrics.

        Options:
          --soundfont <file.sf2>   SoundFont for MIDI/KAR audio (default: auto-detect, or offer to download GeneralUser GS)
          --yes                    Download ffmpeg / a SoundFont without asking if they're missing
          --audio <file>           Use this backing track for MIDI/KAR instead of synthesising
          --ffmpeg <path>          Path to ffmpeg (default: auto-detect, or offer to download it)
          --width <px>             Output width (default 1280)
          --height <px>            Output height (default 720)
          --fps <n>                Frame rate (default 30)
          --crf <n>                x264 quality, lower = better (default 20)
          --preset <name>          x264 preset (default medium)
          --lines <n>              Lyric lines per screen for MIDI/KAR (default 4)
          --font <family|file>     Font family name or .ttf/.otf path for lyrics
          --title <text>           Title card text for MIDI/KAR
          --crop-border            Hide the CDG border area
          --smooth                 Smooth (lanczos) CDG upscaling instead of blocky pixels
        """);
    return args.Length == 0 ? 1 : 0;
}

var options = new ConversionOptions();
string? input = null, output = null;
bool assumeYes = false;

try
{
    for (int i = 0; i < args.Length; i++)
    {
        string a = args[i];
        string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {a}");
        int NextInt() => int.Parse(Next(), CultureInfo.InvariantCulture);

        switch (a)
        {
            case "--soundfont": options.SoundFontPath = Next(); break;
            case "--audio": options.MidiAudioOverridePath = Next(); break;
            case "--ffmpeg": options.FfmpegPath = Next(); break;
            case "--width": options.Width = NextInt(); break;
            case "--height": options.Height = NextInt(); break;
            case "--fps": options.FrameRate = NextInt(); break;
            case "--crf": options.Crf = NextInt(); break;
            case "--preset": options.VideoPreset = Next(); break;
            case "--lines": options.LinesPerPage = NextInt(); break;
            case "--title": options.Title = Next(); break;
            case "--crop-border": options.CdgCropBorder = true; break;
            case "--yes": case "-y": assumeYes = true; break;
            case "--smooth": options.CdgScaleAlgorithm = "lanczos"; break;
            case "--font":
                var f = Next();
                if (File.Exists(f)) options.FontPath = f; else options.FontFamily = f;
                break;
            default:
                if (a.StartsWith("--")) throw new ArgumentException($"Unknown option {a}");
                if (input == null) input = a;
                else if (output == null) output = a;
                else throw new ArgumentException($"Unexpected argument {a}");
                break;
        }
    }
}
catch (Exception ex) when (ex is ArgumentException or FormatException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

if (input == null) { Console.Error.WriteLine("No input file given."); return 1; }
output ??= Path.ChangeExtension(input, ".mp4");
if (Path.GetFullPath(output) == Path.GetFullPath(input))
{
    Console.Error.WriteLine("Output path would overwrite the input.");
    return 1;
}

Console.WriteLine($"{input}  [{KaraokeConverter.DetectFormat(input)}]  ->  {output}");

// Ask before downloading anything (only happens when ffmpeg or a SoundFont isn't installed).
Task<bool> AskToDownload(string what, string why, int megabytes, string destination, string? licence = null)
{
    Console.WriteLine();
    Console.WriteLine($"{what} was not found. {why}");
    Console.WriteLine($"Download it (~{megabytes} MB) to:");
    Console.WriteLine($"  {destination}");
    if (licence != null) Console.WriteLine($"  Licence: {licence}");
    if (assumeYes) return Task.FromResult(true);
    if (Console.IsInputRedirected) { Console.WriteLine("(Re-run with --yes to allow the download.)"); return Task.FromResult(false); }
    Console.Write("Download now? [y/N] ");
    var answer = Console.ReadLine()?.Trim();
    return Task.FromResult(string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase));
}

options.ConfirmFfmpegDownload = (req, _) => AskToDownload(
    "ffmpeg", "It is needed to encode the MP4.", req.Release.SizeMegabytes, req.Destination);
options.FfmpegDownloadProgress = new Progress<double>(f => Console.Write($"\rDownloading ffmpeg {f,7:P1}   "));
options.ConfirmSoundFontDownload = (req, _) => AskToDownload(
    "A SoundFont (.sf2)", "MIDI/KAR audio needs one to synthesise the music.",
    req.SoundFont.SizeMegabytes, req.Destination, req.SoundFont.License);
options.SoundFontDownloadProgress = new Progress<double>(f => Console.Write($"\rDownloading SoundFont {f,7:P1}   "));

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var lastStage = (ConversionStage)(-1);
var progress = new Progress<ConversionProgress>(p =>
{
    if (p.Stage != lastStage && lastStage >= 0) Console.WriteLine();
    lastStage = p.Stage;
    Console.Write($"\r{p.Stage,-18} {p.Fraction,7:P1}  {p.Position:mm\\:ss} / {p.Duration:mm\\:ss}   ");
});

try
{
    await new KaraokeConverter(options).ConvertAsync(input, output, progress, cts.Token);
    Console.WriteLine();
    Console.WriteLine("Done.");
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("\nCancelled.");
    return 2;
}
catch (Exception ex) when (ex is KaraokeConversionException or FileNotFoundException or ArgumentException)
{
    Console.Error.WriteLine($"\nError: {ex.Message}");
    return 1;
}
