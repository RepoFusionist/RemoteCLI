using System.Collections.Concurrent;
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
/// WebView2 hosts the Remote CLI page; connection logic stays in this class and is driven
/// by JSON messages from the page. The page only sees the connection authorization it created, and
/// the remote user owns live access policy.
/// </summary>
internal sealed partial class MainForm : Form
{
    private const string DefaultServer = "https://api.sunxiaochuan258.de5.net/remotecli/";
    private const string UiResourceName = "RemoteCli.Desktop.UI.longshell-ui.html";
    private const string IconResourceName = "RemoteCli.Desktop.Assets.longshell-icon.ico";

    private readonly LocalConnectionStateMachine _connectionStateMachine = new();
    private readonly CancellationTokenSource _serviceLifetime = new();
    private readonly Queue<string> _pendingUiMessages = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _gatewayRequests = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _gatewayApprovals = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ToolApprovalDecision>> _remoteToolApprovals = new();
    private readonly WebView2 _webView;
    private readonly System.Windows.Forms.Timer _mcpPresenceTimer = new() { Interval = 1_000 };
    private readonly InstalledRemoteToolStore _toolStore = new();
    private readonly RemoteAccessController _remoteAccess = new();
    private readonly GatewayTerminalPool _gatewayTerminalPool;

    private LocalConnectionServiceCoordinator _connectionService = null!;
    private LocalConnectionServicePipeServer _connectionPipeServer = null!;
    private CancellationTokenSource? _operationCancellation;
    private Guid? _currentAuthRequestId;
    private bool _uiReady;
    private bool _lastMcpPresence;
    private bool _approveControllerModelRequests;
    private int _controllerModelRequestActive;
    private Func<string, CancellationToken, Task<string>>? _controllerModelRequestHandler;
    private LocalSessionBroker? _localControllerModelBroker;
    private RemotePermission? _remoteSessionApprovedPermissions;
    private string? _remoteWorkspaceRoot;
    private string? _codexCliExecutablePath;

