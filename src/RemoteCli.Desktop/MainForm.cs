using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Common.Protocol;
using Common.Security;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using RemoteCli.Local;
using RemoteCli.Remote;

namespace RemoteCli.Desktop;

/// <summary>
/// WebView2 hosts the LongShell page; connection logic stays in this class and is driven
/// by JSON messages from the page. The page only sees the connection authorization it created, and
/// the remote user owns live access policy.
/// </summary>
internal sealed class MainForm : Form
{
    private const string DefaultServer = "https://api.sunxiaochuan258.de5.net/remotecli/";
    private const string UiResourceName = "RemoteCli.Desktop.UI.longshell-ui.html";
    private const string IconResourceName = "RemoteCli.Desktop.Assets.longshell-icon.ico";

    private readonly LocalConnectionStateMachine _connectionStateMachine = new();
    private readonly CancellationTokenSource _serviceLifetime = new();
    private readonly Queue<string> _pendingUiMessages = new();
    private readonly WebView2 _webView;
    private readonly System.Windows.Forms.Timer _mcpPresenceTimer = new() { Interval = 1_000 };
    private readonly InstalledRemoteToolStore _toolStore = new();
    private readonly RemoteAccessController _remoteAccess = new();

    private LocalConnectionServiceCoordinator _connectionService = null!;
    private LocalConnectionServicePipeServer _connectionPipeServer = null!;
    private CancellationTokenSource? _operationCancellation;
    private Guid? _currentAuthRequestId;
    private bool _uiReady;
    private bool _lastMcpPresence;
    private string? _remoteWorkspaceRoot;

