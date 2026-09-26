using System.Runtime.CompilerServices;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace SemanticKernel;

/// <summary>Serializes inference on the shared local model across correction and minutes generation.</summary>
internal static class ChatCompletionRunner
{
    private static readonly ConditionalWeakTable<IChatCompletionService, SemaphoreSlim> Gates = new();

    internal static async Task<ChatMessageContent> CompleteAsync(
        IChatCompletionService chat, ChatHistory history, PromptExecutionSettings settings,
        CancellationToken cancellationToken)
    {
        var gate = Gates.GetValue(chat, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await chat.GetChatMessageContentAsync(history, settings, cancellationToken: cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }
}
