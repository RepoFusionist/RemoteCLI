using System.Text.Json;
using Common.Protocol;

namespace RemoteCli.Remote;

public interface IRemoteToolRegistry
{
    string CapabilitiesVersion { get; }
    IReadOnlyList<RemoteToolDescriptor> ListTools();
    bool TryValidateCall(string name, JsonElement arguments, out string error);
    Task<JsonElement> ExecuteAsync(string name, JsonElement arguments, CancellationToken cancellationToken);
}
