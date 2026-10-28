using RemoteCli.Desktop;

namespace RemoteCli.UnitTests;

public sealed class GatewaySubagentLoopTests
{
    [Fact]
    public async Task ChildRunsIndependentReadOnlyToolLoopAndRejectsEscalation()
    {
        var reads = 0; var writes = 0; var modelCalls = 0;
        var tools = new[]
        {
            new LocalChatTool("read_file", "", "{}", (_, _) => { reads++; return Task.FromResult("file contents"); }),
            new LocalChatTool("exec_command", "", "{}", (_, _) => { writes++; return Task.FromResult("unsafe"); })
        };
        var result = await GatewaySubagentLoop.RunAsync("inspect", tools, (history, offered, _) =>
        {
            modelCalls++;
            Assert.Single(offered);
            Assert.Equal("read_file", offered[0].Name);
            if (modelCalls == 1)
            {
                Assert.Single(history);
                Assert.Contains("inspect", history[0].Content);
                return Task.FromResult(new GatewayAgentRound(null,
                    [new("a", "read_file", "{}"), new("b", "exec_command", "{}") ]));
            }
            Assert.Contains(history, turn => turn.CallId == "a" && turn.Content == "file contents");
            Assert.Contains(history, turn => turn.CallId == "b" && turn.Content!.StartsWith("错误："));
            return Task.FromResult(new GatewayAgentRound("done", []));
        }, CancellationToken.None);
        Assert.Equal("done", result);
        Assert.Equal(1, reads);
        Assert.Equal(0, writes);
        Assert.Equal(2, modelCalls);
    }
}
