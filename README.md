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
* **ffmpeg** (and ffprobe). If they aren't installed, the library can download them with the
  user's permission. See [Downloading ffmpeg and SoundFonts](#downloading-ffmpeg-and-soundfonts).
* **A SoundFont (`.sf2`)** for KAR/MIDI audio. You don't have to find one yourself: if none is
  installed, the library can download **GeneralUser GS** (~31 MB, free to use and redistribute) with
  the user's permission. Not needed if you supply your own backing
  track with `MidiAudioOverridePath`.

NuGet dependencies: [MeltySynth](https://github.com/sinshu/meltysynth) (pure C# SoundFont synth),
[SkiaSharp](https://github.com/mono/SkiaSharp) (lyric text rendering) and
[SharpCompress](https://github.com/adamhathcock/sharpcompress) (reading 7z/RAR/TAR/ZIP song bundles).

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

### Song title, artist and output names

The title and artist are read from the file's own data:

| Input | Where they come from |
|---|---|
| KAR | `@T` lines: first = title, second = artist (Soft Karaoke convention) |
| MIDI with lyrics (e.g. Tune 1000) | Text events before the first lyric (title, then artist); else the sequence name as title |
| MP3+CDG / zip | The MP3's ID3 tags (v1, v2.2-2.4); other audio formats via ffprobe |

Copyright notices, sequencer credits, web addresses and generic names ("Words", "Untitled", ...) are skipped.

**Cleanup.** Karaoke files often pack extra text into these fields, so `SongInfoCleaner` reduces them
to the song name and the original performing artist:

| Raw text in the file | Used |
|---|---|
| `Crazy (Karaoke Version)` | Crazy |
| `In the style of Patsy Cline - Words & Music by Willie Nelson (c) 1961` | Patsy Cline |
| `Ed Sheeran feat. Beyoncé` | Ed Sheeran |
| `Sound Choice Karaoke` (a karaoke label) | *(no artist)* |

If what's left still doesn't look like a name (too long, a credit, a karaoke label), no artist is
used at all, because a wrong artist in a file name is worse than none. The original text is kept in
`SongInfo.RawTitle` / `RawArtist`.

**Title only.** Set `IncludeArtist = false` (CLI `--title-only`, or untick *Include artist* in the
Windows app) to leave the artist out of the file name, MP4 tags and title screen.

* **MP4 metadata:** title, artist and `genre=Karaoke` are written into the MP4
  (`WriteMetadata`, `MetadataGenre`).
* **Output name:** `SuggestOutputPathAsync` / `ConvertNextToSourceAsync` keep the source name and append the
  artist and/or title only if the name doesn't already contain them. The check ignores case,
  punctuation, accents, a leading "The", "&" vs "and" and bracketed extras such as "(Remastered)":

  | Source | Title / Artist in file | MP4 |
  |---|---|---|
  | `SC8127-05.mp3` | Yesterday / The Beatles | `SC8127-05 - The Beatles - Yesterday.mp4` |
  | `Yesterday.kar` | Yesterday / The Beatles | `Yesterday - The Beatles.mp4` |
  | `beatles_yesterday.cdg` | Yesterday / The Beatles | `beatles_yesterday.mp4` |

```csharp
SongInfo info = await converter.ReadSongInfoAsync("song.kar");        // Title, Artist, Source
string mp4 = await converter.ConvertNextToSourceAsync("SC8127-05.mp3"); // returns the path it wrote
```

The CLI's `--info` option prints what was found (and, for MIDI files, the raw text events) without
converting. `--keep-name` and `--no-metadata` turn the two features off.

### Damaged CDG files (scratched-disc rips)

Every CD+G packet carries Reed-Solomon error-correction data that can locate and fix up to two damaged
bytes. A rip from a scratched disc often has a few hundred damaged packets. Because many discs draw
with XOR tiles, each one leaves a stray block on screen until the next screen clear, so the garbage
builds up as the song plays.

`CdgInspector` checks a file and `CdgErrorCorrection` (on by default) repairs it before rendering.
Packets too damaged to repair are skipped instead of drawn. Some rippers strip the error-correction
bytes; such files are reported as "can't be checked" and left unchanged.

```bash
karaoke2mp4 "song.cdg" --info
#  CDG check: 198 damaged packets (repaired automatically).
#  damaged at: 0:00, 0:03, 0:04x2, ...
```

The Windows app shows the same result on its *File check* line. Use `--no-cdg-repair` to see a file
exactly as stored. Some producers fill unused graphics slots with filler packets; Music Maestro
discs, for example, use instruction code 9 with every symbol 0x09. These are reported as filler and
ignored, as a real player ignores them. Other packets with non-standard instruction codes that pass
the error check are reported but not treated as damage. CD+EG (extended graphics) packets are also
ignored; the standard CD+G layer is drawn.

### Damaged files

Many KAR collections contain truncated files (often cut at exactly 32 KB) or files with garbage at
the end. These are still converted as far as the data goes: the damaged part is dropped and the
synthesiser gets a cleaned-up copy. You're warned that the video may stop early: through
`ConversionOptions.OnWarning`, in the CLI, and on the *File check* line in the Windows app.

Other fixes from testing real files:
* Tune 1000 files store a "rights reserved / not for broadcast" notice as lyrics at time 0; it is no
  longer shown.
* A line feed in lyric events starts a new screen (verse), as the MIDI lyric standard intends.
* Tune 1000 sequence names are "TITLE ;Songwriters"; only the title is used.
* Song length ignores End-of-Track markers placed far after the music.
* Malformed text events (e.g. a lyric whose length byte is too small) are skipped and reading resumes
  at the next event, instead of losing the rest of the track. These are reported as notes, not damage.
* Catalogue codes on titles ("Song - CP000015", "Song [SC8127-05]") are removed.
* Plain MIDI files without lyrics are rejected with a clear "no lyrics" message.

### Downloading ffmpeg and SoundFonts

Neither ffmpeg nor a SoundFont has to be installed by hand. When a conversion needs one that can't
be found, the library calls a permission callback. If the callback returns true, the file is
downloaded once from a pinned URL, checked against its SHA-256 checksum, and saved for later runs.
If no callback is set, nothing is ever downloaded and you get an explanatory error instead.

```csharp
// WinForms: ask before downloading, like karaoke-naudio_vlc-pitch-experiment does
options.ConfirmFfmpegDownload = (req, ct) => Task.FromResult(
    MessageBox.Show(this,
        $"Converting to MP4 needs ffmpeg, which wasn't found.\n\n" +
        $"Download it now (about {req.Release.SizeMegabytes} MB) into:\n{req.Destination}\n\nThis only happens once.",
        "Download ffmpeg?", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes);
options.FfmpegDownloadProgress = new Progress<double>(f => statusLabel.Text = $"Downloading ffmpeg {f:P0}");

// Or check/download up front:
FfmpegTools? tools = await FfmpegLocator.EnsureAsync(askUser);
```

#### ffmpeg

When `FfmpegPath` is null (the default), `FfmpegLocator` looks for a folder containing both ffmpeg
and ffprobe: the app folder, then `%LOCALAPPDATA%\KaraokeTools\ffmpeg`, then
`FfmpegLocator.AdditionalSearchFolders`, then `PATH`. The download folder is the same one
karaoke-naudio_vlc-pitch-experiment uses, so a copy downloaded by either app is shared.

Downloads are ffmpeg 6.1 static builds from
[ffbinaries](https://github.com/ffbinaries/ffbinaries-prebuilt), the same source FFMpegCore's
downloader uses, fetched from their GitHub release. There are builds for Windows x64, macOS (Intel;
runs on Apple Silicon via Rosetta) and Linux x64/ARM64, all with libx264. Sizes are about 105 MB on
Windows and 50-60 MB elsewhere. To use another build, set `FfmpegLocator.Release`.

#### SoundFonts

When `SoundFontPath` is not set, `SoundFontLocator` looks for an `.sf2` in this order:

1. `%LOCALAPPDATA%\KaraokeTools\soundfonts` (`~/.local/share/KaraokeTools/soundfonts` on Linux), where downloads go
2. the app folder and its `soundfonts` subfolder
3. `SoundFontLocator.AdditionalSearchFolders`
4. common system folders (`/usr/share/sounds/sf2`, `C:\soundfonts`, `~/Library/Audio/Sounds/Banks`, ...)

If nothing is found, `ConfirmSoundFontDownload` is asked whether to download GeneralUser GS from a
pinned GitHub commit.

```csharp
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
| `FfmpegPath`, `FfprobePath` | auto | Auto-detected when null; see [ffmpeg](#ffmpeg) |
| `ConfirmFfmpegDownload` | null | Callback asking permission to download ffmpeg |
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

Run with `--help` for all options. If ffmpeg or (for KAR/MIDI) a SoundFont is missing, the CLI asks
before downloading it (`--yes` skips the question).

## Windows test app

`samples/KaraokeToMp4.WinForms` is a small Windows app for trying the library:

1. Click **Browse...** (or drag a file onto the window) and pick an `.mp3`/`.cdg`/`.zip`, `.kar` or `.mid`.
   The detected format and the output path are shown.
2. Click **Convert to MP4**. The video is written **next to the source file** with the same name,
   plus the artist/title from the file if the name doesn't already contain them, e.g.
   `C:\Karaoke\SC8127-05.mp3` becomes `C:\Karaoke\SC8127-05 - The Beatles - Yesterday.mp4`.
   The *Song details* box lists each field on its own line: Format, Title, Artist, where they were
   found, the raw text from the file, and the Output path. Untick *Include artist* to use the song
   title only.
   You're asked before an existing file is replaced.
3. A progress bar shows downloading, synthesis and encoding. **Cancel** stops the conversion, and
   **Open Folder** shows the finished MP4 in Explorer.

If ffmpeg or a SoundFont is missing, the app asks before downloading it (once).

Open **`KaraokeToMp4.sln`** in Visual Studio (File > Open > Project/Solution, not "Open Folder") and press
F5. `KaraokeToMp4.WinForms` is the solution's default startup project. If Visual Studio remembers an
older choice, right-click **KaraokeToMp4.WinForms** in Solution Explorer and choose **Set as Startup
Project**. Or run:

```bash
dotnet run --project samples/KaraokeToMp4.WinForms
```

## Karaoke Song Checker (Windows)

`apps/KaraokeSongChecker` is a separate Windows program that scans whole karaoke collections for damaged songs:

1. **Add Folder...** (or drag folders onto the list) for each karaoke folder. Tick *Include subfolders*
   to scan everything below them. The list is remembered between runs.
2. **Scan** checks every song and sorts it into five tabs:
   * **No issues detected**
   * **Bad but repairable**, e.g. a scratched-disc CDG whose damaged packets the error-correction data
     can fix, or a MIDI/KAR file with malformed bytes that can be cleaned up.
   * **Cannot repair**, e.g. a file that was cut off (part of the song is missing), an empty or unreadable file.
   * **Only music**: an MP3 (or other audio file) without a matching .cdg (missing lyrics), an empty .cdg,
     a .zip with audio but no .cdg, or a plain MIDI file with no lyrics.
   * **Only lyrics**: a .cdg without an audio file (or with an empty one), a .zip with a .cdg but no
     audio, or a KAR/MIDI file with lyrics but no notes.
3. **Fix Checked** / **Fix All Repairable** repairs those songs in place. With *Keep a backup* ticked
   (the default), each original is kept next to it as `name.ext.bak`. Fixed songs move to the
   *No issues* tab.

Select a song to see details (e.g. where in the song the damage is). Double-click it to show it in
Explorer. **Export Report...** saves all the lists as a CSV file.

What is checked:

| Song | Checks |
|---|---|
| `.cdg` + audio | audio present and not empty; every graphics packet checked with the CD+G error-correction data |
| `.zip` (MP3+G) | the same, inside the zip (repairs rewrite the zip); **one song per zip**: a zip holding several songs is listed as repairable, and Fix splits it into one zip per song (named after each song, same extension) |
| `.7z`, `.rar`, `.tar`, `.tar.gz`/`.tgz` | read (and converted) like a zip, but **read-only**: damage or several songs in one archive are reported under *Cannot repair*, because only ZIPs are rewritten |
| archives with other extensions | recognised by content, so e.g. a zip or 7z renamed to `.mp3gz` is handled like its real format |
| `.kar` / `.mid` | valid MIDI, has lyrics, not cut off, no malformed events |
| audio files (.mp3, .ogg, .wav, ...) | has a matching `.cdg` (otherwise listed as missing lyrics) |

The same checks are available to other programs through `KaraokeToMp4.Health.SongHealthChecker`
(`FindSongs`, `Check`, `Repair`).

### Archive support

Song bundles are read with [SharpCompress](https://github.com/adamhathcock/sharpcompress) (MIT,
pure .NET): ZIP (including the LZMA, BZip2, PPMd and Deflate64 methods 7-Zip can use, which .NET's
own `System.IO.Compression` can't decompress), 7-Zip, RAR/RAR5, TAR and gzip/tar.gz. The format
is detected from the file's content, so the extension doesn't matter. Password-protected archives are
reported but not opened.

When the Song Checker repairs or splits a ZIP, the new files are standard (Deflate) ZIPs. Other
formats are never modified.

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

The tests build tiny synthetic CDG/KAR/MIDI files in code (no copyrighted media).

To also check a folder of real karaoke files that can't be committed (e.g. a clone of a private
test-media repo), point `KARAOKE_TEST_MEDIA` at it. Every KAR/MIDI file is parsed and checked for a
title, real lyrics (no copyright notices), a sensible length and a synthesisable MIDI stream; every
`.cdg` is checked for a matching audio file and real graphics:

```bash
KARAOKE_TEST_MEDIA=/path/to/karaoke-test-media dotnet test --filter RealMediaTests
``` The end-to-end
tests run real conversions when ffmpeg is installed; the KAR test also needs a SoundFont (set
`KARAOKE_TEST_SOUNDFONT` or install `timgm6mb-soundfont` on Debian/Ubuntu).
