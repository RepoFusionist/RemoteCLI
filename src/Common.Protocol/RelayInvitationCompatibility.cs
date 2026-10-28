namespace Common.Protocol;

/// <summary>Creates invitation metadata compatible with older Relay releases.</summary>
public static class RelayInvitationCompatibility
{
    /// <summary>
    /// Removes permissions that are only enforced by the authenticated peer handshake and are
    /// unknown to older Relays. The caller must keep the original invitation for that handshake.
    /// </summary>
    public static InvitationRequest ForRelayRegistration(InvitationRequest invitation)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        return invitation with
        {
            RequestedPermissions = invitation.RequestedPermissions & ~RemotePermission.ControllerModelUse
        };
    }
}
