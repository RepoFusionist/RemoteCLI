using System.Globalization;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Authentication;
using Common.Protocol;
using Common.Security;
using RemoteCli.Remote;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        if (args.Length == 1 && (args[0] == "--help" || args[0] == "help" || args[0] == "-h"))
        {
            PrintUsage();
            return 0;
        }

        if (args.Length == 0 || args[0] != "invite")
        {
            PrintUsage();
            return 2;
        }

        try
        {
            return await RunInviteAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or HttpRequestException or WebSocketException or InvalidOperationException or AuthenticationException or IOException or FormatException or OverflowException)
        {
            Console.Error.WriteLine("邀请或安全连接失败：" + exception.Message);
            return 1;
        }
    }

    private static async Task<int> RunInviteAsync(string[] args)
    {
        var options = ParseOptions(args);
        var server = Require(options, "server");
        var identity = options.TryGetValue("identity", out var customIdentity) ? customIdentity : Environment.MachineName;
        var permissionsText = Require(options, "permissions");
        var permissions = ParsePermissions(permissionsText);
        var lifetimeMinutes = options.TryGetValue("minutes", out var minutesText)
            ? int.Parse(minutesText, CultureInfo.InvariantCulture)
            : 10;
        if (lifetimeMinutes is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException("minutes", "Invitation lifetime must be 1 to 10 minutes.");
        }

        var serverUri = ValidateServer(server, options.ContainsKey("allow-insecure-loopback"));
        var invitation = OneTimeInvitationFactory.Create(identity, permissions, TimeSpan.FromMinutes(lifetimeMinutes));
        var request = new InvitationRequest(
            ProtocolConstants.CurrentVersion,
            invitation.SessionId,
            invitation.RemoteIdentity,
            invitation.ExpiresAt,
            invitation.RequestedPermissions);

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var response = await httpClient.PostAsJsonAsync(
            new Uri(serverUri, "v1/invitations"), request, ProtocolJson.Options).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("VPS refused invitation registration (HTTP " + (int)response.StatusCode + ").");
        }

        Console.WriteLine("请通过你认可的带外方式将以下邀请信息交给本地操作者：");
        Console.WriteLine("VPS: " + serverUri);
        Console.WriteLine("Session ID: " + invitation.SessionId.ToString("D"));
        Console.WriteLine("一次性密码: " + invitation.OneTimeSecret);
        Console.WriteLine("远端身份: " + invitation.RemoteIdentity);
        Console.WriteLine("申请权限: " + permissions);
        Console.WriteLine("到期时间: " + invitation.ExpiresAt.ToLocalTime().ToString("O", CultureInfo.InvariantCulture));
        Console.WriteLine("等待本地审批。密码只显示一次，不要把它写入对话或日志。");

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            var socketUri = new UriBuilder(new Uri(serverUri, "v1/relay/" + invitation.SessionId.ToString("D")))
            {
                Scheme = serverUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
                Query = "role=remote"
            }.Uri;
            using var socket = await WebSocketConnectHelper.ConnectClientWithRetryAsync(
                static () => new ClientWebSocket(),
                socketUri,
                cancellation.Token,
                requestId: invitation.SessionId).ConfigureAwait(false);
            await using var channel = await SecurePeerChannel.AcceptRemoteAsync(
                socket,
                invitation.SessionId,
                invitation.OneTimeSecret,
                invitation.RemoteIdentity,
                invitation.ExpiresAt,
                invitation.RequestedPermissions,
                cancellation.Token).ConfigureAwait(false);
            invitation = invitation with { OneTimeSecret = string.Empty };

            Console.WriteLine("已建立 TLS 1.3 端到端会话，经 VPS relay。批准权限：" + channel.ApprovedPermissions);
            Console.WriteLine("等待当前授权会话内的远程 PowerShell 命令。按 Ctrl+C 结束。");
            await RemoteCommandSession.RunAsync(channel, cancellation.Token).ConfigureAwait(false);

            Console.WriteLine("远端会话已关闭。");
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var key = args[index];
            if (!key.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Unexpected argument: " + key);
            }

            var name = key[2..];
            if (name is not ("server" or "permissions" or "identity" or "minutes" or "allow-insecure-loopback"))
            {
                throw new ArgumentException("Unknown option: " + key);
            }

            if (name == "allow-insecure-loopback")
            {
                options.Add(name, "true");
                continue;
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Missing value for " + key);
            }

            options.Add(name, args[++index]);
        }

        return options;
    }

    private static string Require(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("Required option missing: --" + name);

    private static RemotePermission ParsePermissions(string value)
    {
        var result = RemotePermission.None;
        foreach (var permission in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            result |= permission switch
            {
                "session.read" => RemotePermission.SessionRead,
                "command.execute" => RemotePermission.CommandExecute,
                "command.cancel" => RemotePermission.CommandCancel,
                _ => throw new ArgumentException("Unknown permission: " + permission)
            };
        }

        if (result == RemotePermission.None)
        {
            throw new ArgumentException("At least one permission must be requested.");
        }

        return result;
    }

    private static Uri ValidateServer(string value, bool allowInsecureLoopback)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var serverUri) ||
            (serverUri.Scheme != Uri.UriSchemeHttps && serverUri.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(serverUri.UserInfo) || !string.IsNullOrEmpty(serverUri.Query) || !string.IsNullOrEmpty(serverUri.Fragment))
        {
            throw new ArgumentException("--server must be an absolute HTTPS URL (HTTP is allowed only for explicit loopback development).");
        }

        if (serverUri.Scheme == Uri.UriSchemeHttp && (!allowInsecureLoopback || !serverUri.IsLoopback))
        {
            throw new ArgumentException("Insecure HTTP requires --allow-insecure-loopback and a loopback URL.");
        }

        return new Uri(serverUri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("RemoteCli.Remote invite --server <https://vps> --permissions <session.read,command.execute[,command.cancel]> [--identity <name>] [--minutes 1..10]");
        Console.WriteLine("Loopback development only: add --allow-insecure-loopback and use an http://127.0.0.1 URL.");
        Console.WriteLine("The current build accepts remote PowerShell commands only inside the approved encrypted session.");
    }
}
