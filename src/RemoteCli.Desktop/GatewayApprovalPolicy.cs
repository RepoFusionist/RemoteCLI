using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace RemoteCli.Desktop;

internal enum GatewayApprovalCategory
{
    WorkspaceWrite,
    Command,
    CommandInput,
    Network,
    Mcp
}

// Tiered auto approval for the gateway chat only. Remote session and MCP server
// approvals keep their own rules and never consult this policy.
internal static class GatewayApprovalPolicy
{
    internal const string Low = "low";
    internal const string Medium = "medium";
    internal const string High = "high";

    internal static string NormalizeTier(string? tier) => tier?.Trim().ToLowerInvariant() switch
    {
        Low => Low,
        High => High,
        _ => Medium
    };

    internal static bool ShouldAutoApprove(GatewayApprovalCategory category, string? tier, bool readOnlyMode, string? content)
    {
        if (readOnlyMode) return false;
        switch (NormalizeTier(tier))
        {
            case Low:
                return false;
            case Medium:
                return category is GatewayApprovalCategory.WorkspaceWrite or GatewayApprovalCategory.Network;
            default:
                return category switch
                {
                    GatewayApprovalCategory.Command => !IsDestructiveCommand(content),
                    GatewayApprovalCategory.CommandInput => false,
                    GatewayApprovalCategory.Mcp => false,
                    _ => true
                };
        }
    }


    internal static LocalChatTool Protect(LocalChatTool tool,
        Func<GatewayApprovalCategory, string, CancellationToken, Task<bool>> approve) => tool.ApprovalCategory is not { } category ? tool : tool with
    {
        Execute = async (arguments, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!await approve(category, arguments.GetRawText(), token).ConfigureAwait(false)) return "错误：用户拒绝执行工具。";
            token.ThrowIfCancellationRequested();
            return await tool.Execute(arguments, token).ConfigureAwait(false);
        }
    };

    // Unknown syntax and indirect execution require approval. Parse with the same 5.1
    // interpreter used by GatewayCommandSession; never execute the supplied script.
    private static readonly ConcurrentDictionary<string, bool> CommandCache = new(StringComparer.Ordinal);
    private const string ParserScript = """
        $ProgressPreference = 'SilentlyContinue'
        $ErrorActionPreference = 'Stop'
        [Console]::InputEncoding = New-Object System.Text.UTF8Encoding($false)
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
        $source = [Console]::In.ReadToEnd()
        $tokens = $null; $errors = $null
        $tree = [System.Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
        $uncertain = $errors.Count -ne 0
        $complex = $tree.FindAll({param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] -or
            $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -or
            $n -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -or
            $n -is [System.Management.Automation.Language.ScriptBlockExpressionAst] -or
            $n -is [System.Management.Automation.Language.RedirectionAst]}, $true)
        if ($complex.Count -gt 0) { $uncertain = $true }
        $commands = @($tree.FindAll({param($n) $n -is [System.Management.Automation.Language.CommandAst]}, $true) | ForEach-Object {
            $name = $_.GetCommandName(); $parts = @(); $dynamic = [string]::IsNullOrEmpty($name)
            if ($_.InvocationOperator.ToString() -eq 'Dot') { $dynamic = $true }
            foreach ($element in $_.CommandElements | Select-Object -Skip 1) {
                if ($element -is [System.Management.Automation.Language.StringConstantExpressionAst]) { $parts += $element.Value }
                elseif ($element -is [System.Management.Automation.Language.CommandParameterAst] -and $null -eq $element.Argument) { $parts += '-' + $element.ParameterName }
                elseif ($element -is [System.Management.Automation.Language.ConstantExpressionAst]) { $parts += [string]$element.Value }
                else { $dynamic = $true; $parts += $element.Extent.Text }
            }
            [pscustomobject]@{ name=$name; parts=@($parts); dynamic=$dynamic }
        })
        [pscustomobject]@{ uncertain=$uncertain; commands=@($commands) } | ConvertTo-Json -Compress -Depth 8
        """;

    internal static bool IsDestructiveCommand(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        if (content.Length > 8192) return true;
        if (CommandCache.TryGetValue(content, out var cached)) return cached;
        var blocked = InspectCommand(content);
        if (CommandCache.Count >= 256) CommandCache.Clear();
        CommandCache[content] = blocked;
        return blocked;
    }

    private static bool InspectCommand(string content)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(executable)) return true;
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8 };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(ParserScript)) }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.StandardInput.Write(content); process.StandardInput.Close();
            if (!process.WaitForExit(5000)) { process.Kill(entireProcessTree: true); return true; }
            if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(stderr.GetAwaiter().GetResult())) return true;
            using var json = JsonDocument.Parse(stdout.GetAwaiter().GetResult());
            if (json.RootElement.GetProperty("uncertain").GetBoolean()) return true;
            var commands = json.RootElement.GetProperty("commands").EnumerateArray().ToArray();
            return commands.Length == 0 || commands.Any(command =>
                command.GetProperty("dynamic").GetBoolean() || !IsAllowedCommand(command.GetProperty("name").GetString() ?? "",
                    command.GetProperty("parts").EnumerateArray().Select(item => item.GetString() ?? "").ToArray()));
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException or JsonException or ArgumentException or UnauthorizedAccessException)
        { return true; }
    }

    private static bool IsAllowedCommand(string name, string[] args)
    {
        name = Path.GetFileNameWithoutExtension(name.Replace('\\', '/')).ToLowerInvariant();
        if (name == "git")
        {
            var index = 0;
            while (index < args.Length && args[index].StartsWith('-'))
            {
                var option = args[index++];
                if (option is "-C" or "--git-dir" or "--work-tree") { if (index >= args.Length) return false; index++; }
                else if (option is not ("--no-pager" or "--no-optional-locks")) return false;
            }
            return index < args.Length && new[] { "status", "diff", "log", "show", "ls-files", "rev-parse", "version" }.Contains(args[index], StringComparer.OrdinalIgnoreCase)
                && !args.Any(arg => arg.StartsWith("--output", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("--ext", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("--textconv", StringComparison.OrdinalIgnoreCase));
        }
        if (name == "dotnet") return args.Length == 1 && new[] { "--version", "--info", "--list-sdks", "--list-runtimes" }.Contains(args[0], StringComparer.OrdinalIgnoreCase);
        if (name == "rg" && args.Any(arg => arg.StartsWith("--pre", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("--hostname-bin", StringComparison.OrdinalIgnoreCase))) return false;
        return new[] { "get-childitem", "ls", "dir", "gci", "get-content", "cat", "gc", "get-location", "pwd", "get-date", "write-output", "echo",
            "select-object", "select", "where-object", "where", "sort-object", "sort", "measure-object", "test-path", "get-item", "get-command", "get-process",
            "format-table", "format-list", "out-string", "rg", "whoami", "hostname" }.Contains(name, StringComparer.Ordinal);
    }
}
