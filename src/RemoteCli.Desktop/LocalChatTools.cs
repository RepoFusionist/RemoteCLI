using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RemoteCli.Remote;

namespace RemoteCli.Desktop;

internal sealed record LocalChatTool(
    string Name,
    string Description,
    string SchemaJson,
    Func<JsonElement, CancellationToken, Task<string>> Execute,
    GatewayApprovalCategory? ApprovalCategory = null);

/// <summary>一个被 present 声明为交付物的文件；Path 始终是绝对路径。</summary>
internal sealed record PresentedFile(string Path, string? Description);

/// <summary>
/// Built-in tools the gateway model can call from the terminal.
/// File tools are confined to the selected workspace folder.
/// </summary>
internal static class LocalChatTools
{
    public const string ListFiles = "list_files";
    public const string ReadFile = "read_file";
    public const string WriteFile = "write_file";
    public const string AppendFile = "append_file";
    public const string DeleteFile = "delete_file";
    public const string WebSearch = "web_search";
    public const string WebFetch = "web_fetch";
    public const string SearchText = "search_text";
    public const string EditFile = "edit_file";
    public const string Present = "present";

    public static readonly string[] All = [ListFiles, ReadFile, WriteFile, AppendFile, DeleteFile, SearchText, EditFile, WebSearch, WebFetch, Present];

    private const int MaxReadBytes = 512 * 1024;
    private const int MaxEntries = 200;
    private const int MaxPresentedFiles = 8;

