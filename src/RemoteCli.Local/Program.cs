using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;
using System.Reflection;

namespace RemoteCli.Local;

public static class LocalCommandHost
{
    public static async Task<int> RunAsync(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        var installDirectory = AppContext.BaseDirectory;
        var storageEnabled = args.Contains("--enable-conversation-storage", StringComparer.Ordinal);
        var command = args[0];
        if (command == "mcp")
        {
            return await RunMcpAsync(installDirectory, storageEnabled).ConfigureAwait(false);
        }

        if (command == "conversations")
        {
            return await RunConversationCommandAsync(args.Skip(1).ToArray(), installDirectory).ConfigureAwait(false);
        }

        if (command == "pair")
        {
            Console.Error.WriteLine("The standalone pair entry point was removed. Start RemoteCli.Desktop.exe and use the local pairing UI so authentication stays in the local connection service.");
            return 2;
        }

        Console.Error.WriteLine($"Unsupported command '{command}'. Pairing, close, and uninstall are not implemented in this build.");
        return 2;
    }

    private static async Task<int> RunMcpAsync(string installDirectory, bool storageEnabled)
    {
        var desktopProcessLocator = new DesktopProcessLocator(Environment.ProcessPath);
        var initiallyFoundProcesses = desktopProcessLocator.FindCandidateProcessIds();
        Trace.TraceInformation("MCP startup found {0} matching desktop process(es).", initiallyFoundProcesses.Count);
        var serviceClient = new LocalConnectionServiceClient(
            processLookup: desktopProcessLocator.FindCandidateProcessIds);
        var startupStatus = await serviceClient.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
        Trace.TraceInformation(
            "MCP startup service binding resolved state {0}, stateVersion {1}.",
            startupStatus.ConnectionState,
            startupStatus.StateVersion);

        var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddSingleton(new ConversationStore(installDirectory, storageEnabled));
        var connectionAdapter = new ConnectionMcpAdapter(serviceClient);
        var mcpServer = builder.Services.AddMcpServer(options =>
        {
            options.ServerInfo = new Implementation
            {
                Name = "LongShell",
                Version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "development"
            };
            options.ServerInstructions = SecurityPrompt.Text;
            ConnectionMcpAdapter.EnableToolsListChanged(options);
        })
        .WithStdioServerTransport()
        .WithTools<ConversationStatusTools>();
        connectionAdapter.Register(mcpServer);

        if (storageEnabled)
        {
            mcpServer.WithTools<ConversationSyncTools>();
        }

        mcpServer.WithPrompts<RemoteSessionPolicyPrompt>();

        var host = builder.Build();
        try
        {
            await host.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            await connectionAdapter.DisposeAsync().ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task<int> RunConversationCommandAsync(string[] args, string installDirectory)
    {
        var store = new ConversationStore(installDirectory, enabled: false);
        using var cancellation = new CancellationTokenSource();
        try
        {
            if (args.Length == 1 && args[0] == "list")
            {
                var conversations = await store.ListAsync(cancellation.Token).ConfigureAwait(false);
                foreach (var item in conversations)
                {
                    Console.WriteLine(JsonSerializer.Serialize(item));
                }

                return 0;
            }

            if (args.Length == 2 && args[0] == "show")
            {
                var entries = await store.ReadAsync(args[1], cancellation.Token).ConfigureAwait(false);
                foreach (var entry in entries)
                {
                    Console.WriteLine(JsonSerializer.Serialize(entry));
                }

                return 0;
            }

            if (args.Length == 4 && args[0] == "export" && args[2] == "--format")
            {
                await store.ExportAsync(args[1], args[3], Console.Out, cancellation.Token).ConfigureAwait(false);
                return 0;
            }

            Console.Error.WriteLine("Usage: RemoteCli.Local conversations list | show <id> | export <id> --format json|markdown");
            return 2;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("RemoteCli.Desktop");
        Console.WriteLine("  mcp [--enable-conversation-storage]   Run the local MCP server over stdio");
        Console.WriteLine("  conversations list");
        Console.WriteLine("  conversations show <id>");
        Console.WriteLine("  conversations export <id> --format json|markdown");
        Console.WriteLine();
        Console.WriteLine("Start RemoteCli.Desktop.exe for invite creation, local pairing, and authentication approval. P2P, standalone close, and uninstall commands are not implemented.");
    }
}
