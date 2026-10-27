using System.Text.Json.Serialization;

namespace Common.Protocol;

public sealed record RemoteSessionPolicy(
    long Version,
    RemotePermission Permissions,
    string? WorkspaceRoot,
    bool WorkspaceRestricted,
    [property: JsonIgnore] bool AutoApproveTools = false);
