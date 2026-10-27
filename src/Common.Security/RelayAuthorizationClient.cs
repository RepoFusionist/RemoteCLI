using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Common.Protocol;

namespace Common.Security;

public static class RelayAuthorizationClient
{
    public static async Task<AuthorizedRelayConnection> ConnectLocalAsync(
        HttpClient client,
        Uri serverUri,
        string userName,
        string password,
        CancellationToken cancellationToken,
        Guid? requestId = null)
    {
        userName = ConnectionAuthorizationCredential.NormalizeUserName(userName);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(32));
        const int maximumAttempts = 3;
        try
        {
            for (var attempt = 1; attempt <= maximumAttempts; attempt++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                try
                {
                    // A lease can change after lookup. Never retry the old Session ID on 404.
                    var invitation = await ReadAuthorizationAsync(client, serverUri, userName, deadline.Token)
                        .ConfigureAwait(false);
                    var secret = ConnectionAuthorizationCredential.ResolveConnectionSecret(
                        userName, password, invitation.CredentialMode);
                    var socketUri = new UriBuilder(new Uri(serverUri, "v1/relay/" + invitation.SessionId.ToString("D")))
                    {
                        Scheme = serverUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
                        Query = "role=local"
                    }.Uri;
                    var socket = await WebSocketConnectHelper.ConnectClientWithRetryAsync(
                        static () => new ClientWebSocket(), socketUri, deadline.Token,
                        maximumAttempts: 1, requestId: requestId).ConfigureAwait(false);
                    return new AuthorizedRelayConnection(invitation, socket, secret);
                }
                catch (RelayWebSocketException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
                {
                    if (attempt == maximumAttempts)
                    {
                        throw new RelayAuthorizationException(
                            "relay_session_changed",
                            "被控端连接授权持续更新或已失效，重新查询 3 次后仍无法连接。请确认被控端授权仍开启；若随机密码已刷新，请复制当前密码后重试。");
                    }
                }
                catch (RelayAuthorizationException exception) when (exception.Code == "authorization_not_found" || exception.Retryable)
                {
                    if (attempt == maximumAttempts)
                    {
                        throw;
                    }
                }
                catch (RelayWebSocketException exception) when (exception.Retryable)
                {
                    if (attempt == maximumAttempts)
                    {
                        throw;
                    }
                }
                catch (Exception exception) when (exception is WebSocketException or HttpRequestException or TimeoutException)
                {
                    if (attempt == maximumAttempts)
                    {
                        throw;
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(attempt), deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("查询或连接 Relay 授权超时，请检查网络和被控端连接状态。");
        }

        throw new InvalidOperationException("The bounded authorization connection loop did not return a result.");
    }

    private static async Task<InvitationRequest> ReadAuthorizationAsync(
        HttpClient client, Uri serverUri, string userName, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            new Uri(serverUri, "v1/authorizations/" + Uri.EscapeDataString(userName)), cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new RelayAuthorizationException(
                "authorization_not_found", "找不到此用户名的有效连接授权，请确认用户名正确，并在被控端开启连接授权后重试。");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new RelayAuthorizationException(
                "relay_http_" + (int)response.StatusCode,
                "Relay 无法提供连接授权（HTTP " + (int)response.StatusCode + "），请检查服务状态和访问规则。",
                response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
                    (int)response.StatusCode >= 500);
        }
        if (response.Content.Headers.ContentType?.MediaType is not "application/json")
        {
            throw new InvalidDataException("Relay 返回了非 JSON 内容，请检查 Relay 地址和 /remotecli/ 反向代理路由。");
        }

        InvitationRequest? invitation;
        try
        {
            invitation = await response.Content.ReadFromJsonAsync<InvitationRequest>(ProtocolJson.Options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Relay 返回的连接授权格式无效，请检查服务版本与反向代理路由。");
        }

        if (invitation is null || invitation.ProtocolVersion != ProtocolConstants.CurrentVersion ||
            invitation.SessionId == Guid.Empty || invitation.ExpiresAt <= DateTimeOffset.UtcNow ||
            !string.Equals(invitation.UserName, userName, StringComparison.Ordinal) ||
            invitation.CredentialExpiresAt is not { } credentialExpiresAt || credentialExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new InvalidDataException("连接授权不匹配、已过期或协议版本不兼容。");
        }

        return invitation;
    }
}

public sealed class AuthorizedRelayConnection(InvitationRequest invitation, ClientWebSocket socket, string secret) : IDisposable
{
    public InvitationRequest Invitation { get; } = invitation;
    public ClientWebSocket Socket { get; } = socket;
    public string Secret { get; private set; } = secret;

    public void ClearSecret() => Secret = string.Empty;

    public void Dispose()
    {
        ClearSecret();
        Socket.Dispose();
    }
}

public sealed class RelayAuthorizationException(string code, string message, bool retryable = false) : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}
