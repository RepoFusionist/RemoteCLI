using RemoteCli.Local;
using Common.Protocol;

namespace RemoteCli.UnitTests;

public sealed class CodexExecModelProviderTests
{
    [Fact]
    public void RemoteControllerRequestDeadlineExceedsCodexExecutionTimeout()
    {
        Assert.Equal(120, ProtocolConstants.ControllerModelExecutionTimeoutSeconds);
        Assert.Equal(135, ProtocolConstants.ControllerModelResponseTimeoutSeconds);
        Assert.True(ProtocolConstants.ControllerModelResponseTimeoutSeconds >
                    ProtocolConstants.ControllerModelExecutionTimeoutSeconds);
    }

    [Fact]
    public void CreateStartInfoAllowsConfiguredMcpAndDisablesNonMcpTools()
    {
        var startInfo = CodexExecModelProvider.CreateStartInfo("codex.exe", Path.GetTempPath());
        var arguments = startInfo.ArgumentList.ToArray();
        var settings = arguments
            .Select((value, index) => (value, index))
            .Where(item => item.value == "--config" && item.index + 1 < arguments.Length)
            .Select(item => arguments[item.index + 1])
            .ToArray();

        Assert.Contains("--sandbox", arguments);
        Assert.Contains("read-only", arguments);
        Assert.DoesNotContain("--strict-config", arguments);
        Assert.DoesNotContain("--ignore-user-config", arguments);
        Assert.Contains("features.shell_tool=false", settings);
        Assert.Contains("features.multi_agent=false", settings);
        Assert.Contains("features.apps=false", settings);
        Assert.Contains("features.remote_plugin=false", settings);
        Assert.Contains("features.hooks=false", settings);
        Assert.Contains("features.shell_snapshot=false", settings);
        Assert.Contains("features.unified_exec=false", settings);
        Assert.Contains("web_search=\"disabled\"", settings);
        Assert.Contains(settings, setting => setting.StartsWith("developer_instructions=", StringComparison.Ordinal) &&
            setting.Contains("only tools exposed by MCP servers", StringComparison.Ordinal) &&
            setting.Contains("Do not use local shell", StringComparison.Ordinal) &&
            setting.Contains("not MCP resources", StringComparison.Ordinal) &&
            setting.Contains("remotecli_remote_tools_list", StringComparison.Ordinal) &&
            setting.Contains("remotecli_remote_workspace_read", StringComparison.Ordinal) &&
            setting.Contains("remotecli_remote_tool_receive", StringComparison.Ordinal) &&
            setting.Contains("remote.directory_list", StringComparison.Ordinal) &&
            setting.Contains("remoteWorkspace", StringComparison.Ordinal) &&
            setting.Contains("do not list", StringComparison.Ordinal) &&
            setting.Contains("mutating tools", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateStartInfoCarriesSelectedModelAndReasoningEffort()
    {
        var startInfo = CodexExecModelProvider.CreateStartInfo(
            "codex.exe",
            Path.GetTempPath(),
            "gpt-6.1-sol",
            "xhigh");
        var arguments = startInfo.ArgumentList.ToArray();

        var modelIndex = Array.IndexOf(arguments, "--model");
        Assert.True(modelIndex >= 0);
        Assert.Equal("gpt-6.1-sol", arguments[modelIndex + 1]);
        Assert.Contains("model_reasoning_effort=\"xhigh\"", arguments);
    }

    [Fact]
    public void ModelCatalogKeepsOnlyListedModelsAndSupportedReasoningEfforts()
    {
        var catalog = CodexExecModelProvider.LoadModelCatalogForTests(
            "C:\\codex",
            "model = \"gpt-6.1-sol\"\nmodel_reasoning_effort = \"high\"\n",
            """
            {
              "models": [
                {
                  "slug": "gpt-6.1-sol",
                  "display_name": "GPT-6.1 Sol",
                  "visibility": "list",
                  "supported_in_api": true,
                  "default_reasoning_level": "medium",
                  "supported_reasoning_levels": [{"effort":"low"},{"effort":"medium"},{"effort":"high"},{"effort":"unsafe"}]
                },
                {"slug":"hidden-model","visibility":"hidden"}
              ]
            }
            """);

        Assert.True(catalog.Success);
        var model = Assert.Single(catalog.Models);
        Assert.Equal("gpt-6.1-sol", model.Id);
        Assert.Equal(["low", "medium", "high"], model.ReasoningEfforts);
        Assert.Equal("high", catalog.DefaultReasoningEffort);
    }

    [Theory]
    [InlineData("MCP server failed to initialize", "codex_mcp_unavailable")]
    [InlineData("MCP startup timed out", "codex_mcp_unavailable")]
    [InlineData("Error loading config.toml: unknown configuration field", "codex_cli_config_invalid")]
    [InlineData("Windows unelevated restricted-token sandbox cannot enforce restrictions", "codex_sandbox_unavailable")]
    [InlineData("HTTP 401 Unauthorized", "codex_authentication_failed")]
    [InlineData("unexpected process failure", "codex_exec_failed")]
    public void ClassifyFailurePreservesActionableFailureCategory(string diagnostic, string expectedErrorCode)
    {
        Assert.Equal(expectedErrorCode, CodexExecModelProvider.ClassifyFailure(diagnostic, null));
    }

    [Theory]
    [InlineData("codex_authentication_failed")]
    [InlineData("codex_mcp_unavailable")]
    [InlineData("codex_sandbox_unavailable")]
    [InlineData("codex_cli_config_invalid")]
    public void BrokerAllowsSpecificProviderFailuresToReachTheUser(string errorCode)
    {
        Assert.True(LocalSessionBroker.IsSafeModelError(errorCode));
    }

    [Fact]
    public void ConversationPromptAllowsMcpWhileForbiddingLocalTools()
    {
        var prompt = LocalSessionBroker.BuildControllerModelPrompt("Use a configured MCP", []);

        Assert.Contains("Use only MCP tools configured for this Codex CLI invocation", prompt);
        Assert.Contains("do not use local shell", prompt);
        Assert.Contains("not MCP resources", prompt);
        Assert.Contains("remotecli_remote_tools_list", prompt);
        Assert.Contains("remotecli_remote_workspace_read", prompt);
        Assert.Contains("remotecli_remote_tool_receive", prompt);
        Assert.Contains("remoteWorkspace is null", prompt);
        Assert.Contains("do not list .", prompt);
        Assert.Contains("remote.directory_list", prompt);
        Assert.DoesNotContain("Do not use tools", prompt);

        Assert.Contains("not as MCP resources", SecurityPrompt.Text);
        Assert.Contains("remotecli_remote_tools_list", SecurityPrompt.Text);
        Assert.Contains("remotecli_remote_workspace_read", SecurityPrompt.Text);
        Assert.Contains("remotecli_remote_tool_receive", SecurityPrompt.Text);
        Assert.Contains("remoteWorkspace` is null", SecurityPrompt.Text);
    }

    [Fact]
    public void ExecOutputParserTreatsTurnCompletedAsFinalWithoutWaitingForProcessExit()
    {
        var parser = new CodexExecModelProvider.CodexExecOutputParser();
        parser.ProcessLine("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"MCP status: Connected\"}}");

        parser.ProcessLine("{\"type\":\"turn.completed\"}");

        var output = parser.ToOutput();
        Assert.True(output.TurnFinished);
        Assert.False(output.TurnFailed);
        Assert.Equal("MCP status: Connected", output.FinalMessage);
    }

    [Fact]
    public void ExecOutputParserSurfacesNestedTurnFailureMessage()
    {
        var parser = new CodexExecModelProvider.CodexExecOutputParser();

        parser.ProcessLine("{\"type\":\"turn.failed\",\"error\":{\"message\":\"MCP tool timed out\"}}");

        var output = parser.ToOutput();
        Assert.True(output.TurnFinished);
        Assert.True(output.TurnFailed);
        Assert.Equal("MCP tool timed out", output.ErrorMessage);
    }

    [Fact]
    public async Task InvalidPromptFailsBeforeAttemptingToStartCli()
    {
        var provider = new CodexExecModelProvider(() => "missing-codex.exe");

        var result = await provider.GenerateAsync(" ", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.Response);
        Assert.Equal("invalid_model_prompt", result.ErrorCode);
    }
}
