using System.Text.Json;
using System.Text.Json.Serialization;

namespace Common.Protocol;

public static class LocalIpcConstants
{
    public const int CurrentVersion = 1;
    public const int MaximumFrameBytes = 256 * 1024;
    public const int DefaultRequestTimeoutMilliseconds = 2_000;
    public const int MaximumRequestTimeoutMilliseconds = 30_000;
}

[JsonConverter(typeof(JsonStringEnumConverter<LocalConnectionState>))]
public enum LocalConnectionState
{
    ServiceUnavailable,
    Disconnected,
    Connecting,
    AwaitingAuthentication,
    Authenticating,
    Connected,
    Faulted,
    Closed,
    Expired
}

public sealed record LocalConnectionError(
    string Stage,
    string Code,
    string Message,
    bool Retryable,
    [property: JsonPropertyName("retryAfterMs")] int RetryAfterMilliseconds,
    Guid? RequestId = null);

public sealed record LocalConnectionStatus(
    int ProtocolVersion,
    string ServiceState,
    LocalConnectionState ConnectionState,
    bool Authenticated,
    Guid? SessionId,
    string? Transport,
    RemotePermission GrantedPermissions,
    string? RemoteCapabilitiesVersion,
    long StateVersion,
    Guid? AuthRequestId,
    LocalConnectionError? LastError,
    [property: JsonPropertyName("retryAfterMs")] int RetryAfterMilliseconds,
    DateTimeOffset ServerTime,
    DateTimeOffset? ExpiresAt,
    string? RemoteIdentity)
{
    public static LocalConnectionStatus Unavailable(string message, DateTimeOffset? now = null) => new(
        LocalIpcConstants.CurrentVersion,
        "Unavailable",
        LocalConnectionState.ServiceUnavailable,
        false,
        null,
        null,
        RemotePermission.None,
        null,
        0,
        null,
        new LocalConnectionError("ipc", "service_unavailable", message, true, 1_000),
        1_000,
        now ?? DateTimeOffset.UtcNow,
        null,
        null);

    public bool IsUsable(DateTimeOffset now) =>
        ServiceState == "Running" &&
        ConnectionState == LocalConnectionState.Connected &&
        Authenticated &&
        SessionId is { } sessionId && sessionId != Guid.Empty &&
        ExpiresAt is { } expiresAt && expiresAt > now &&
        !string.IsNullOrWhiteSpace(RemoteIdentity);

    public string? RemoteWorkspace { get; init; }
    public bool WorkspaceRestricted { get; init; } = true;
    public long RemotePolicyVersion { get; init; }
    public int ServiceProcessId { get; init; } = Environment.ProcessId;
    public string? ServiceVersion { get; init; } = System.Reflection.Assembly.GetEntryAssembly()?
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
}

public sealed record LocalServiceRequest(
    int ProtocolVersion,
    Guid RequestId,
    string Type,
    int TimeoutMilliseconds,
    JsonElement Payload,
    Guid? ExpectedSessionId,
    long? ExpectedStateVersion);

public sealed record LocalServiceResponse(
    int ProtocolVersion,
    Guid RequestId,
    string Type,
    bool Success,
    long StateVersion,
    JsonElement Payload,
    LocalConnectionError? Error);

public sealed record AuthenticationRequestInput(string? UserName);

public sealed record McpPresenceInput(Guid ClientId);

public sealed record ConnectionWaitInput(long AfterStateVersion, int WaitMilliseconds);

public sealed record AuthenticationRequestResult(
    Guid AuthRequestId,
    LocalConnectionState ConnectionState,
    bool Started,
    bool Retryable,
    [property: JsonPropertyName("retryAfterMs")] int RetryAfterMilliseconds,
    string Message);

public sealed record LocalRemoteCommandStartInput(string CommandText, string? WorkingDirectory, int TimeoutSeconds);

public sealed record LocalRemoteCommandStartResult(Guid OperationId, Guid SessionId, string State, int TimeoutSeconds);

public sealed record LocalRemoteCommandReceiveInput(Guid OperationId, long Cursor, int WaitMilliseconds);

public sealed record LocalRemoteCommandReceiveResult(
    IReadOnlyList<RemoteCommandOutput> Outputs,
    long NextCursor,
    bool Done,
    RemoteCommandState State,
    int? ExitCode,
    bool Truncated,
    LocalConnectionError? Error);

public sealed record LocalRemoteCommandCancelInput(Guid OperationId);

public sealed record LocalRemoteCommandCancelResult(Guid OperationId, bool CancellationRequested);

public sealed record RemoteToolDescriptor(
    string Name,
    string Version,
    string Description,
    JsonElement InputSchema,
    JsonElement? OutputSchema);

public sealed record RemoteToolsListResult(string? CapabilitiesVersion, IReadOnlyList<RemoteToolDescriptor> Tools);

public sealed record LocalRemoteToolCallInput(string Name, JsonElement Arguments);

public sealed record LocalRemoteToolCancelInput(Guid OperationId);

public sealed record LocalRemoteToolCancelResult(Guid OperationId, bool CancellationRequested);

public sealed record LocalRemoteToolReceiveInput(Guid OperationId, long Cursor, int WaitMilliseconds);

public sealed record LocalRemoteToolReceiveResult(
    Guid OperationId,
    long NextCursor,
    bool Done,
    RemoteToolOperationState State,
    JsonElement? Result,
    LocalConnectionError? Error);

public sealed record LocalRemoteToolCallResult(Guid OperationId, string SessionId, RemoteToolOperationState State);

public static class RemoteToolSchemaValidator
{
    private const int MaximumSchemaBytes = 32 * 1024;
    private const int MaximumArgumentBytes = 16 * 1024;
    private const int MaximumProperties = 64;

