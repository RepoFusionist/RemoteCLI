using System.Text.Json;

namespace RemoteCli.Desktop;

internal sealed partial class MainForm
{
    private Task<bool> ApproveGatewayCommandAsync(string command, CancellationToken token)
        => ApproveGatewayActionAsync("批准本机命令", "网关模型请求运行以下 Windows PowerShell 5.1 命令。它可访问本机文件和网络，工作目录不是沙箱。", command, token);

    private Task<bool> ApproveGatewayActionAsync(string title, string warningText, string content, CancellationToken token)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var approvalId = Guid.NewGuid().ToString("D");
        if (!_gatewayApprovals.TryAdd(approvalId, completion))
        {
            return Task.FromResult(false);
        }

        PostToUi(new
        {
            type = "gateway-approval-request",
            approvalId,
            title,
            description = warningText,
            content = TruncateApprovalContent(content)
        });

        var registration = token.Register(() =>
        {
            _gatewayApprovals.TryRemove(approvalId, out _);
            completion.TrySetCanceled(token);
            PostToUi(new { type = "approval-cancelled", approvalId });
        });
        return AwaitGatewayApprovalAsync(approvalId, completion, registration);
    }

    private async Task<bool> AwaitGatewayApprovalAsync(string approvalId, TaskCompletionSource<bool> completion, CancellationTokenRegistration registration)
    {
        try { return await completion.Task.ConfigureAwait(false); }
        finally
        {
            registration.Dispose();
            _gatewayApprovals.TryRemove(approvalId, out _);
        }
    }

    private void ResolveGatewayApproval(JsonElement root)
    {
        var approvalId = ReadString(root, "approvalId");
        if (string.IsNullOrWhiteSpace(approvalId) || !_gatewayApprovals.TryRemove(approvalId, out var completion)) return;
        var allowed = root.TryGetProperty("allow", out var allow) && allow.ValueKind == JsonValueKind.True;
        completion.TrySetResult(allowed);
    }
}
