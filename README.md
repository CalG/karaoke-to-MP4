# karaoke-to-MP4

A .NET 8 C# library (plus a small command-line tool) that converts karaoke files into MP4 videos
(H.264 video + AAC audio):

| Input | What happens |
|---|---|
| **MP3 + CDG** (`song.mp3` + `song.cdg`, or a `.zip` holding both) | The CD+Graphics stream is decoded frame by frame and muxed with the original audio. |
| **KAR** (Soft-Karaoke `.kar`) | The MIDI is synthesised to audio with a SoundFont; lyrics from text events are drawn with a colour wipe. |
| **MIDI with lyrics** (`.mid` with Lyric meta events) | Same as KAR, using the Lyric (0x05) events. |

## Requirements

* **.NET 8 SDK**
* **ffmpeg** (and ffprobe) on the `PATH`, or set `ConversionOptions.FfmpegPath`.
  On Windows: `winget install Gyan.FFmpeg`.
* **A SoundFont (`.sf2`)** for KAR/MIDI audio. You don't have to find one yourself: if none is
  installed, the library can download **GeneralUser GS** (~31 MB, free to use and redistribute) with
  the user's permission. See [SoundFonts](#soundfonts). Not needed if you supply your own backing
  track with `MidiAudioOverridePath`.

NuGet dependencies: [MeltySynth](https://github.com/sinshu/meltysynth) (pure C# SoundFont synth)
and [SkiaSharp](https://github.com/mono/SkiaSharp) (lyric text rendering).

## Library usage

```csharp
using KaraokeToMp4;

var options = new ConversionOptions
{
    Width = 1920,
    Height = 1080,
    SoundFontPath = @"C:\SoundFonts\GeneralUser GS.sf2", // only needed for KAR / MIDI
};
var converter = new KaraokeConverter(options);

var progress = new Progress<ConversionProgress>(p =>
    Console.WriteLine($"{p.Stage}: {p.Fraction:P0}"));

// Format is detected automatically:
await converter.ConvertAsync("Artist - Song.mp3", "Artist - Song.mp4", progress); // finds the .cdg next to it
await converter.ConvertAsync("Artist - Song.zip", "zip.mp4");                    // MP3+G zip bundle
await converter.ConvertAsync("song.kar", "kar.mp4");
await converter.ConvertAsync("song.mid", "midi.mp4");

// Or explicitly:
await converter.ConvertCdgAsync("audio.mp3", "graphics.cdg", "out.mp4");
await converter.ConvertMidiAsync("song.kar", "out.mp4");

KaraokeFormat format = KaraokeConverter.DetectFormat("song.mid"); // Kar / MidiWithLyrics / Mp3Cdg / Unknown
```

All conversions accept a `CancellationToken`. Failures are reported as `KaraokeConversionException`
(including ffmpeg's error output).

### SoundFonts

When `SoundFontPath` is not set, `SoundFontLocator` looks for an `.sf2` in this order:

1. `%LOCALAPPDATA%\KaraokeTools\soundfonts` (`~/.local/share/KaraokeTools/soundfonts` on Linux), where downloads go
2. the app folder and its `soundfonts` subfolder
3. `SoundFontLocator.AdditionalSearchFolders`
4. common system folders (`/usr/share/sounds/sf2`, `C:\soundfonts`, `~/Library/Audio/Sounds/Banks`, ...)

If nothing is found, the library calls `ConfirmSoundFontDownload`. If that returns true, it downloads
GeneralUser GS from a pinned GitHub commit, checks its SHA-256 and saves it so later runs find it.
If no callback is set, nothing is downloaded and you get an explanatory error.

```csharp
// WinForms: ask once, just like the ffmpeg download
options.ConfirmSoundFontDownload = (req, ct) => Task.FromResult(
    MessageBox.Show(this,
        $"KAR/MIDI songs need a SoundFont to play the music.\n\n" +
        $"Download {req.SoundFont.Name} (about {req.SoundFont.SizeMegabytes} MB) into:\n{req.Destination}\n\n" +
        $"This only happens once.",
        "Download SoundFont?", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes);

// Or check/download up front:
string? sf2 = await SoundFontLocator.EnsureAsync(askUser);
```

**Why GeneralUser GS?** It's a complete General MIDI/GS bank, which is what KAR and karaoke MIDI
files are written for. It sounds noticeably better than tiny banks like TimGM6mb (6 MB) and is far
smaller than FluidR3_GM (~140 MB) or MuseScore_General (~200+ MB). It is actively maintained and
its licence allows free use and redistribution, including in commercial software. To use a
different bank, set `SoundFontPath`, drop the `.sf2` in a search folder, or assign your own
`SoundFontInfo` to `SoundFontLocator.Recommended`.

### Useful options

| Option | Default | Notes |
|---|---|---|
| `Width`, `Height`, `FrameRate` | 1280, 720, 30 | Output video size/rate |
| `VideoCodec`, `VideoPreset`, `Crf` | libx264, medium, 20 | Encoder quality |
| `AudioCodec`, `AudioBitrateKbps` | aac, 192 | |
| `CdgScaleAlgorithm` | `neighbor` | `lanczos` for a smoother look |
| `CdgCropBorder` | false | Show only the 288x192 safe area |
| `SoundFontPath` | auto | Auto-detected when null; see [SoundFonts](#soundfonts) |
| `ConfirmSoundFontDownload` | null | Callback asking permission to download GeneralUser GS |
| `MidiAudioOverridePath` | — | Use a real backing track instead of synthesising the MIDI |
| `LinesPerPage` | 4 | Lyric lines on screen |
| `FontFamily` / `FontPath` / `FontSize` | Arial / — / auto | |
| `UnsungColor`, `SungColor`, `OutlineColor`, `BackgroundColor`, `TitleColor` | | `SKColor` values |
| `ShowTitleScreen`, `Title` | true / from file | Title card before the first lyric |

The lower-level pieces are public too: `CdgDecoder` (CD+G → BGRA frames), `StandardMidiFile`,
`MidiLyricExtractor`, `MidiAudioRenderer`, `LyricScreenPlan` and `LyricFrameRenderer`.

## Command line

```bash
dotnet run --project samples/KaraokeToMp4.Cli -- "Artist - Song.mp3"
```

```bash
dotnet run --project samples/KaraokeToMp4.Cli -- song.kar song.mp4 --soundfont GeneralUser.sf2 --width 1920 --height 1080
```

Run with `--help` for all options. For KAR/MIDI input with no SoundFont installed, the CLI asks
before downloading GeneralUser GS (`--yes` skips the question).

## How it works

* **CDG**: packets are 24 bytes at 300 packets/second. `CdgDecoder` implements Memory Preset,
  Border Preset, Tile Block (normal + XOR), Scroll Preset/Copy (with fine-scroll offsets) and the
  colour-table loads. Each video frame advances the decoder to the frame's time, renders the
  300x216 screen and pipes it to ffmpeg, which scales/pads to the output size.
* **KAR**: lyrics come from Text events in the busiest text track. `@T` lines become the title card;
  `\` starts a new page and `/` a new line.
* **MIDI with lyrics**: Lyric events, with CR/LF (and `/`, `\`) as line breaks. The track name is
  used as the title if there is none.
* Lyrics are grouped into pages; each page appears when the previous one finishes (or with a
  lead-in after a long instrumental break), and every syllable is wiped from the unsung to the sung
  colour over its duration.
* MIDI audio is synthesised by MeltySynth into a temporary 32-bit float WAV.
* Video frames are streamed as raw BGRA into ffmpeg's stdin, so nothing large is held in memory.

## Building and testing

```bash
dotnet test
```

The tests build tiny synthetic CDG/KAR/MIDI files in code (no copyrighted media). The end-to-end
tests run real conversions when ffmpeg is installed; the KAR test also needs a SoundFont (set
`KARAOKE_TEST_SOUNDFONT` or install `timgm6mb-soundfont` on Debian/Ubuntu).
