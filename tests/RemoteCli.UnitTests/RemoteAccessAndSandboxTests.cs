using System.Text;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using Common.Protocol;
using RemoteCli.Remote;

namespace RemoteCli.UnitTests;

public sealed class RemoteAccessAndSandboxTests
{
    private static readonly RemotePermission All = RemotePermission.SessionRead | RemotePermission.CommandExecute | RemotePermission.CommandCancel;

    [Fact]
    public void AutomaticApprovalSettingStaysLocalAndDoesNotChangeProtocolOnePayloads()
    {
        var policy = new RemoteSessionPolicy(2, All, "C:\\Workspace", true, AutoApproveTools: true);
        var json = JsonSerializer.SerializeToElement(policy, ProtocolJson.Options);

        Assert.False(json.TryGetProperty("autoApproveTools", out _));
        var received = JsonSerializer.Deserialize<RemoteSessionPolicy>(json.GetRawText(), ProtocolJson.Options);
        Assert.NotNull(received);
        Assert.False(received.AutoApproveTools);
    }

    [Fact]
    public async Task DynamicWorkspaceSwitchRevokesOldLeasesAndChangesTheActualFileTarget()
    {
        using var fixture = new Workspaces();
        using var access = new RemoteAccessController(All, fixture.First, true);
        var registry = new DynamicRemoteToolRegistry(access, policy => RemoteToolRegistry.CreateForDesktop(policy.WorkspaceRoot, [],
            (_, _) => Task.FromResult(ToolApprovalDecision.AllowOnce), policy.WorkspaceRestricted, policy.AutoApproveTools));
        var old = access.Acquire();
        var create = JsonSerializer.SerializeToElement(new { path = "same.txt", content = "first" });
        await registry.ExecuteAsync("remote.file_create", create, CancellationToken.None);
        access.Update(All, fixture.Second, true);
        Assert.True(old.Revoked.IsCancellationRequested);
        await registry.ExecuteAsync("remote.file_create", create, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(fixture.First, "same.txt")));
        Assert.True(File.Exists(Path.Combine(fixture.Second, "same.txt")));
        access.Update(RemotePermission.SessionRead, fixture.Second, true);
        var error = await Assert.ThrowsAsync<RemoteToolException>(() => registry.ExecuteAsync("remote.file_create", create, CancellationToken.None));
        Assert.Equal("permission_denied", error.Code);
    }

    [Fact]
    public void AutomaticApprovalChangesRevokeOldLeasesAndDefaultUpdatesPreserveTheSetting()
    {
        using var fixture = new Workspaces();
        using var access = new RemoteAccessController(All, fixture.First, true);
        var beforeEnable = access.Acquire();

        access.Update(All, fixture.First, true, autoApproveTools: true);
        Assert.True(beforeEnable.Revoked.IsCancellationRequested);
        Assert.True(access.Snapshot.AutoApproveTools);

        var beforePermissionChange = access.Acquire();
        access.Update(RemotePermission.SessionRead, fixture.First, true);
        Assert.True(beforePermissionChange.Revoked.IsCancellationRequested);
        Assert.True(access.Snapshot.AutoApproveTools);

        access.Update(RemotePermission.SessionRead, fixture.First, true, autoApproveTools: false);
        Assert.False(access.Snapshot.AutoApproveTools);
    }

    [Fact]
    public async Task OutsideFileAccessRequiresRemoteUserToDisableRestriction()
    {
        using var fixture = new Workspaces();
        var target = Path.Combine(fixture.Second, "outside.txt");
        await File.WriteAllTextAsync(target, "outside", new UTF8Encoding(false));
        var args = JsonSerializer.SerializeToElement(new { path = target });
        var restricted = RemoteToolRegistry.CreateForDesktop(fixture.First, [], (_, _) => Task.FromResult(ToolApprovalDecision.AllowOnce));
        var error = await Assert.ThrowsAsync<RemoteToolException>(() => restricted.ExecuteAsync("remote.file_read", args, CancellationToken.None));
        Assert.Equal("path_outside_workspace", error.Code);
        var unrestricted = RemoteToolRegistry.CreateForDesktop(fixture.First, [], (_, _) => Task.FromResult(ToolApprovalDecision.AllowOnce), false);
        Assert.Equal("outside", (await unrestricted.ExecuteAsync("remote.file_read", args, CancellationToken.None)).GetProperty("content").GetString());
    }

    [Fact]
    public void PreviewFileScopeAllowsWorkspaceRootAndRejectsOutsidePaths()
    {
        using var fixture = new Workspaces();
        var scope = new RemoteFileScope(fixture.First, true);
        Assert.Equal(Path.GetFullPath(fixture.First), scope.Resolve(fixture.First, allowRoot: true));
        Assert.Equal(Path.Combine(fixture.First, "notes.txt"), scope.Resolve(Path.Combine(fixture.First, "notes.txt")));
        var outside = Assert.Throws<RemoteToolException>(() => scope.Resolve(Path.Combine(fixture.Second, "secret.txt")));
        Assert.Equal("path_outside_workspace", outside.Code);
    }

    [Fact]
    public async Task BuiltinAgentToolsSearchMoveEditAndDeleteRemoteFixtures()
    {
        using var fixture = new Workspaces();
        var registry = RemoteToolRegistry.CreateForDesktop(fixture.First, [], (_, _) => Task.FromResult(ToolApprovalDecision.AllowForSession));
        async Task<JsonElement> Call(string name, object args) => await registry.ExecuteAsync(name, JsonSerializer.SerializeToElement(args), CancellationToken.None);
        await Call("remote.directory_create", new { path = "notes" });
        await Call("remote.file_create", new { path = "notes/one.txt", content = "find me" });
        var result = await Call("remote.text_search", new { path = ".", pattern = "find me" });
        Assert.Single(result.GetProperty("matches").EnumerateArray());
        await Call("remote.file_copy", new { source = "notes/one.txt", destination = "two.txt" });
        await Call("remote.file_move", new { source = "two.txt", destination = "three.txt" });
        Assert.False(File.Exists(Path.Combine(fixture.First, "two.txt")));
        await Call("remote.file_delete", new { path = "three.txt" });
        Assert.False(File.Exists(Path.Combine(fixture.First, "three.txt")));
        var hash = (await Call("remote.file_read", new { path = "notes/one.txt" })).GetProperty("sha256").GetString();
        await Call("remote.file_write", new { path = "notes/one.txt", content = "updated", expectedSha256 = hash });
        Assert.Equal("updated", await File.ReadAllTextAsync(Path.Combine(fixture.First, "notes", "one.txt")));
    }

    [Fact]
    public async Task RestrictedCommandsCanWriteWorkspaceButCannotReadOrWriteOutsideIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Workspaces();
        var outside = Path.Combine(fixture.Second, "secret.txt");
        await File.WriteAllTextAsync(outside, "PRIVATE-FIXTURE", new UTF8Encoding(false));
        var outsideFile = new FileInfo(outside);
        var outsideAcl = outsideFile.GetAccessControl();
        outsideAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-15-2-1"), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        outsideFile.SetAccessControl(outsideAcl);
        var output = new StringBuilder();
        var escaped = outside.Replace("'", "''");
        var code = "$ErrorActionPreference='Stop'; Set-Content -LiteralPath './allowed.txt' -Value 'inside'; " +
            "try { [IO.File]::ReadAllText('" + escaped + "') | Out-Null; throw 'ESCAPED-READ' } catch [UnauthorizedAccessException] { 'READ-BLOCKED' }; " +
            "try { [IO.File]::WriteAllText('" + escaped + "','bad'); throw 'ESCAPED-WRITE' } catch [UnauthorizedAccessException] { 'WRITE-BLOCKED' }";
        var result = await new PowerShellCommandRunner().ExecuteAsync(code, fixture.First, TimeSpan.FromSeconds(20),
            (_, text) => { lock (output) output.Append(text); return Task.CompletedTask; }, CancellationToken.None,
            new RemoteSessionPolicy(1, All, fixture.First, true));
        Assert.True(result.State == RemoteCommandState.Succeeded, $"{result.ErrorCode}: {output}");
        Assert.Contains("READ-BLOCKED", output.ToString());
        Assert.Contains("WRITE-BLOCKED", output.ToString());
        Assert.Equal("PRIVATE-FIXTURE", await File.ReadAllTextAsync(outside));
        Assert.True(File.Exists(Path.Combine(fixture.First, "allowed.txt")));
    }

    [Fact]
    public async Task RevokingExecutionKillsTheSandboxAndDiscardsItsUncommittedChanges()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Workspaces();
        await File.WriteAllTextAsync(Path.Combine(fixture.First, "original.txt"), "original", new UTF8Encoding(false));
        using var access = new RemoteAccessController(All, fixture.First, true);
        var lease = access.Acquire();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new PowerShellCommandRunner().ExecuteAsync(
            "Set-Content -LiteralPath './original.txt' -Value 'changed'; 'READY-TO-REVOKE'; Start-Sleep -Seconds 30",
            fixture.First, TimeSpan.FromSeconds(40), (_, text) => { if (text.Contains("READY-TO-REVOKE", StringComparison.Ordinal)) started.TrySetResult(); return Task.CompletedTask; },
            lease.Revoked, lease.Policy);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        access.Update(RemotePermission.SessionRead, fixture.Second, true);
        var result = await command.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RemoteCommandState.Cancelled, result.State);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(fixture.First, "original.txt")));
    }

    [Fact]
    public async Task RestrictedCommandsAcceptScriptsLargerThanTheWindowsCommandLineLimit()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Workspaces();
        var output = new StringBuilder();
        var script = "#" + new string('x', 40000) + "\n'LONG-INPUT-OK'";
        var result = await new PowerShellCommandRunner().ExecuteAsync(script, fixture.First, TimeSpan.FromSeconds(15),
            (_, text) => { lock (output) output.Append(text); return Task.CompletedTask; }, CancellationToken.None,
            new RemoteSessionPolicy(1, All, fixture.First, true));
        Assert.True(result.State == RemoteCommandState.Succeeded, result.ErrorCode + ": " + output);
        Assert.Contains("LONG-INPUT-OK", output.ToString());
    }

    private sealed class Workspaces : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "longshell-policy-" + Guid.NewGuid().ToString("N"));
        public string First => Path.Combine(_root, "first");
        public string Second => Path.Combine(_root, "second");
        public Workspaces() { Directory.CreateDirectory(First); Directory.CreateDirectory(Second); }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
