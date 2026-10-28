namespace RemoteCli.Local;

public sealed record CodexCliExecutableSearchResult(string? ExecutablePath, string Source)
{
    public bool Found => !string.IsNullOrWhiteSpace(ExecutablePath);
}

/// <summary>Locates the Windows Codex CLI executable without relying on a stale desktop PATH.</summary>
public static class CodexCliExecutableLocator
{
    public static CodexCliExecutableSearchResult Find(string? configuredPath = null) => FindFrom(
        configuredPath,
        Environment.GetEnvironmentVariable("CODEX_HOME"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        File.Exists,
        path => File.Exists(path) ? File.ReadAllText(path) : null);

    public static string? ValidateSelectedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = NormalizePath(path);
        if (normalized is null || !IsCodexExecutable(normalized))
        {
            return null;
        }

        try
        {
            return File.Exists(normalized) ? Path.GetFullPath(normalized) : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>Deterministic search entry point for tests and callers with an explicit environment snapshot.</summary>
    public static CodexCliExecutableSearchResult FindFrom(
        string? configuredPath,
        string? codexHomeDirectory,
        string? userProfileDirectory,
        string? localAppDataDirectory,
        Func<string, bool>? fileExists = null,
        Func<string, string?>? readAllText = null)
    {
        fileExists ??= File.Exists;
        readAllText ??= path => File.Exists(path) ? File.ReadAllText(path) : null;

        var candidates = new List<(string? Path, string Source)>
        {
            (configuredPath, "Remote CLI 手动设置")
        };

        foreach (var configPath in GetCodexConfigPaths(codexHomeDirectory, userProfileDirectory))
        {
            candidates.Add((ReadConfiguredCliPath(configPath, readAllText), "Codex 桌面版配置"));
        }

        candidates.Add((
            Combine(localAppDataDirectory, "Programs", "OpenAI", "Codex", "bin", "codex.exe"),
            "Windows 官方默认安装目录"));

        foreach (var candidate in candidates)
        {
            var normalized = NormalizeExecutablePath(candidate.Path, fileExists);
            if (normalized is not null)
            {
                return new(normalized, candidate.Source);
            }
        }

        return new(null, "未找到");
    }

    private static IEnumerable<string> GetCodexConfigPaths(string? codexHomeDirectory, string? userProfileDirectory)
    {
        var paths = new List<string>();
        AddPath(paths, Combine(codexHomeDirectory, "config.toml"));
        AddPath(paths, Combine(userProfileDirectory, ".codex", "config.toml"));
        return paths;
    }

    private static void AddPath(ICollection<string> paths, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) &&
            !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            paths.Add(path);
        }
    }

    private static string? ReadConfiguredCliPath(string configPath, Func<string, string?> readAllText)
    {
        string? contents;
        try
        {
            contents = readAllText(configPath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or
                                           System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }

        if (string.IsNullOrEmpty(contents))
        {
            return null;
        }

        var section = string.Empty;
        foreach (var rawLine in contents.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('['))
            {
                var sectionEnd = line.IndexOf(']');
                section = sectionEnd > 0 ? line[1..sectionEnd].Trim() : string.Empty;
                continue;
            }

            if (section.Length != 0 && !string.Equals(section, "mcp_servers.node_repl.env", StringComparison.Ordinal))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator < 0 || !string.Equals(line[..separator].Trim(), "CODEX_CLI_PATH", StringComparison.Ordinal))
            {
                continue;
            }

            return ParseTomlString(line[(separator + 1)..]);
        }

        return null;
    }

    private static string? ParseTomlString(string value)
    {
        value = value.Trim();
        if (value.Length < 2)
        {
            return null;
        }

        if (value[0] == '\'')
        {
            var closingQuote = value.IndexOf('\'', 1);
            if (closingQuote < 0 || !IsTomlTrailingComment(value[(closingQuote + 1)..]))
            {
                return null;
            }

            return value[1..closingQuote];
        }

        if (value[0] != '"')
        {
            return null;
        }

        var escaped = false;
        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (character == '\\')
            {
                escaped = true;
                continue;
            }

            if (character != '"')
            {
                continue;
            }

            if (!IsTomlTrailingComment(value[(index + 1)..]))
            {
                return null;
            }

            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<string>(value[..(index + 1)]);
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        return null;
    }

    private static bool IsTomlTrailingComment(string value)
    {
        var suffix = value.Trim();
        return suffix.Length == 0 || suffix.StartsWith('#');
    }

    private static string? NormalizeExecutablePath(string? path, Func<string, bool> fileExists)
    {
        var normalized = NormalizePath(path);
        if (normalized is null || !IsCodexExecutable(normalized))
        {
            return null;
        }

        try
        {
            return fileExists(normalized) ? Path.GetFullPath(normalized) : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return path.Trim().Trim('"');
    }

    private static bool IsCodexExecutable(string path) =>
        string.Equals(Path.GetFileName(path), "codex.exe", StringComparison.OrdinalIgnoreCase);

    private static string? Combine(string? directory, params string[] segments)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        try
        {
            return Path.Combine(new[] { directory.Trim().Trim('"') }.Concat(segments).ToArray());
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }
}
