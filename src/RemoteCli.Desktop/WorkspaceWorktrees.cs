using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteCli.Desktop;

// Native Git worktrees. No mirrored inventories, ZIP archives, or in-place restore.
internal sealed class WorkspaceWorktrees
{
    private readonly string root;
    private readonly string git;
    private readonly string data;
    private readonly string trees;
    private readonly string statePath;
    internal sealed record Worktree(string Path, string Branch, string Head);
    internal sealed record Preview(string Fingerprint, int Added, int Changed, int Removed, int DirectoryChanges, IReadOnlyList<string> Changes);
    private sealed class State
    {
        public Dictionary<string, Worktree> Conversations { get; set; } = new(StringComparer.Ordinal);
    }

    internal WorkspaceWorktrees(string workspaceRoot, string gitExecutable)
    {
        var full = Path.GetFullPath(workspaceRoot);
        root = full.TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(root, Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new IOException("请选择具体项目目录作为工作目录。");
        git = gitExecutable;
        data = Path.Combine(root, ".remotecli"); trees = Path.Combine(data, "worktrees"); statePath = Path.Combine(data, "worktrees.json");
    }

    internal static bool TryCreate(string root, out WorkspaceWorktrees store)
    {
        store = null!;
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory.Trim('"'), "git.exe"))
            .Concat(new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe") });
        var executable = candidates.FirstOrDefault(File.Exists);
        if (executable is null) return false;
        store = new WorkspaceWorktrees(root, executable); return true;
    }

