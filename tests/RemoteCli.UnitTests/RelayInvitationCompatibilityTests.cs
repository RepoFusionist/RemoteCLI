using Common.Protocol;

namespace RemoteCli.UnitTests;

public sealed class RelayInvitationCompatibilityTests
{
    [Fact]
    public void ForRelayRegistration_RemovesControllerModelPermissionButPreservesHandshakeRequest()
    {
        var original = new InvitationRequest(
            ProtocolConstants.CurrentVersion,
            Guid.NewGuid(),
            "remote",
            DateTimeOffset.UtcNow.AddMinutes(5),
            RemotePermission.SessionRead | RemotePermission.ControllerModelUse);

        var relayRequest = RelayInvitationCompatibility.ForRelayRegistration(original);

        Assert.Equal(RemotePermission.SessionRead, relayRequest.RequestedPermissions);
        Assert.Equal(RemotePermission.SessionRead | RemotePermission.ControllerModelUse, original.RequestedPermissions);
        Assert.Equal(original.SessionId, relayRequest.SessionId);
        Assert.Equal(original.ExpiresAt, relayRequest.ExpiresAt);
    }
}
