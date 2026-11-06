using KaraokeToMp4.Health;

namespace KaraokeToMp4.Tests;

public class SongOrganizerTests
{
    private static SongHealth Song(string path, SongHealthStatus status = SongHealthStatus.CannotRepair) =>
        new(path, "test", status, "", Array.Empty<string>());

    private static string Write(string path, string content = "data")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Move_KeepsRelativePath_AndMovesTheWholeSong()
    {
        using var dir = new TempDir();
        var src = dir.File("Archive");
        var dst = dir.File("Missing Music");
        var cdg = Write(Path.Combine(src, "CDG", "CDG", "e", "E., Sheila - Love Bizzare, A - SC8718-05.cdg"));
        var mp3 = Write(Path.ChangeExtension(cdg, ".mp3"));
        var bak = Write(cdg + ".bak");
        var other = Write(Path.Combine(src, "CDG", "CDG", "e", "Other Song.mp3"));

        var result = SongOrganizer.Relocate(Song(cdg), src, dst, RelocateAction.Move);

        Assert.True(result.Succeeded, result.Problem);
        Assert.Equal(3, result.Files.Count);
        foreach (var f in new[] { cdg, mp3, bak })
        {
            Assert.False(File.Exists(f));
            Assert.True(File.Exists(f.Replace(src, dst)));
        }
        Assert.True(File.Exists(other)); // unrelated file untouched
    }

    [Fact]
    public void Copy_LeavesTheOriginal()
    {
        using var dir = new TempDir();
        var src = dir.File("Archive");
        var dst = dir.File("Repairable");
        var zip = Write(Path.Combine(src, "a", "Song.zip"));

        var result = SongOrganizer.Relocate(Song(zip, SongHealthStatus.Repairable), src, dst, RelocateAction.Copy);

        Assert.True(result.Succeeded);
        Assert.Equal(FileOutcome.Copied, result.Files[0].Outcome);
        Assert.True(File.Exists(zip));
        Assert.True(File.Exists(Path.Combine(dst, "a", "Song.zip")));
    }

    [Fact]
    public void DifferentFileAtDestination_IsAConflict_AndNothingMoves()
    {
        using var dir = new TempDir();
        var src = dir.File("Archive");
        var dst = dir.File("Out");
        var cdg = Write(Path.Combine(src, "x", "S.cdg"), "graphics");
        var mp3 = Write(Path.Combine(src, "x", "S.mp3"), "audio");
        Write(Path.Combine(dst, "x", "S.mp3"), "DIFFERENT audio");

        var result = SongOrganizer.Relocate(Song(cdg), src, dst, RelocateAction.Move);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Files, f => f.Outcome == FileOutcome.Conflict);
        Assert.True(File.Exists(cdg));  // the pair is not split up
        Assert.True(File.Exists(mp3));
        Assert.False(File.Exists(Path.Combine(dst, "x", "S.cdg")));
    }

    [Fact]
    public void IdenticalFileAtDestination_CountsAsDone()
    {
        using var dir = new TempDir();
        var src = dir.File("Archive");
        var dst = dir.File("Out");
        var kar = Write(Path.Combine(src, "S.kar"), "same");
        Write(Path.Combine(dst, "S.kar"), "same");

        var result = SongOrganizer.Relocate(Song(kar), src, dst, RelocateAction.Move);
        Assert.True(result.Succeeded);
        Assert.Equal(FileOutcome.AlreadyThere, result.Files[0].Outcome);
        Assert.False(File.Exists(kar));
    }

    [Fact]
    public void SongOutsideTheSourceRoot_IsSkipped()
    {
        using var dir = new TempDir();
        var song = Write(dir.File("Elsewhere/S.kar"));
        var result = SongOrganizer.Relocate(Song(song), dir.File("Archive"), dir.File("Out"), RelocateAction.Move);
        Assert.False(result.Succeeded);
        Assert.Equal(FileOutcome.Skipped, result.Files[0].Outcome);
        Assert.True(File.Exists(song));
    }

    [Fact]
    public void OverlappingRoots_AreRejected()
    {
        using var dir = new TempDir();
        var src = dir.File("Songs");
        Assert.NotNull(SongOrganizer.ValidateRoots(src, src));
        Assert.NotNull(SongOrganizer.ValidateRoots(src, Path.Combine(src, "No Issues")));
        Assert.NotNull(SongOrganizer.ValidateRoots(Path.Combine(src, "Archive"), src));
        Assert.Null(SongOrganizer.ValidateRoots(Path.Combine(src, "Archive"), Path.Combine(src, "No Issues")));
    }

    [Fact]
    public void EmptyFolders_AreRemovedUpToButNotIncludingTheRoot()
    {
        using var dir = new TempDir();
        var src = dir.File("Archive");
        var song = Write(Path.Combine(src, "CDG", "e", "S.kar"));
        Write(Path.Combine(src, "CDG", "keep.txt"));

        SongOrganizer.Relocate(Song(song), src, dir.File("Out"), RelocateAction.Move);
        int removed = SongOrganizer.RemoveEmptyFolders(src, new[] { Path.Combine(src, "CDG", "e") });

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(Path.Combine(src, "CDG", "e")));
        Assert.True(Directory.Exists(Path.Combine(src, "CDG")));   // still has keep.txt
        Assert.True(Directory.Exists(src));
    }

    [Fact]
    public void SongFiles_IncludesAudioOfACdgAndBackups_ButNotLookalikes()
    {
        using var dir = new TempDir();
        var cdg = Write(dir.File("Song.cdg"));
        Write(dir.File("Song.mp3"));
        Write(dir.File("Song.ogg"));
        Write(dir.File("Song.mp3.bak2"));
        Write(dir.File("Song.cdg.bak"));
        Write(dir.File("Song.txt"));          // not part of the song
        Write(dir.File("Song.cdg.backup"));   // not a backup made by the checker
        Write(dir.File("Song 2.mp3"));

        var names = SongOrganizer.SongFiles(cdg).Select(Path.GetFileName).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "Song.cdg", "Song.cdg.bak", "Song.mp3", "Song.mp3.bak2", "Song.ogg" }, names);
    }
}
