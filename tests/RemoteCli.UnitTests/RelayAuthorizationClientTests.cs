using System.Net;
using System.Net.WebSockets;
using Common.Protocol;
using Common.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RemoteCli.UnitTests;

public sealed class RelayAuthorizationClientTests
{
    private const string UserName = "test.user";

    [Fact]
    public async Task StaleSession404RequeriesAuthorizationAndConnectsToNewSession()
    {
        var password = ConnectionAuthorizationCredential.CreateRandomSecret();
        var stale = Invitation() with { CredentialMode = ConnectionCredentialMode.FixedPassword };
        var current = Invitation();
        var lookups = 0;
        var attemptedSessions = new List<Guid>();
        await using var app = CreateApp();
        app.MapGet("/remotecli/v1/authorizations/{userName}", () =>
            Results.Json(Interlocked.Increment(ref lookups) == 1 ? stale : current, ProtocolJson.Options));
        app.MapGet("/remotecli/v1/relay/{sessionId:guid}", async (HttpContext context, Guid sessionId) =>
        {
            attemptedSessions.Add(sessionId);
            if (sessionId != current.SessionId)
            {
                context.Response.StatusCode = 404;
                return;
            }

            await HoldSocketAsync(context);
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await app.StartAsync(cancellation.Token);
        using var client = new HttpClient();
        using var connection = await RelayAuthorizationClient.ConnectLocalAsync(
            client, BaseUri(app), " TEST.USER ", password, cancellation.Token);

        Assert.Equal(2, lookups);
        Assert.Equal(new[] { stale.SessionId, current.SessionId }, attemptedSessions);
        Assert.Equal(current.SessionId, connection.Invitation.SessionId);
        Assert.Equal(password, connection.Secret);
        Assert.Equal(WebSocketState.Open, connection.Socket.State);
        connection.ClearSecret();
        Assert.Equal(string.Empty, connection.Secret);
        await connection.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cancellation.Token);
    }

    [Fact]
    public async Task RepeatedWebSocket404StopsAfterThreeFreshLookupsWithActionableError()
    {
        var lookups = 0;
        var connects = 0;
        await using var app = CreateApp();
        app.MapGet("/remotecli/v1/authorizations/{userName}", () =>
        {
            Interlocked.Increment(ref lookups);
            return Results.Json(Invitation(), ProtocolJson.Options);
        });
        app.MapGet("/remotecli/v1/relay/{sessionId:guid}", () =>
        {
            Interlocked.Increment(ref connects);
            return Results.NotFound();
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await app.StartAsync(cancellation.Token);
        using var client = new HttpClient();

        var exception = await Assert.ThrowsAsync<RelayAuthorizationException>(() =>
            RelayAuthorizationClient.ConnectLocalAsync(
                client, BaseUri(app), UserName, ConnectionAuthorizationCredential.CreateRandomSecret(), cancellation.Token));

        Assert.Equal("relay_session_changed", exception.Code);
        Assert.Equal(3, lookups);
        Assert.Equal(3, connects);
        Assert.Contains("3", exception.Message);
        Assert.DoesNotContain("101", exception.Message);
    }

    [Fact]
    public async Task AuthorizationTemporarilyMissingDuringRenewalIsQueriedAgain()
    {
        var lookups = 0;
        var current = Invitation();
        await using var app = CreateApp();
        app.MapGet("/remotecli/v1/authorizations/{userName}", () =>
            Interlocked.Increment(ref lookups) == 1
                ? (IResult)Results.NotFound()
                : Results.Json(current, ProtocolJson.Options));
        app.MapGet("/remotecli/v1/relay/{sessionId:guid}", HoldSocketAsync);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await app.StartAsync(cancellation.Token);
        using var client = new HttpClient();
        using var connection = await RelayAuthorizationClient.ConnectLocalAsync(
            client, BaseUri(app), UserName, ConnectionAuthorizationCredential.CreateRandomSecret(), cancellation.Token);

        Assert.Equal(2, lookups);
        Assert.Equal(current.SessionId, connection.Invitation.SessionId);
        await connection.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cancellation.Token);
    }

    [Fact]
    public async Task FixedSessionWebSocket404IsClassifiedAndNeverRetriedWithTheSameUri()
    {
        var attempts = 0;
        await using var app = CreateApp();
        app.MapGet("/remotecli/missing", () =>
        {
            Interlocked.Increment(ref attempts);
            return Results.NotFound();
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.StartAsync(cancellation.Token);
        var uri = new UriBuilder(new Uri(BaseUri(app), "missing")) { Scheme = "ws" }.Uri;

        var exception = await Assert.ThrowsAsync<RelayWebSocketException>(() =>
            WebSocketConnectHelper.ConnectClientWithRetryAsync(
                static () => new ClientWebSocket(), uri, cancellation.Token));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.False(exception.Retryable);
        Assert.Equal(1, attempts);
        Assert.DoesNotContain("101", exception.Message);
    }

    [Fact]
    public async Task TemporaryServerFailureIsRetriedButAccessDenialIsNot()
    {
        var lookups = 0;
        await using var app = CreateApp();
        app.MapGet("/remotecli/v1/authorizations/{userName}", () =>
            Results.StatusCode(Interlocked.Increment(ref lookups) == 1 ? 503 : 403));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.StartAsync(cancellation.Token);
        using var client = new HttpClient();

        var exception = await Assert.ThrowsAsync<RelayAuthorizationException>(() =>
            RelayAuthorizationClient.ConnectLocalAsync(
                client, BaseUri(app), UserName, ConnectionAuthorizationCredential.CreateRandomSecret(), cancellation.Token));

        Assert.Equal("relay_http_403", exception.Code);
        Assert.False(exception.Retryable);
        Assert.Equal(2, lookups);
    }

    [Fact]
    public async Task CancellationDuringAuthorizationRetryIsPreserved()
    {
        using var cancellation = new CancellationTokenSource();
        var lookups = 0;
        await using var app = CreateApp();
        app.MapGet("/remotecli/v1/authorizations/{userName}", () =>
        {
            Interlocked.Increment(ref lookups);
            cancellation.Cancel();
            return Results.NotFound();
        });
        await app.StartAsync();
        using var client = new HttpClient();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RelayAuthorizationClient.ConnectLocalAsync(
                client, BaseUri(app), UserName, ConnectionAuthorizationCredential.CreateRandomSecret(), cancellation.Token));
        Assert.Equal(1, lookups);
    }

    [Fact]
    public async Task HtmlFromWrongProxyRouteIsRejectedBeforeWebSocketConnect()
    {
        var connects = 0;
        await using var app = CreateApp();
        app.MapGet("/remotecli/v1/authorizations/{userName}", () =>
            Results.Content("<!doctype html><title>Other service</title>", "text/html"));
        app.MapGet("/remotecli/v1/relay/{sessionId:guid}", () =>
        {
            Interlocked.Increment(ref connects);
            return Results.NotFound();
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.StartAsync(cancellation.Token);
        using var client = new HttpClient();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            RelayAuthorizationClient.ConnectLocalAsync(
                client, BaseUri(app), UserName, ConnectionAuthorizationCredential.CreateRandomSecret(), cancellation.Token));
        Assert.Contains("反向代理", exception.Message);
        Assert.Equal(0, connects);
    }

    [Fact]
    public async Task InvalidPasswordDoesNotOpenAWebSocketOrConsumeAnAttempt()
    {
        var connects = 0;
        await using var app = CreateApp();
        app.MapGet("/remotecli/v1/authorizations/{userName}", () => Results.Json(Invitation(), ProtocolJson.Options));
        app.MapGet("/remotecli/v1/relay/{sessionId:guid}", () =>
        {
            Interlocked.Increment(ref connects);
            return Results.NotFound();
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.StartAsync(cancellation.Token);
        using var client = new HttpClient();

        await Assert.ThrowsAsync<ConnectionCredentialException>(() =>
            RelayAuthorizationClient.ConnectLocalAsync(client, BaseUri(app), UserName, "invalid!", cancellation.Token));
        Assert.Equal(0, connects);
    }

    private static InvitationRequest Invitation() => new(
        ProtocolConstants.CurrentVersion, Guid.NewGuid(), "remote-test", DateTimeOffset.UtcNow.AddMinutes(5),
        RemotePermission.SessionRead, UserName, ConnectionCredentialMode.RotatingRandomPassword,
        DateTimeOffset.UtcNow.AddMinutes(10));

    private static WebApplication CreateApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.UseWebSockets();
        return app;
    }

    private static Uri BaseUri(WebApplication app) => new(
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/remotecli/");

    private static async Task HoldSocketAsync(HttpContext context)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        try
        {
            await socket.ReceiveAsync(new byte[32], context.RequestAborted);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", context.RequestAborted);
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
        {
        }
    }
}