    public MainForm()
    {
        _gatewayTerminalPool = new GatewayTerminalPool(workspace => new GatewayCommandSession(workspace, ApproveGatewayCommandAsync,
            (input, token) => ApproveGatewayActionAsync("批准命令输入", "网关模型请求将以下内容发送给正在运行的本机命令。", input, token)));
        _remoteWorkspaceRoot = LoadRemoteWorkspace();
        _codexCliExecutablePath = LoadCodexCliExecutablePath();
        try { _remoteAccess.Update(RemotePermission.SessionRead, _remoteWorkspaceRoot, true); }
        catch (IOException) { _remoteWorkspaceRoot = null; }
        using (var iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(IconResourceName))
        {
            if (iconStream is not null)
            {
                Icon = new System.Drawing.Icon(iconStream);
            }
        }

        Text = "Remote CLI";
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
            Trace.TraceWarning("Remote CLI IPC request failed requestId={0} type={1} code={2}", requestId, type, code);
        _mcpPresenceTimer.Tick += (_, _) => RefreshMcpPresence();

        Shown += async (_, _) => await InitializeUiAsync();
        FormClosing += (_, _) =>
        {
            _mcpPresenceTimer.Stop();
            _serviceLifetime.Cancel();
            try { _gatewayTerminalPool.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(6)); }
            catch (AggregateException) { }
            _operationCancellation?.Cancel();
            _connectionStateMachine.Close();
        };
    }

    // ── 界面宿主 ───────────────────────────────────────────────

    private async Task InitializeUiAsync()
    {
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RemoteCLI",
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
                    "界面组件（WebView2 Runtime）不可用，Remote CLI 无法启动。\n\n" + exception.Message +
                    "\n\n首次尝试：" + firstFailure.Message,
                    "Remote CLI",
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
        return UiAssets.Inject(reader.ReadToEnd());
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
                case "controller-model-request":
                    _ = RequestControllerModelAsync(root);
                    break;
                case "local-controller-model-request":
                    _ = RequestLocalControllerModelAsync(root);
                    break;
                case "codex-cli-detect":
                    AutoFindCodexCliExecutable();
                    break;
                case "codex-cli-browse":
                    SelectCodexCliExecutable();
                    break;
                case "codex-cli-save":
                    SaveCodexCliExecutable(ReadString(root, "path"));
                    break;
                case "stop":
                    _ = StopFromUiAsync(ReadString(root, "role"));
                    break;
                case "copy":
                    CopyToClipboard(ReadString(root, "text"));
                    break;
                case "save-state":
                    SaveUiState(root);
                    break;
                case "checkpoint-preview":
                    _ = PreviewWorkspaceCheckpointAsync(root.Clone());
                    break;
                case "checkpoint-restore":
                    _ = RestoreWorkspaceCheckpointAsync(root.Clone());
                    break;
                case "select-workspace-folder":
                    SelectWorkspaceFolder();
                    break;
                case "open-workspace":
                    OpenWorkspace(ReadString(root, "path"), ReadString(root, "mode"));
                    break;
                case "cancel-model-request":
                    CancelModelRequest(ReadString(root, "requestId"));
                    break;
                case "discover-models":
                    _ = DiscoverModelsAsync(root);
                    break;
                case "list-workspace-files":
                    ListWorkspaceFiles(root);
                    break;
                case "read-workspace-file":
                    var fileRequest = root.Clone();
                    _ = Task.Run(() => ReadWorkspaceFile(fileRequest));
                    break;
                case "gateway-approval-response":
                    ResolveGatewayApproval(root);
                    break;
                case "approval-response":
                    ResolveRemoteToolApproval(root);
                    break;
                case "gateway-chat-request":
                    _ = RequestGatewayChatAsync(root);
                    break;
                case "generate-conversation-title":
                    _ = GenerateConversationTitleAsync(root);
                    break;
                case "gateway-image-select":
                    _ = SelectGatewayImagesAsync();
                    break;
                case "gateway-session-close":
                    _ = _gatewayTerminalPool.CloseAsync(ReadString(root, "conversationId"));
                    break;
                case "pty-watch":
                case "pty-user-input":
                case "pty-resize":
                case "pty-user-stop":
                    _ = HandleGatewayTerminalAsync(root.Clone());
                    break;
                case "select-workspace":
                    SelectRemoteWorkspace();
                    break;
                case "sync-remote-workspace":
                    SyncRemoteWorkspace(ReadString(root, "path"));
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
            conversationFile = UiStatePath,
            version = Application.ProductVersion
        });
        PublishToolSettings();
        PublishCodexCliStatus();
        PublishUiState();

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
                var relayRequest = RelayInvitationCompatibility.ForRelayRegistration(request);
                var registration = new ConnectionAuthorizationRegistration(userName, ownerToken, relayRequest);

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
                        var errorCode = await ReadRelayErrorCodeAsync(response, token);
                        if (string.Equals(errorCode, "username_in_use", StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(
                                "Relay 拒绝连接授权（HTTP " + (int)response.StatusCode + "：此用户名已由另一个 Remote CLI 实例占用，请关闭旧实例或更换名称后重试）。");
                        }

                        var errorDetail = errorCode is null ? string.Empty : ": " + errorCode;
                        throw new InvalidOperationException(
                            "Relay 拒绝连接授权（HTTP " + (int)response.StatusCode + errorDetail + "）。");
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
                    _remoteSessionApprovedPermissions = channel.ApprovedPermissions;
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
                    await RemoteCommandSession.RunAsync(channel, token, registry, _remoteAccess, handler =>
                    {
                        Volatile.Write(ref _controllerModelRequestHandler, handler);
                    });
                    Volatile.Write(ref _controllerModelRequestHandler, null);
                    _remoteSessionApprovedPermissions = null;
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
            Volatile.Write(ref _controllerModelRequestHandler, null);
            _remoteSessionApprovedPermissions = null;
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
        _approveControllerModelRequests = root.TryGetProperty("allowControllerModel", out var modelConsent) &&
            modelConsent.ValueKind == JsonValueKind.True;

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
            await using var broker = new LocalSessionBroker(
                channel,
                token,
                LocalSessionBroker.PipeNameForProcess(Environment.ProcessId),
                new CodexExecModelProvider(() => Volatile.Read(ref _codexCliExecutablePath)));
            Volatile.Write(ref _localControllerModelBroker, broker);
            broker.PolicyChanged += policy =>
            {
                _connectionStateMachine.ApplyRemotePolicy(channel.SessionId, policy);
                PostToUi(new { type = "remote-policy", granted = ToJsPermissions(policy.Permissions),
                    workspace = policy.WorkspaceRoot, workspaceRestricted = policy.WorkspaceRestricted });
            };

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
            Interlocked.Exchange(ref _localControllerModelBroker, null);
            password = string.Empty;
            _approveControllerModelRequests = false;
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
        var approved = announcement.RequestedPermissions;
        if (!_approveControllerModelRequests)
        {
            approved &= ~RemotePermission.ControllerModelUse;
        }

        return Task.FromResult(approved);
    }

    private async Task RequestControllerModelAsync(JsonElement root)
    {
        var prompt = ReadString(root, "prompt");
        var workspaceRoot = ReadString(root, "workspaceRoot");
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > ProtocolConstants.MaximumControllerModelRequestCharacters)
        {
            PostToUi(new { type = "controller-model-response", success = false, error = "请输入不超过 8,192 个字符的问题。" });
            return;
        }

        var handler = Volatile.Read(ref _controllerModelRequestHandler);
        if (handler is null)
        {
            PostToUi(new { type = "controller-model-response", success = false, error = "当前会话未启用主控端模型请求。" });
            return;
        }

        if (Interlocked.CompareExchange(ref _controllerModelRequestActive, 1, 0) != 0)
        {
            PostToUi(new { type = "controller-model-response", success = false, error = "已有一个模型请求正在处理。" });
            return;
        }

        var requestId = Guid.NewGuid().ToString("D");
        try
        {
            PostToUi(new { type = "controller-model-started", requestId, prompt, cancellable = false, checkpointPending = true });
            var checkpoint = await CaptureWorkspaceCheckpointAsync(workspaceRoot).ConfigureAwait(false);
            PostToUi(new { type = "checkpoint-captured", requestId, checkpointId = checkpoint.Id, checkpointError = checkpoint.Error });
            var response = await handler(prompt, CancellationToken.None).ConfigureAwait(false);
            PostToUi(new { type = "controller-model-response", requestId, success = true, response });
        }
        catch (OperationCanceledException)
        {
            PostToUi(new { type = "controller-model-response", requestId, success = false, error = "模型请求已取消。" });
        }
        catch (UnauthorizedAccessException)
        {
            PostToUi(new { type = "controller-model-response", requestId, success = false, error = "主控端未授权使用 Codex。" });
        }
        catch (TimeoutException)
        {
            PostToUi(new { type = "controller-model-response", requestId, success = false, error = "Codex 请求超时，请稍后重试。" });
        }
        catch (InvalidOperationException exception)
        {
            PostToUi(new { type = "controller-model-response", requestId, success = false, error = FormatControllerModelError(exception.Message) });
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            PostToUi(new { type = "controller-model-response", requestId, success = false, error = "Codex 请求失败，请检查主控端 CLI 和 MCP 配置。" });
        }
        finally
        {
            if (!string.IsNullOrEmpty(requestId)) _gatewayRequests.TryRemove(requestId, out _);
            Interlocked.Exchange(ref _controllerModelRequestActive, 0);
        }
    }

    private void CancelModelRequest(string requestId)
    {
        if (string.IsNullOrEmpty(requestId) || !_gatewayRequests.TryGetValue(requestId, out var cancellation))
        {
            PostToUi(new { type = "model-cancel-rejected", requestId, reason = "not_cancellable" });
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task RequestLocalControllerModelAsync(JsonElement root)
    {
        var prompt = ReadString(root, "prompt");
        var workspaceRoot = ReadString(root, "workspaceRoot");
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > ProtocolConstants.MaximumControllerModelRequestCharacters)
        {
            PostToUi(new { type = "local-controller-model-response", success = false, error = "请输入不超过 8,192 个字符的问题。" });
            return;
        }

        var broker = Volatile.Read(ref _localControllerModelBroker);
        if (broker is null)
        {
            PostToUi(new { type = "local-controller-model-response", success = false, error = "请先建立主控端连接。" });
            return;
        }

        if (Interlocked.CompareExchange(ref _controllerModelRequestActive, 1, 0) != 0)
        {
            PostToUi(new { type = "local-controller-model-response", success = false, error = "已有一个 Codex 请求正在处理。" });
            return;
        }

        var requestId = Guid.NewGuid().ToString("D");
        try
        {
            PostToUi(new { type = "local-controller-model-started", requestId, prompt, cancellable = false, checkpointPending = true });
            var checkpoint = await CaptureWorkspaceCheckpointAsync(workspaceRoot).ConfigureAwait(false);
            PostToUi(new { type = "checkpoint-captured", requestId, checkpointId = checkpoint.Id, checkpointError = checkpoint.Error });
            var result = await broker.GenerateControllerModelForLocalUiAsync(prompt, _serviceLifetime.Token).ConfigureAwait(false);
            PostToUi(new
            {
                type = "local-controller-model-response",
                requestId,
                success = result.Success,
                response = result.Response,
                error = result.Success ? null : FormatControllerModelError(result.ErrorCode)
            });
        }
        catch (OperationCanceledException)
        {
            PostToUi(new { type = "local-controller-model-response", requestId, success = false, error = "Codex 请求已取消。" });
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            PostToUi(new { type = "local-controller-model-response", requestId, success = false, error = "Codex 请求失败，请检查主控端 CLI 登录状态。" });
        }
        finally
        {
            Interlocked.Exchange(ref _controllerModelRequestActive, 0);
        }
    }

    private static string FormatControllerModelError(string? errorCode) => errorCode switch
    {
        "permission_denied" => "被控端已撤回主控端 Codex 权限。",
        "model_request_busy" => "已有一个 Codex 请求正在处理。",
        "model_session_limit" => "本次会话的 Codex 请求次数已达上限。",
        "model_request_cancelled" => "Codex 请求已取消。",
        "codex_cli_not_found" => "未找到 Codex CLI。请在主控端连接页点击“自动查找”或选择 codex.exe。",
        "codex_cli_start_failed" => "找到的文件无法启动。请确认选择的是 Windows 版 Codex CLI 可执行文件。",
        "codex_exec_timeout" => "Codex 请求超时，请稍后重试。",
        "codex_authentication_failed" => "主控端 Codex CLI 登录已失效，请先重新登录。",
        "codex_mcp_unavailable" => "Codex 无法连接已配置的 MCP 服务器，请检查主控端 MCP 配置和服务状态。",
        "codex_sandbox_unavailable" => "Codex 安全隔离无法启动，请检查 Windows 沙箱组件；本次请求已停止。",
        "codex_cli_config_invalid" => "主控端 Codex 配置文件无法解析。请检查 ~/.codex/config.toml 中的无效配置项。",
        "codex_exec_failed" => "Codex 请求失败，请检查主控端 CLI 和 MCP 配置。",
        "codex_empty_response" => "Codex 没有返回文字回复。",
        "codex_response_too_large" => "Codex 回复超出长度限制。",
        "temporary_workspace_unavailable" => "无法创建 Codex 临时工作区。",
        "invalid_model_prompt" => "请输入不超过 8,192 个字符的问题。",
        _ => "Codex 请求失败，请检查主控端 CLI 和 MCP 配置。"
    };

    private void AutoFindCodexCliExecutable()
    {
        var result = CodexCliExecutableLocator.Find();
        if (!result.Found || string.IsNullOrWhiteSpace(result.ExecutablePath))
        {
            var configuredPath = Volatile.Read(ref _codexCliExecutablePath);
            var configuredPathIsValid = CodexCliExecutableLocator.ValidateSelectedPath(configuredPath) is not null;
            var message = configuredPathIsValid
                ? "没有找到新的安装路径；继续保留当前已保存的 Codex CLI 路径。"
                : "自动查找未找到 Codex CLI。请确认已安装 Windows 版 CLI，或浏览选择 codex.exe。";
            PublishCodexCliStatus(message, isError: !configuredPathIsValid);
            return;
        }

        SaveCodexCliExecutable(result.ExecutablePath, $"已自动找到并保存（{result.Source}）。");
    }

    private void SelectCodexCliExecutable()
    {
        var configuredPath = Volatile.Read(ref _codexCliExecutablePath);
        var initialPath = CodexCliExecutableLocator.ValidateSelectedPath(configuredPath) ??
                          CodexCliExecutableLocator.Find().ExecutablePath;
        var initialDirectory = Path.GetDirectoryName(initialPath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(initialDirectory) || !Directory.Exists(initialDirectory))
        {
            initialDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "OpenAI", "Codex", "bin");
        }

        using var dialog = new OpenFileDialog
        {
            Title = "选择 Windows 版 Codex CLI",
            Filter = "Codex CLI (codex.exe)|codex.exe",
            CheckFileExists = true,
            Multiselect = false,
            FileName = "codex.exe",
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : string.Empty
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            SaveCodexCliExecutable(dialog.FileName, "Codex CLI 路径已保存到本机 Remote CLI 设置。");
        }
    }

    private void SaveCodexCliExecutable(string? path, string? successMessage = null)
    {
        var requestedPath = path?.Trim();
        var executablePath = string.IsNullOrWhiteSpace(requestedPath)
            ? null
            : CodexCliExecutableLocator.ValidateSelectedPath(requestedPath);
        if (!string.IsNullOrWhiteSpace(requestedPath) && executablePath is null)
        {
            PublishCodexCliStatus("路径无效或文件不存在。请选择 Windows 版 Codex CLI 的 .exe 文件。", requestedPath, isError: true);
            return;
        }

        try
        {
            PersistCodexCliExecutablePath(executablePath);
            Volatile.Write(ref _codexCliExecutablePath, executablePath);
            var message = successMessage ?? (executablePath is null
                ? "已清除手动路径，后续请求会自动查找 Codex CLI。"
                : "Codex CLI 路径已保存到本机 Remote CLI 设置。");
            PublishCodexCliStatus(message, executablePath ?? string.Empty);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            PublishCodexCliStatus("无法保存 Codex CLI 路径。请检查 Remote CLI 本机设置目录的写入权限。", requestedPath, isError: true);
        }
    }

    private void PublishCodexCliStatus(string? message = null, string? pathOverride = null, bool isError = false)
    {
        var configuredPath = Volatile.Read(ref _codexCliExecutablePath);
        var result = CodexCliExecutableLocator.Find(configuredPath);
        var hasConfiguredPath = !string.IsNullOrWhiteSpace(configuredPath);
        var configuredPathIsValid = !hasConfiguredPath ||
                                    CodexCliExecutableLocator.ValidateSelectedPath(configuredPath) is not null;
        var pathToDisplay = hasConfiguredPath && configuredPathIsValid
            ? configuredPath
            : result.ExecutablePath ?? configuredPath;
        var defaultMessage = !configuredPathIsValid
            ? result.Found
                ? "已保存路径不可用；当前请求会使用自动找到的 Codex CLI。"
                : "已保存路径不可用，且自动查找未找到 Codex CLI。请更新路径或点击自动查找。"
            : result.Found
                ? hasConfiguredPath
                    ? "Codex CLI 路径已就绪。路径只保存在主控端本机。"
                    : $"已自动找到 Codex CLI（{result.Source}）。路径只保存在主控端本机。"
                : "未找到 Codex CLI。请点击自动查找，或浏览选择 codex.exe。";

        PostToUi(new
        {
            type = "codex-cli-status",
            path = pathOverride ?? pathToDisplay ?? string.Empty,
            resolvedPath = result.ExecutablePath,
            found = result.Found,
            source = result.Source,
            isConfigured = hasConfiguredPath,
            message = message ?? defaultMessage,
            isError = isError || !configuredPathIsValid
        });
    }

    private async Task<ToolApprovalDecision> ApproveRemoteToolAsync(
        RemoteToolApprovalRequest request,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<ToolApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var approvalId = request.RequestId.ToString("D");
        if (!_remoteToolApprovals.TryAdd(approvalId, completion))
        {
            return ToolApprovalDecision.Deny;
        }

        PostToUi(new
        {
            type = "approval-request",
            approvalId,
            approvalType = "remote-tool",
            name = request.Name,
            description = request.Description,
            permission = request.PermissionDescription,
            content = TruncateApprovalContent(request.Arguments.GetRawText())
        });

        using var registration = cancellationToken.Register(() =>
        {
            _remoteToolApprovals.TryRemove(approvalId, out _);
            completion.TrySetCanceled(cancellationToken);
            PostToUi(new { type = "approval-cancelled", approvalId });
        });

        try
        {
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _remoteToolApprovals.TryRemove(approvalId, out _);
        }
    }

    private void ResolveRemoteToolApproval(JsonElement root)
    {
        var approvalId = ReadString(root, "approvalId");
        if (string.IsNullOrWhiteSpace(approvalId) || !_remoteToolApprovals.TryRemove(approvalId, out var completion)) return;
        var decision = ReadString(root, "decision").Trim().ToLowerInvariant() switch
        {
            "allow_once" => ToolApprovalDecision.AllowOnce,
            "allow_session" => ToolApprovalDecision.AllowForSession,
            _ => ToolApprovalDecision.Deny
        };
        completion.TrySetResult(decision);
    }

    private static string TruncateApprovalContent(string? content)
    {
        var value = content ?? string.Empty;
        return value.Length > 32768 ? value[..32768] + "\n…（内容已截断）" : value;
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

    private void SyncRemoteWorkspace(string path)
    {
        try
        {
            var normalized = string.IsNullOrWhiteSpace(path) ? null : RemoteAccessController.NormalizeRoot(path)
                ?? throw new DirectoryNotFoundException("请选择有效目录。");
            if (string.Equals(_remoteWorkspaceRoot, normalized, StringComparison.OrdinalIgnoreCase)) return;
            _remoteAccess.Update(_remoteAccess.Snapshot.Permissions, normalized,
                _remoteAccess.Snapshot.WorkspaceRestricted, _remoteAccess.Snapshot.AutoApproveTools);
            _remoteWorkspaceRoot = normalized;
            SaveRemoteWorkspace(normalized ?? "");
            PublishToolSettings();
            PostToUi(new { type = "toast", text = normalized is null ? "已清除远程工作区，旧工作区中的运行操作已取消。" : "远程工作区已同步到当前终端，旧工作区中的运行操作已取消。" });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            PublishToolSettings();
            PostToUi(new { type = "toast", text = "无法同步远程工作区：" + exception.Message });
        }
    }

    private async Task InstallRemoteToolAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "安装 Remote CLI 工具清单",
            Filter = "Remote CLI 工具清单 (*.json)|*.json",
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
        var permissions = _remoteAccess.Snapshot.Permissions;
        if (_remoteSessionApprovedPermissions is { } approved)
        {
            permissions &= approved;
        }
        PostToUi(new
        {
            type = "tool-settings",
            workspace = _remoteWorkspaceRoot,
            installedTools = names,
            workspaceRestricted = _remoteAccess.Snapshot.WorkspaceRestricted,
            autoApproveTools = _remoteAccess.Snapshot.AutoApproveTools,
            permissions = ToJsPermissions(permissions)
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
        "只在你信任主控端和所用 AI 时开启。此设置会立即生效，并持续到关闭连接授权或退出 Remote CLI。\n\n" +
        "确定开启自动批准吗？",
        "自动批准风险确认",
        MessageBoxButtons.YesNo,
        MessageBoxIcon.Warning,
        MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RemoteCLI",
        "settings.json");

    private static string CodexCliSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RemoteCLI",
        "codex-cli.json");

    private static string? LoadCodexCliExecutablePath()
    {
        try
        {
            if (!File.Exists(CodexCliSettingsPath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(CodexCliSettingsPath));
            return document.RootElement.TryGetProperty("executablePath", out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static void PersistCodexCliExecutablePath(string? path)
    {
        var settingsPath = CodexCliSettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(
            settingsPath,
            JsonSerializer.Serialize(new { executablePath = path }, ProtocolJson.Options),
            new UTF8Encoding(false));
    }

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

    // ── 终端：会话持久化与自定义网关 ─────────────────────────

    private static string UiStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RemoteCLI",
        "ui-state.json");

    private void PublishUiState()
    {
        try
        {
            if (!File.Exists(UiStatePath))
            {
                return;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(UiStatePath));
            if (document.RootElement.TryGetProperty("state", out var state))
            {
                PostToUi(new { type = "ui-state", state = state.Clone() });
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Trace.TraceWarning("Remote CLI ui-state load failed: {0}", exception.Message);
        }
    }

    private void OpenWorkspace(string path, string mode)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            PostToUi(new { type = "toast", text = "工作区文件夹不存在或不可访问。" });
            return;
        }

        try
        {
            var useTerminal = string.Equals(mode, "terminal", StringComparison.Ordinal);
            var startInfo = new ProcessStartInfo
            {
                FileName = useTerminal ? "wt.exe" : "explorer.exe",
                Arguments = useTerminal ? "-d \"" + path + "\"" : "\"" + path + "\"",
                UseShellExecute = true
            };
            Process.Start(startInfo);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "\"" + path + "\"", UseShellExecute = true });
            }
            catch (Exception fallbackException) when (fallbackException is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                PostToUi(new { type = "toast", text = "无法打开文件夹。" });
            }
        }
    }

    private void SelectWorkspaceFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择要作为工作区的文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            PostToUi(new { type = "workspace-folder", path = dialog.SelectedPath });
        }
        else
        {
            PostToUi(new { type = "workspace-folder", cancelled = true });
        }
    }

    private void SaveUiState(JsonElement root)
    {        try
        {
            if (!root.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var path = UiStatePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(new { state }, ProtocolJson.Options),
                new UTF8Encoding(false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Trace.TraceWarning("Remote CLI ui-state save failed: {0}", exception.Message);
        }
    }

    // ── 工作区文件预览 ───────────────────────────────────────

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".json", ".jsonc", ".cs", ".csx", ".ts", ".tsx", ".js", ".jsx",
        ".css", ".scss", ".html", ".htm", ".xml", ".yml", ".yaml", ".toml", ".ini", ".cfg", ".conf",
        ".ps1", ".sh", ".bat", ".cmd", ".sql", ".py", ".go", ".rs", ".java", ".kt", ".c", ".h", ".cpp",
        ".hpp", ".sln", ".slnx", ".csproj", ".props", ".targets", ".gitignore", ".editorconfig"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"
    };

    private void ListWorkspaceFiles(JsonElement root)
    {
        var path = ReadString(root, "path");
        var workspaceRoot = ReadString(root, "root");
        var requestId = ReadString(root, "requestId");
        try
        {
            var scopedPath = new RemoteFileScope(workspaceRoot, true).Resolve(path, allowRoot: true);
            if (!Directory.Exists(scopedPath))
            {
                PostToUi(new { type = "workspace-files", path, root = workspaceRoot, requestId, error = "目录不存在。" });
                return;
            }

            var directory = new DirectoryInfo(scopedPath);
            var entries = new List<object>();
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (entries.Count >= 400)
                {
                    break;
                }

                if (entry.Attributes.HasFlag(FileAttributes.Hidden) || entry.Attributes.HasFlag(FileAttributes.System) || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                entries.Add(new
                {
                    name = entry.Name,
                    path = entry.FullName,
                    isDirectory = entry is DirectoryInfo,
                    size = entry is FileInfo file ? file.Length : 0
                });
            }

            entries.Sort((left, right) =>
            {
                dynamic a = left;
                dynamic b = right;
                if (a.isDirectory != b.isDirectory) return a.isDirectory ? -1 : 1;
                return string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
            });

            PostToUi(new { type = "workspace-files", path, root = workspaceRoot, requestId, parent = directory.Parent?.FullName, entries });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or RemoteToolException)
        {
            PostToUi(new { type = "workspace-files", path, root = workspaceRoot, requestId, error = "无法读取目录或目录不在工作区内。" });
        }
    }

    private void ReadWorkspaceFile(JsonElement root)
    {
        var filePath = ReadString(root, "path");
        var workspaceRoot = ReadString(root, "root");
        var requestId = ReadString(root, "requestId");

        void Fail(string error) => PostToUi(new { type = "workspace-file", path = filePath, requestId, error });

        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                Fail("文件不存在。");
                return;
            }

            var fullPath = new RemoteFileScope(workspaceRoot, true).Resolve(filePath);

            var info = new FileInfo(fullPath);
            var extension = info.Extension;
            if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) || extension.Equals(".doc", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) || extension.Equals(".xls", StringComparison.OrdinalIgnoreCase))
            {
                if (info.Length > DocumentPreview.MaxFileBytes) { Fail("文件超过 16 MiB，暂不支持预览。"); return; }
                using var documentStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var documentBytes = DocumentPreview.ReadBounded(documentStream);
                if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    if (!DocumentPreview.IsPdf(documentBytes)) { Fail("PDF 文件无效或已损坏。"); return; }
                    PostToUi(new { type = "workspace-file", path = fullPath, requestId, kind = "pdf", content = Convert.ToBase64String(documentBytes) });
                }
                else
                {
                    DocumentPreview.ValidateOfficeContainer(documentBytes, extension.ToLowerInvariant());
                    var officePreviewEnabled = root.TryGetProperty("officePreviewEnabled", out var officeFlag) && officeFlag.ValueKind == JsonValueKind.True;
                    if (!officePreviewEnabled)
                    {
                        Fail("Office 文档预览默认关闭。请到设置 → 全局工具 → Office 文档预览中开启后再试；未开启时不会下载组件。");
                        return;
                    }
                    var engine = OfficeKitAssets.EnsureInstalledAsync(_serviceLifetime.Token).GetAwaiter().GetResult();
                    var pdf = OfficeKitPreview.ConvertAsync(documentBytes, extension, engine, _serviceLifetime.Token).GetAwaiter().GetResult();
                    DocumentPreview.Workbook? workbook = null;
                    if (extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
                    {
                        try { workbook = DocumentPreview.ReadExcel(documentBytes); }
                        catch (Exception exception) when (exception is IOException or ArgumentException or System.Xml.XmlException or FormatException) { }
                    }
                    PostToUi(new { type = "workspace-file", path = fullPath, requestId, kind = "office", content = Convert.ToBase64String(pdf), workbook });
                }
                return;
            }
            if (ImageExtensions.Contains(extension))
            {
                if (info.Length > 4 * 1024 * 1024)
                {
                    Fail("图片过大，暂不支持预览（上限 4 MiB）。");
                    return;
                }

                var mime = extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png"
                    : extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ? "image/gif"
                    : extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ? "image/webp"
                    : extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ? "image/bmp"
                    : "image/jpeg";
                PostToUi(new
                {
                    type = "workspace-file",
                    path = fullPath,
                    requestId,
                    kind = "image",
                    mime,
                    size = info.Length,
                    content = Convert.ToBase64String(File.ReadAllBytes(fullPath))
                });
                return;
            }

            if (info.Length > 1024 * 1024)
            {
                Fail("文件超过 1 MiB，暂不支持预览。");
                return;
            }

            var bytes = File.ReadAllBytes(fullPath);
            var looksText = TextExtensions.Contains(extension) || Array.IndexOf(bytes, (byte)0) < 0;
            if (!looksText)
            {
                PostToUi(new { type = "workspace-file", path = fullPath, requestId, kind = "binary", size = info.Length });
                return;
            }

            PostToUi(new
            {
                type = "workspace-file",
                path = fullPath,
                requestId,
                kind = "text",
                size = info.Length,
                content = Encoding.UTF8.GetString(bytes)
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or RemoteToolException or System.Xml.XmlException or FormatException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            Fail(exception is OperationCanceledException ? "文件预览已取消或超时。" : exception is OfficeKitUnavailableException ? exception.Message : "无法读取或转换文件，请确认格式有效且未受密码保护。");
        }
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
                "Remote CLI connection state={0} requestId={1} stage={2} code={3} retryable={4}",
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
                "controller-model" => RemotePermission.ControllerModelUse,
                _ => RemotePermission.None
            };
        }

        return permissions;
    }

    private static string[] ToJsPermissions(RemotePermission permissions)
    {
        var names = new List<string>(4);
        if ((permissions & RemotePermission.SessionRead) != 0) names.Add("session");
        if ((permissions & RemotePermission.CommandExecute) != 0) names.Add("execute");
        if ((permissions & RemotePermission.CommandCancel) != 0) names.Add("cancel");
        if ((permissions & RemotePermission.ControllerModelUse) != 0) names.Add("controller-model");
        return names.ToArray();
    }

    private static async Task<string?> ReadRelayErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!document.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var value = error.GetString();
            return !string.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
                   value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
                ? value
                : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return null;
        }
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
        if ((permissions & RemotePermission.ControllerModelUse) != 0) names.Add("调用主控端 Codex");
        return names.Count == 0 ? "无" : string.Join("、", names);
    }
}
