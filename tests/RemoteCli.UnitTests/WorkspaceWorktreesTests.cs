using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using RemoteCli.Desktop;

namespace RemoteCli.UnitTests;

public sealed class WorkspaceWorktreesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "remotecli-native-worktrees-" + Guid.NewGuid().ToString("N"));
    private readonly string git = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe");
    public WorkspaceWorktreesTests() => Directory.CreateDirectory(root);
    private WorkspaceWorktrees Repository()
    {
        Git(root, "init", "--initial-branch=main", ".");
        File.WriteAllText(Path.Combine(root, "source.txt"), "HEAD content");
        Git(root, "add", "--all");
        Git(root, "-c", "user.name=Test", "-c", "user.email=test@localhost", "-c", "commit.gpgsign=false", "commit", "-m", "base");
        return new WorkspaceWorktrees(root, git);
    }
    [Fact]
    public void WorktreeDefaultsInsideSelectedDirectoryAndStartsFromHeadOnly()
    {
        var store = Repository(); var head = Git(root, "rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(root, "source.txt"), "uncommitted source edit");
        Git(root, "add", "source.txt");
        File.WriteAllText(Path.Combine(root, "untracked.txt"), "leave in source");
        var tree = store.Open("conversation");
        Assert.StartsWith(Path.Combine(root, ".remotecli", "worktrees"), tree.Path);
        Assert.True(File.Exists(Path.Combine(tree.Path, ".git")));
        Assert.Equal(head, tree.Head);
        Assert.Equal("HEAD content", File.ReadAllText(Path.Combine(tree.Path, "source.txt")));
        Assert.False(File.Exists(Path.Combine(tree.Path, "untracked.txt")));
        Assert.Equal("uncommitted source edit", File.ReadAllText(Path.Combine(root, "source.txt")));
        Assert.Equal("uncommitted source edit", Git(root, "show", ":source.txt"));
        Assert.Equal(head, Git(root, "rev-parse", "HEAD"));
        Assert.DoesNotContain(".remotecli", Git(root, "status", "--porcelain"));
    }
    [Fact]
    public void ChangesAreDirectlyInTheWorktreeAndNativeCommitsLeaveSourceUntouched()
    {
        var store = Repository(); var tree = store.Open("c");
        File.WriteAllText(Path.Combine(store.ExecutionRoot(tree), "source.txt"), "AI change");
        var commit = store.Commit("c");
        Assert.NotEqual(tree.Head, commit); Assert.True(WorkspaceWorktrees.IsCommit(commit));
        Assert.Equal("AI change", Git(tree.Path, "show", commit + ":source.txt"));
        Assert.Equal("HEAD content", File.ReadAllText(Path.Combine(root, "source.txt")));
        Assert.Empty(Git(tree.Path, "status", "--porcelain"));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, ".remotecli"), "*.zip", SearchOption.AllDirectories));
    }
    [Fact]
    public void ConversationReopensTheSameNativeWorktreeAndChildUsesParentCommit()
    {
        var store = Repository(); var parent = store.Open("parent");
        File.WriteAllText(Path.Combine(parent.Path, "source.txt"), "parent committed"); var commit = store.Commit("parent");
        File.WriteAllText(Path.Combine(parent.Path, "source.txt"), "parent uncommitted");
        Assert.Equal(parent.Path, new WorkspaceWorktrees(root, git).Open("parent").Path);
        var child = store.Open("child", "parent");
        Assert.NotEqual(parent.Path, child.Path); Assert.Equal(commit, child.Head);
        Assert.Equal("parent committed", File.ReadAllText(Path.Combine(child.Path, "source.txt")));
        Assert.Equal("parent uncommitted", File.ReadAllText(Path.Combine(parent.Path, "source.txt")));
    }
    [Fact]
    public void RollbackCreatesAnotherWorktreeAndPreservesAllUncommittedOldBytes()
    {
        var store = Repository(); var old = store.Open("c");
        File.WriteAllText(Path.Combine(old.Path, "source.txt"), "change"); store.Commit("c");
        File.WriteAllText(Path.Combine(old.Path, "source.txt"), "external editor unsaved to git");
        File.WriteAllText(Path.Combine(old.Path, "new.txt"), "untracked data");
        var preview = store.Inspect("c", old.Head); var restored = store.Restore("c", old.Head, preview.Fingerprint);
        Assert.NotEqual(old.Path, restored.Path);
        Assert.Equal("HEAD content", File.ReadAllText(Path.Combine(restored.Path, "source.txt")));
        Assert.Equal("external editor unsaved to git", File.ReadAllText(Path.Combine(old.Path, "source.txt")));
        Assert.Equal("untracked data", File.ReadAllText(Path.Combine(old.Path, "new.txt")));
        Assert.Equal(restored.Path, store.Open("c").Path);
        File.WriteAllText(Path.Combine(old.Path, "source.txt"), "editor continues after switch");
        Assert.Equal("HEAD content", File.ReadAllText(Path.Combine(restored.Path, "source.txt")));
    }
    [Fact]
    public void ChangedPreviewAndUnsupportedNodesCannotRollback()
    {
        var store = Repository(); var tree = store.Open("c"); var preview = store.Inspect("c", tree.Head);
        File.WriteAllText(Path.Combine(tree.Path, "source.txt"), "changed after preview");
        Assert.Throws<IOException>(() => store.Restore("c", tree.Head, preview.Fingerprint));
        Assert.Throws<IOException>(() => store.Inspect("c", new string('a', 32)));
        Assert.Equal("changed after preview", File.ReadAllText(Path.Combine(tree.Path, "source.txt")));
    }
    [Fact]
    public void FreshNonRepositoryIsInitializedNativelyAndInternalDirectoryIsNotCommitted()
    {
        File.WriteAllText(Path.Combine(root, "source.txt"), "initial source");
        var previousCeiling = Environment.GetEnvironmentVariable("GIT_CEILING_DIRECTORIES");
        WorkspaceWorktrees.Worktree tree;
        try
        {
            // The test temp directory can itself be under the development checkout.
            Environment.SetEnvironmentVariable("GIT_CEILING_DIRECTORIES", Path.GetDirectoryName(root));
            tree = new WorkspaceWorktrees(root, git).Open("c");
        }
        finally { Environment.SetEnvironmentVariable("GIT_CEILING_DIRECTORIES", previousCeiling); }
        Assert.Equal("initial source", File.ReadAllText(Path.Combine(tree.Path, "source.txt")));
        Assert.DoesNotContain(".remotecli", Git(root, "ls-files"));
        Assert.True(WorkspaceWorktrees.IsCommit(tree.Head));
    }
    [Fact]
    public void SelectedRepositorySubdirectoryKeepsItsRelativeExecutionRoot()
    {
        Repository(); var selected = Path.Combine(root, "sub"); Directory.CreateDirectory(selected);
        File.WriteAllText(Path.Combine(selected, "file.txt"), "sub content"); Git(root, "add", "sub");
        Git(root, "-c", "user.name=Test", "-c", "user.email=test@localhost", "-c", "commit.gpgsign=false", "commit", "-m", "sub");
        var store = new WorkspaceWorktrees(selected, git); var tree = store.Open("c");
        Assert.StartsWith(Path.Combine(selected, ".remotecli", "worktrees"), tree.Path);
        Assert.Equal("sub content", File.ReadAllText(Path.Combine(store.ExecutionRoot(tree), "file.txt")));
    }
    [Fact]
    public void TamperedPathsAndBrokenStateNeverRedirectOrDeleteExistingWorktrees()
    {
        var store = Repository(); var tree = store.Open("c"); var statePath = Path.Combine(root, ".remotecli", "worktrees.json");
        var state = JsonNode.Parse(File.ReadAllText(statePath))!;
        state["Conversations"]!.AsObject().First().Value!["Path"] = root;
        File.WriteAllText(statePath, state.ToJsonString());
        Assert.Throws<IOException>(() => store.Open("c"));
        Assert.True(Directory.Exists(tree.Path)); Assert.Equal("HEAD content", File.ReadAllText(Path.Combine(root, "source.txt")));
        File.WriteAllText(statePath, "broken"); Assert.Throws<IOException>(() => store.Open("c")); Assert.True(Directory.Exists(tree.Path));
        File.WriteAllText(statePath, "{\"Conversations\":null}"); Assert.Throws<IOException>(() => store.Open("c"));
    }
    [Fact]
    public void UnbornExistingRepositoryDoesNotAutomaticallyCommitUserStagedChanges()
    {
        Git(root, "init", "--initial-branch=main", ".");
        File.WriteAllText(Path.Combine(root, "staged.txt"), "user staged content"); Git(root, "add", "staged.txt");
        Assert.Throws<IOException>(() => new WorkspaceWorktrees(root, git).Open("c"));
        Assert.Contains("staged.txt", Git(root, "diff", "--cached", "--name-only"));
    }

    [Fact]
    public void MissingGitFailsWithoutCreatingZipOrCopyingWorkspace()
    {
        File.WriteAllText(Path.Combine(root, "source.txt"), "keep");
        Assert.Throws<IOException>(() => new WorkspaceWorktrees(root, Path.Combine(root, "missing-git.exe")).Open("c"));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(root, "source.txt")));
        Assert.False(Directory.Exists(Path.Combine(root, ".remotecli")));
    }

    [Fact]
    public async Task ApprovedFileToolUsesNativeWorktreeAndCapturesOnlyGitCommit()
    {
        var store = Repository(); var tree = store.Open("c");
        string? before = null;
        var guard = new WorktreeChangeGuard(_ => Task.FromResult(before = store.Commit("c")));
        var raw = LocalChatTools.Create(store.ExecutionRoot(tree), ["write_file"]).Single();
        var tool = GatewayApprovalPolicy.Protect(raw, (category, content, token) => guard.ApproveAsync(category, content, () => Task.FromResult(true), token));
        var arguments = System.Text.Json.JsonSerializer.SerializeToElement(new { path = "source.txt", content = "tool change" });
        await tool.Execute(arguments, CancellationToken.None);
        Assert.Equal(tree.Head, before);
        Assert.Equal("tool change", File.ReadAllText(Path.Combine(tree.Path, "source.txt")));
        Assert.Equal("HEAD content", File.ReadAllText(Path.Combine(root, "source.txt")));
        Assert.NotEqual(before, store.Commit("c"));
        Assert.False(LocalChatTools.TryResolve(tree.Path, ".git", out _, out _));
    }

    [Fact]
    public void StaleWindowCannotCommitToAnotherActiveWorktree()
    {
        var store = Repository(); var old = store.Open("c"); var preview = store.Inspect("c", old.Head);
        var current = store.Restore("c", old.Head, preview.Fingerprint);
        File.WriteAllText(Path.Combine(old.Path, "source.txt"), "old window edit");
        Assert.Throws<IOException>(() => store.Commit("c", old.Path));
        Assert.Equal("HEAD content", File.ReadAllText(Path.Combine(current.Path, "source.txt")));
        Assert.Equal("old window edit", File.ReadAllText(Path.Combine(old.Path, "source.txt")));
    }

    private string Git(string directory, params string[] args)
    {
        var start = new ProcessStartInfo(git) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync(); process.WaitForExit();
        Assert.True(process.ExitCode == 0, error.GetAwaiter().GetResult()); return output.GetAwaiter().GetResult().Trim();
    }
    public void Dispose()
    {
        // Test-owned directories only. Git object files may be read-only.
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }
}
