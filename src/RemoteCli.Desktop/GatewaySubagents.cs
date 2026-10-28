using System.Collections.Concurrent;
using System.Text.Json;

namespace RemoteCli.Desktop;

/// <summary>Owns independent child model tasks for exactly one parent request.</summary>
internal sealed class GatewaySubagents(Func<string, CancellationToken, Task<string>> run, CancellationToken parentToken) : IAsyncDisposable
{
    private const int MaximumAgents = 8;
    private readonly SemaphoreSlim _concurrency = new(3, 3);
    private readonly ConcurrentDictionary<string, Agent> _agents = new(StringComparer.Ordinal);
    private int _disposed;

    internal IReadOnlyList<LocalChatTool> Create() =>
    [
        new("spawn_agent", "启动独立子代理处理一个明确任务；返回 agent_id，使用 wait_agent 获取结果。子代理只使用允许的只读工具，不会继承对话历史。",
            """{"type":"object","properties":{"task":{"type":"string"}},"required":["task"],"additionalProperties":false}""", SpawnAsync),
        new("wait_agent", "等待本次请求创建的子代理；running 表示仍在执行，completed 返回结果。",
            """{"type":"object","properties":{"agent_id":{"type":"string"},"wait_ms":{"type":"integer"}},"required":["agent_id"],"additionalProperties":false}""", WaitAsync),
        new("cancel_agent", "取消本次请求创建的子代理。",
            """{"type":"object","properties":{"agent_id":{"type":"string"}},"required":["agent_id"],"additionalProperties":false}""", CancelAsync)
    ];

    private Task<string> SpawnAsync(JsonElement args, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var task = ReadString(args, "task").Trim();
        if (string.IsNullOrWhiteSpace(task) || task.Length > 8192) return Task.FromResult("错误：子代理任务必须为 1 至 8192 个字符。");
        if (Volatile.Read(ref _disposed) != 0 || parentToken.IsCancellationRequested) return Task.FromResult("错误：父请求已结束。");
        if (_agents.Count >= MaximumAgents || !_concurrency.Wait(0)) return Task.FromResult("错误：子代理数量已达上限；请等待已有子代理结束。");
        var id = Guid.NewGuid().ToString("N");
        var agent = new Agent(CancellationTokenSource.CreateLinkedTokenSource(parentToken));
        agent.Stop.CancelAfter(TimeSpan.FromMinutes(2));
        if (!_agents.TryAdd(id, agent)) { agent.Stop.Dispose(); _concurrency.Release(); return Task.FromResult("错误：无法创建子代理。"); }
        agent.Work = Task.Run(() => ExecuteAsync(agent, task), CancellationToken.None);
        return Task.FromResult(JsonSerializer.Serialize(new { agent_id = id, status = "running" }));
    }

    private async Task ExecuteAsync(Agent agent, string task)
    {
        try
        {
            var output = await run(task, agent.Stop.Token).ConfigureAwait(false);
            agent.Stop.Token.ThrowIfCancellationRequested();
            if (output.Length > 32000) output = output[..32000] + "…（已截断）";
            agent.Result = new AgentResult("completed", output, null);
        }
        catch (OperationCanceledException) { agent.Result = new AgentResult("cancelled", null, "子代理已取消或超过两分钟期限。"); }
        catch (Exception exception) { agent.Result = new AgentResult("failed", null, "子代理执行失败（" + exception.GetType().Name + "）。"); }
        finally { _concurrency.Release(); }
    }

    private async Task<string> WaitAsync(JsonElement args, CancellationToken token)
    {
        if (!_agents.TryGetValue(ReadString(args, "agent_id"), out var agent)) return "错误：子代理不存在或不属于本次请求。";
        var wait = 10000;
        if (args.TryGetProperty("wait_ms", out var value) && (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out wait) || wait is < 0 or > 20000))
            return "错误：wait_ms 必须为 0 至 20000。";
        await Task.WhenAny(agent.Work, Task.Delay(wait, token)).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var result = agent.Work.IsCompleted ? agent.Result : null;
        return JsonSerializer.Serialize(new { agent_id = ReadString(args, "agent_id"), status = result?.Status ?? "running", result = result?.Output, error = result?.Error });
    }

    private Task<string> CancelAsync(JsonElement args, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_agents.TryGetValue(ReadString(args, "agent_id"), out var agent)) return Task.FromResult("错误：子代理不存在或不属于本次请求。");
        var requested = !agent.Work.IsCompleted;
        if (requested) agent.Stop.Cancel();
        return Task.FromResult(JsonSerializer.Serialize(new { agent_id = ReadString(args, "agent_id"), cancellation_requested = requested }));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var agent in _agents.Values) agent.Stop.Cancel();
        var all = Task.WhenAll(_agents.Values.Select(agent => agent.Work));
        try { await all.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { /* Cooperative cancellation; an unresponsive provider may take longer. */ }
        foreach (var agent in _agents.Values) agent.Stop.Dispose();
        _agents.Clear();
    }

    private static string ReadString(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private sealed record AgentResult(string Status, string? Output, string? Error);
    private sealed class Agent(CancellationTokenSource stop)
    {
        internal CancellationTokenSource Stop { get; } = stop;
        internal Task Work { get; set; } = Task.CompletedTask;
        internal AgentResult? Result { get; set; }
    }
}
