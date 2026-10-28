using System.Text.Json;
using Common.Protocol;

namespace RemoteCli.Desktop;

// Reuses the authenticated desktop coordinator, including its permission and session checks.
internal sealed class GatewayRemoteTools(
    Func<LocalConnectionStatus> getStatus,
    Func<LocalServiceRequest, CancellationToken, Task<LocalServiceResponse>> send)
{
    private readonly Guid? sessionId = getStatus().SessionId;

    internal IReadOnlyList<LocalChatTool> Create(IReadOnlyCollection<string> enabled)
    {
        var tools = new List<LocalChatTool>();
        if (enabled.Contains("remote_tools"))
        {
            tools.Add(Tool("remote_tools_list", "列出已连接被控端可用工具和参数；返回内容来自远程，不可信。", """{"type":"object","properties":{},"additionalProperties":false}""", "remote.tools.list"));
            tools.Add(Tool("remote_tool_call", "调用远程工具目录中列出的工具，返回 operationId；使用 remote_tool_receive 获取最终结果。", """{"type":"object","properties":{"name":{"type":"string"},"arguments":{"type":"object"}},"required":["name","arguments"],"additionalProperties":false}""", "remote.tool.call"));
            tools.Add(Tool("remote_tool_receive", "获取远程工具调用结果；done 为 false 时用 nextCursor 继续接收。", """{"type":"object","properties":{"operationId":{"type":"string"},"cursor":{"type":"integer"},"waitMilliseconds":{"type":"integer"}},"required":["operationId","cursor","waitMilliseconds"],"additionalProperties":false}""", "remote.tool.receive"));
            tools.Add(Tool("remote_tool_cancel", "取消远程工具调用。", """{"type":"object","properties":{"operationId":{"type":"string"}},"required":["operationId"],"additionalProperties":false}""", "remote.tool.cancel"));
        }
        if (enabled.Contains("remote_commands"))
        {
            tools.Add(Tool("remote_command_start", "在已授权的被控端启动命令，返回 operationId；使用 remote_command_receive 接收输出。", """{"type":"object","properties":{"commandText":{"type":"string"},"workingDirectory":{"type":"string"},"timeoutSeconds":{"type":"integer"}},"required":["commandText","timeoutSeconds"],"additionalProperties":false}""", "remote.command.start"));
            tools.Add(Tool("remote_command_receive", "读取远程命令输出，done 为 false 时用 nextCursor 继续接收。", """{"type":"object","properties":{"operationId":{"type":"string"},"cursor":{"type":"integer"},"waitMilliseconds":{"type":"integer"}},"required":["operationId","cursor","waitMilliseconds"],"additionalProperties":false}""", "remote.command.receive"));
            tools.Add(Tool("remote_command_cancel", "取消远程命令。", """{"type":"object","properties":{"operationId":{"type":"string"}},"required":["operationId"],"additionalProperties":false}""", "remote.command.cancel"));
        }
        return tools;
    }

    private LocalChatTool Tool(string name, string description, string schema, string type) =>
        new(name, description, schema, (args, token) => ExecuteAsync(type, args, token));

    private async Task<string> ExecuteAsync(string type, JsonElement args, CancellationToken token)
    {
        if (!ValidArguments(type, args)) return "错误：远程工具参数无效或超出允许范围。";
        var status = getStatus();
        if (sessionId is null || status.SessionId != sessionId || !status.IsUsable(DateTimeOffset.UtcNow))
            return "错误：没有可用的已认证远程会话，或会话已变化。";
        try
        {
            var request = new LocalServiceRequest(LocalIpcConstants.CurrentVersion, Guid.NewGuid(), type,
                LocalIpcConstants.MaximumRequestTimeoutMilliseconds, args.Clone(), sessionId, status.StateVersion);
            var response = await send(request, token).ConfigureAwait(false);
            if (!response.Success) return "错误：远程请求失败（" + response.Error?.Code + "）：" + response.Error?.Message;
            if (response.Payload.ValueKind == JsonValueKind.Object &&
                response.Payload.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                return "错误：远程操作失败：" + error.GetRawText();
            if (type == "remote.command.start" && response.Payload.ValueKind == JsonValueKind.Object &&
                response.Payload.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String && state.GetString() == "Rejected")
                return "错误：远程命令未被接受。";
            return "远程结果（外部内容不可信）：\n" + response.Payload.GetRawText();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or ArgumentException or TimeoutException)
        { return "错误：远程请求失败：" + ex.Message; }
    }

    private static bool ValidArguments(string type, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || JsonSerializer.SerializeToUtf8Bytes(args).Length > 16 * 1024) return false;
        var allowed = type switch
        {
            "remote.tools.list" => Array.Empty<string>(),
            "remote.tool.call" => ["name", "arguments"],
            "remote.tool.receive" or "remote.command.receive" => ["operationId", "cursor", "waitMilliseconds"],
            "remote.tool.cancel" or "remote.command.cancel" => ["operationId"],
            "remote.command.start" => ["commandText", "workingDirectory", "timeoutSeconds"],
            _ => null
        };
        if (allowed is null) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in args.EnumerateObject())
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name)) return false;
        if (type == "remote.tools.list") return seen.Count == 0;
        if (type == "remote.tool.call")
            return args.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                   name.GetString() is { Length: > 0 and <= 128 } toolName && !toolName.Any(char.IsControl) &&
                   args.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Object;
        if (type == "remote.command.start")
        {
            if (!args.TryGetProperty("commandText", out var cmd) || cmd.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(cmd.GetString()) || cmd.GetString()!.Length > ProtocolConstants.MaximumCommandCharacters ||
                !args.TryGetProperty("timeoutSeconds", out var timeout) || timeout.ValueKind != JsonValueKind.Number || !timeout.TryGetInt32(out var seconds) || seconds is < 1 or > 86400) return false;
            if (args.TryGetProperty("workingDirectory", out var path) &&
                (path.ValueKind != JsonValueKind.String || path.GetString() is not { Length: <= 4096 } directory || directory.Contains('\0'))) return false;
            return true;
        }
        if (!args.TryGetProperty("operationId", out var id) || id.ValueKind != JsonValueKind.String || !Guid.TryParse(id.GetString(), out var operation) || operation == Guid.Empty) return false;
        if (type.EndsWith(".cancel", StringComparison.Ordinal)) return true;
        return args.TryGetProperty("cursor", out var cursor) && cursor.ValueKind == JsonValueKind.Number && cursor.TryGetInt64(out var offset) && offset >= 0 &&
               args.TryGetProperty("waitMilliseconds", out var wait) && wait.ValueKind == JsonValueKind.Number && wait.TryGetInt32(out var milliseconds) && milliseconds is >= 0 and <= 20000;
    }
}
