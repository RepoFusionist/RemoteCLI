using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Common.Protocol;
using Common.Security;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: RelayDeploymentProbe <NewServer/server.json> [hold-seconds]");
    return 2;
}

var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(args[0]));
if (manifest.RootElement.GetProperty("provider").GetString() != "AlibabaCloud")
{
    throw new InvalidOperationException("Only the NewServer Alibaba Cloud target is permitted.");
}
var baseUri = new Uri(manifest.RootElement.GetProperty("relayBaseUrl").GetString()!);
if (baseUri.Scheme != "https")
{
    throw new InvalidOperationException("The deployment probe requires HTTPS.");
}
var holdSeconds = args.Length == 2 ? int.Parse(args[1]) : 0;
if (holdSeconds is < 0 or > 30) throw new ArgumentOutOfRangeException(nameof(holdSeconds));

using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
foreach (var mode in new[] { ConnectionCredentialMode.RotatingRandomPassword, ConnectionCredentialMode.FixedPassword })
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(100));
    var token = timeout.Token;
    var userName = "deployment-probe-" + Guid.NewGuid().ToString("N")[..12];
    var ownerToken = ConnectionAuthorizationCredential.CreateOwnerToken();
    var password = mode == ConnectionCredentialMode.FixedPassword
        ? "  probe!" + ConnectionAuthorizationCredential.CreateRandomSecret() + "  "
        : ConnectionAuthorizationCredential.CreateRandomSecret();
    var secret = ConnectionAuthorizationCredential.ResolveConnectionSecret(userName, password, mode);
    var invitation = OneTimeInvitationFactory.Create("synthetic-deployment-endpoint", RemotePermission.SessionRead);
    var registration = new ConnectionAuthorizationRegistration(userName, ownerToken, new InvitationRequest(
        ProtocolConstants.CurrentVersion, invitation.SessionId, invitation.RemoteIdentity,
        DateTimeOffset.UtcNow.AddMinutes(3), RemotePermission.SessionRead, userName, mode,
        DateTimeOffset.UtcNow.AddMinutes(4)));
    var registered = false;
    try
    {
        using var response = await client.PostAsJsonAsync(
            new Uri(baseUri, "v1/authorizations"), registration, ProtocolJson.Options, token);
        if (response.StatusCode != HttpStatusCode.Accepted)
            throw new InvalidOperationException("Authorization registration HTTP " + (int)response.StatusCode);
        registered = true;
        Console.WriteLine($"MODE={mode} REGISTER_HTTP=202");

        var remoteUri = new UriBuilder(new Uri(baseUri, "v1/relay/" + invitation.SessionId.ToString("D")))
        {
            Scheme = "wss", Query = "role=remote"
        }.Uri;
        using var remoteSocket = await WebSocketConnectHelper.ConnectClientWithRetryAsync(
            static () => new ClientWebSocket(), remoteUri, token, maximumAttempts: 1);
        Console.WriteLine($"MODE={mode} REMOTE_WSS_HTTP={(int)remoteSocket.HttpStatusCode}");
        var command = Encoding.UTF8.GetBytes("synthetic-encrypted-command-" + Guid.NewGuid().ToString("N"));
        var result = Encoding.UTF8.GetBytes("synthetic-encrypted-result-" + Guid.NewGuid().ToString("N"));
        var finishedReading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remoteTask = Task.Run(async () =>
        {
            await using var channel = await SecurePeerChannel.AcceptRemoteAsync(
                remoteSocket, invitation.SessionId, secret, invitation.RemoteIdentity,
                registration.Invitation.ExpiresAt, RemotePermission.SessionRead, token);
            var input = new byte[command.Length];
            await channel.Stream.ReadExactlyAsync(input, token);
            if (!input.AsSpan().SequenceEqual(command)) throw new InvalidDataException("Encrypted request mismatch.");
            await channel.Stream.WriteAsync(result, token);
            await finishedReading.Task.WaitAsync(token);
        }, token);
        _ = remoteTask.ContinueWith(task =>
            Console.Error.WriteLine($"MODE={mode} REMOTE_HANDSHAKE_FAILURE={task.Exception!.GetBaseException()}"),
            TaskContinuationOptions.OnlyOnFaulted);

        using var connection = await RelayAuthorizationClient.ConnectLocalAsync(
            client, baseUri, userName, password, token);
        await using var local = await SecurePeerChannel.ConnectLocalAsync(
            connection.Socket, connection.Invitation.SessionId, connection.Secret, "synthetic-probe-controller",
            static (_, _) => Task.FromResult(RemotePermission.SessionRead), token);
        connection.ClearSecret();
        Console.WriteLine($"MODE={mode} WSS_HTTP={(int)connection.Socket.HttpStatusCode} INNER_TLS={local.Stream.SslProtocol}");
        await local.Stream.WriteAsync(command, token);
        var received = new byte[result.Length];
        await local.Stream.ReadExactlyAsync(received, token);
        if (!received.AsSpan().SequenceEqual(result)) throw new InvalidDataException("Encrypted response mismatch.");
        Console.WriteLine($"MODE={mode} ENCRYPTED_ROUNDTRIP=PASS ACTIVE_HOLD_SECONDS={holdSeconds}");
        await Task.Delay(TimeSpan.FromSeconds(holdSeconds), token);
        finishedReading.TrySetResult();
        await local.DisposeAsync();
        await remoteTask.WaitAsync(token);
        Console.WriteLine($"MODE={mode} CLEAN_CLOSE=PASS");
    }
    finally
    {
        if (registered)
        {
            using var response = await client.PostAsJsonAsync(
                new Uri(baseUri, "v1/authorizations/" + userName + "/revoke"),
                new ConnectionAuthorizationRevocation(ownerToken), ProtocolJson.Options);
            if (response.StatusCode != HttpStatusCode.NoContent)
                throw new InvalidOperationException("Authorization revocation HTTP " + (int)response.StatusCode);
            Console.WriteLine($"MODE={mode} REVOKE_HTTP=204");
        }
        ownerToken = string.Empty;
        password = string.Empty;
        secret = string.Empty;
    }
}
Console.WriteLine("NEW_SERVER_DEPLOYMENT_PROBE=PASS (synthetic endpoints only; no user desktop session)");
return 0;
