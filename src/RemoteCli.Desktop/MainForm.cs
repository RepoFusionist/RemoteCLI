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
    private const string OfficePreviewPluginId = "office-preview";

    private readonly LocalConnectionStateMachine _connectionStateMachine = new();
    private readonly CancellationTokenSource _serviceLifetime = new();
    private readonly Queue<string> _pendingUiMessages = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _gatewayRequests = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _gatewayApprovals = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _relayFingerprintApprovals = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ToolApprovalDecision>> _remoteToolApprovals = new();
    private readonly WebView2 _webView;
    private readonly System.Windows.Forms.Timer _mcpPresenceTimer = new() { Interval = 1_000 };
    private readonly InstalledRemoteToolStore _toolStore = new();
    private readonly RemoteAccessController _remoteAccess = new();
    private readonly GatewayTerminalPool _gatewayTerminalPool;
    private readonly CodexExecModelProvider _codexModelProvider;

    private LocalConnectionServiceCoordinator _connectionService = null!;
    private LocalConnectionServicePipeServer _connectionPipeServer = null!;
    private CancellationTokenSource? _operationCancellation;
    private Guid? _currentAuthRequestId;
    private bool _uiReady;
    private bool _lastMcpPresence;
    private bool _approveControllerModelRequests;
    private int _controllerModelRequestActive;
    private Func<ControllerModelInput, CancellationToken, Task<string>>? _controllerModelRequestHandler;
    private LocalSessionBroker? _localControllerModelBroker;
    private RemotePermission? _remoteSessionApprovedPermissions;
    private string? _remoteWorkspaceRoot;
    private string? _codexCliExecutablePath;
    private bool _pluginOperationBusy;
    private CancellationTokenSource? _pluginInstallCancellation;
    private string? _pluginInstallCancellationId;

    public MainForm()
    {
        _gatewayTerminalPool = new GatewayTerminalPool(workspace => new GatewayCommandSession(workspace, ApproveGatewayCommandAsync,
            (input, token) => ApproveGatewayActionAsync("批准命令输入", "网关模型请求将以下内容发送给正在运行的本机命令。", input, token)));
        _remoteWorkspaceRoot = LoadRemoteWorkspace();
        _codexCliExecutablePath = LoadCodexCliExecutablePath();
        _codexModelProvider = new CodexExecModelProvider(() => Volatile.Read(ref _codexCliExecutablePath));
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
        try
        {
            foreach (var script in UiPluginPackages.LoadInstalledScripts())
            {
                await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            Trace.TraceWarning("Remote CLI UI plugins could not be registered: {0}", exception.Message);
        }

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
                case "controller-model-catalog-request":
                    _ = PublishControllerModelCatalogForGrantAsync();
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
                case "worktree-preview":
                    _ = PreviewWorkspaceWorktreeAsync(root.Clone());
                    break;
                case "worktree-restore":
                    _ = RestoreWorkspaceWorktreeAsync(root.Clone());
                    break;
                case "select-workspace-folder":
                    SelectWorkspaceFolder();
                    break;
                case "select-token-training-folder":
                    SelectTokenTrainingFolder();
                    break;
                case "open-workspace":
                    OpenWorkspace(ReadString(root, "path"), ReadString(root, "mode"));
                    break;
                case "open-path":
                    OpenPathExternally(ReadString(root, "path"), ReadString(root, "root"), ReadString(root, "mode"), ReadString(root, "app"));
                    break;
                case "list-open-with":
                    ListOpenWith(root);
                    break;
                case "select-app":
                    SelectOpenWithApplication();
                    break;
                case "path-icons":
                    PostPathIcons(root);
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
                case "read-text-page":
                    var pageRequest = root.Clone();
                    _ = Task.Run(() => ReadTextPage(pageRequest));
                    break;
                case "open-browser":
                    OpenBrowserWindow(ReadString(root, "url"));
                    break;
                case "gateway-approval-response":
                    ResolveGatewayApproval(root);
                    break;
                case "approval-response":
                    ResolveRemoteToolApproval(root);
                    break;
                case "relay-fingerprint-response":
                    ResolveRelayFingerprintApproval(root);
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
                case "install-plugin":
                    _ = InstallPluginAsync(ReadString(root, "id"));
                    break;
                case "install-plugin-file":
                    _ = InstallPluginFromFileAsync(ReadString(root, "id"));
                    break;
                case "uninstall-plugin":
                    _ = UninstallPluginAsync(ReadString(root, "id"));
                    break;
                case "cancel-plugin":
                    CancelPluginInstall(ReadString(root, "id"));
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
        PublishPluginSettings();
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
        var expectedFingerprint = ReadString(root, "expectedFingerprint");
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
            expectedFingerprint = NormalizeExpectedFingerprint(expectedFingerprint);
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
            expectedFingerprint,
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
        string? expectedFingerprint,
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
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(RelayClientIdentity.HeaderName, RelayClientIdentity.HeaderValue);
            await VerifyRelayServerAsync(httpClient, serverUri, expectedFingerprint, token);
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
                        static () =>
                        {
                            var clientSocket = new ClientWebSocket();
                            clientSocket.Options.SetRequestHeader(RelayClientIdentity.HeaderName, RelayClientIdentity.HeaderValue);
                            return clientSocket;
                        },
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
                    }, catalog => PostControllerModelCatalogToUi(catalog));
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
            client.DefaultRequestHeaders.TryAddWithoutValidation(RelayClientIdentity.HeaderName, RelayClientIdentity.HeaderValue);
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
        var expectedFingerprint = ReadString(root, "expectedFingerprint");
        var approveControllerModelRequests = root.TryGetProperty("allowControllerModel", out var modelConsent) &&
            modelConsent.ValueKind == JsonValueKind.True;
        var controllerModelIds = ReadControllerModelIds(root, out var controllerModelPolicySpecified);
        var controllerModelMaxEffort = ReadControllerModelMaxEffort(root, out var controllerModelMaxEffortSpecified);
        if (approveControllerModelRequests && controllerModelPolicySpecified &&
            (controllerModelIds is null || controllerModelIds.Count == 0 ||
             !controllerModelMaxEffortSpecified || controllerModelMaxEffort is null))
        {
            PostPhase("local", "idle");
            PostStatus("local", "请先选择至少一个可用模型和最大思考档位。", "warn");
            return;
        }

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
            expectedFingerprint = NormalizeExpectedFingerprint(expectedFingerprint);
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

        _approveControllerModelRequests = approveControllerModelRequests;

        var resumable = initialStatus.ConnectionState is LocalConnectionState.Connecting
            or LocalConnectionState.AwaitingAuthentication
            or LocalConnectionState.Authenticating;

        _currentAuthRequestId = resumable && initialStatus.AuthRequestId is { } pendingRequestId
            ? pendingRequestId
            : _connectionStateMachine.BeginAuthentication().Status.AuthRequestId;

        _ = RunJoinSessionAsync(
            serverUri,
            identity,
            userName,
            password,
            expectedFingerprint,
            controllerModelIds,
            controllerModelMaxEffort,
            cancellation);
    }

    private async Task RunJoinSessionAsync(
        Uri serverUri,
        string identity,
        string userName,
        string password,
        string? expectedFingerprint,
        HashSet<string>? controllerModelIds,
        string? controllerModelMaxEffort,
        CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        PostStatus("local", "正在检查连接授权并连接…");

        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(RelayClientIdentity.HeaderName, RelayClientIdentity.HeaderValue);
            await VerifyRelayServerAsync(httpClient, serverUri, expectedFingerprint, token);
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
                _codexModelProvider,
                controllerModelIds,
                controllerModelMaxEffort);
            Volatile.Write(ref _localControllerModelBroker, broker);
            broker.PolicyChanged += policy =>
            {
                _connectionStateMachine.ApplyRemotePolicy(channel.SessionId, policy);
                PostToUi(new { type = "remote-policy", granted = ToJsPermissions(policy.Permissions),
                    workspace = policy.WorkspaceRoot, workspaceRestricted = policy.WorkspaceRestricted });
                _ = PublishLocalControllerModelCatalogAsync(policy.Permissions, token);
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

            _ = PublishLocalControllerModelCatalogAsync(channel.ApprovedPermissions, token);

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

    private void PostControllerModelCatalogToUi(ControllerModelCatalogResult catalog, bool forGrant = false)
    {
        PostToUi(new
        {
            type = "controller-model-catalog",
            forGrant,
            success = catalog.Success,
            models = catalog.Models.Select(model => new
            {
                id = model.Id,
                displayName = model.DisplayName,
                reasoningEfforts = model.ReasoningEfforts,
                defaultReasoningEffort = model.DefaultReasoningEffort
            }).ToArray(),
            defaultModel = catalog.DefaultModel,
            defaultReasoningEffort = catalog.DefaultReasoningEffort,
            error = catalog.ErrorCode is null ? null : FormatControllerModelError(catalog.ErrorCode)
        });
    }

    private async Task PublishLocalControllerModelCatalogAsync(
        RemotePermission permissions,
        CancellationToken cancellationToken)
    {
        if ((permissions & RemotePermission.ControllerModelUse) == 0)
        {
            PostToUi(new
            {
                type = "controller-model-catalog",
                success = false,
                models = Array.Empty<object>(),
                defaultModel = (string?)null,
                defaultReasoningEffort = (string?)null,
                error = "当前会话未授权使用主控端 Codex。"
            });
            return;
        }

        try
        {
            var catalog = await _codexModelProvider.GetModelCatalogAsync(cancellationToken).ConfigureAwait(false);
            PostControllerModelCatalogToUi(catalog);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            PostToUi(new
            {
                type = "controller-model-catalog",
                success = false,
                models = Array.Empty<object>(),
                defaultModel = (string?)null,
                defaultReasoningEffort = (string?)null,
                error = FormatControllerModelError("codex_model_catalog_unavailable")
            });
        }
    }

    private async Task PublishControllerModelCatalogForGrantAsync()
    {
        try
        {
            var catalog = await _codexModelProvider.GetModelCatalogAsync(_serviceLifetime.Token).ConfigureAwait(false);
            PostControllerModelCatalogToUi(catalog, forGrant: true);
        }
        catch (OperationCanceledException) when (_serviceLifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            PostToUi(new
            {
                type = "controller-model-catalog",
                forGrant = true,
                success = false,
                models = Array.Empty<object>(),
                defaultModel = (string?)null,
                defaultReasoningEffort = (string?)null,
                error = FormatControllerModelError("codex_model_catalog_unavailable")
            });
        }
    }

    private async Task RequestControllerModelAsync(JsonElement root)
    {
        var prompt = ReadString(root, "prompt");
        var displayPrompt = ReadString(root, "displayPrompt");
        var composerTarget = ReadString(root, "target");
        var composerMode = ComposerModePolicy.Normalize(ReadString(root, "composerMode"), false, composerTarget);
        var model = ReadString(root, "model");
        var reasoningEffort = ReadString(root, "reasoningEffort");
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
            PostToUi(new { type = "controller-model-started", requestId, prompt = string.IsNullOrWhiteSpace(displayPrompt) ? prompt : displayPrompt,
                composerMode, composerTarget, cancellable = false });
            var response = await handler(new ControllerModelInput(
                prompt,
                string.IsNullOrWhiteSpace(model) ? null : model,
                string.IsNullOrWhiteSpace(reasoningEffort) ? null : reasoningEffort), CancellationToken.None).ConfigureAwait(false);
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
        var displayPrompt = ReadString(root, "displayPrompt");
        var composerTarget = ReadString(root, "target");
        var composerMode = ComposerModePolicy.Normalize(ReadString(root, "composerMode"), false, composerTarget);
        var model = ReadString(root, "model");
        var reasoningEffort = ReadString(root, "reasoningEffort");
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
            PostToUi(new { type = "local-controller-model-started", requestId, prompt = string.IsNullOrWhiteSpace(displayPrompt) ? prompt : displayPrompt,
                composerMode, composerTarget, cancellable = false });
            var result = await broker.GenerateControllerModelForLocalUiAsync(
                prompt,
                string.IsNullOrWhiteSpace(model) ? null : model,
                string.IsNullOrWhiteSpace(reasoningEffort) ? null : reasoningEffort,
                _serviceLifetime.Token).ConfigureAwait(false);
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
        "controller_model_selection_not_allowed" => "被控端请求的模型或思考档位不在本次授权范围内。",
        "codex_mcp_unavailable" => "Codex 无法连接已配置的 MCP 服务器，请检查主控端 MCP 配置和服务状态。",
        "codex_sandbox_unavailable" => "Codex 安全隔离无法启动，请检查 Windows 沙箱组件；本次请求已停止。",
        "codex_cli_config_invalid" => "主控端 Codex 配置文件无法解析。请检查 ~/.codex/config.toml 中的无效配置项。",
        "invalid_model_selection" => "所选主控端模型或思考档位不可用，请重新打开模型选择窗口。",
        "codex_model_catalog_unavailable" => "无法读取主控端 Codex 模型列表，将使用主控端当前默认模型。",
        "codex_model_catalog_timeout" => "获取主控端模型列表超时，将使用主控端当前默认模型。",
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
                ? "没有找到新的安装路径；继续保留当前已保存的本机主控端 Codex CLI 路径。"
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
            SaveCodexCliExecutable(dialog.FileName, "本机主控端 Codex CLI 路径已保存。");
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
                : "本机主控端 Codex CLI 路径已保存。");
            PublishCodexCliStatus(message, executablePath ?? string.Empty);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            PublishCodexCliStatus("无法保存本机主控端 Codex CLI 路径。请检查 Remote CLI 本机设置目录的写入权限。", requestedPath, isError: true);
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
                    ? "本机主控端 Codex CLI 路径已就绪。路径只保存在主控端本机。"
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

    private void ResolveRelayFingerprintApproval(JsonElement root)
    {
        var requestId = ReadString(root, "requestId");
        if (string.IsNullOrWhiteSpace(requestId) || !_relayFingerprintApprovals.TryRemove(requestId, out var completion))
        {
            return;
        }

        var allow = root.TryGetProperty("allow", out var value) && value.ValueKind == JsonValueKind.True;
        completion.TrySetResult(allow);
    }

    private async Task VerifyRelayServerAsync(
        HttpClient client,
        Uri serverUri,
        string? expectedFingerprint,
        CancellationToken cancellationToken)
    {
        var health = await RelayServerIdentityClient.ReadAsync(client, serverUri, cancellationToken).ConfigureAwait(false);
        var savedFingerprint = LoadRelayFingerprint(serverUri);
        var knownFingerprint = !string.IsNullOrWhiteSpace(expectedFingerprint) ? expectedFingerprint : savedFingerprint;
        var matchesConfiguredFingerprint = string.Equals(knownFingerprint, health.ServerFingerprintSha256, StringComparison.OrdinalIgnoreCase);
        var matchesOfficialFingerprint = RelayServerIdentityClient.IsOfficialServerFingerprint(health.ServerFingerprintSha256);
        if (matchesConfiguredFingerprint || matchesOfficialFingerprint)
        {
            return;
        }

        var requestId = Guid.NewGuid().ToString("D");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_relayFingerprintApprovals.TryAdd(requestId, completion))
        {
            throw new AuthenticationException("服务端指纹校验请求冲突，请稍后重试。");
        }

        PostToUi(new
        {
            type = "relay-fingerprint-warning",
            requestId,
            relay = serverUri.AbsoluteUri,
            fingerprint = health.ServerFingerprintSha256,
            previousFingerprint = knownFingerprint,
            mismatch = !string.IsNullOrWhiteSpace(knownFingerprint),
            nonOfficial = true
        });

        using var registration = cancellationToken.Register(() =>
        {
            _relayFingerprintApprovals.TryRemove(requestId, out _);
            completion.TrySetCanceled(cancellationToken);
        });

        bool allow;
        try
        {
            allow = await completion.Task.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new AuthenticationException("等待用户确认服务端 SHA-256 超时，连接已取消。");
        }
        finally
        {
            _relayFingerprintApprovals.TryRemove(requestId, out _);
        }

        if (!allow)
        {
            throw new AuthenticationException("用户未确认服务端 SHA-256，连接已取消。");
        }

        SaveRelayFingerprint(serverUri, health.ServerFingerprintSha256);
    }

    private static string? LoadRelayFingerprint(Uri serverUri)
    {
        try
        {
            var path = RelayFingerprintPath();
            if (!File.Exists(path)) return null;
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path, Encoding.UTF8), ProtocolJson.Options);
            return values is not null && values.TryGetValue(serverUri.AbsoluteUri, out var fingerprint) &&
                   RelayServerIdentityClient.IsSha256(fingerprint)
                ? fingerprint.ToUpperInvariant()
                : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Relay fingerprint cache could not be read: {0}", exception.GetType().Name);
            return null;
        }
    }

    private static void SaveRelayFingerprint(Uri serverUri, string fingerprint)
    {
        try
        {
            var path = RelayFingerprintPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (File.Exists(path))
            {
                values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path, Encoding.UTF8), ProtocolJson.Options)
                    ?? values;
            }

            values[serverUri.AbsoluteUri] = fingerprint.ToUpperInvariant();
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, JsonSerializer.Serialize(values, ProtocolJson.Options), new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Relay fingerprint cache could not be saved: {0}", exception.GetType().Name);
        }
    }

    private static string RelayFingerprintPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RemoteCLI",
        "relay-fingerprints.json");

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

    private void PublishPluginSettings()
        => PostToUi(new
        {
            type = "plugin-settings",
            plugins = new object[]
            {
                new { id = OfficePreviewPluginId, installed = OfficeKitAssets.IsInstalled() },
                new { id = UiPluginPackages.CodeHighlightId, installed = UiPluginPackages.IsInstalled(UiPluginPackages.CodeHighlightId) }
            }
        });

    private void CancelPluginInstall(string id)
    {
        if (string.Equals(id, _pluginInstallCancellationId, StringComparison.Ordinal))
        {
            _pluginInstallCancellation?.Cancel();
        }
    }

    private async Task InstallPluginAsync(string id)
    {
        if (id == OfficePreviewPluginId)
        {
            await InstallOfficePluginAsync(id);
            return;
        }
        if (id == UiPluginPackages.CodeHighlightId)
        {
            await InstallUiPluginAsync(id, packagePath: null);
        }
    }

    private async Task InstallOfficePluginAsync(string id)
    {
        if (_pluginOperationBusy) return;
        _pluginOperationBusy = true;
        var reportProgress = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_serviceLifetime.Token);
        _pluginInstallCancellation = cancellation;
        _pluginInstallCancellationId = id;
        try
        {
            PostPluginStatus(id, "installing", OfficeKitInstallPhase.Prepare);
            var progress = new Progress<OfficeKitInstallProgress>(value =>
            {
                if (reportProgress) PostPluginStatus(id, "installing", value.Phase, value.Percent);
            });
            await Task.Run(() => OfficeKitAssets.EnsureInstalledAsync(cancellation.Token, progress: progress));
            PublishPluginSettings();
            PostToUi(new { type = "toast", text = "Office 预览插件已安装。" });
        }
        catch (OperationCanceledException)
        {
            var cancelledByUser = cancellation.IsCancellationRequested && !_serviceLifetime.Token.IsCancellationRequested;
            OfficeKitAssets.CleanupResiduals();
            PublishPluginSettings();
            if (cancelledByUser) PostToUi(new { type = "toast", text = "已取消安装，残留文件已清理。" });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
            InvalidOperationException or JsonException or HttpRequestException)
        {
            OfficeKitAssets.CleanupResiduals();
            PublishPluginSettings();
            PostPluginStatus(id, "failed", null, null, exception.Message);
            PostToUi(new { type = "toast", text = "Office 预览插件安装失败：" + exception.Message });
        }
        finally
        {
            reportProgress = false;
            if (ReferenceEquals(_pluginInstallCancellation, cancellation))
            {
                _pluginInstallCancellation = null;
                _pluginInstallCancellationId = null;
            }
            _pluginOperationBusy = false;
        }
    }

    private async Task InstallPluginFromFileAsync(string id)
    {
        if (id != UiPluginPackages.CodeHighlightId || _pluginOperationBusy) return;
        using var dialog = new OpenFileDialog
        {
            Title = "安装 Remote CLI 插件",
            Filter = "Remote CLI 插件包 (*.lspkg)|*.lspkg|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await InstallUiPluginAsync(id, dialog.FileName);
    }

    private async Task InstallUiPluginAsync(string id, string? packagePath)
    {
        if (_pluginOperationBusy) return;
        if (packagePath is null && UiPluginPackages.IsInstalled(id))
        {
            PublishPluginSettings();
            return;
        }

        _pluginOperationBusy = true;
        var reportProgress = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_serviceLifetime.Token);
        _pluginInstallCancellation = cancellation;
        _pluginInstallCancellationId = id;
        try
        {
            PostPluginStatus(id, "installing", packagePath is null ? OfficeKitInstallPhase.Download : OfficeKitInstallPhase.Prepare,
                packagePath is null ? 0 : null);
            var progress = new Progress<OfficeKitInstallProgress>(value =>
            {
                if (reportProgress) PostPluginStatus(id, "installing", value.Phase, value.Percent);
            });
            if (packagePath is null)
            {
                await Task.Run(() => UiPluginPackages.InstallFromUrlAsync(id, progress, cancellation.Token), cancellation.Token);
            }
            else
            {
                await Task.Run(() => UiPluginPackages.InstallFromFileAsync(packagePath, id, progress, cancellation.Token), cancellation.Token);
            }

            var script = await Task.Run(() => UiPluginPackages.LoadEntryScript(id), cancellation.Token);
            var activated = false;
            try
            {
                if (_webView.CoreWebView2 is { } core)
                {
                    await core.ExecuteScriptAsync(script);
                    activated = true;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                Trace.TraceWarning("Remote CLI UI plugin script injection failed: {0}", exception.Message);
            }

            PublishPluginSettings();
            PostToUi(new
            {
                type = "toast",
                text = activated ? "代码高亮插件已安装，对话中的代码块会按语言着色。" : "代码高亮插件已安装，重新打开应用后生效。"
            });
        }
        catch (OperationCanceledException)
        {
            var cancelledByUser = cancellation.IsCancellationRequested && !_serviceLifetime.Token.IsCancellationRequested;
            UiPluginPackages.CleanupResiduals();
            PublishPluginSettings();
            if (cancelledByUser) PostToUi(new { type = "toast", text = "已取消安装，残留文件已清理。" });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
            InvalidOperationException or JsonException or HttpRequestException or System.Runtime.InteropServices.COMException)
        {
            UiPluginPackages.CleanupResiduals();
            PublishPluginSettings();
            PostPluginStatus(id, "failed", null, null, exception.Message);
            PostToUi(new { type = "toast", text = "代码高亮插件安装失败：" + exception.Message });
        }
        finally
        {
            reportProgress = false;
            if (ReferenceEquals(_pluginInstallCancellation, cancellation))
            {
                _pluginInstallCancellation = null;
                _pluginInstallCancellationId = null;
            }
            _pluginOperationBusy = false;
        }
    }

    private async Task UninstallPluginAsync(string id)
    {
        if (id == OfficePreviewPluginId)
        {
            await UninstallOfficePluginAsync(id);
            return;
        }
        if (id == UiPluginPackages.CodeHighlightId)
        {
            await UninstallUiPluginAsync(id);
        }
    }

    private async Task UninstallOfficePluginAsync(string id)
    {
        if (_pluginOperationBusy) return;
        _pluginOperationBusy = true;
        try
        {
            PostPluginStatus(id, "uninstalling");
            await Task.Run(() => OfficeKitAssets.UninstallAsync(_serviceLifetime.Token));
            PublishPluginSettings();
            PostToUi(new { type = "toast", text = "Office 预览插件已卸载。" });
        }
        catch (OperationCanceledException)
        {
            PublishPluginSettings();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            PublishPluginSettings();
            PostPluginStatus(id, "failed", null, null, exception.Message);
            PostToUi(new { type = "toast", text = "Office 预览插件卸载失败：" + exception.Message });
        }
        finally
        {
            _pluginOperationBusy = false;
        }
    }

    private async Task UninstallUiPluginAsync(string id)
    {
        if (_pluginOperationBusy) return;
        _pluginOperationBusy = true;
        try
        {
            PostPluginStatus(id, "uninstalling");
            await Task.Run(() => UiPluginPackages.UninstallAsync(id, _serviceLifetime.Token));
            PublishPluginSettings();
            PostToUi(new { type = "toast", text = "代码高亮插件已卸载。" });
        }
        catch (OperationCanceledException)
        {
            PublishPluginSettings();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            PublishPluginSettings();
            PostPluginStatus(id, "failed", null, null, exception.Message);
            PostToUi(new { type = "toast", text = "代码高亮插件卸载失败：" + exception.Message });
        }
        finally
        {
            _pluginOperationBusy = false;
        }
    }

    private void OpenBrowserWindow(string url)
    {
        var normalized = BrowserAddress.Normalize(url);
        if (!string.IsNullOrWhiteSpace(url) && normalized is null)
        {
            PostToUi(new { type = "toast", text = "内置浏览器只支持 http/https 地址。" });
            return;
        }
        var browser = new BrowserForm(normalized);
        browser.Show(this);
    }

    private void PostPluginStatus(string id, string state, OfficeKitInstallPhase? phase = null, int? percent = null, string? text = null)
        => PostToUi(new
        {
            type = "plugin-status",
            id,
            state,
            phase = phase switch
            {
                OfficeKitInstallPhase.Prepare => "prepare",
                OfficeKitInstallPhase.Download => "download",
                OfficeKitInstallPhase.Extract => "extract",
                _ => null
            },
            percent,
            text
        });

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

    private const int MaxIconRequests = 16;

    /// <summary>按前端给的 key 返回文件或程序图标，供右键菜单显示；拿不到图标时该 key 返回 null。</summary>
    private void PostPathIcons(JsonElement root)
    {
        var requestId = ReadString(root, "requestId");
        var icons = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            var handled = 0;
            foreach (var item in items.EnumerateArray())
            {
                if (handled++ >= MaxIconRequests) break;
                if (item.ValueKind != JsonValueKind.Object) continue;
                var key = ReadString(item, "key");
                if (string.IsNullOrWhiteSpace(key) || icons.ContainsKey(key)) continue;
                var kind = ReadString(item, "kind");
                var value = ReadString(item, "value");
                var target = string.Equals(kind, "app", StringComparison.Ordinal) ? PathIcons.ResolveApplication(value) : value;
                icons[key] = PathIcons.TryGetDataUri(target);
            }
        }

        PostToUi(new { type = "path-icons", requestId, icons });
    }

    /// <summary>
    /// 用系统程序打开工作区内的路径。mode: explorer / terminal / code / reveal / default / notepad / openas / app。
    /// app 模式使用调用方指定的可执行文件，并记住这次选择。
    /// 与 OpenWorkspace 不同，这里会复核工作区范围，避免前端传入任意路径。
    /// </summary>
    private void OpenPathExternally(string path, string root, string mode, string app)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            PostToUi(new { type = "toast", text = "没有可打开的文件或目录。" });
            return;
        }

        string fullPath;
        try
        {
            fullPath = string.IsNullOrWhiteSpace(root)
                ? Path.GetFullPath(path)
                : new RemoteFileScope(root, true).Resolve(path, allowRoot: true);
        }
        catch (Exception exception) when (exception is ArgumentException or RemoteToolException or NotSupportedException or PathTooLongException)
        {
            PostToUi(new { type = "toast", text = "路径不在当前工作区内，已阻止打开。" });
            return;
        }

        var isDirectory = Directory.Exists(fullPath);
        if (!isDirectory && !File.Exists(fullPath))
        {
            PostToUi(new { type = "toast", text = "文件或目录不存在。" });
            return;
        }

        var explicitApp = string.Equals(mode, "app", StringComparison.Ordinal);
        if (explicitApp && (string.IsNullOrWhiteSpace(app) || !File.Exists(app)))
        {
            PostToUi(new { type = "open-path-failed", path = fullPath, message = "找不到所选程序，可能已经被卸载或移动。" });
            return;
        }

        try
        {
            if (explicitApp)
            {
                Process.Start(new ProcessStartInfo { FileName = app, Arguments = "\"" + fullPath + "\"", UseShellExecute = true });
                OpenWithCatalog.Remember(fullPath, app);
            }
            else
            {
                var startInfo = ExternalPathLauncher.Build(fullPath, isDirectory, mode);
                if (string.Equals(mode, "code", StringComparison.Ordinal))
                {
                    // VS Code 可能装在别的盘，优先用注册表登记的真实路径。
                    var codePath = PathIcons.ResolveApplication("code");
                    if (codePath is not null) startInfo.FileName = codePath;
                }
                Process.Start(startInfo);
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Trace.TraceWarning("Remote CLI open path '{0}' with mode '{1}' failed: {2}", fullPath, mode, exception.Message);
            if (string.Equals(mode, "terminal", StringComparison.Ordinal))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = "\"" + (isDirectory ? fullPath : Path.GetDirectoryName(fullPath)) + "\"",
                        UseShellExecute = true
                    });
                    return;
                }
                catch (Exception fallbackException) when (fallbackException is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                }
            }

            var message = explicitApp ? "无法用所选程序打开这个文件。"
                : string.Equals(mode, "default", StringComparison.Ordinal) ? "系统里没有登记可以打开这个文件的程序。可以在“打开方式”里选择其他应用。"
                : string.Equals(mode, "code", StringComparison.Ordinal) ? "未找到 Visual Studio Code，请先安装或加入 PATH。"
                : string.Equals(mode, "terminal", StringComparison.Ordinal) ? "无法打开终端。"
                : "无法用系统程序打开该文件。";

            // 文件打开类失败显示在预览中心，不弹系统错误对话框。
            if (explicitApp || string.Equals(mode, "default", StringComparison.Ordinal))
                PostToUi(new { type = "open-path-failed", path = fullPath, message });
            else
                PostToUi(new { type = "toast", text = message });
        }
    }

    private void ListOpenWith(JsonElement root)
    {
        var path = ReadString(root, "path");
        var workspaceRoot = ReadString(root, "root");
        var requestId = ReadString(root, "requestId");

        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                PostToUi(new { type = "open-with-list", requestId, path, apps = Array.Empty<object>(), error = "没有可打开的文件。" });
                return;
            }

            var fullPath = string.IsNullOrWhiteSpace(workspaceRoot)
                ? Path.GetFullPath(path)
                : new RemoteFileScope(workspaceRoot, true).Resolve(path, allowRoot: true);
            if (!File.Exists(fullPath))
            {
                PostToUi(new { type = "open-with-list", requestId, path = fullPath, apps = Array.Empty<object>(), error = "文件不存在。" });
                return;
            }

            var apps = OpenWithCatalog.List(fullPath)
                .Select(app => new { name = app.Name, path = app.Path, isDefault = app.IsDefault })
                .ToList();
            PostToUi(new { type = "open-with-list", requestId, path = fullPath, apps });
        }
        catch (Exception exception) when (exception is ArgumentException or RemoteToolException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            PostToUi(new { type = "open-with-list", requestId, path, apps = Array.Empty<object>(), error = "无法读取可用的打开方式。" });
        }
    }

    private void SelectOpenWithApplication()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择用来打开这个文件的程序",
            Filter = "程序 (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.FileName))
        {
            PostToUi(new { type = "selected-app", path = dialog.FileName });
        }
        else
        {
            PostToUi(new { type = "selected-app", cancelled = true });
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

    private void SelectTokenTrainingFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择 Token 预测训练数据文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
            PostToUi(new { type = "token-training-folder", path = dialog.SelectedPath });
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
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(new { state }, ProtocolJson.Options), new UTF8Encoding(false));
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
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
            if (string.IsNullOrWhiteSpace(filePath))
            {
                Fail("文件不存在。");
                return;
            }

            var fullPath = new RemoteFileScope(workspaceRoot, true).Resolve(filePath);

            // 目录不是预览目标：交回前端切到文件列表，避免把目录当成缺文件。
            if (Directory.Exists(fullPath))
            {
                PostToUi(new { type = "workspace-file", path = fullPath, requestId, kind = "directory" });
                return;
            }

            if (!File.Exists(fullPath))
            {
                Fail("文件不存在。");
                return;
            }

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
                    if (OfficeKitAssets.GetVerifiedRoot() is not { } engine)
                    {
                        Fail("Office 预览插件未安装或已损坏。请到设置 → 全局工具 → 插件中点击“安装”，安装完成后重新打开文件。");
                        return;
                    }
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

            if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase) || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
            {
                if (info.Length > TextPreviewPages.MaxHtmlBytes)
                {
                    Fail("HTML 文件超过 4 MiB，暂不支持预览。");
                    return;
                }

                var html = TextPreviewPages.Decode(TextPreviewPages.ReadAllBytesShared(fullPath, TextPreviewPages.MaxHtmlBytes)).Text;
                if (html.Contains('\0'))
                {
                    Fail("HTML 文件包含二进制内容，无法预览。");
                    return;
                }

                PostToUi(new
                {
                    type = "workspace-file",
                    path = fullPath,
                    requestId,
                    kind = "html",
                    size = info.Length,
                    version = TextPreviewPages.VersionTag(info),
                    content = html
                });
                return;
            }

            if (info.Length > TextPreviewPages.MaxFileBytes)
            {
                Fail("文件超过 16 MiB，暂不支持预览。");
                return;
            }

            var bytes = TextPreviewPages.ReadAllBytesShared(fullPath, TextPreviewPages.MaxFileBytes);
            string text;
            try
            {
                text = TextPreviewPages.Decode(bytes).Text;
            }
            catch (InvalidDataException)
            {
                if (TextExtensions.Contains(extension))
                {
                    throw;
                }

                PostToUi(new { type = "workspace-file", path = fullPath, requestId, kind = "binary", size = info.Length });
                return;
            }

            if (text.Contains('\0'))
            {
                if (TextExtensions.Contains(extension))
                {
                    Fail("文件包含二进制内容，无法作为文本预览。");
                    return;
                }

                PostToUi(new { type = "workspace-file", path = fullPath, requestId, kind = "binary", size = info.Length });
                return;
            }

            var textLines = TextPreviewPages.SplitLines(text);
            var firstPage = TextPreviewPages.Slice(textLines, 1, TextPreviewPages.DefaultLines);
            PostToUi(new
            {
                type = "workspace-file",
                path = fullPath,
                requestId,
                kind = "text",
                size = info.Length,
                version = TextPreviewPages.VersionTag(info),
                offset = 1,
                lines = firstPage.Count,
                eof = firstPage.Eof,
                content = firstPage.Content
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or RemoteToolException or System.Xml.XmlException or FormatException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception or InvalidDataException)
        {
            Fail(exception switch
            {
                OperationCanceledException => "文件预览已取消或超时。",
                InvalidDataException => exception.Message,
                OfficeKitUnavailableException => exception.Message,
                _ => "无法读取或转换文件，请确认格式有效且未受密码保护。"
            });
        }
    }

    private void ReadTextPage(JsonElement root)
    {
        var filePath = ReadString(root, "path");
        var workspaceRoot = ReadString(root, "root");
        var requestId = ReadString(root, "requestId");
        var offset = Math.Max(1, ReadInt(root, "offset") ?? 1);
        var limit = Math.Clamp(ReadInt(root, "limit") ?? TextPreviewPages.DefaultLines, 1, TextPreviewPages.MaxLines);

        void Fail(string error) => PostToUi(new { type = "text-page", path = filePath, requestId, offset, error });

        try
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                Fail("文件不存在。");
                return;
            }

            var fullPath = new RemoteFileScope(workspaceRoot, true).Resolve(filePath);
            if (!File.Exists(fullPath))
            {
                Fail("文件不存在。");
                return;
            }

            var info = new FileInfo(fullPath);
            if (info.Length > TextPreviewPages.MaxFileBytes)
            {
                Fail("文件超过 16 MiB，暂不支持预览。");
                return;
            }

            var text = TextPreviewPages.Decode(TextPreviewPages.ReadAllBytesShared(fullPath, TextPreviewPages.MaxFileBytes)).Text;
            if (text.Contains('\0'))
            {
                Fail("文件包含二进制内容，无法作为文本预览。");
                return;
            }

            var lines = TextPreviewPages.SplitLines(text);
            var page = TextPreviewPages.Slice(lines, offset, limit);
            PostToUi(new
            {
                type = "text-page",
                path = fullPath,
                requestId,
                offset,
                lines = page.Count,
                content = page.Content,
                eof = page.Eof,
                size = info.Length,
                version = TextPreviewPages.VersionTag(info)
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or RemoteToolException or InvalidDataException)
        {
            Fail(exception is InvalidDataException ? exception.Message : "无法读取文件，请确认文件仍在工作区内。");
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

    private static int? ReadInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static HashSet<string>? ReadControllerModelIds(JsonElement root, out bool specified)
    {
        specified = root.TryGetProperty("controllerModelIds", out var value);
        if (!specified)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var modelId = item.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(modelId) && modelId.Length <= ProtocolConstants.MaximumControllerModelIdCharacters)
            {
                result.Add(modelId);
            }
        }

        return result;
    }

    private static string? ReadControllerModelMaxEffort(JsonElement root, out bool specified)
    {
        specified = root.TryGetProperty("controllerModelMaxEffort", out var value);
        if (!specified || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var effort = value.GetString()?.Trim().ToLowerInvariant();
        return effort is "low" or "medium" or "high" or "xhigh" or "max" or "ultra" ? effort : null;
    }

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

    private static string NormalizeExpectedFingerprint(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length == 0) return string.Empty;
        if (!RelayServerIdentityClient.IsSha256(normalized))
        {
            throw new ArgumentException("服务端 SHA-256 必须是 64 位十六进制字符串。");
        }

        return normalized;
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
