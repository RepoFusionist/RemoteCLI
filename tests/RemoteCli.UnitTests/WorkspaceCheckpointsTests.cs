using RemoteCli.Desktop;
using System.IO.Compression;
using System.Text.Json;

namespace RemoteCli.UnitTests;

public sealed class WorkspaceCheckpointsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "longshell-checkpoints-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _workspace;
    private readonly WorkspaceCheckpoints _store;
    public WorkspaceCheckpointsTests()
    {
        _workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(_workspace);
        _store = new WorkspaceCheckpoints(Path.Combine(_root, "snapshots"));
    }

    [Fact]
    public void RestoresFilesAndEmptyDirectoriesAndKeepsAnUndoSnapshot()
    {
        File.WriteAllText(Path.Combine(_workspace, "old.txt"), "before");
        Directory.CreateDirectory(Path.Combine(_workspace, "empty"));
        Directory.CreateDirectory(Path.Combine(_workspace, ".git"));
        File.WriteAllText(Path.Combine(_workspace, ".git", "config"), "keep");
        var before = _store.Capture(_workspace);
        File.WriteAllText(Path.Combine(_workspace, "old.txt"), "after");
        Directory.Delete(Path.Combine(_workspace, "empty"));
        File.WriteAllText(Path.Combine(_workspace, "new.txt"), "new");
        var preview = _store.Inspect(_workspace, before);
        Assert.Equal((1, 1, 0), (preview.Added, preview.Changed, preview.Removed));
        var backup = _store.Restore(_workspace, before, preview.Fingerprint);
        Assert.Equal("before", File.ReadAllText(Path.Combine(_workspace, "old.txt")));
        Assert.False(File.Exists(Path.Combine(_workspace, "new.txt")));
        Assert.True(Directory.Exists(Path.Combine(_workspace, "empty")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(_workspace, ".git", "config")));
        var undo = _store.Inspect(_workspace, backup);
        _store.Restore(_workspace, backup, undo.Fingerprint);
        Assert.Equal("after", File.ReadAllText(Path.Combine(_workspace, "old.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(_workspace, "new.txt")));
        Assert.False(Directory.Exists(Path.Combine(_workspace, "empty")));
    }

    [Fact]
    public void RejectsChangedWorkspaceAfterPreviewAndMismatchedRoot()
    {
        var path = Path.Combine(_workspace, "file.txt");
        File.WriteAllText(path, "original");
        var checkpoint = _store.Capture(_workspace);
        File.WriteAllText(path, "first change");
        var preview = _store.Inspect(_workspace, checkpoint);
        File.WriteAllText(path, "later change");
        Assert.Throws<IOException>(() => _store.Restore(_workspace, checkpoint, preview.Fingerprint));
        Assert.Equal("later change", File.ReadAllText(path));
        var other = Path.Combine(_root, "other"); Directory.CreateDirectory(other);
        Assert.Throws<IOException>(() => _store.Inspect(other, checkpoint));
        Assert.Throws<IOException>(() => _store.Inspect(_workspace, "../../other"));
    }

    [Fact]
    public void RejectsChangesMadeDuringBackupWithoutModifyingWorkspace()
    {
        var path = Path.Combine(_workspace, "file.txt");
        File.WriteAllText(path, "original");
        var checkpoint = _store.Capture(_workspace);
        File.WriteAllText(path, "before preview");
        var preview = _store.Inspect(_workspace, checkpoint);
        string? backup = null;
        Assert.Throws<IOException>(() => _store.Restore(_workspace, checkpoint, preview.Fingerprint, id =>
        {
            backup = id;
            File.WriteAllText(path, "changed during backup");
        }));
        Assert.NotNull(backup);
        Assert.Equal("changed during backup", File.ReadAllText(path));
        Assert.Equal(1, _store.Inspect(_workspace, backup!).Changed);
    }

    [Fact]
    public void RejectsArchivePathsOutsideTheWorkspace()
    {
        var storage = Path.Combine(_root, "snapshots"); Directory.CreateDirectory(storage);
        var id = Guid.NewGuid().ToString("N");
        using (var archive = ZipFile.Open(Path.Combine(storage, id + ".zip"), ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("manifest.json");
            using var stream = entry.Open();
            JsonSerializer.Serialize(stream, new { Root = _workspace, Directories = Array.Empty<string>(),
                Files = new[] { new { Path = "../outside.txt", Size = 1, Hash = new string('0', 64) } } });
        }
        Assert.Throws<IOException>(() => _store.Inspect(_workspace, id));
        Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
