using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace RemoteCli.Desktop;

// A separate WebView2 window: no host object, no web messages, no file/data schemes,
// no permission grants, downloads or popups. Links that need a new window open in the
// system browser instead.
internal sealed class BrowserForm : Form
{
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private readonly TextBox _address = new()
    {
        Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.FixedSingle,
        PlaceholderText = "输入网址，例如 example.com"
    };
    private readonly Button _openButton = new() { Text = "打开", Dock = DockStyle.Fill };
    private readonly Button _backButton = new() { Text = "←", Dock = DockStyle.Fill, Enabled = false };
    private readonly Button _forwardButton = new() { Text = "→", Dock = DockStyle.Fill, Enabled = false };
    private readonly Button _reloadButton = new() { Text = "刷新", Dock = DockStyle.Fill };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly string? _initialUrl;
    private bool _coreReady;

    internal BrowserForm(string? initialUrl = null)
    {
        _initialUrl = initialUrl;
        Text = "Remote CLI 浏览器";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(720, 480);
        Size = new Size(1100, 760);
        BackColor = Color.FromArgb(25, 25, 24);

        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 38,
            ColumnCount = 6,
            Padding = new Padding(6, 4, 6, 4)
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        bar.Controls.Add(_backButton, 0, 0);
        bar.Controls.Add(_forwardButton, 1, 0);
        bar.Controls.Add(_reloadButton, 2, 0);
        bar.Controls.Add(_address, 3, 0);
        bar.Controls.Add(_openButton, 4, 0);
        bar.Controls.Add(_status, 5, 0);
        Controls.Add(_webView);
        Controls.Add(bar);

        _openButton.Click += (_, _) => NavigateFromAddress();
        _address.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                NavigateFromAddress();
            }
        };
        _backButton.Click += (_, _) => { if (_webView.CoreWebView2?.CanGoBack == true) _webView.CoreWebView2.GoBack(); };
        _forwardButton.Click += (_, _) => { if (_webView.CoreWebView2?.CanGoForward == true) _webView.CoreWebView2.GoForward(); };
        _reloadButton.Click += (_, _) => _webView.CoreWebView2?.Reload();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RemoteCLI",
                "Browser");
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _webView.EnsureCoreWebView2Async(environment);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, "内置浏览器无法启动：" + exception.Message, "Remote CLI 浏览器",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
            return;
        }

        var core = _webView.CoreWebView2;
        var settings = core.Settings;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsWebMessageEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, args) =>
        {
            args.Cancel = true;
            _status.Text = "下载已阻止";
        };
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            OpenExternal(args.Uri);
        };
        core.NavigationStarting += (_, args) =>
        {
            if (!BrowserAddress.IsAllowedNavigation(args.Uri)) args.Cancel = true;
        };
        core.NavigationCompleted += (_, args) =>
        {
            _status.Text = args.IsSuccess ? "" : "页面加载失败";
            UpdateNavigation();
        };
        core.SourceChanged += (_, _) => UpdateNavigation();
        core.DocumentTitleChanged += (_, _) =>
            Text = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "Remote CLI 浏览器" : core.DocumentTitle + " - Remote CLI 浏览器";
        _coreReady = true;

        if (BrowserAddress.Normalize(_initialUrl) is { } url)
        {
            Navigate(url);
        }
        else
        {
            _address.Focus();
        }
    }

    private void NavigateFromAddress()
    {
        if (!_coreReady) return;
        if (BrowserAddress.Normalize(_address.Text) is not { } url)
        {
            _status.Text = "只支持 http/https 地址，且不能包含账号密码。";
            return;
        }
        Navigate(url);
    }

    private void Navigate(string url)
    {
        _status.Text = "";
        _address.Text = url;
        _webView.CoreWebView2.Navigate(url);
    }

    private void UpdateNavigation()
    {
        var core = _webView.CoreWebView2;
        if (core is null) return;
        _backButton.Enabled = core.CanGoBack;
        _forwardButton.Enabled = core.CanGoForward;
        if (!string.IsNullOrEmpty(core.Source) && !string.Equals(core.Source, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            _address.Text = core.Source;
        }
    }

    private void OpenExternal(string? url)
    {
        if (BrowserAddress.Normalize(url) is not { } normalized) return;
        try
        {
            Process.Start(new ProcessStartInfo(normalized) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _status.Text = "无法打开外部浏览器：" + exception.Message;
        }
    }
}
