using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using Common.Protocol;
using Common.Security;

namespace RemoteCli.UnitTests;

public sealed class PeerConfirmationTests
{
    private static readonly MethodInfo Confirm = typeof(SecurePeerChannel).GetMethod(
        "ConfirmPeerAsync", BindingFlags.NonPublic | BindingFlags.Static)!;

    [Fact]
    public void InvitationDeadlineCannotBeExtendedInTransit()
    {
        var channel = typeof(SecurePeerChannel);
        var createCertificate = channel.GetMethod("CreateEphemeralCertificate", BindingFlags.NonPublic | BindingFlags.Static)!;
        var createHello = channel.GetMethod("CreateHello", BindingFlags.NonPublic | BindingFlags.Static)!;
        var validateHello = channel.GetMethod("ValidateHello", BindingFlags.NonPublic | BindingFlags.Static)!;
        using var certificate = (System.Security.Cryptography.X509Certificates.X509Certificate2)createCertificate.Invoke(null, null)!;
        var secret = ConnectionAuthorizationCredential.CreateRandomSecret();
        var session = Guid.NewGuid();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(5);
        var hello = createHello.Invoke(null, [session, "remote", "remote-host", expiry,
            RemotePermission.SessionRead, certificate, null, secret])!;
        validateHello.Invoke(null, [hello, session, "remote", secret, null]);
        var expiresProperty = hello.GetType().GetProperty("ExpiresAtUnixSeconds")!;
        expiresProperty.SetValue(hello, expiry.AddMinutes(10).ToUnixTimeSeconds());
        var error = Assert.Throws<TargetInvocationException>(() =>
            validateHello.Invoke(null, [hello, session, "remote", secret, null]));
        Assert.IsType<AuthenticationException>(error.InnerException);
    }

    [Fact]
    public async Task CapturedHelloProofCannotAuthenticateAnotherConnection()
    {
        var secret = ConnectionAuthorizationCredential.CreateRandomSecret();
        var sessionId = Guid.NewGuid();
        var permission = RemotePermission.CommandExecute;
        var capturedProof = OneTimeInvitationFactory.CreateApprovalProof(secret, sessionId,
            "local:operator", permission, new byte[32]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var attacker = new TcpClient();
            await attacker.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
            using var server = await listener.AcceptTcpClientAsync(timeout.Token);
            var confirmation = (Task)Confirm.Invoke(null, [server.GetStream(), secret, sessionId, permission, false, timeout.Token])!;
            var challenge = new byte[32];
            await attacker.GetStream().ReadExactlyAsync(challenge, timeout.Token);
            await attacker.GetStream().WriteAsync(capturedProof, timeout.Token);
            await Assert.ThrowsAsync<AuthenticationException>(() => confirmation);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task BothPeersConfirmFreshChallenges()
    {
        var secret = ConnectionAuthorizationCredential.CreateRandomSecret();
        var sessionId = Guid.NewGuid();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
            using var server = await listener.AcceptTcpClientAsync(timeout.Token);
            var remote = (Task)Confirm.Invoke(null, [server.GetStream(), secret, sessionId, RemotePermission.SessionRead, false, timeout.Token])!;
            var local = (Task)Confirm.Invoke(null, [client.GetStream(), secret, sessionId, RemotePermission.SessionRead, true, timeout.Token])!;
            await Task.WhenAll(remote, local);
        }
        finally { listener.Stop(); }
    }
}
