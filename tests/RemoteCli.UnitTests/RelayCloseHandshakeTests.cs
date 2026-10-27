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
using RemoteCli.Vps;

namespace RemoteCli.UnitTests;

public sealed class RelayCloseHandshakeTests
{
    [Fact]
    public async Task RelayForwardsCloseFramesAndCompletesBothWebSocketHandshakes()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var invitation = OneTimeInvitationFactory.Create("remote-close-test", RemotePermission.SessionRead);
        var invitationRequest = new InvitationRequest(
            ProtocolConstants.CurrentVersion,
            invitation.SessionId,
            invitation.RemoteIdentity,
            invitation.ExpiresAt,
            invitation.RequestedPermissions);
        var relay = new RelayRoomService();
        Assert.True(relay.TryRegister(invitationRequest, out var registerError), registerError);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(relay);
        await using var app = builder.Build();
        app.UseWebSockets();
        app.MapGet("/relay/{sessionId:guid}", async (HttpContext context, Guid sessionId, RelayRoomService relayService) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await relayService.JoinAsync(sessionId, context.Request.Query["role"].ToString(), socket, context.RequestAborted);
        });
        await app.StartAsync(cancellation.Token);

        var server = app.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var remoteSocketUri = new UriBuilder(address)
        {
            Scheme = "ws",
            Path = $"/relay/{invitation.SessionId:D}",
            Query = "role=remote"
        }.Uri;
        var localSocketUri = new UriBuilder(remoteSocketUri) { Query = "role=local" }.Uri;

        using var remoteSocket = new ClientWebSocket();
        await remoteSocket.ConnectAsync(remoteSocketUri, cancellation.Token);
        using var localSocket = new ClientWebSocket();
        await localSocket.ConnectAsync(localSocketUri, cancellation.Token);

        var remoteCloseTask = remoteSocket.CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "remote_done",
            cancellation.Token);
        var localCloseTask = localSocket.CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "local_done",
            cancellation.Token);
        await Task.WhenAll(remoteCloseTask, localCloseTask).WaitAsync(cancellation.Token);

        Assert.Equal(WebSocketState.Closed, remoteSocket.State);
        Assert.Equal(WebSocketState.Closed, localSocket.State);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, remoteSocket.CloseStatus);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, localSocket.CloseStatus);
        await app.StopAsync(cancellation.Token);
    }
}
