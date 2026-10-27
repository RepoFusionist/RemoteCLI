using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace RemoteCli.Local;

[McpServerToolType]
public sealed class ConversationSyncTools(ConversationStore store)
{
    [McpServerTool(Name = "conversation_sync")]
    [Description("Explicitly stores client-reported conversation messages in the local install directory. Storage must be enabled when this MCP process starts. No local path is accepted.")]
    public async Task<string> SyncAsync(
        [Description("Client conversation identifier; used only as a data key, never as a path.")] string conversationId,
        [Description("Name of the client reporting this conversation.")] string clientName,
        [Description("Stable identifier for this client instance.")] string clientInstanceId,
        [Description("True only when the client has completed a full conversation snapshot.")] bool isCompleteSnapshot,
        [Description("True when the client believes messages may be missing.")] bool isPossiblyIncomplete,
        [Description("Messages with stable IDs, roles, timestamps, order, content, and optional related remote session or command identifiers.")] ConversationMessageInput[] messages,
        CancellationToken cancellationToken)
    {
        if (!store.IsEnabled)
        {
            return "Conversation storage is disabled. Restart the MCP server with --enable-conversation-storage to opt in.";
        }

        var storedCount = await store.SyncAsync(
            conversationId,
            clientName,
            clientInstanceId,
            isCompleteSnapshot,
            isPossiblyIncomplete,
            messages,
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { conversationId, storedMessageCount = storedCount, mode = isCompleteSnapshot ? "FullReported" : "ToolCallsOnly", isPossiblyIncomplete });
    }

}

[McpServerToolType]
public sealed class ConversationStatusTools(ConversationStore store)
{
    [McpServerTool(Name = "conversation_status")]
    [Description("Shows whether explicit conversation storage is enabled and lists locally stored conversation IDs and completeness status.")]
    public async Task<string> StatusAsync(CancellationToken cancellationToken)
    {
        var conversations = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            storageEnabled = store.IsEnabled,
            currentMode = store.IsEnabled ? "AwaitingClientSync" : "Off",
            conversations
        });
    }
}

[McpServerPromptType]
public sealed class RemoteSessionPolicyPrompt
{
    [McpServerPrompt(Name = "longshell-remote-policy")]
    [Description("Read-only safety policy for one-time-authorized remote command sessions.")]
    public static IReadOnlyCollection<ChatMessage> GetPolicy() => [new(ChatRole.User, SecurityPrompt.Text)];
}
