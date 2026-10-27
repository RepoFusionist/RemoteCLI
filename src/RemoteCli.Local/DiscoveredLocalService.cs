using Common.Protocol;

namespace RemoteCli.Local;

public sealed record DiscoveredLocalService(int ProcessId, LocalConnectionStatus Status);
