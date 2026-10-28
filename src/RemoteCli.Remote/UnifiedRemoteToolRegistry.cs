using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Common.Protocol;

namespace RemoteCli.Remote;

public interface IRemoteTool
{
    RemoteToolDescriptor Descriptor { get; }
    string? ApprovalKey { get; }
    string PermissionDescription { get; }
    Task<JsonElement> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken);
}

public enum ToolApprovalDecision
{
    Deny,
    AllowOnce,
    AllowForSession
}

public sealed record RemoteToolApprovalRequest(
    Guid RequestId,
    string Name,
    string Description,
    string PermissionDescription,
    JsonElement Arguments);

public sealed class RemoteToolException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class RemoteToolRegistry : IRemoteToolRegistry
{
    private const int MaximumResultBytes = 16 * 1024;
    private const int MaximumPendingCalls = 16;
    private readonly IReadOnlyDictionary<string, IRemoteTool> _tools;
    private readonly Func<RemoteToolApprovalRequest, CancellationToken, Task<ToolApprovalDecision>>? _approve;
    private readonly bool _autoApproveTools;
    private readonly ConcurrentDictionary<string, byte> _sessionApprovals = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _approvalLock = new(1, 1);
    private readonly SemaphoreSlim _executionSlots = new(2, 2);
    private int _pendingCalls;

    public RemoteToolRegistry(
        IEnumerable<IRemoteTool> tools,
        Func<RemoteToolApprovalRequest, CancellationToken, Task<ToolApprovalDecision>>? approve = null,
        bool autoApproveTools = false)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var materialized = tools.ToArray();
        if (materialized.Length is < 1 or > 64)
        {
            throw new ArgumentException("A remote tool registry must contain between 1 and 64 tools.", nameof(tools));
        }

        var registry = new Dictionary<string, IRemoteTool>(StringComparer.Ordinal);
        foreach (var tool in materialized)
        {
            var descriptor = tool.Descriptor;
            if (string.IsNullOrWhiteSpace(descriptor.Name) || descriptor.Name.Length > 128 ||
                descriptor.Name.Any(char.IsControl) || string.IsNullOrWhiteSpace(descriptor.Version) ||
                descriptor.Version.Length > 64 || descriptor.Description.Length > 2_048 ||
                !RemoteToolSchemaValidator.ValidateSchema(descriptor.InputSchema, out _) ||
                (descriptor.OutputSchema is { } output && !RemoteToolSchemaValidator.ValidateSchema(output, out _)) ||
                !registry.TryAdd(descriptor.Name, tool))
            {
                throw new ArgumentException("The remote tool catalog contains an invalid or duplicate descriptor.", nameof(tools));
            }
        }

