using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

if (args.Length is < 1 or > 2 || !File.Exists(args[0]) ||
    (args.Length == 2 && (!int.TryParse(args[1], out var seconds) || seconds is < 0 or > 60)))
{
    Console.Error.WriteLine("Usage: RemoteCli.McpProbe <published RemoteCli.Desktop.exe> [hold-seconds: 0-60]");
    return 2;
}

var executable = Path.GetFullPath(args[0]);
var holdSeconds = args.Length == 2 ? int.Parse(args[1]) : 0;
var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "LongShell live MCP connection probe",
    Command = executable,
    Arguments = ["mcp"]
});

await using var client = await McpClient.CreateAsync(transport);
var tools = await client.ListToolsAsync();
Console.WriteLine("MCP protocol: connected; tools: " + tools.Count);
await PrintStatusAsync(client);
Console.Out.Flush();

if (holdSeconds > 0)
{
    await Task.Delay(TimeSpan.FromSeconds(holdSeconds));
    await PrintStatusAsync(client);
}

return 0;

static async Task PrintStatusAsync(McpClient client)
{
    var result = await client.CallToolAsync(
        "remotecli_connection_status",
        new Dictionary<string, object?>());
    var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text
        ?? throw new InvalidDataException("MCP status returned no text.");
    using var document = JsonDocument.Parse(text);
    var state = document.RootElement.GetProperty("connectionState").GetString();
    var authenticated = document.RootElement.GetProperty("authenticated").GetBoolean();
    Console.WriteLine("Local connection state: " + state + "; remote authenticated: " + authenticated);
}
