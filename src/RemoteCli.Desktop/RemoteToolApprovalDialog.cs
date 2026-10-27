using RemoteCli.Remote;

namespace RemoteCli.Desktop;

internal sealed class RemoteToolApprovalDialog : Form
{
    public RemoteToolApprovalDialog(RemoteToolApprovalRequest request)
    {
        Text = "LongShell 工具权限审批";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(620, 330);

        var title = new Label
        {
            Text = "远程 AI 请求调用工具：" + request.Name,
            Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold),
            AutoSize = false,
            Location = new Point(20, 18),
            Size = new Size(580, 28)
        };
        var description = new Label
        {
            Text = request.Description,
            AutoSize = false,
            Location = new Point(20, 55),
            Size = new Size(580, 56)
        };
        var permission = new Label
        {
            Text = "请求权限：" + request.PermissionDescription,
            AutoSize = false,
            Location = new Point(20, 122),
            Size = new Size(580, 56)
        };
        var note = new Label
        {
            Text = "队列中的请求会逐项显示。请求取消或超时后，审批窗口会自动关闭。",
            AutoSize = false,
            Location = new Point(20, 200),
            Size = new Size(580, 38),
            ForeColor = SystemColors.GrayText
        };

        var deny = new Button { Text = "拒绝", Size = new Size(92, 34), DialogResult = DialogResult.Cancel };
        var allowOnce = new Button { Text = "仅本次允许", Size = new Size(112, 34) };
        var allowSession = new Button { Text = "同类权限本次会话允许", Size = new Size(172, 34) };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Location = new Point(18, 273),
            Size = new Size(584, 42)
        };
        buttons.Controls.Add(deny);
        buttons.Controls.Add(allowSession);
        buttons.Controls.Add(allowOnce);

        Controls.Add(title);
        Controls.Add(description);
        Controls.Add(permission);
        Controls.Add(note);
        Controls.Add(buttons);
        CancelButton = deny;

        deny.Click += (_, _) =>
        {
            Decision = ToolApprovalDecision.Deny;
            DialogResult = DialogResult.Cancel;
        };
        allowOnce.Click += (_, _) =>
        {
            Decision = ToolApprovalDecision.AllowOnce;
            DialogResult = DialogResult.OK;
        };
        allowSession.Click += (_, _) =>
        {
            Decision = ToolApprovalDecision.AllowForSession;
            DialogResult = DialogResult.OK;
        };
        FormClosing += (_, _) =>
        {
            if (DialogResult == DialogResult.None)
            {
                Decision = ToolApprovalDecision.Deny;
            }
        };
    }

    public ToolApprovalDecision Decision { get; private set; } = ToolApprovalDecision.Deny;

    public void CancelForExpiredRequest()
    {
        if (IsDisposed)
        {
            return;
        }

        Decision = ToolApprovalDecision.Deny;
        DialogResult = DialogResult.Cancel;
        Close();
    }
}
