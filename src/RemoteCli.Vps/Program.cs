using System.Net;
using System.Net.WebSockets;
using Common.Protocol;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using RemoteCli.Vps;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32 * 1024);

var configuredUrls = builder.Configuration["urls"]
    ?? builder.Configuration["ASPNETCORE_URLS"]
    ?? builder.WebHost.GetSetting(WebHostDefaults.ServerUrlsKey);
var developmentMode = builder.Environment.IsDevelopment();
var allowInsecureLoopback = builder.Configuration.GetValue<bool>("Relay:AllowInsecureLoopback");
var behindTrustedReverseProxy = builder.Configuration.GetValue<bool>("Relay:BehindTrustedReverseProxy");
if (developmentMode)
{
    if (!allowInsecureLoopback)
    {
        throw new InvalidOperationException("Development relay requires Relay:AllowInsecureLoopback=true.");
    }

    if (configuredUrls is not null && configuredUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Any(url => !Uri.TryCreate(url, UriKind.Absolute, out var parsed) || !IsLoopbackHost(parsed.Host)))
    {
        throw new InvalidOperationException("Insecure development relay URLs must bind only to loopback addresses.");
    }
}
else if (behindTrustedReverseProxy)
{
    if (string.IsNullOrWhiteSpace(configuredUrls) || configuredUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Any(url => !Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttp || !IsLoopbackHost(parsed.Host)))
    {
        throw new InvalidOperationException("Trusted reverse-proxy mode requires explicit HTTP URLs bound only to loopback.");
    }

    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Add(IPAddress.Loopback);
    });
}
else
{
    if (string.IsNullOrWhiteSpace(configuredUrls) || configuredUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Any(url => !Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps))
    {
        throw new InvalidOperationException("Production relay requires explicit HTTPS URLs and valid Kestrel TLS certificate configuration.");
    }
}

builder.Services.AddSingleton<RelayRoomService>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("session", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 30,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        }));
});
var app = builder.Build();
if (behindTrustedReverseProxy)
{
    app.UseForwardedHeaders();
}

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.UseRateLimiter();

app.MapGet("/healthz", (RelayRoomService _) => Results.Json(new
{
    status = "ready",
    webSocketRelayAvailable = true,
    endToEndSecurityEnforced = true,
    relayReceivesConnectionSecret = false,
    relayPayloadInspectionAvailable = false,
    p2pAvailable = false,
    userNameAuthorizationAvailable = true,
    clientProgramsIntegrated = true
}));

app.MapPost("/v1/invitations", (HttpContext context, InvitationRequest request, RelayRoomService relay) =>
{
    if (!developmentMode && !context.Request.IsHttps)
    {
        return Results.StatusCode(StatusCodes.Status400BadRequest);
    }

    return relay.TryRegister(request, out var errorCode)
        ? Results.Accepted($"/v1/invitations/{request.SessionId:D}", new { request.SessionId, request.ExpiresAt })
        : Results.BadRequest(new { error = errorCode });
}).RequireRateLimiting("session");

app.MapPost("/v1/authorizations", (HttpContext context, ConnectionAuthorizationRegistration registration, RelayRoomService relay) =>
{
    if (!developmentMode && !context.Request.IsHttps)
    {
        return Results.StatusCode(StatusCodes.Status400BadRequest);
    }

    return relay.TryRegisterAuthorization(registration, out var errorCode)
        ? Results.Accepted(
            "/v1/authorizations/" + Uri.EscapeDataString(registration.UserName),
            new { registration.Invitation.SessionId, registration.Invitation.ExpiresAt })
        : Results.BadRequest(new { error = errorCode });
}).RequireRateLimiting("session");

app.MapGet("/v1/authorizations/{userName}", (HttpContext context, string userName, RelayRoomService relay) =>
{
    if (!developmentMode && !context.Request.IsHttps)
    {
        return Results.StatusCode(StatusCodes.Status400BadRequest);
    }

    return relay.TryGetAuthorization(userName, out var request)
        ? Results.Ok(request)
        : Results.NotFound(new { error = "authorization_not_found" });
}).RequireRateLimiting("session");

app.MapPost("/v1/authorizations/{userName}/revoke", (
    HttpContext context,
    string userName,
    ConnectionAuthorizationRevocation revocation,
    RelayRoomService relay) =>
{
    if (!developmentMode && !context.Request.IsHttps)
    {
        return Results.StatusCode(StatusCodes.Status400BadRequest);
    }

    return relay.TryRevokeAuthorization(userName, revocation, out var errorCode)
        ? Results.NoContent()
        : Results.NotFound(new { error = errorCode });
}).RequireRateLimiting("session");

app.MapGet("/v1/invitations/{sessionId:guid}", (HttpContext context, Guid sessionId, RelayRoomService relay) =>
{
    if (!developmentMode && !context.Request.IsHttps)
    {
        return Results.StatusCode(StatusCodes.Status400BadRequest);
    }

    return relay.TryGetInvitation(sessionId, out var request)
        ? Results.Ok(request)
        : Results.NotFound(new { error = "invitation_not_found" });
}).RequireRateLimiting("session");

app.MapGet("/v1/relay/{sessionId:guid}", async (HttpContext context, Guid sessionId, RelayRoomService relay) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    if (!developmentMode && !context.Request.IsHttps)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var role = context.Request.Query["role"].ToString();
    if ((role != "remote" && role != "local") || !relay.TryGetInvitation(sessionId, out _))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    try
    {
        await relay.JoinAsync(sessionId, role, socket, context.RequestAborted).ConfigureAwait(false);
    }
    catch (RelayJoinException)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "relay_join_rejected", CancellationToken.None).ConfigureAwait(false);
        }
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
    }
}).RequireRateLimiting("session");

app.MapFallback(() => Results.NotFound(new { error = "not_found" }));
await app.RunAsync();

static bool IsLoopbackHost(string host) =>
    string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
    IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