        _tools = registry;
        _approve = approve;
        _autoApproveTools = autoApproveTools;
        var catalog = string.Join("\n", registry.Values
            .OrderBy(tool => tool.Descriptor.Name, StringComparer.Ordinal)
            .Select(tool => tool.Descriptor.Name + "@" + tool.Descriptor.Version));
        CapabilitiesVersion = "remote-tools-v2-" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(catalog))).ToLowerInvariant()[..16];
    }

    public static RemoteToolRegistry Default { get; } = new([new SystemInfoRemoteTool()]);

    public static RemoteToolRegistry CreateForDesktop(
        string? workspaceRoot,
        IEnumerable<IRemoteTool>? installedTools,
        Func<RemoteToolApprovalRequest, CancellationToken, Task<ToolApprovalDecision>> approve,
        bool workspaceRestricted = true,
        bool autoApproveTools = false)
    {
        var tools = new List<IRemoteTool> { new SystemInfoRemoteTool() };
        if (!string.IsNullOrWhiteSpace(workspaceRoot) || !workspaceRestricted)
        {
            var root = workspaceRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            tools.AddRange(WorkspaceFileTools.Create(root, workspaceRestricted));
            tools.AddRange(AgentFileTools.Create(new RemoteFileScope(root, workspaceRestricted)));
        }

        if (installedTools is not null)
        {
            tools.AddRange(installedTools);
        }

        return new RemoteToolRegistry(tools, approve, autoApproveTools);
    }

    public string CapabilitiesVersion { get; }

    public IReadOnlyList<RemoteToolDescriptor> ListTools() =>
        _tools.Values.Select(tool => tool.Descriptor).OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray();

    public bool TryValidateCall(string name, JsonElement arguments, out string error)
    {
        if (!_tools.TryGetValue(name, out var tool))
        {
            error = "unknown_remote_tool";
            return false;
        }

        return RemoteToolSchemaValidator.ValidateArguments(tool.Descriptor.InputSchema, arguments, out error);
    }

    public async Task<JsonElement> ExecuteAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!TryValidateCall(name, arguments, out var error) || !_tools.TryGetValue(name, out var tool))
        {
            throw new RemoteToolException(error, "The remote tool call did not match its registered schema.");
        }

        if (Interlocked.Increment(ref _pendingCalls) > MaximumPendingCalls)
        {
            Interlocked.Decrement(ref _pendingCalls);
            throw new RemoteToolException("remote_tool_queue_full", "The remote tool queue is full. Retry after current operations finish.");
        }

        try
        {
            await _executionSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_autoApproveTools && tool.ApprovalKey is { Length: > 0 } approvalKey && !_sessionApprovals.ContainsKey(approvalKey))
                {
                    if (_approve is null)
                    {
                        throw new RemoteToolException("tool_approval_unavailable", "The remote desktop approval UI is unavailable.");
                    }

                    await _approvalLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (!_sessionApprovals.ContainsKey(approvalKey))
                        {
                            var decision = await _approve(new RemoteToolApprovalRequest(
                                Guid.NewGuid(),
                                tool.Descriptor.Name,
                                tool.Descriptor.Description,
                                tool.PermissionDescription,
                                arguments.Clone()), cancellationToken).ConfigureAwait(false);
                            cancellationToken.ThrowIfCancellationRequested();
                            if (decision == ToolApprovalDecision.Deny)
                            {
                                throw new RemoteToolException("tool_approval_denied", "The user on the remote computer denied this tool call.");
                            }

                            if (decision == ToolApprovalDecision.AllowForSession)
                            {
                                _sessionApprovals.TryAdd(approvalKey, 0);
                            }
                        }
                    }
                    finally
                    {
                        _approvalLock.Release();
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                var result = await tool.ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false);
                if (JsonSerializer.SerializeToUtf8Bytes(result, ProtocolJson.Options).Length > MaximumResultBytes)
                {
                    throw new RemoteToolException("remote_tool_result_too_large", "The remote tool result exceeded 16 KiB.");
                }

                if (tool.Descriptor.OutputSchema is { } outputSchema &&
                    !RemoteToolSchemaValidator.ValidateArguments(outputSchema, result, out _))
                {
                    throw new RemoteToolException("invalid_remote_tool_result", "The remote tool result did not match its registered output schema.");
                }

                return result;
            }
            finally
            {
                _executionSlots.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _pendingCalls);
        }
    }

    internal static JsonElement ParseSchema(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        return document.RootElement.Clone();
    }

    private sealed class SystemInfoRemoteTool : IRemoteTool
    {
        public RemoteToolDescriptor Descriptor { get; } = new(
            "remote.system_info",
            "1.0.0",
            "Returns a bounded snapshot of the operating system and .NET runtime of this remote Remote CLI process.",
            ParseSchema("""
            {"type":"object","properties":{},"required":[],"additionalProperties":false}
            """),
            ParseSchema("""
            {"type":"object","properties":{"machineName":{"type":"string","maxLength":256},"osDescription":{"type":"string","maxLength":512},"osArchitecture":{"type":"string","maxLength":32},"processArchitecture":{"type":"string","maxLength":32},"runtimeVersion":{"type":"string","maxLength":128}},"required":["machineName","osDescription","osArchitecture","processArchitecture","runtimeVersion"],"additionalProperties":false}
            """));

        public string? ApprovalKey => null;
        public string PermissionDescription => string.Empty;

        public Task<JsonElement> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                machineName = Limit(Environment.MachineName, 256),
                osDescription = Limit(RuntimeInformation.OSDescription, 512),
                osArchitecture = Limit(RuntimeInformation.OSArchitecture.ToString(), 32),
                processArchitecture = Limit(RuntimeInformation.ProcessArchitecture.ToString(), 32),
                runtimeVersion = Limit(RuntimeInformation.FrameworkDescription, 128)
            }, ProtocolJson.Options));
        }

        private static string Limit(string? value, int maximumLength)
        {
            value ??= string.Empty;
            return value.Length <= maximumLength ? value : value[..maximumLength];
        }
    }
}
