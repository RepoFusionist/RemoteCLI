using Common.Protocol;
using Common.Security;

namespace RemoteCli.Desktop;

internal sealed class PermissionApprovalDialog : Form
{
    private readonly CheckBox _sessionRead = new() { Text = "查看会话", AutoSize = true };
    private readonly CheckBox _commandExecute = new() { Text = "执行命令", AutoSize = true };
    private readonly CheckBox _commandCancel = new() { Text = "取消命令", AutoSize = true };

    public PermissionApprovalDialog(RemotePeerAnnouncement announcement)
    {
        Text = "审批远程连接";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(520, 330);
        Font = new Font("Microsoft YaHei UI", 9F);

        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(18),
            AutoScroll = true
        };
        stack.Controls.Add(new Label
        {
            Text = "连接密码验证通过。确认身份和权限后再批准连接。",
            AutoSize = true,
            MaximumSize = new Size(470, 0),
            Margin = new Padding(3, 3, 3, 12)
        });
        stack.Controls.Add(new Label { Text = "被控端身份：" + announcement.RemoteIdentity, AutoSize = true, Margin = new Padding(3, 4, 3, 8) });
        stack.Controls.Add(new Label { Text = "本次认证到期：" + announcement.ExpiresAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), AutoSize = true, Margin = new Padding(3, 4, 3, 8) });
        stack.Controls.Add(new Label { Text = "请选择允许本次会话使用的权限（默认全部关闭）：", AutoSize = true, Margin = new Padding(3, 10, 3, 8) });

        SetRequested(_sessionRead, announcement.RequestedPermissions, RemotePermission.SessionRead);
        SetRequested(_commandExecute, announcement.RequestedPermissions, RemotePermission.CommandExecute);
        SetRequested(_commandCancel, announcement.RequestedPermissions, RemotePermission.CommandCancel);
        stack.Controls.Add(_sessionRead);
        stack.Controls.Add(_commandExecute);
        stack.Controls.Add(_commandCancel);
        stack.Controls.Add(new Label
        {
            Text = "远端命令及其输出可能不可信。只批准你愿意授予的权限。",
            AutoSize = true,
            MaximumSize = new Size(470, 0),
            ForeColor = Color.DarkRed,
            Margin = new Padding(3, 12, 3, 8)
        });

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(3, 10, 3, 3) };
        var approveButton = new Button { Text = "批准所选权限", AutoSize = true, DialogResult = DialogResult.None };
        var rejectButton = new Button { Text = "拒绝", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(approveButton);
        buttons.Controls.Add(rejectButton);
        stack.Controls.Add(buttons);
        Controls.Add(stack);
        AcceptButton = approveButton;
        CancelButton = rejectButton;
        approveButton.Click += (_, _) =>
        {
            if (ApprovedPermissions == RemotePermission.None)
            {
                MessageBox.Show(this, "至少选择一项权限，或点击“拒绝”。", "请选择权限", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DialogResult = DialogResult.OK;
        };
    }

    public RemotePermission ApprovedPermissions
    {
        get
        {
            var permissions = RemotePermission.None;
            if (_sessionRead.Checked) permissions |= RemotePermission.SessionRead;
            if (_commandExecute.Checked) permissions |= RemotePermission.CommandExecute;
            if (_commandCancel.Checked) permissions |= RemotePermission.CommandCancel;
            return permissions;
        }
    }

    private static void SetRequested(CheckBox checkBox, RemotePermission requested, RemotePermission permission)
    {
        checkBox.Enabled = (requested & permission) != 0;
        checkBox.Checked = false;
        if (!checkBox.Enabled)
        {
            checkBox.Text += "（被控端未申请）";
        }
    }
}
