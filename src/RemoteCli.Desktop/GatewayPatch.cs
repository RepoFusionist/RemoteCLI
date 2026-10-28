using System.Text;
using System.Text.Json;

namespace RemoteCli.Desktop;

// Validate the entire patch, then require a per-patch user decision before any file changes.
internal sealed class GatewayPatch(string root, Func<string, CancellationToken, Task<bool>> approve)
{
    private sealed record Change(string Path, byte[]? Original, byte[]? Replacement);

    internal LocalChatTool Create() => new("apply_patch", "应用 Codex 格式文本补丁。运行前用户逐次审核完整补丁。",
        """{"type":"object","properties":{"patch":{"type":"string"},"workdir":{"type":"string","description":"工作区内的相对工作目录"},"environment_id":{"type":"string","description":"仅支持 local"}},"required":["patch"],"additionalProperties":false}""", ApplyAsync);

    private async Task<string> ApplyAsync(JsonElement args, CancellationToken token)
    {
        var patch = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("patch", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        if (patch.Length is 0 or > 100000) return "错误：补丁为空或超过 100000 字符。";
        var workdir = ".";
        if (args.TryGetProperty("workdir", out var workingDirectory))
        {
            if (workingDirectory.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(workingDirectory.GetString())) return "错误：工作目录无效。";
            workdir = workingDirectory.GetString()!;
        }
        if (!LocalChatTools.TryResolve(root, workdir, out var patchRoot, out var directoryError)) return directoryError;
        if (!Directory.Exists(patchRoot)) return "错误：补丁工作目录不存在。";
        if (args.TryGetProperty("environment_id", out var environment) && (environment.ValueKind != JsonValueKind.String || environment.GetString() != "local"))
            return "错误：补丁仅支持 local 环境。";
        List<Change> changes;
        try { changes = Parse(patch, patchRoot); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { return "错误：补丁无效：" + ex.Message; }
        if (!await approve("工作目录：" + patchRoot + "\n环境：local\n\n" + patch, token).ConfigureAwait(false)) return "错误：用户拒绝应用补丁。";
        token.ThrowIfCancellationRequested();
        foreach (var change in changes)
        {
            if (!LocalChatTools.TryResolve(root, Path.GetRelativePath(root, change.Path), out _, out var error)) return error;
            if (HasChanged(change))
                return "错误：文件在补丁审批期间发生变化，请重新读取。";
        }
        var staged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var applied = new List<string>();
        try
        {
            foreach (var change in changes.Where(item => item.Replacement is not null))
            {
                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(change.Path)!);
                var temporary = Path.Combine(Path.GetDirectoryName(change.Path)!, ".longshell-patch-" + Guid.NewGuid().ToString("N") + ".tmp");
                await using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
                staged.Add(change.Path, temporary);
                await stream.WriteAsync(change.Replacement!, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            foreach (var change in changes)
            {
                if (!LocalChatTools.TryResolve(root, Path.GetRelativePath(root, change.Path), out _, out var error)) return error;
                if (HasChanged(change))
                    return "错误：文件在补丁准备期间发生变化，请重新读取。";
            }
            // Do not cancel midway through commit; report any partial commit explicitly.
            foreach (var change in changes)
            {
                if (change.Replacement is null) File.Delete(change.Path);
                else File.Move(staged[change.Path], change.Path, overwrite: change.Original is not null);
                applied.Add(Path.GetRelativePath(root, change.Path));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return "错误：补丁提交失败；已应用文件：" + (applied.Count == 0 ? "无" : string.Join(", ", applied)) + "。" + ex.Message; }
        finally
        {
            foreach (var temporary in staged.Values)
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return "已应用补丁：\n" + string.Join("\n", changes.Select(c => Path.GetRelativePath(root, c.Path)));
    }

    private List<Change> Parse(string patch, string patchRoot)
    {
        var lines = patch.Replace("\r\n", "\n").Trim().Split('\n');
        if (lines.Length >= 4 && (lines[0] == "<<EOF" || lines[0] == "<<'EOF'" || lines[0] == "<<\"EOF\"") && lines[^1] == "EOF")
            lines = lines[1..^1];
        if (lines.Length < 3 || lines[0].Trim() != "*** Begin Patch") throw new ArgumentException("缺少 Begin Patch。");
        var states = new Dictionary<string, VirtualFile>(StringComparer.OrdinalIgnoreCase);
        var index = 1; var ended = false; var hunkCount = 0;
        VirtualFile GetState(string path)
        {
            if (states.TryGetValue(path, out var existing)) return existing;
            if (Directory.Exists(path)) throw new ArgumentException("补丁目标是目录：" + path);
            if (File.Exists(path) && new FileInfo(path).Length > 524288) throw new ArgumentException("文件超过 512 KiB。");
            var bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
            if (bytes is { Length: > 524288 }) throw new ArgumentException("文件超过 512 KiB。");
            var state = new VirtualFile(path, bytes);
            states.Add(path, state);
            return state;
        }
        if (index < lines.Length && lines[index].StartsWith("*** Environment ID: ", StringComparison.Ordinal))
        {
            if (lines[index++][20..] != "local") throw new ArgumentException("补丁仅支持 local 环境。");
        }
        while (index < lines.Length)
        {
            var header = lines[index++];
            if (header.Trim() == "*** End Patch") { ended = true; break; }
            if (string.IsNullOrWhiteSpace(header)) continue;
            string action, path;
            if (header.StartsWith("*** Add File: ", StringComparison.Ordinal)) { action = "add"; path = header[14..]; }
            else if (header.StartsWith("*** Update File: ", StringComparison.Ordinal)) { action = "update"; path = header[17..]; }
            else if (header.StartsWith("*** Delete File: ", StringComparison.Ordinal)) { action = "delete"; path = header[17..]; }
            else throw new ArgumentException("不支持的补丁文件标头：" + header);
            if (++hunkCount > 20 || !LocalChatTools.TryResolve(patchRoot, path, out var target, out _))
                throw new ArgumentException("文件路径无效或文件操作数量超过 20。");
            var current = GetState(target);
            var original = current.Current;
            if (action == "add" && original is not null || action != "add" && original is null) throw new ArgumentException("文件存在状态与补丁操作不符：" + path);
            if (action == "delete") { current.Current = null; continue; }
            if (action == "add")
            {
                var added = new List<string>();
                while (index < lines.Length && !lines[index].StartsWith("*** ", StringComparison.Ordinal))
                {
                    var line = lines[index++]; if (!line.StartsWith('+')) throw new ArgumentException("新增文件内容必须以 + 开头。"); added.Add(line[1..]);
                }
                current.Current = new UTF8Encoding(false).GetBytes(string.Join("\n", added) + (added.Count > 0 ? "\n" : ""));
                continue;
            }
            string? moveTarget = null;
            VirtualFile? moveState = null;
            if (index < lines.Length && lines[index].StartsWith("*** Move to: ", StringComparison.Ordinal))
            {
                var movePath = lines[index++][13..];
                if (!LocalChatTools.TryResolve(patchRoot, movePath, out moveTarget, out _) || string.Equals(target, moveTarget, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("移动目标无效：" + movePath);
                moveState = GetState(moveTarget);
                if (moveState.Current is not null) throw new ArgumentException("移动目标已存在：" + movePath);
            }
            var hasBom = original!.Length >= 3 && original[0] == 0xef && original[1] == 0xbb && original[2] == 0xbf;
            var text = new UTF8Encoding(false, true).GetString(original, hasBom ? 3 : 0, original.Length - (hasBom ? 3 : 0));
            if (text.Contains('\0')) throw new ArgumentException("不支持二进制文件。");
            var source = SourceFile.Parse(text);
            var content = source.Lines.Select(line => line.Text).ToArray();
            var replacements = new List<Replacement>();
            var position = 0; var hunks = 0;
            while (index < lines.Length && !lines[index].StartsWith("*** ", StringComparison.Ordinal))
            {
                if (lines[index].StartsWith("@@", StringComparison.Ordinal))
                {
                    var marker = lines[index++];
                    if (marker != "@@" && !marker.StartsWith("@@ ", StringComparison.Ordinal)) throw new ArgumentException("修改块锚点格式无效。");
                    var anchor = marker.Length > 3 ? marker[3..] : "";
                    if (anchor.Length > 0)
                    {
                        var foundAnchor = FindSequence(content, [anchor], position, eof: false);
                        if (foundAnchor < 0) throw new ArgumentException("找不到修改块锚点：" + anchor);
                        position = foundAnchor + 1;
                    }
                }
                var before = new List<string>(); var after = new List<(string Text, int? Context)>();
                while (index < lines.Length && !lines[index].StartsWith("*** ", StringComparison.Ordinal) && !lines[index].StartsWith("@@", StringComparison.Ordinal))
                {
                    var line = lines[index++];
                    if (line.Length == 0) { before.Add(""); after.Add(("", before.Count - 1)); continue; }
                    if (line[0] is not (' ' or '+' or '-')) throw new ArgumentException("补丁行格式无效。");
                    if (line[0] != '+') before.Add(line[1..]);
                    if (line[0] != '-') after.Add((line[1..], line[0] == ' ' ? before.Count - 1 : null));
                }
                if (before.Count == 0 && after.Count == 0) throw new ArgumentException("修改块不能为空。");
                var atEof = index < lines.Length && lines[index] == "*** End of File";
                if (atEof) index++;
                if (before.Count == 0)
                {
                    // Codex inserts an addition-only chunk at the end of the file.
                    replacements.Add(new Replacement(content.Length, 0, after.Select(line => line.Text).ToArray()));
                }
                else
                {
                    var found = FindSequence(content, before, position, atEof);
                    // Upstream retries without the final empty context line when it denotes a trailing terminator.
                    if (found < 0 && before[^1].Length == 0)
                    {
                        before.RemoveAt(before.Count - 1);
                        if (after.Count > 0 && after[^1].Text.Length == 0) after.RemoveAt(after.Count - 1);
                        found = FindSequence(content, before, position, atEof);
                    }
                    if (found < 0) throw new ArgumentException("找不到修改块原文：" + path);
                    var oldStart = 0; var newStart = 0;
                    for (var next = 0; next < after.Count; next++)
                    {
                        if (after[next].Context is not { } context || context >= before.Count) continue;
                        if (oldStart != context || newStart != next)
                            replacements.Add(new Replacement(found + oldStart, context - oldStart, after.Skip(newStart).Take(next - newStart).Select(line => line.Text).ToArray()));
                        oldStart = context + 1; newStart = next + 1;
                    }
                    if (oldStart != before.Count || newStart != after.Count)
                        replacements.Add(new Replacement(found + oldStart, before.Count - oldStart, after.Skip(newStart).Select(line => line.Text).ToArray()));
                    position = found + before.Count;
                }
                hunks++;
                if (atEof)
                {
                    while (index < lines.Length && lines[index].Length == 0) index++;
                    break;
                }
            }
            if (hunks == 0 && moveTarget is null) throw new ArgumentException("修改文件缺少修改块。");
            var updated = source.Apply(replacements);
            var updatedBytes = hunks == 0 ? original! : new UTF8Encoding(hasBom).GetPreamble().Concat(new UTF8Encoding(false).GetBytes(updated)).ToArray();
            if (moveState is null) current.Current = updatedBytes;
            else { moveState.Current = updatedBytes; current.Current = null; }
        }
        if (!ended || hunkCount == 0 || lines.Skip(index).Any(line => line.Length != 0)) throw new ArgumentException("补丁结束标记无效。");
        // Creates and updates precede deletions so an unsuccessful move never deletes its source first.
        return states.Values.Where(state => !BytesEqual(state.Initial, state.Current))
            .OrderBy(state => state.Current is null ? 1 : 0)
            .Select(state => new Change(state.Path, state.Initial, state.Current)).ToList();
    }

    private static bool BytesEqual(byte[]? left, byte[]? right) => left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);
    private static bool HasChanged(Change change)
    {
        if (change.Original is null) return File.Exists(change.Path) || Directory.Exists(change.Path);
        var file = new FileInfo(change.Path);
        if (!file.Exists || file.Length != change.Original.Length) return true;
        return !File.ReadAllBytes(change.Path).AsSpan().SequenceEqual(change.Original);
    }
    private sealed class VirtualFile(string path, byte[]? bytes)
    {
        internal string Path { get; } = path;
        internal byte[]? Initial { get; } = bytes;
        internal byte[]? Current { get; set; } = bytes;
    }

    // Mirrors Codex seek_sequence: exact, trim-end, trim, then normalized Unicode punctuation.
    private static int FindSequence(IReadOnlyList<string> lines, IReadOnlyList<string> pattern, int start, bool eof)
    {
        if (pattern.Count == 0) return start;
        if (pattern.Count > lines.Count) return -1;
        var first = eof ? Math.Max(start, lines.Count - pattern.Count) : start;
        for (var mode = 0; mode < 4; mode++)
        {
            for (var at = first; at <= lines.Count - pattern.Count; at++)
            {
                var matches = true;
                for (var n = 0; n < pattern.Count; n++)
                {
                    var actual = lines[at + n]; var expected = pattern[n];
                    if (mode == 1) { actual = actual.TrimEnd(); expected = expected.TrimEnd(); }
                    else if (mode == 2) { actual = actual.Trim(); expected = expected.Trim(); }
                    else if (mode == 3) { actual = NormalizePunctuation(actual); expected = NormalizePunctuation(expected); }
                    if (!string.Equals(actual, expected, StringComparison.Ordinal)) { matches = false; break; }
                }
                if (matches) return at;
            }
        }
        return -1;
    }

    private static string NormalizePunctuation(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var c in value.Trim())
        {
            result.Append(c switch
            {
                '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2015' or '\u2212' => '-',
                '\u2018' or '\u2019' or '\u201a' or '\u201b' => '\'',
                '\u201c' or '\u201d' or '\u201e' or '\u201f' => '"',
                '\u00a0' or '\u2002' or '\u2003' or '\u2004' or '\u2005' or '\u2006' or '\u2007' or '\u2008' or '\u2009' or '\u200a' or '\u202f' or '\u205f' or '\u3000' => ' ',
                _ => c
            });
        }
        return result.ToString();
    }

    private sealed record Replacement(int Start, int OldCount, IReadOnlyList<string> NewLines);
    private sealed record SourceLine(string Text, string? Ending);

    private sealed class SourceFile(List<SourceLine> lines, string preferredEnding)
    {
        internal IReadOnlyList<SourceLine> Lines => lines;

        internal static SourceFile Parse(string text)
        {
            var result = new List<SourceLine>(); string? preferred = null; var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] is not ('\r' or '\n')) continue;
                var ending = text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? "\r\n" : text[i].ToString();
                result.Add(new SourceLine(text[start..i], ending));
                preferred ??= ending;
                if (ending.Length == 2) i++;
                start = i + 1;
            }
            if (start < text.Length) result.Add(new SourceLine(text[start..], null));
            return new SourceFile(result, preferred ?? "\n");
        }

        internal string Apply(IReadOnlyList<Replacement> replacements)
        {
            var output = new List<SourceLine>(); var cursor = 0;
            foreach (var change in replacements.OrderBy(change => change.Start))
            {
                if (change.Start < cursor || change.OldCount < 0 || change.Start + change.OldCount > lines.Count)
                    throw new ArgumentException("补丁修改块相互重叠。");
                for (; cursor < change.Start; cursor++) output.Add(lines[cursor]);
                cursor += change.OldCount;
                output.AddRange(change.NewLines.Select(line => new SourceLine(line, preferredEnding)));
            }
            for (; cursor < lines.Count; cursor++) output.Add(lines[cursor]);
            var result = new StringBuilder();
            foreach (var line in output) result.Append(line.Text).Append(line.Ending ?? preferredEnding);
            return result.ToString();
        }
    }
}