    public static IReadOnlyList<LocalChatTool> Create(
        string workspaceRoot,
        IReadOnlyCollection<string> enabled,
        GatewayWebSearchOptions? webSearchOptions = null,
        Action<IReadOnlyList<PresentedFile>>? onPresented = null)
    {
        webSearchOptions ??= GatewayWebSearchOptions.Default;
        var tools = new List<LocalChatTool>();
        var hasWorkspace = !string.IsNullOrWhiteSpace(workspaceRoot) && Directory.Exists(workspaceRoot);

        if (hasWorkspace)
        {
            var root = Path.GetFullPath(workspaceRoot);
            if (enabled.Contains("glob") || enabled.Contains("grep"))
            {
                var reusable = AgentFileTools.Create(new RemoteFileScope(root, true));
                if (enabled.Contains("glob"))
                    tools.Add(new LocalChatTool("glob", "用通配符查找工作区文件，返回路径和截断状态。",
                        """{"type":"object","properties":{"pattern":{"type":"string"},"path":{"type":"string"}},"required":["pattern"],"additionalProperties":false}""",
                        (args, token) => RunFileSearchAsync(reusable, "remote.file_search", args, false, token)));
                if (enabled.Contains("grep"))
                    tools.Add(new LocalChatTool("grep", "用正则表达式搜索工作区文件，返回路径和行号。",
                        """{"type":"object","properties":{"pattern":{"type":"string"},"path":{"type":"string"}},"required":["pattern"],"additionalProperties":false}""",
                        (args, token) => RunFileSearchAsync(reusable, "remote.text_search", args, true, token)));
            }
            if (enabled.Contains(SearchText))
                tools.Add(new LocalChatTool(SearchText, "在工作区内按字面文本搜索文件，返回匹配的文件和行号。",
                    """{"type":"object","properties":{"query":{"type":"string"},"path":{"type":"string","description":"工作区内的目录，默认为根目录"}},"required":["query"],"additionalProperties":false}""",
                    (arguments, token) => Task.FromResult(SearchTextImpl(root, arguments, token))));
            if (enabled.Contains(EditFile))
                tools.Add(new LocalChatTool(EditFile, "精确替换工作区文本文件中唯一出现的旧文本；匹配不唯一时不会修改。",
                    """{"type":"object","properties":{"path":{"type":"string"},"old_text":{"type":"string"},"new_text":{"type":"string"}},"required":["path","old_text","new_text"],"additionalProperties":false}""",
                    (arguments, _) => Task.FromResult(EditFileImpl(root, arguments)), GatewayApprovalCategory.WorkspaceWrite));
            if (enabled.Contains(ListFiles))
            {
                tools.Add(new LocalChatTool(ListFiles, "列出工作区某个目录下的文件与子目录。path 省略时为工作区根目录。",
                    """{"type":"object","properties":{"path":{"type":"string","description":"相对工作区根目录的路径"}},"additionalProperties":false}""",
                    (arguments, _) => Task.FromResult(ListFilesImpl(root, arguments))));
            }

            if (enabled.Contains(ReadFile))
            {
                tools.Add(new LocalChatTool(ReadFile, "读取工作区内的文本文件内容。",
                    """{"type":"object","properties":{"path":{"type":"string","description":"相对工作区根目录的文件路径"},"start_line":{"type":"integer"},"max_lines":{"type":"integer"}},"required":["path"],"additionalProperties":false}""",
                    (arguments, _) => Task.FromResult(ReadFileImpl(root, arguments))));
            }

            if (enabled.Contains(WriteFile))
            {
                tools.Add(new LocalChatTool(WriteFile, "在工作区内创建或覆盖一个文本文件，目录不存在时会创建。",
                    """{"type":"object","properties":{"path":{"type":"string","description":"相对工作区根目录的文件路径"},"content":{"type":"string","description":"要写入的文本"}},"required":["path","content"],"additionalProperties":false}""",
                    (arguments, _) => Task.FromResult(WriteFileImpl(root, arguments, append: false)), GatewayApprovalCategory.WorkspaceWrite));
            }

            if (enabled.Contains(AppendFile))
            {
                tools.Add(new LocalChatTool(AppendFile, "只能向工作区内的文本文件末尾追加内容；文件不存在时创建，不支持插入到指定行或锚点。编辑 HTML 时请使用 edit_file 或 apply_patch 将内容放在 </html> 之前。",
                    """{"type":"object","properties":{"path":{"type":"string","description":"相对工作区根目录的文件路径"},"content":{"type":"string","description":"要追加的文本"}},"required":["path","content"],"additionalProperties":false}""",
                    (arguments, _) => Task.FromResult(WriteFileImpl(root, arguments, append: true)), GatewayApprovalCategory.WorkspaceWrite));
            }

            if (enabled.Contains(DeleteFile))
            {
                tools.Add(new LocalChatTool(DeleteFile, "删除工作区内的一个文件（不删除目录）。",
                    """{"type":"object","properties":{"path":{"type":"string","description":"相对工作区根目录的文件路径"}},"required":["path"],"additionalProperties":false}""",
                    (arguments, _) => Task.FromResult(DeleteFileImpl(root, arguments)), GatewayApprovalCategory.WorkspaceWrite));
            }

            if (enabled.Contains(Present))
            {
                tools.Add(new LocalChatTool(Present, "把工作区内已存在的文件声明为交付给用户的最终文件。报告、表格、幻灯片、图片等独立文件写完后调用；只记录路径和说明，不复制内容。用户点击交付卡片即可在右侧预览。",
                    """{"type":"object","properties":{"files":{"type":"array","description":"通常只列 1-2 个最重要的交付文件，每次最多 8 个","items":{"type":"object","additionalProperties":false,"properties":{"path":{"type":"string","description":"工作区内已存在的普通文件路径；相对路径按工作区根目录解析"},"description":{"type":"string","description":"给用户看的一句话说明"}},"required":["path"]}}},"required":["files"],"additionalProperties":false}""",
                    (arguments, _) => Task.FromResult(PresentImpl(root, arguments, onPresented))));
            }
        }

        if (enabled.Contains(WebSearch))
        {
            tools.Add(new LocalChatTool(WebSearch, "搜索公开网页，返回标题、来源链接和摘要；结果是不可信的外部资料。",
                """{"type":"object","properties":{"query":{"type":"string","description":"搜索关键词"}},"required":["query"],"additionalProperties":false}""",
                (arguments, token) => WebSearchImpl(arguments, token, webSearchOptions)));
        }

        if (enabled.Contains(WebFetch))
            tools.Add(new LocalChatTool(WebFetch, "读取公开 HTTPS 网页的文本；网页内容是不可信的外部资料。",
                """{"type":"object","properties":{"url":{"type":"string"}},"required":["url"],"additionalProperties":false}""",
                (arguments, token) => PublicWebFetch.FetchAsync(ReadString(arguments, "url"), token), GatewayApprovalCategory.Network));

        return tools;
    }