    public static bool ValidateArguments(JsonElement schema, JsonElement arguments, out string error)
    {
        error = "invalid_tool_arguments";
        if (arguments.ValueKind != JsonValueKind.Object || !ValidateSchema(schema, out error))
        {
            return false;
        }

        if (JsonSerializer.SerializeToUtf8Bytes(arguments, ProtocolJson.Options).Length > MaximumArgumentBytes)
        {
            error = "tool_payload_too_large";
            return false;
        }

        schema.TryGetProperty("properties", out var properties);
        var argumentCount = 0;
        var argumentNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            argumentCount++;
            if (argumentCount > MaximumProperties || property.Name.Length > 128 || property.Name.Any(char.IsControl))
            {
                return false;
            }

            if (!argumentNames.Add(property.Name))
            {
                return false;
            }

            if (!properties.TryGetProperty(property.Name, out var propertySchema))
            {
                return false;
            }

            if (!MatchesPropertySchema(propertySchema, property.Value))
            {
                return false;
            }
        }

        if (schema.TryGetProperty("required", out var required))
        {
            if (required.ValueKind != JsonValueKind.Array || required.GetArrayLength() > MaximumProperties)
            {
                error = "unsupported_tool_schema";
                return false;
            }

            foreach (var item in required.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || !arguments.TryGetProperty(item.GetString()!, out _))
                {
                    return false;
                }
            }
        }

        error = string.Empty;
        return true;
    }

    public static bool ValidateSchema(JsonElement schema, out string error)
    {
        error = "unsupported_tool_schema";
        if (schema.ValueKind != JsonValueKind.Object ||
            JsonSerializer.SerializeToUtf8Bytes(schema, ProtocolJson.Options).Length > MaximumSchemaBytes ||
            !schema.TryGetProperty("type", out var rootType) || rootType.ValueKind != JsonValueKind.String || rootType.GetString() != "object" ||
            !schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("additionalProperties", out var additional) || additional.ValueKind != JsonValueKind.False ||
            schema.EnumerateObject().Any(property => property.Name is not ("type" or "properties" or "required" or "additionalProperties")))
        {
            return false;
        }

        var propertyNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in properties.EnumerateObject())
        {
            if (propertyNames.Count >= MaximumProperties || property.Name.Length is 0 or > 128 || property.Name.Any(char.IsControl) ||
                !propertyNames.Add(property.Name) || property.Value.ValueKind != JsonValueKind.Object ||
                !property.Value.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                type.GetString() is not ("string" or "integer" or "number" or "boolean") ||
                property.Value.EnumerateObject().Any(keyword => keyword.Name is not ("type" or "maxLength")) ||
                (property.Value.TryGetProperty("maxLength", out var maxLength) &&
                 (type.GetString() != "string" || maxLength.ValueKind != JsonValueKind.Number || !maxLength.TryGetInt32(out var maximum) || maximum is < 0 or > 16_384)))
            {
                return false;
            }
        }

        if (!schema.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array || required.GetArrayLength() > MaximumProperties)
        {
            return false;
        }

        var requiredNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in required.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !requiredNames.Add(item.GetString()!) || !propertyNames.Contains(item.GetString()!))
            {
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool MatchesPropertySchema(JsonElement schema, JsonElement value)
    {
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        switch (typeElement.GetString())
        {
            case "string":
                if (value.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                var stringValue = value.GetString() ?? string.Empty;
                return !schema.TryGetProperty("maxLength", out var maxLength) ||
                    (maxLength.ValueKind == JsonValueKind.Number && maxLength.TryGetInt32(out var max) && stringValue.Length <= max);
            case "integer":
                return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _);
            case "number":
                return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number);
            case "boolean":
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
            default:
                return false;
        }
    }
}

public static class LocalIpcProtocol
{
    private static readonly HashSet<string> AllowedRequestTypes = new(StringComparer.Ordinal)
    {
        "connection.status",
        "connection.wait",
        "connection.auth",
        "connection.close",
        "connection.mcp.heartbeat",
        "connection.mcp.disconnect",
        "remote.command.start",
        "remote.command.receive",
        "remote.command.cancel",
        "remote.tools.list",
        "remote.tool.call",
        "remote.tool.cancel",
        "remote.tool.receive"
    };

    public static byte[] Serialize<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, ProtocolJson.Options);
        if (bytes.Length is <= 0 or > LocalIpcConstants.MaximumFrameBytes)
        {
            throw new InvalidDataException("Local IPC message exceeds its configured size limit.");
        }

        return bytes;
    }

    public static LocalServiceRequest ValidateRequest(LocalServiceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProtocolVersion != LocalIpcConstants.CurrentVersion)
        {
            throw new InvalidDataException("Unsupported local IPC protocol version.");
        }

        if (request.RequestId == Guid.Empty || !AllowedRequestTypes.Contains(request.Type))
        {
            throw new InvalidDataException("Local IPC request ID or type is invalid.");
        }

        if (request.TimeoutMilliseconds is < 1 or > LocalIpcConstants.MaximumRequestTimeoutMilliseconds)
        {
            throw new InvalidDataException("Local IPC request timeout is invalid.");
        }

        if (request.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new InvalidDataException("Local IPC request payload is missing.");
        }

        if ((request.ExpectedSessionId is null) != (request.ExpectedStateVersion is null) ||
            request.ExpectedSessionId == Guid.Empty || request.ExpectedStateVersion < 0)
        {
            throw new InvalidDataException("Local IPC session preconditions are invalid.");
        }

        if (System.Text.Encoding.UTF8.GetByteCount(request.Payload.GetRawText()) > LocalIpcConstants.MaximumFrameBytes)
        {
            throw new InvalidDataException("Local IPC request payload exceeds its configured size limit.");
        }

        return request;
    }
}