    public MainForm()
    {
        _remoteWorkspaceRoot = LoadRemoteWorkspace();
        try { _remoteAccess.Update(RemotePermission.SessionRead, _remoteWorkspaceRoot, true); }
        catch (IOException) { _remoteWorkspaceRoot = null; }
        using (var iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(IconResourceName))
        {
            if (iconStream is not null)
            {
                Icon = new System.Drawing.Icon(iconStream);
            }
        }

        Text = "LongShell";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 560);
        Size = new Size(980, 700);
        BackColor = Color.FromArgb(25, 25, 24);

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.FromArgb(25, 25, 24)
        };
        Controls.Add(_webView);

        _connectionService = new LocalConnectionServiceCoordinator(_connectionStateMachine,
            new LocalSessionBrokerClient(LocalSessionBroker.PipeNameForProcess(Environment.ProcessId)));
        _connectionService.AuthenticationRequested += OnAuthenticationRequested;
        _connectionService.CloseRequested += () => _operationCancellation?.Cancel();
        _connectionStateMachine.StateChanged += OnConnectionStateChanged;
        _connectionPipeServer = new LocalConnectionServicePipeServer(
            _connectionService.HandleAsync, LocalConnectionServicePipeServer.PipeNameForProcess(Environment.ProcessId));
        _connectionPipeServer.Listening += () => PostToUi(new { type = "service-presence", connected = true });
        _connectionPipeServer.RequestFailed += (requestId, type, code) =>
            Trace.TraceWarning("LongShell IPC request failed requestId={0} type={1} code={2}", requestId, type, code);
        _mcpPresenceTimer.Tick += (_, _) => RefreshMcpPresence();

        Shown += async (_, _) => await InitializeUiAsync();
        FormClosing += (_, _) =>
        {
            _mcpPresenceTimer.Stop();
            _serviceLifetime.Cancel();
            _operationCancellation?.Cancel();
            _connectionStateMachine.Close();
        };
    }

    // ── 界面宿主 ───────────────────────────────────────────────

    private async Task InitializeUiAsync()
    {
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LongShell",
            "WebView2");

        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _webView.EnsureCoreWebView2Async(environment);
        }
        catch (Exception firstFailure)
        {
            try
            {
                // 另一个实例占用了同一个用户数据目录时，退回到本进程独占的目录。
                var fallback = await CoreWebView2Environment.CreateAsync(null, userDataFolder + "-" + Environment.ProcessId);
                await _webView.EnsureCoreWebView2Async(fallback);
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    this,
                    "界面组件（WebView2 Runtime）不可用，LongShell 无法启动。\n\n" + exception.Message +
                    "\n\n首次尝试：" + firstFailure.Message,
                    "LongShell",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                BeginInvoke(new Action(Close));
                return;
            }
        }

        var core = _webView.CoreWebView2;
        var settings = core.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;

        core.WebMessageReceived += OnWebMessageReceived;
        core.NavigateToString(LoadUiHtml());

        _ = RunLocalConnectionServiceAsync();
        _mcpPresenceTimer.Start();
    }

    private static string LoadUiHtml()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(UiResourceName)
            ?? throw new InvalidOperationException("Embedded UI resource was not found: " + UiResourceName);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(e.WebMessageAsJson);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                return;
            }

            switch (typeElement.GetString())
            {
                case "ready":
                    MarkUiReady();
                    break;
                case "create":
                    BeginCreateInvitation(root);
                    break;
                case "join":
                    BeginJoinSession(root);
                    break;
                case "stop":
                    _ = StopFromUiAsync(ReadString(root, "role"));
                    break;
                case "copy":
                    CopyToClipboard(ReadString(root, "text"));
                    break;
                case "select-workspace":
                    SelectRemoteWorkspace();
                    break;
                case "update-access":
                    UpdateRemoteAccess(root);
                    break;
                case "install-tool":
                    _ = InstallRemoteToolAsync();
                    break;
            }
        }
    }

    private void MarkUiReady()
    {
        _uiReady = true;
        PostToUi(new
        {
            type = "init",
            userName = PreferredIdentity(),
            machineName = Environment.MachineName,
            relay = DefaultServer,
            version = Application.ProductVersion
        });
        PublishToolSettings();

        while (_pendingUiMessages.Count > 0)
        {
            _webView.CoreWebView2?.PostWebMessageAsJson(_pendingUiMessages.Dequeue());
        }
    }

    private void PostToUi(object payload)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action(() => PostToUi(payload)));
            }
            catch (InvalidOperationException)
            {
            }

            return;
        }

        var json = JsonSerializer.Serialize(payload);
        if (!_uiReady || _webView.CoreWebView2 is null)
        {
            _pendingUiMessages.Enqueue(json);
            return;
        }

        _webView.CoreWebView2.PostWebMessageAsJson(json);
    }

    private void PostStatus(string side, string text, string kind = "normal")
        => PostToUi(new { type = "status", side, text, kind });

    private void PostPhase(string side, string phase)
        => PostToUi(new { type = "phase", side, phase });

    private void PostError(string side, string text)
        => PostToUi(new { type = "error", side, text });

    // ── 被控端：开启用户名授权 ─────────────────────────────────

    private void BeginCreateInvitation(JsonElement root)
    {
        var identity = ReadString(root, "identity").Trim();
        var userName = ReadString(root, "userName");
        var fixedPassword = ReadString(root, "fixedPassword");
        var mode = ReadString(root, "credentialMode") == "fixed"
            ? ConnectionCredentialMode.FixedPassword
            : ConnectionCredentialMode.RotatingRandomPassword;
        var permissions = ReadPermissions(root);
        var rotation = root.TryGetProperty("rotationMinutes", out var rotationElement) &&
                       rotationElement.TryGetInt32(out var rotationMinutes)
            ? TimeSpan.FromMinutes(rotationMinutes)
            : TimeSpan.FromMinutes(10);

        if (string.IsNullOrWhiteSpace(identity))
        {
            PostPhase("remote", "idle");
            PostStatus("remote", "请填写对方看到的名称。", "warn");
            return;
        }

        try
        {
            userName = ConnectionAuthorizationCredential.NormalizeUserName(userName);
            if (mode == ConnectionCredentialMode.FixedPassword)
            {
                _ = ConnectionAuthorizationCredential.DeriveFixedSecret(userName, fixedPassword);
            }
            else if (!ConnectionAuthorizationCredential.IsSupportedRotation(rotation))
            {
                throw new ArgumentException("请选择受支持的随机密码刷新时间。");
            }
        }
        catch (ArgumentException exception)
        {
            PostPhase("remote", "idle");
            PostStatus("remote", exception.Message, "warn");
            return;
        }

        if (permissions == RemotePermission.None)
        {
            PostPhase("remote", "idle");
            PostStatus("remote", "至少申请一项权限。", "warn");
            return;
        }

        Uri serverUri;
        try
        {
            serverUri = ValidateServerUri(ReadString(root, "relay"));
        }
        catch (ArgumentException exception)
        {
            PostPhase("remote", "idle");
            PostStatus("remote", exception.Message, "warn");
            return;
        }

        if (!TryBeginOperation("remote", out var cancellation))
        {
            return;
        }

        var workspaceRestricted = !root.TryGetProperty("workspaceRestricted", out var restriction) || restriction.ValueKind != JsonValueKind.False;
        var autoApproveTools = root.TryGetProperty("autoApproveTools", out var autoApproveValue)
            ? autoApproveValue.ValueKind == JsonValueKind.True
            : _remoteAccess.Snapshot.AutoApproveTools;
        if (autoApproveTools && !_remoteAccess.Snapshot.AutoApproveTools && !ConfirmAutomaticToolApprovals())
        {
            EndOperation(cancellation);
            PostPhase("remote", "idle");
            PublishToolSettings();
            return;
        }

        _remoteAccess.Update(permissions, _remoteWorkspaceRoot, workspaceRestricted, autoApproveTools);

        _ = RunAuthorizationHostAsync(
            serverUri,
            identity,
            userName,
            mode,
            fixedPassword,
            rotation,
            permissions,
            cancellation);
    }

    private async Task RunAuthorizationHostAsync(
        Uri serverUri,
        string identity,
        string userName,
        ConnectionCredentialMode credentialMode,
        string fixedPassword,
        TimeSpan rotation,
        RemotePermission permissions,
        CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        var ownerToken = ConnectionAuthorizationCredential.CreateOwnerToken();
        var secret = credentialMode == ConnectionCredentialMode.FixedPassword
            ? ConnectionAuthorizationCredential.DeriveFixedSecret(userName, fixedPassword)
            : ConnectionAuthorizationCredential.CreateRandomSecret();
        fixedPassword = string.Empty;
        var credentialExpiresAt = credentialMode == ConnectionCredentialMode.FixedPassword
            ? DateTimeOffset.UtcNow.AddDays(30)
            : GetRandomCredentialExpiry(rotation, DateTimeOffset.UtcNow);
        PostStatus("remote", "正在开启连接授权…");

        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            while (!token.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                if (credentialMode == ConnectionCredentialMode.FixedPassword && now >= credentialExpiresAt)
                {
                    credentialExpiresAt = now.AddDays(30);
                }

                if (credentialMode == ConnectionCredentialMode.RotatingRandomPassword && now >= credentialExpiresAt)
                {
                    secret = ConnectionAuthorizationCredential.CreateRandomSecret();
                    credentialExpiresAt = now.Add(rotation);
                }

                var attemptLifetime = credentialExpiresAt - now;
                if (attemptLifetime > TimeSpan.FromMinutes(9))
                {
                    attemptLifetime = TimeSpan.FromMinutes(9);
                }

                if (attemptLifetime <= TimeSpan.Zero)
                {
                    continue;
                }

                var currentPermissions = _remoteAccess.Snapshot.Permissions;
                var invitation = OneTimeInvitationFactory.Create(identity, currentPermissions, attemptLifetime) with
                {
                    OneTimeSecret = secret
                };
                var request = new InvitationRequest(
                    ProtocolConstants.CurrentVersion,
                    invitation.SessionId,
                    invitation.RemoteIdentity,
                    invitation.ExpiresAt,
                    invitation.RequestedPermissions,
                    userName,
                    credentialMode,
                    credentialExpiresAt);
                var registration = new ConnectionAuthorizationRegistration(userName, ownerToken, request);

                HttpResponseMessage response;
                try
                {
                    response = await httpClient.PostAsJsonAsync(
                        new Uri(serverUri, "v1/authorizations"),
                        registration,
                        ProtocolJson.Options,
                        token);
                }
                catch (Exception exception) when (!token.IsCancellationRequested &&
                    exception is HttpRequestException or TaskCanceledException)
                {
                    Trace.TraceWarning("Connection authorization registration will retry after {0}.", exception.GetType().Name);
                    PostStatus("remote", "中继连接暂时不可用，正在重连…", "warn");
                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                    continue;
                }

                using (response)
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        throw new InvalidOperationException("当前 Relay 尚未支持用户名连接授权，请先更新 VPS 服务。");
                    }

                    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                        (int)response.StatusCode >= 500)
                    {
                        PostStatus("remote", "中继暂时拒绝连接，正在重连…", "warn");
                        await Task.Delay(TimeSpan.FromSeconds(2), token);
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(
                            "Relay 拒绝连接授权（HTTP " + (int)response.StatusCode + "）。");
                    }
                }

                PostToUi(new
                {
                    type = "authorization-ready",
                    userName,
                    password = credentialMode == ConnectionCredentialMode.RotatingRandomPassword ? secret : string.Empty,
                    credentialMode = credentialMode == ConnectionCredentialMode.FixedPassword ? "fixed" : "random",
                    refreshAfterConnection = credentialMode == ConnectionCredentialMode.RotatingRandomPassword &&
                        rotation == TimeSpan.Zero,
                    expiresAt = credentialExpiresAt,
                    permissions = ToJsPermissions(currentPermissions)
                });
                PostStatus(
                    "remote",
                    credentialMode == ConnectionCredentialMode.FixedPassword
                        ? "固定密码连接授权已开启，等待主控端连接。"
                        : "随机密码连接授权已开启，等待主控端连接。",
                    "ok");

                try
                {
                    using var socket = await WebSocketConnectHelper.ConnectClientWithRetryAsync(
                        static () => new ClientWebSocket(),
                        CreateRelaySocketUri(serverUri, invitation.SessionId, "remote"),
                        token,
                        requestId: invitation.SessionId);
                    await using var channel = await SecurePeerChannel.AcceptRemoteAsync(
                        socket,
                        invitation.SessionId,
                        secret,
                        invitation.RemoteIdentity,
                        invitation.ExpiresAt,
                        invitation.RequestedPermissions,
                        token);

                    PostToUi(new
                    {
                        type = "connected",
                        side = "remote",
                        peer = channel.RemoteIdentity,
                        granted = ToJsPermissions(channel.ApprovedPermissions),
                        expiresAt = channel.SessionExpiresAt
                    });
                    PostStatus(
                        "remote",
                        "被控端已进行连接；当前权限：" + FormatPermissions(channel.ApprovedPermissions) + "。",
                        "ok");

                    var registry = new DynamicRemoteToolRegistry(_remoteAccess, policy => RemoteToolRegistry.CreateForDesktop(
                        policy.WorkspaceRoot,
                        _toolStore.LoadInstalled(policy),
                        ApproveRemoteToolAsync,
                        policy.WorkspaceRestricted,
                        policy.AutoApproveTools));
                    await RemoteCommandSession.RunAsync(channel, token, registry, _remoteAccess);
                    PostToUi(new { type = "connection-ended", side = "remote" });
                    if (credentialMode == ConnectionCredentialMode.RotatingRandomPassword)
                    {
                        secret = ConnectionAuthorizationCredential.CreateRandomSecret();
                        credentialExpiresAt = GetRandomCredentialExpiry(rotation, DateTimeOffset.UtcNow);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (exception is AuthenticationException or TimeoutException or
                    WebSocketException or IOException)
                {
                    Trace.TraceWarning("Connection authorization attempt renewed after {0}.", exception.GetType().Name);
                    if (credentialMode == ConnectionCredentialMode.RotatingRandomPassword && exception is AuthenticationException)
                    {
                        secret = ConnectionAuthorizationCredential.CreateRandomSecret();
                        credentialExpiresAt = GetRandomCredentialExpiry(rotation, DateTimeOffset.UtcNow);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            PostStatus("remote", "会话已停止。");
        }
        catch (Exception exception)
        {
            PostStatus("remote", "操作失败。", "warn");
            PostError("remote", exception.Message);
        }
        finally
        {
            var policy = _remoteAccess.Snapshot;
            if (policy.AutoApproveTools)
            {
                _remoteAccess.Update(policy.Permissions, policy.WorkspaceRoot, policy.WorkspaceRestricted, autoApproveTools: false);
                PublishToolSettings();
            }

            await RevokeAuthorizationAsync(serverUri, userName, ownerToken);
            ownerToken = string.Empty;
            secret = string.Empty;
            EndOperation(cancellation);
            PostToUi(new { type = "ended", side = "remote" });
        }
    }

    private static DateTimeOffset GetRandomCredentialExpiry(TimeSpan rotation, DateTimeOffset now) =>
        rotation == TimeSpan.Zero ? now.AddDays(30) : now.Add(rotation);

    private static async Task RevokeAuthorizationAsync(Uri serverUri, string userName, string ownerToken)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.PostAsJsonAsync(
                new Uri(serverUri, "v1/authorizations/" + Uri.EscapeDataString(userName) + "/revoke"),
                new ConnectionAuthorizationRevocation(ownerToken),
                ProtocolJson.Options,
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            Trace.TraceWarning("Connection authorization revoke failed after shutdown: {0}.", exception.GetType().Name);
        }
    }

    // ── 主控端：密码认证与加密连接 ───────────────────────────

    private void BeginJoinSession(JsonElement root)
    {
        var identity = ReadString(root, "identity").Trim();
        var userName = ReadString(root, "userName");
        var password = ReadString(root, "secret");

        if (string.IsNullOrWhiteSpace(identity))
        {
            PostPhase("local", "idle");
            PostStatus("local", "请填写你的名称。", "warn");
            return;
        }

        try
        {
            userName = ConnectionAuthorizationCredential.NormalizeUserName(userName);
        }
        catch (ArgumentException exception)
        {
            PostPhase("local", "idle");
            PostStatus("local", exception.Message, "warn");
            return;
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            PostPhase("local", "idle");
            PostStatus("local", "请填写远程密码。", "warn");
            return;
        }

        Uri serverUri;
        try
        {
            serverUri = ValidateServerUri(ReadString(root, "relay"));
        }
        catch (ArgumentException exception)
        {
            PostPhase("local", "idle");
            PostStatus("local", exception.Message, "warn");
            return;
        }

        var initialStatus = _connectionStateMachine.GetSnapshot();
        if (initialStatus.ConnectionState == LocalConnectionState.Connected)
        {
            PostPhase("local", "idle");
            PostStatus("local", "当前已有已认证会话。", "warn");
            return;
        }

        if (!TryBeginOperation("local", out var cancellation))
        {
            return;
        }

        var resumable = initialStatus.ConnectionState is LocalConnectionState.Connecting
            or LocalConnectionState.AwaitingAuthentication
            or LocalConnectionState.Authenticating;

        _currentAuthRequestId = resumable && initialStatus.AuthRequestId is { } pendingRequestId
            ? pendingRequestId
            : _connectionStateMachine.BeginAuthentication().Status.AuthRequestId;

        _ = RunJoinSessionAsync(serverUri, identity, userName, password, cancellation);
    }

    private async Task RunJoinSessionAsync(
        Uri serverUri,
        string identity,
        string userName,
        string password,
        CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        PostStatus("local", "正在检查连接授权并连接…");

        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var connection = await RelayAuthorizationClient.ConnectLocalAsync(
                httpClient, serverUri, userName, password, token, _currentAuthRequestId);
            var invitation = connection.Invitation;

            await using var channel = await SecurePeerChannel.ConnectLocalAsync(
                connection.Socket,
                invitation.SessionId,
                connection.Secret,
                identity,
                ApproveRemotePermissionsAsync,
                token);

            if (_currentAuthRequestId is { } authRequestId &&
                !_connectionStateMachine.TrySetConnected(
                    authRequestId,
                    channel.SessionId,
                    channel.RemoteIdentity,
                    "Encrypted VPS Relay",
                    channel.ApprovedPermissions,
                    channel.SessionExpiresAt))
            {
                throw new InvalidOperationException("The local connection state changed before authentication completed.");
            }

            password = string.Empty;
            connection.ClearSecret();
            PostToUi(new
            {
                type = "connected",
                side = "local",
                peer = channel.RemoteIdentity,
                granted = ToJsPermissions(channel.ApprovedPermissions),
                expiresAt = channel.SessionExpiresAt
            });
            PostStatus(
                "local",
                "主控端已进行连接；当前权限：" + FormatPermissions(channel.ApprovedPermissions) + "。本机 MCP 连接服务已就绪。",
                "ok");

            await using var broker = new LocalSessionBroker(channel, token, LocalSessionBroker.PipeNameForProcess(Environment.ProcessId));
            broker.PolicyChanged += policy =>
            {
                _connectionStateMachine.ApplyRemotePolicy(channel.SessionId, policy);
                PostToUi(new { type = "remote-policy", granted = ToJsPermissions(policy.Permissions),
                    workspace = policy.WorkspaceRoot, workspaceRestricted = policy.WorkspaceRestricted });
            };
            await broker.RunAsync();
            _connectionStateMachine.TrySetDisconnected(channel.SessionId);
            PostStatus("local", "会话已断开，可点击“重新连接”。随机密码已刷新时，请使用被控端当前显示的新密码。", "warn");
        }
        catch (OperationCanceledException)
        {
            if (_connectionStateMachine.GetSnapshot().ConnectionState != LocalConnectionState.Closed)
            {
                _connectionStateMachine.TrySetDisconnected();
            }

            PostStatus("local", "会话已停止。");
        }
        catch (Exception exception)
        {
            if (_currentAuthRequestId is { } authRequestId)
            {
                var failure = ClassifyConnectionFailure(exception);
                _connectionStateMachine.TrySetFaulted(authRequestId, failure.Stage, failure.Code,
                    exception.Message, failure.Retryable, retryAfterMilliseconds: failure.Retryable ? 1_000 : 0);
            }
            PostStatus("local", "操作失败。", "warn");
            PostError("local", exception.Message);
        }
        finally
        {
            password = string.Empty;
            _currentAuthRequestId = null;
            EndOperation(cancellation);
            PostToUi(new { type = "ended", side = "local" });
        }
    }

    private Task<RemotePermission> ApproveRemotePermissionsAsync(
        RemotePeerAnnouncement announcement,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (announcement.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("本次连接授权已过期。");
        }

        if (_currentAuthRequestId is { } authRequestId)
        {
            _connectionStateMachine.TrySetPhase(authRequestId, LocalConnectionState.Authenticating);
        }
        // The remote user owns permissions; password authentication needs no controller consent dialog.
        return Task.FromResult(announcement.RequestedPermissions);
    }

    private async Task<ToolApprovalDecision> ApproveRemoteToolAsync(
        RemoteToolApprovalRequest request,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<ToolApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        void ShowApproval()
        {
            if (IsDisposed || cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                using var dialog = new RemoteToolApprovalDialog(request);
                _ = dialog.Handle;
                using var registration = cancellationToken.Register(() =>
                {
                    try { dialog.BeginInvoke((Action)dialog.CancelForExpiredRequest); }
                    catch (InvalidOperationException) { }
                });

                dialog.ShowDialog(this);
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                }
                else
                {
                    completion.TrySetResult(dialog.Decision);
                }
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        if (InvokeRequired)
        {
            BeginInvoke((Action)ShowApproval);
        }
        else
        {
            ShowApproval();
        }

        return await completion.Task.ConfigureAwait(false);
    }

    private void SelectRemoteWorkspace()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择允许远程 AI 文件工具访问的工作区",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = _remoteWorkspaceRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try { _remoteWorkspaceRoot = RemoteAccessController.NormalizeRoot(dialog.SelectedPath) ?? throw new DirectoryNotFoundException("请选择有效目录。"); }
        catch (IOException exception) { MessageBox.Show(this, exception.Message, "工作区不可用"); return; }
        SaveRemoteWorkspace(_remoteWorkspaceRoot);
        _remoteAccess.Update(_remoteAccess.Snapshot.Permissions, _remoteWorkspaceRoot,
            _remoteAccess.Snapshot.WorkspaceRestricted, _remoteAccess.Snapshot.AutoApproveTools);
        PublishToolSettings();
        PostToUi(new { type = "toast", text = "远程工作区已切换，旧工作区中的运行操作已取消。" });
    }

    private async Task InstallRemoteToolAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "安装 LongShell 工具清单",
            Filter = "LongShell 工具清单 (*.json)|*.json",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            var manifest = await _toolStore.InstallAsync(dialog.FileName, _serviceLifetime.Token);
            _remoteAccess.Refresh();
            PublishToolSettings();
            PostToUi(new { type = "toast", text = "已安装工具 user." + manifest.Id + "，当前会话立即可用。" });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or JsonException or InvalidOperationException)
        {
            MessageBox.Show(this, exception.Message, "工具安装失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void PublishToolSettings()
    {
        var names = _toolStore.LoadInstalled().Select(tool => tool.Descriptor.Name).ToArray();
        PostToUi(new
        {
            type = "tool-settings",
            workspace = _remoteWorkspaceRoot,
            installedTools = names,
            workspaceRestricted = _remoteAccess.Snapshot.WorkspaceRestricted,
            autoApproveTools = _remoteAccess.Snapshot.AutoApproveTools,
            permissions = ToJsPermissions(_remoteAccess.Snapshot.Permissions)
        });
    }

    private void UpdateRemoteAccess(JsonElement root)
    {
        var permissions = ReadPermissions(root);
        if ((permissions & RemotePermission.CommandExecute) != 0) permissions |= RemotePermission.CommandCancel;
        else permissions &= ~RemotePermission.CommandCancel;
        var restricted = !root.TryGetProperty("workspaceRestricted", out var value) || value.ValueKind != JsonValueKind.False;
        var autoApproveTools = root.TryGetProperty("autoApproveTools", out var autoApproveValue)
            ? autoApproveValue.ValueKind == JsonValueKind.True
            : _remoteAccess.Snapshot.AutoApproveTools;
        if (autoApproveTools && !_remoteAccess.Snapshot.AutoApproveTools && !ConfirmAutomaticToolApprovals())
        {
            PublishToolSettings();
            return;
        }

        _remoteAccess.Update(permissions, _remoteWorkspaceRoot, restricted, autoApproveTools);
        PublishToolSettings();
    }

    private bool ConfirmAutomaticToolApprovals() => MessageBox.Show(
        this,
        "开启后，远程 AI 对文件读取、修改和已安装工具的调用将不再逐项询问。" +
        "这些操作仍受当前连接权限和工作区限制，但可能读取、改写或删除工作区内容，也可能执行已安装工具声明的操作。\n\n" +
        "若关闭工作区限制，工具和命令可按当前 Windows 用户权限访问本机文件。\n\n" +
        "只在你信任主控端和所用 AI 时开启。此设置会立即生效，并持续到关闭连接授权或退出 LongShell。\n\n" +
        "确定开启自动批准吗？",
        "自动批准风险确认",
        MessageBoxButtons.YesNo,
        MessageBoxIcon.Warning,
        MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LongShell",
        "settings.json");

    private static string? LoadRemoteWorkspace()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(SettingsPath));
            if (!document.RootElement.TryGetProperty("remoteWorkspace", out var value) ||
                value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var path = value.GetString();
            return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? Path.GetFullPath(path) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static void SaveRemoteWorkspace(string path)
    {
        var settingsPath = SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(
            settingsPath,
            JsonSerializer.Serialize(new { remoteWorkspace = path }, ProtocolJson.Options),
            new UTF8Encoding(false));
    }

    // ── 停止 / 复制 / 服务 ────────────────────────────────────

    private async Task StopFromUiAsync(string role)
    {
        try
        {
            if (string.Equals(role, "remote", StringComparison.Ordinal))
            {
                _operationCancellation?.Cancel();
                return;
            }

            try
            {
                await _connectionService.CloseFromUserAsync(CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException)
            {
                _connectionStateMachine.Close();
            }

            _operationCancellation?.Cancel();
        }
        catch (Exception exception)
        {
            PostStatus(
                string.Equals(role, "remote", StringComparison.Ordinal) ? "remote" : "local",
                "停止失败：" + exception.Message,
                "warn");
        }
    }

    private void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
            PostToUi(new { type = "toast", text = "已复制" });
        }
        catch (ThreadStateException exception)
        {
            Trace.TraceWarning("Clipboard copy failed because the UI thread is not STA: {0}", exception.Message);
            PostToUi(new { type = "toast", text = "当前窗口无法访问系统剪贴板，请手动选中复制" });
        }
        catch (ExternalException exception)
        {
            Trace.TraceWarning("Clipboard copy failed: {0}", exception.Message);
            PostToUi(new { type = "toast", text = "系统剪贴板暂不可用，请手动选中复制" });
        }
    }

    private async Task RunLocalConnectionServiceAsync()
    {
        try
        {
            await _connectionPipeServer.RunAsync(_serviceLifetime.Token);
        }
        catch (OperationCanceledException) when (_serviceLifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _connectionStateMachine.TrySetFaulted(
                null,
                "ipc",
                "connection_service_failed",
                "The local connection service stopped unexpectedly.",
                true);
            PostStatus("local", "本地连接服务启动失败：" + exception.Message, "warn");
        }
        finally
        {
            PostToUi(new { type = "service-presence", connected = false });
        }
    }

    private void RefreshMcpPresence()
    {
        var connected = _connectionService.HasActiveMcpClient();
        if (connected == _lastMcpPresence)
        {
            return;
        }

        _lastMcpPresence = connected;
        PostToUi(new { type = "mcp-presence", connected });
    }

    private void OnAuthenticationRequested(Guid authRequestId, string? userName)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(new Action(() =>
            {
                _currentAuthRequestId = authRequestId;
                PostToUi(new { type = "auth-requested", userName });
            }));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void OnConnectionStateChanged(LocalConnectionStatus status)
    {
        if (status.LastError is { } error)
        {
            Trace.TraceWarning(
                "LongShell connection state={0} requestId={1} stage={2} code={3} retryable={4}",
                status.ConnectionState,
                error.RequestId ?? status.AuthRequestId,
                error.Stage,
                error.Code,
                error.Retryable);
        }

        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        var (text, kind) = status.ConnectionState switch
        {
            LocalConnectionState.ServiceUnavailable => ("本地连接服务不可用。", "warn"),
            LocalConnectionState.Disconnected => ("尚未加入会话。", "normal"),
            LocalConnectionState.Connecting => ("正在连接 Relay…", "normal"),
            LocalConnectionState.AwaitingAuthentication => ("请填写被控端用户名和密码。", "normal"),
            LocalConnectionState.Authenticating => ("身份已验证，正在建立加密通道…", "normal"),
            LocalConnectionState.Connected =>
                ("已连接：" + status.RemoteIdentity + "；权限：" + FormatPermissions(status.GrantedPermissions), "ok"),
            LocalConnectionState.Faulted =>
                ("连接失败：" + (status.LastError?.Message ?? "未知错误"), "warn"),
            LocalConnectionState.Closed => ("会话已关闭。", "normal"),
            LocalConnectionState.Expired => ("会话已过期。", "warn"),
            _ => (status.ConnectionState.ToString(), "normal")
        };

        PostStatus("local", text, kind);
    }

    // ── 通用 ─────────────────────────────────────────────────

    private bool TryBeginOperation(string side, out CancellationTokenSource cancellation)
    {
        cancellation = null!;
        if (_operationCancellation is not null)
        {
            PostPhase(side, "idle");
            PostStatus(side, "当前已有会话正在运行。", "warn");
            return false;
        }

        cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        return true;
    }

    private void EndOperation(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(_operationCancellation, cancellation))
        {
            _operationCancellation = null;
        }

        cancellation.Dispose();
    }

    private static string PreferredIdentity()
    {
        var userName = Environment.UserName;
        return string.IsNullOrWhiteSpace(userName) ? string.Empty : userName.Trim();
    }

    private static string ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static RemotePermission ReadPermissions(JsonElement root, string propertyName = "permissions")
    {
        var permissions = RemotePermission.None;
        if (!root.TryGetProperty(propertyName, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return permissions;
        }

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            permissions |= item.GetString() switch
            {
                "session" => RemotePermission.SessionRead,
                "execute" => RemotePermission.CommandExecute,
                "cancel" => RemotePermission.CommandCancel,
                _ => RemotePermission.None
            };
        }

        return permissions;
    }

    private static string[] ToJsPermissions(RemotePermission permissions)
    {
        var names = new List<string>(3);
        if ((permissions & RemotePermission.SessionRead) != 0) names.Add("session");
        if ((permissions & RemotePermission.CommandExecute) != 0) names.Add("execute");
        if ((permissions & RemotePermission.CommandCancel) != 0) names.Add("cancel");
        return names.ToArray();
    }

    private static Uri ValidateServerUri(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("Relay 地址必须是 HTTPS；仅允许对本机回环地址使用 HTTP，并且不能包含账号、查询参数或片段。");
        }

        var valueWithSlash = uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? uri.AbsoluteUri : uri.AbsoluteUri + "/";
        return new Uri(valueWithSlash, UriKind.Absolute);
    }

    private static Uri CreateRelaySocketUri(Uri serverUri, Guid sessionId, string role)
    {
        return new UriBuilder(new Uri(serverUri, "v1/relay/" + sessionId.ToString("D")))
        {
            Scheme = serverUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Query = "role=" + role
        }.Uri;
    }

    private static (string Stage, string Code, bool Retryable) ClassifyConnectionFailure(Exception exception)
    {
        if (exception is TimeoutException timeout)
        {
            const string duringMarker = "during ";
            const string afterMarker = " after ";
            var start = timeout.Message.IndexOf(duringMarker, StringComparison.Ordinal);
            var end = start >= 0 ? timeout.Message.IndexOf(afterMarker, start + duringMarker.Length, StringComparison.Ordinal) : -1;
            if (start >= 0 && end > start)
            {
                return (timeout.Message[(start + duringMarker.Length)..end], "stage_timeout", true);
            }

            if (timeout.Message.Contains("WebSocket", StringComparison.OrdinalIgnoreCase))
            {
                return ("relay_connect", "relay_timeout", true);
            }

            return ("relay_metadata", "relay_timeout", true);
        }

        return exception switch
        {
            RelayAuthorizationException authorization => ("relay_metadata", authorization.Code, authorization.Retryable),
            RelayWebSocketException relay => ("relay_connect", "relay_http_" + (int)relay.StatusCode, relay.Retryable),
            ConnectionCredentialException credential => ("credentials", credential.Code, false),
            AuthenticationException => ("authentication", "authentication_failed", false),
            InvalidDataException or JsonException => ("protocol_validation", "invalid_remote_message", false),
            HttpRequestException => ("relay_metadata", "relay_unavailable", true),
            WebSocketException => ("relay_connect", "relay_unavailable", true),
            IOException => ("transport", "connection_lost", true),
            _ => ("authentication", "authentication_failed", false)
        };
    }

    private static string FormatPermissions(RemotePermission permissions)
    {
        var names = new List<string>();
        if ((permissions & RemotePermission.SessionRead) != 0) names.Add("查看会话");
        if ((permissions & RemotePermission.CommandExecute) != 0) names.Add("执行命令");
        if ((permissions & RemotePermission.CommandCancel) != 0) names.Add("取消命令");
        return names.Count == 0 ? "无" : string.Join("、", names);
    }
}
