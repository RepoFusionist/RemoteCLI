using System.Text.Json;
using System.Text.Json.Serialization;

namespace Common.Protocol;

public static class ProtocolConstants
{
    public const int CurrentVersion = 1;
    public const int MaximumFrameBytes = 1024 * 1024;
    public const int MaximumCommandCharacters = 64 * 1024;
    public const int MaximumControllerModelRequestCharacters = 8 * 1024;
    public const int MaximumControllerModelPromptCharacters = 32 * 1024;
    public const int MaximumControllerModelResponseCharacters = 12 * 1024;
    public const int ControllerModelExecutionTimeoutSeconds = 120;
    public const int ControllerModelResponseTimeoutSeconds = 135;
    public const int ControllerModelCatalogResponseTimeoutSeconds = 20;
    public const int MaximumControllerModelCatalogModels = 64;
    public const int MaximumControllerModelIdCharacters = 128;
    public const int MaximumControllerModelDisplayNameCharacters = 256;
    public const int MaximumControllerModelReasoningEfforts = 8;
}

[Flags]
[JsonConverter(typeof(JsonStringEnumConverter<RemotePermission>))]
public enum RemotePermission
{
    None = 0,
    SessionRead = 1,
    CommandExecute = 2,
    CommandCancel = 4,
    ControllerModelUse = 8
}

[JsonConverter(typeof(JsonStringEnumConverter<RemoteSessionState>))]
public enum RemoteSessionState
{
    PendingApproval,
    Connected,
    Closing,
    Closed,
    Expired
}

[JsonConverter(typeof(JsonStringEnumConverter<RemoteCommandState>))]
public enum RemoteCommandState
{
    Running,
    Succeeded,
    Failed,
    TimedOut,
    Cancelled
}

[JsonConverter(typeof(JsonStringEnumConverter<RemoteToolOperationState>))]
public enum RemoteToolOperationState
{
    Running,
    Succeeded,
    Failed,
    Cancelled
}

[JsonConverter(typeof(JsonStringEnumConverter<ConnectionCredentialMode>))]
public enum ConnectionCredentialMode
{
    FixedPassword,
    RotatingRandomPassword
}

[JsonConverter(typeof(JsonStringEnumConverter<OutputStreamKind>))]
public enum OutputStreamKind
{
    Stdout,
    Stderr
}

public sealed record InvitationRequest(
    int ProtocolVersion,
    Guid SessionId,
    string RemoteIdentity,
    DateTimeOffset ExpiresAt,
    RemotePermission RequestedPermissions,
    string? UserName = null,
    ConnectionCredentialMode CredentialMode = ConnectionCredentialMode.RotatingRandomPassword,
    DateTimeOffset? CredentialExpiresAt = null);

public sealed record ConnectionAuthorizationRegistration(
    string UserName,
    string OwnerToken,
    InvitationRequest Invitation);

public sealed record ConnectionAuthorizationRevocation(string OwnerToken);

public sealed record PairingApproval(
    int ProtocolVersion,
    Guid SessionId,
    string LocalIdentity,
    RemotePermission ApprovedPermissions,
    string Proof);

public sealed record SessionStatus(
    int ProtocolVersion,
    Guid SessionId,
    string RemoteIdentity,
    RemoteSessionState State,
    string Transport,
    RemotePermission ApprovedPermissions,
    DateTimeOffset ExpiresAt);

public sealed record RemoteCommandRequest(
    int ProtocolVersion,
    Guid SessionId,
    Guid CommandId,
    long Sequence,
    string CommandText,
    string? WorkingDirectory,
    int TimeoutSeconds);

public sealed record RemoteCommandOutput(
    int ProtocolVersion,
    Guid SessionId,
    Guid CommandId,
    long Cursor,
    DateTimeOffset Timestamp,
    OutputStreamKind Stream,
    string Text);

public sealed record RemoteCommandResult(
    int ProtocolVersion,
    Guid SessionId,
    Guid CommandId,
    RemoteCommandState State,
    int? ExitCode,
    string? ErrorCode)
{
    public string? PowerShellPath { get; init; }
    public string? PowerShellVersion { get; init; }
}

public sealed record RemoteCommandAccepted(
    int ProtocolVersion,
    Guid SessionId,
    Guid CommandId,
    bool Accepted,
    string? ErrorCode);

