using System.Text.Json;

namespace RemoteCli.Local;

/// <summary>
/// 读取桌面端 ui-state.json 中用户设置的远程模型系统提示；文件缺失或损坏时返回空，按 mtime 缓存。
/// </summary>
internal sealed class RemotePromptStore
{
    internal const int MaximumPromptCharacters = 8000;

    private readonly string path;
    private readonly object gate = new();
    private bool loaded;
    private DateTime cachedWriteTimeUtc;
    private long cachedLength = -1;
    private string? cachedValue;

    internal RemotePromptStore(string? path = null)
    {
        this.path = string.IsNullOrWhiteSpace(path) ? DefaultPath : path;
    }

    internal static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteCLI", "ui-state.json");

    internal string? Read()
    {
        lock (gate)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    loaded = true;
                    cachedValue = null;
                    cachedWriteTimeUtc = default;
                    cachedLength = -1;
                    return null;
                }

                if (loaded && info.LastWriteTimeUtc == cachedWriteTimeUtc && info.Length == cachedLength) return cachedValue;
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var bytes = new MemoryStream();
                input.CopyTo(bytes);
                var value = Parse(bytes.ToArray());
                cachedWriteTimeUtc = info.LastWriteTimeUtc;
                cachedLength = info.Length;
                loaded = true;
                cachedValue = value;
                return cachedValue;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                                              NotSupportedException or ArgumentException or System.Security.SecurityException)
            {
                return cachedValue;
            }
        }
    }

    internal static string? Parse(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.Object ||
                !state.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object ||
                !settings.TryGetProperty("remoteSystemPrompt", out var prompt) || prompt.ValueKind != JsonValueKind.String)
                return null;
            var text = prompt.GetString()?.Trim();
            if (string.IsNullOrEmpty(text)) return null;
            return text.Length <= MaximumPromptCharacters ? text : text[..MaximumPromptCharacters];
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