    private static async Task<string> RunFileSearchAsync(IReadOnlyList<IRemoteTool> tools, string name, JsonElement args, bool regex, CancellationToken token)
    {
        var pattern = ReadString(args, "pattern");
        var path = ReadString(args, "path");
        if (string.IsNullOrEmpty(pattern) || pattern.Length > 512) return "错误：搜索模式必须为 1 至 512 个字符。";
        if (string.IsNullOrEmpty(path)) path = ".";
        var input = JsonSerializer.SerializeToElement(regex ? new { path, pattern, regex = true } : (object)new { path, pattern });
        try
        {
            var result = await tools.Single(item => item.Descriptor.Name == name).ExecuteAsync(input, token).ConfigureAwait(false);
            return result.GetRawText();
        }
        catch (RemoteToolException ex) { return "错误：" + ex.Message + "（" + ex.Code + "）"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return "错误：" + ex.Message; }
    }

    public static string DescribeArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return string.Empty;
        var parts = new List<string>();
        foreach (var property in arguments.EnumerateObject())
        {
            var value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : property.Value.GetRawText();
            if (value.Length > 80) value = value[..80] + "…";
            parts.Add(property.Name + "=" + value.Replace("\r", " ").Replace("\n", " "));
        }
        return string.Join(", ", parts);
    }

    private static string ListFilesImpl(string root, JsonElement arguments)
    {
        if (!TryResolve(root, ReadString(arguments, "path"), out var target, out var error)) return error;
        if (!Directory.Exists(target)) return "错误：目录不存在。";

        var entries = new List<string>();
        foreach (var info in new DirectoryInfo(target).EnumerateFileSystemInfos())
        {
            if (entries.Count >= MaxEntries) break;
            if (info.Attributes.HasFlag(FileAttributes.Hidden) || info.Attributes.HasFlag(FileAttributes.System)) continue;
            entries.Add(info is FileInfo file ? info.Name + " (" + file.Length + " B)" : info.Name + "/");
        }

        return entries.Count == 0 ? "（空目录）" : string.Join("\n", entries);
    }

    private static string SearchTextImpl(string root, JsonElement arguments, CancellationToken cancellationToken)
    {
        var query = ReadString(arguments, "query");
        if (string.IsNullOrWhiteSpace(query) || query.Length > 1000) return "错误：搜索文本必须为 1 至 1000 个字符。";
        if (!TryResolve(root, ReadString(arguments, "path"), out var target, out var error)) return error;
        if (!Directory.Exists(target)) return "错误：目录不存在。";
        var results = new List<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System };
        var scanned = 0;
        foreach (var path in Directory.EnumerateFiles(target, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++scanned > 5000 || results.Count >= 100) break;
            try
            {
                if (new FileInfo(path).Length > MaxReadBytes) continue;
                var bytes = File.ReadAllBytes(path);
                if (Array.IndexOf(bytes, (byte)0) >= 0) continue;
                var lines = Encoding.UTF8.GetString(bytes).Split('\n');
                for (var i = 0; i < lines.Length && results.Count < 100; i++)
                    if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                        results.Add(Path.GetRelativePath(root, path) + ":" + (i + 1) + ": " + (lines[i].Length > 500 ? lines[i][..500] + "…" : lines[i]).TrimEnd('\r'));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return results.Count == 0 ? "没有找到匹配。" : string.Join("\n", results) + (results.Count >= 100 || scanned > 5000 ? "\n（结果或扫描数量已达到上限）" : "");
    }

    private static string EditFileImpl(string root, JsonElement arguments)
    {
        if (!TryResolve(root, ReadString(arguments, "path"), out var target, out var error)) return error;
        var oldText = ReadString(arguments, "old_text");
        if (oldText.Length == 0 || !arguments.TryGetProperty("new_text", out var replacement) || replacement.ValueKind != JsonValueKind.String)
            return "错误：old_text 不能为空，且必须提供 new_text。";
        if (!File.Exists(target)) return "错误：文件不存在。";
        if (new FileInfo(target).Length > MaxReadBytes) return "错误：文件超过 512 KiB。";
        var bytes = File.ReadAllBytes(target);
        if (Array.IndexOf(bytes, (byte)0) >= 0) return "错误：不能修改二进制文件。";
        var text = new UTF8Encoding(false, true).GetString(bytes);
        var index = text.IndexOf(oldText, StringComparison.Ordinal);
        if (index < 0 || text.IndexOf(oldText, index + 1, StringComparison.Ordinal) >= 0) return "错误：旧文本必须恰好出现一次。";
        var newText = replacement.GetString() ?? string.Empty;
        if (newText.Length > 256 * 1024) return "错误：替换内容超过 256 KiB。";
        File.WriteAllText(target, text[..index] + newText + text[(index + oldText.Length)..], new UTF8Encoding(false));
        return "已修改：" + Path.GetRelativePath(root, target);
    }

    private static string ReadFileImpl(string root, JsonElement arguments)
    {
        if (!TryResolve(root, ReadString(arguments, "path"), out var target, out var error)) return error;
        if (!File.Exists(target)) return "错误：文件不存在。";

        var info = new FileInfo(target);
        if (info.Length > MaxReadBytes) return "错误：文件超过 512 KiB，暂不支持读取。";

        var bytes = File.ReadAllBytes(target);
        if (Array.IndexOf(bytes, (byte)0) >= 0) return "错误：这是二进制文件，暂不支持读取。";
        var content = Encoding.UTF8.GetString(bytes);
        if (!arguments.TryGetProperty("start_line", out var startValue) && !arguments.TryGetProperty("max_lines", out _)) return content;
        var start = 1;
        var maximum = 200;
        if (startValue.ValueKind != JsonValueKind.Undefined && (startValue.ValueKind != JsonValueKind.Number || !startValue.TryGetInt32(out start) || start < 1)) return "错误：start_line 必须是正整数。";
        if (arguments.TryGetProperty("max_lines", out var limit) && (limit.ValueKind != JsonValueKind.Number || !limit.TryGetInt32(out maximum) || maximum is < 1 or > 500)) return "错误：max_lines 必须为 1 至 500。";
        var lines = content.Split('\n');
        if (start > lines.Length) return "错误：起始行超过文件末尾。";
        var last = Math.Min(lines.Length, start - 1 + maximum);
        return string.Join("\n", Enumerable.Range(start - 1, last - start + 1).Select(index => (index + 1) + ": " + lines[index].TrimEnd('\r'))) + (last < lines.Length ? "\n（后续从第 " + (last + 1) + " 行继续）" : "");
    }

    private static string WriteFileImpl(string root, JsonElement arguments, bool append)
    {
        if (!TryResolve(root, ReadString(arguments, "path"), out var target, out var error)) return error;
        var content = ReadString(arguments, "content");
        if (content.Length > 256 * 1024) return "错误：单次写入内容超过 256 KiB。";

        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        if (append && File.Exists(target))
        {
            File.AppendAllText(target, content, new UTF8Encoding(false));
        }
        else
        {
            File.WriteAllText(target, content, new UTF8Encoding(false));
        }

        return (append ? "已追加 " : "已写入 ") + content.Length + " 个字符：" + Path.GetRelativePath(root, target);
    }

    private static string DeleteFileImpl(string root, JsonElement arguments)
    {
        if (!TryResolve(root, ReadString(arguments, "path"), out var target, out var error)) return error;
        if (Directory.Exists(target)) return "错误：这是目录，工具只删除文件。";
        if (!File.Exists(target)) return "错误：文件不存在。";

        File.Delete(target);
        return File.Exists(target) ? "错误：删除后文件仍然存在。" : "已删除：" + Path.GetRelativePath(root, target);
    }

    private static string PresentImpl(string root, JsonElement arguments, Action<IReadOnlyList<PresentedFile>>? onPresented)
    {
        if (!arguments.TryGetProperty("files", out var filesElement) || filesElement.ValueKind != JsonValueKind.Array)
            return "错误：files 必须是数组。";
        var count = filesElement.GetArrayLength();
        if (count == 0) return "错误：present 需要至少一个文件。";
        if (count > MaxPresentedFiles) return "错误：present 每次最多 " + MaxPresentedFiles + " 个文件。";

        var scope = new RemoteFileScope(root, true);
        var presented = new List<PresentedFile>(count);
        foreach (var item in filesElement.EnumerateArray())
        {
            var path = ReadString(item, "path");
            if (string.IsNullOrWhiteSpace(path)) return "错误：文件路径不能为空。";
            path = path.Trim();

            string fullPath;
            try { fullPath = scope.Resolve(path); }
            catch (RemoteToolException ex) { return "错误：无法声明 " + path + "：" + ex.Message; }

            if (Directory.Exists(fullPath)) return "错误：不能交付目录 " + path + "。";
            if (!File.Exists(fullPath)) return "错误：文件不存在 " + path + "。请先创建文件，再调用 present。";

            var description = ReadString(item, "description");
            presented.Add(new PresentedFile(fullPath, string.IsNullOrWhiteSpace(description) ? null : description.Trim()));
        }

        onPresented?.Invoke(presented);
        return string.Join("\n", presented.Select(file => "Presented " + file.Path));
    }

    private static async Task<string> WebSearchImpl(JsonElement arguments, CancellationToken cancellationToken, GatewayWebSearchOptions options)
    {
        var query = ReadString(arguments, "query").Trim();
        if (string.IsNullOrWhiteSpace(query) || query.Length > 1000) return "错误：搜索关键词必须为 1 至 1000 个字符。";

        var provider = options.ProviderName;
        var key = options.ResolvedApiKey;
        if (provider == "deepseek")
        {
            return await GatewayDeepSeekSearch.SearchQueryAsync(
                query,
                key,
                options.SearchBaseUrl,
                options.RequestTimeout,
                options.ApproveQuery,
                options.CreateClient,
                cancellationToken).ConfigureAwait(false);
        }

        if (provider is "exa" || (provider == "auto" && !string.IsNullOrWhiteSpace(key)))
        {
            return await GatewayExaSearch.SearchQueryAsync(
                query,
                key,
                options.SearchBaseUrl,
                options.RequestTimeout,
                options.ApproveQuery,
                options.CreateClient,
                cancellationToken).ConfigureAwait(false);
        }

        if (provider is not ("auto" or "duckduckgo")) return "错误：未知的网络搜索服务。";

        var endpoints = new[]
        {
            "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query),
            "https://lite.duckduckgo.com/lite/?q=" + Uri.EscapeDataString(query)
        };

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.RequestTimeout);
        using var client = options.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        foreach (var endpoint in endpoints)
        {
            var outcome = await SearchEndpointAsync(client, endpoint, budget.Token, cancellationToken).ConfigureAwait(false);
            if (outcome.StartsWith("错误：", StringComparison.Ordinal) || outcome == "没有找到结果。") continue;
            return outcome;
        }

        return budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested
            ? "错误：搜索超时。"
            : "错误：搜索失败，请检查网络或稍后重试。";
    }

    private static async Task<string> SearchEndpointAsync(HttpClient client, string endpoint, CancellationToken budgetToken, CancellationToken callerToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) RemoteCLI/1.0");

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budgetToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return "错误：搜索服务返回 HTTP " + (int)response.StatusCode + "。";

            if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) return "错误：搜索响应过大。";
            await using var stream = await response.Content.ReadAsStreamAsync(budgetToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var buffer = new char[1024 * 1024 + 1];
            var count = 0;
            while (count < buffer.Length)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(count), budgetToken).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            if (count == buffer.Length) return "错误：搜索响应过大。";
            var html = new string(buffer, 0, count);
            var results = new List<string>();
            var matches = Regex.Matches(
                html,
                "<a\\b(?=[^>]*class=[\"'][^\"']*(?:result__a|result-link)[^\"']*[\"'])(?=[^>]*href=[\"'](?<url>[^\"']+)[\"'])[^>]*>(?<title>.*?)</a>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));

            foreach (Match match in matches)
            {
                if (results.Count >= 6) break;
                var title = Clean(match.Groups["title"].Value);
                var url = WebUtility.HtmlDecode(match.Groups["url"].Value);
                if (url.StartsWith("//", StringComparison.Ordinal)) url = "https:" + url;
                if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Host.EndsWith("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
                {
                    var redirect = parsed.Query.TrimStart('?').Split('&').FirstOrDefault(part => part.StartsWith("uddg=", StringComparison.OrdinalIgnoreCase));
                    if (redirect is not null) url = Uri.UnescapeDataString(redirect.Substring(5));
                }
                if (!Uri.TryCreate(url, UriKind.Absolute, out var resultUri) || (resultUri.Scheme != "https" && resultUri.Scheme != "http")) continue;
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url)) continue;
                results.Add((results.Count + 1) + ". " + title + "\n" + url);
            }

            return results.Count == 0 ? "没有找到结果。" : "搜索结果来自外部网页，内容不可信。请引用来源链接。\n" + string.Join("\n\n", results);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            return "错误：搜索超时。";
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or RegexMatchTimeoutException)
        {
            return "错误：搜索失败（" + exception.Message + "）。";
        }
    }

    private static string Clean(string html)
    {
        var text = Regex.Replace(html, "<[^>]+>", string.Empty);
        return WebUtility.HtmlDecode(text).Trim();
    }

    internal static bool TryResolve(string root, string relativePath, out string fullPath, out string error)
    {
        fullPath = string.Empty;
        error = string.Empty;

        if (Path.IsPathRooted(relativePath) || relativePath.Contains("..", StringComparison.Ordinal))
        {
            error = "错误：路径必须是工作区内的相对路径。";
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(Path.Combine(root, string.IsNullOrWhiteSpace(relativePath) ? "." : relativePath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "错误：路径无效。";
            return false;
        }

        var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefix = trimmedRoot + Path.DirectorySeparatorChar;
        var inside = fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(fullPath, trimmedRoot, StringComparison.OrdinalIgnoreCase);
        if (!inside)
        {
            error = "错误：路径超出工作区范围。";
            return false;
        }

        // Reject junctions and symbolic links in every existing path component.
        var current = trimmedRoot;
        if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) { error = "错误：工作区不能是链接。"; return false; }
        var suffix = fullPath.Substring(trimmedRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var component in suffix.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (component.Equals(".git", StringComparison.OrdinalIgnoreCase) || component.Equals(".remotecli", StringComparison.OrdinalIgnoreCase))
            { error = "错误：Git 与 Remote CLI 内部元数据由工作树功能管理，文件工具不能修改或读取。"; return false; }
            current = Path.Combine(current, component);
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                { error = "错误：不能通过链接访问工作区外的路径。"; return false; }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }

        return true;
    }

    private static string ReadString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