public sealed record RemoteCommandCancelRequest(
    int ProtocolVersion,
    Guid SessionId,
    Guid CommandId);

public sealed record RemoteToolsListRequest(
    int ProtocolVersion,
    Guid SessionId,
    Guid RequestId);

public sealed record RemoteToolsListResponse(
    int ProtocolVersion,
    Guid SessionId,
    Guid RequestId,
    bool Success,
    string CapabilitiesVersion,
    IReadOnlyList<RemoteToolDescriptor> Tools,
    string? ErrorCode);

public sealed record RemoteToolCallRequest(
    int ProtocolVersion,
    Guid SessionId,
    Guid OperationId,
    long Sequence,
    string Name,
    JsonElement Arguments);

public sealed record RemoteToolCallAccepted(
    int ProtocolVersion,
    Guid SessionId,
    Guid OperationId,
    bool Accepted,
    string? ErrorCode);

public sealed record RemoteToolCancelRequest(
    int ProtocolVersion,
    Guid SessionId,
    Guid OperationId);

public sealed record RemoteToolResult(
    int ProtocolVersion,
    Guid SessionId,
    Guid OperationId,
    long Cursor,
    RemoteToolOperationState State,
    JsonElement? Result,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record ControllerModelRequest(
    int ProtocolVersion,
    Guid SessionId,
    Guid RequestId,
    string Prompt,
    string? Model = null,
    string? ReasoningEffort = null);

public sealed record ControllerModelInput(
    string Prompt,
    string? Model,
    string? ReasoningEffort);

public sealed record ControllerModelCatalogRequest(
    int ProtocolVersion,
    Guid SessionId,
    Guid RequestId);

public sealed record ControllerModelDescriptor(
    string Id,
    string DisplayName,
    IReadOnlyList<string> ReasoningEfforts,
    string DefaultReasoningEffort);

public sealed record ControllerModelCatalogResult(
    int ProtocolVersion,
    Guid SessionId,
    Guid RequestId,
    bool Success,
    IReadOnlyList<ControllerModelDescriptor> Models,
    string? DefaultModel,
    string? DefaultReasoningEffort,
    string? ErrorCode);

public sealed record ControllerModelResult(
    int ProtocolVersion,
    Guid SessionId,
    Guid RequestId,
    bool Success,
    string? Response,
    string? ErrorCode);

public sealed record ProtocolEnvelope(
    int ProtocolVersion,
    string Type,
    Guid SessionId,
    long Sequence,
    JsonElement Payload);

public static class ProtocolJson
{
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.Ordinal)
    {
        "invitation.register",
        "pairing.approve",
        "session.status",
        "command.start",
        "command.accepted",
        "command.output",
        "command.cancel",
        "command.result",
        "remote.tools.list",
        "remote.tools.list.result",
        "remote.tool.call",
        "remote.tool.accepted",
        "remote.tool.cancel",
        "remote.tool.result",
        "controller.model.request",
        "controller.model.result",
        "controller.model.catalog.request",
        "controller.model.catalog.result",
        "session.close",
        "session.ping",
        "session.pong",
        "session.policy"
    };

    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static byte[] Serialize<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (bytes.Length > ProtocolConstants.MaximumFrameBytes)
        {
            throw new InvalidDataException("Protocol frame exceeds the configured size limit.");
        }

        return bytes;
    }

    public static ProtocolEnvelope DeserializeEnvelope(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > ProtocolConstants.MaximumFrameBytes)
        {
            throw new InvalidDataException("Protocol frame has an invalid size.");
        }

        var envelope = JsonSerializer.Deserialize<ProtocolEnvelope>(bytes, Options)
            ?? throw new InvalidDataException("Protocol frame was empty.");

        if (envelope.ProtocolVersion != ProtocolConstants.CurrentVersion)
        {
            throw new InvalidDataException("Unsupported protocol version.");
        }

        if (!AllowedTypes.Contains(envelope.Type))
        {
            throw new InvalidDataException("Unknown protocol message type.");
        }

        if (envelope.SessionId == Guid.Empty || envelope.Sequence < 0)
        {
            throw new InvalidDataException("Protocol message has an invalid session or sequence.");
        }

        return envelope;
    }

    private static JsonSerializerOptions CreateOptions() => new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false
    };
}