    internal static bool IsCommit(string? value) => value is { Length: 40 or 64 } && value.All(char.IsAsciiHexDigit);
    private static string Key(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant()[..24];
    private Mutex Lock()
    {
        var mutex = new Mutex(false, "Local\\RemoteCLI-Worktree-" + Key(root.ToUpperInvariant()));
        try { if (!mutex.WaitOne(TimeSpan.FromSeconds(30))) { mutex.Dispose(); throw new IOException("工作树正被其他请求使用，请重试。"); } }
        catch (AbandonedMutexException) { }
        return mutex;
    }
    private State ReadState()
    {
        if (!File.Exists(statePath)) return new State();
        try
        {
            if ((File.GetAttributes(statePath) & FileAttributes.ReparsePoint) != 0) throw new IOException("工作树记录不能是链接。");
            var state = JsonSerializer.Deserialize<State>(File.ReadAllBytes(statePath));
            if (state?.Conversations is null || state.Conversations.Values.Any(tree => tree is null || string.IsNullOrWhiteSpace(tree.Path)))
                throw new IOException("工作树记录无效，现有工作树已保留。");
            return state;
        }
        catch (JsonException ex) { throw new IOException("工作树记录损坏，请保留现有目录并修复记录后重试。", ex); }
    }
    private void WriteState(State state)
    {
        Directory.CreateDirectory(data);
        var temp = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(state), new UTF8Encoding(false)); File.Move(temp, statePath, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private string Repository()
    {
        if (!Directory.Exists(root)) throw new IOException("工作目录不存在。");
        NoLinks(root);
        NoLinks(data);
        var top = Run(root, ["rev-parse", "--show-toplevel"], allowFailure: true);
        if (top.Code != 0)
        {
            var ceilings = (Environment.GetEnvironmentVariable("GIT_CEILING_DIRECTORIES") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            for (var parent = new DirectoryInfo(root); parent is not null; parent = parent.Parent)
            {
                if (ceilings.Any(ceiling => string.Equals(ceiling.TrimEnd('\\'), parent.FullName.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) break;
                if (File.Exists(Path.Combine(parent.FullName, ".git")) || Directory.Exists(Path.Combine(parent.FullName, ".git")))
                    throw new IOException("已有 Git 仓库不可用：" + top.Error);
            }
            Run(root, ["init", "--initial-branch=main", "."]);
            AddExclude(root);
            Run(root, ["add", "--all"]);
            CommitAt(root, "Initialize Remote CLI workspace");
            top = Run(root, ["rev-parse", "--show-toplevel"]);
        }
        var repository = Path.GetFullPath(top.Output.Trim());
        AddExclude(repository);
        if (!IsCommit(Run(repository, ["rev-parse", "--verify", "HEAD"], allowFailure: true).Output.Trim()))
            throw new IOException("已有 Git 仓库尚无当前提交，请先提交再创建会话工作树。");
        return repository;
    }
    private void AddExclude(string repository)
    {
        var relative = Path.GetRelativePath(repository, data).Replace('\\', '/');
        var rule = "/" + relative + "/";
        var info = Run(repository, ["rev-parse", "--git-path", "info/exclude"]).Output.Trim();
        var path = Path.GetFullPath(info, repository); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var existing = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
        if (!existing.Split('\n').Any(line => line.TrimEnd('\r') == rule)) File.AppendAllText(path, "\n" + rule + "\n", new UTF8Encoding(false));
    }
    private static void NoLinks(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("工作树路径包含链接或联接点。");
    }
    private Worktree Validate(Worktree tree, string repository)
    {
        var path = Path.GetFullPath(tree.Path);
        if (!path.StartsWith(trees + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("工作树路径不在当前工作目录内。");
        NoLinks(path);
        var common = Run(path, ["rev-parse", "--git-common-dir"]).Output.Trim();
        var expected = Path.GetFullPath(Run(repository, ["rev-parse", "--git-common-dir"]).Output.Trim(), repository);
        if (!string.Equals(Path.GetFullPath(common, path), expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("工作树不属于当前仓库。");
        return tree with { Path = path, Head = Run(path, ["rev-parse", "HEAD"]).Output.Trim() };
    }
    private Worktree Create(string repository, string key, string start)
    {
        Directory.CreateDirectory(trees); NoLinks(trees);
        var suffix = key + "-" + Guid.NewGuid().ToString("N")[..8];
        var path = Path.Combine(trees, suffix); var branch = "codex/remotecli-" + suffix;
        Run(repository, ["worktree", "add", "-b", branch, path, start]);
        return new Worktree(path, branch, Run(path, ["rev-parse", "HEAD"]).Output.Trim());
    }
    internal Worktree Open(string conversationId, string? parentConversationId = null)
    {
        if (string.IsNullOrWhiteSpace(conversationId)) throw new IOException("缺少会话编号，无法打开工作树。");
        using var mutex = Lock();
        try
        {
            var repository = Repository(); var state = ReadState(); var key = Key(conversationId);
            if (state.Conversations.TryGetValue(key, out var tree)) return Validate(tree, repository);
            var start = "HEAD";
            if (!string.IsNullOrEmpty(parentConversationId) && state.Conversations.TryGetValue(Key(parentConversationId), out var parent))
                start = Validate(parent, repository).Head;
            tree = Create(repository, key, start); state.Conversations[key] = tree; WriteState(state); return tree;
        }
        finally { mutex.ReleaseMutex(); }
    }
    internal string ExecutionRoot(Worktree tree)
    {
        var repository = Repository();
        var relative = Path.GetRelativePath(repository, root);
        var path = relative == "." ? tree.Path : Path.Combine(tree.Path, relative);
        Directory.CreateDirectory(path); return path;
    }
    private string CommitAt(string path, string message)
    {
        Run(path, ["-c", "user.name=Remote CLI", "-c", "user.email=remotecli@localhost", "-c", "commit.gpgsign=false", "commit", "--allow-empty", "--no-verify", "-m", message]);
        return Run(path, ["rev-parse", "HEAD"]).Output.Trim();
    }
    internal string Commit(string conversationId, string? expectedPath = null)
    {
        using var mutex = Lock();
        try
        {
            var repository = Repository(); var state = ReadState();
            if (!state.Conversations.TryGetValue(Key(conversationId), out var saved)) throw new IOException("尚未打开会话工作树。");
            var tree = Validate(saved, repository);
            if (expectedPath is not null && !string.Equals(Path.GetFullPath(expectedPath), tree.Path, StringComparison.OrdinalIgnoreCase))
                throw new IOException("会话工作树已被另一个窗口切换，改动保留在原工作树；请重新发送。");
            Run(tree.Path, ["add", "--all"]);
            if (Run(tree.Path, ["diff", "--cached", "--quiet"], allowFailure: true).Code != 0)
                tree = tree with { Head = CommitAt(tree.Path, "Remote CLI conversation change") };
            state.Conversations[Key(conversationId)] = tree; WriteState(state); return tree.Head;
        }
        finally { mutex.ReleaseMutex(); }
    }
    internal Preview Inspect(string conversationId, string commit)
    {
        if (!IsCommit(commit)) throw new IOException("此节点没有有效 Git 提交，不支持回退。");
        var tree = Open(conversationId);
        Run(tree.Path, ["cat-file", "-e", commit + "^{commit}"]);
        var diff = Run(tree.Path, ["diff", "--name-status", commit, "--"]).Output;
        var untracked = Run(tree.Path, ["ls-files", "--others", "--exclude-standard"]).Output;
        var rows = diff.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var added = rows.Count(line => line.StartsWith('A')) + untracked.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        var removed = rows.Count(line => line.StartsWith('D')); var changed = rows.Length - rows.Count(line => line.StartsWith('A') || line.StartsWith('D'));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tree.Path + tree.Head + diff + untracked + Run(tree.Path, ["diff", "--binary", commit]).Output)));
        return new Preview(fingerprint, added, changed, removed, 0, rows.Concat(untracked.Split('\n', StringSplitOptions.RemoveEmptyEntries)).Take(100).ToArray());
    }
    internal Worktree Restore(string conversationId, string commit, string expectedFingerprint)
    {
        using var mutex = Lock();
        try
        {
            if (string.IsNullOrEmpty(expectedFingerprint) || Inspect(conversationId, commit).Fingerprint != expectedFingerprint)
                throw new IOException("工作树在预览后发生变化，请重新预览。");
            var repository = Repository(); var state = ReadState();
            var tree = Create(repository, Key(conversationId), commit);
            // Keep the former worktree and every uncommitted byte. Rollback changes only the active mapping.
            state.Conversations[Key(conversationId)] = tree; WriteState(state); return tree;
        }
        finally { mutex.ReleaseMutex(); }
    }
    private sealed record GitResult(int Code, string Output, string Error);
    private GitResult Run(string directory, string[] arguments, bool allowFailure = false)
    {
        var start = new ProcessStartInfo(git) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        start.Environment.Remove("GIT_DIR"); start.Environment.Remove("GIT_WORK_TREE"); start.Environment.Remove("GIT_INDEX_FILE"); start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var argument in new[] { "-c", "core.hooksPath=" + Path.Combine(data, "hooks-disabled") }.Concat(arguments)) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120000)) { process.Kill(true); throw new IOException("Git 操作超时。"); }
            var result = new GitResult(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
            if (result.Code != 0 && !allowFailure) throw new IOException("Git 操作失败：" + result.Error.Trim());
            return result;
        }
        catch (Win32Exception ex) { throw new IOException("无法启动 Git。", ex); }
    }
}
