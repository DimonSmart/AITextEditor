using System.Linq;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AiTextEditor.SemanticKernel;

public sealed class FunctionCallAwareChatHistoryCompressor(CursorAgentLimits limits)
{
    private readonly CursorAgentLimits limits = limits;

    public void Trim(ChatHistory history)
    {
        if (history.Count <= limits.MaxChatMessages)
        {
            return;
        }

        var index = 0;
        while (history.Count > limits.MaxChatMessages && index < history.Count)
        {
            if (history[index].Role == AuthorRole.System)
            {
                index++;
                continue;
            }

            if (IsFunctionCall(history[index]))
            {
                var resultIndex = FindNextFunctionResult(history, index + 1);
                if (resultIndex != -1)
                {
                    RemoveRange(history, index, resultIndex);
                    continue;
                }
            }

            history.RemoveAt(index);
        }
    }

    private static void RemoveRange(ChatHistory history, int start, int endInclusive)
    {
        for (var i = endInclusive; i >= start; i--)
        {
            history.RemoveAt(i);
        }
    }

    private static bool IsFunctionCall(ChatMessageContent message)
    {
        return message.Items.Any(item => item is FunctionCallContent);
    }

    private static bool IsFunctionResult(ChatMessageContent message)
    {
        return message.Role == AuthorRole.Tool
               || message.Items.Any(item => item is FunctionResultContent);
    }

    private static int FindNextFunctionResult(ChatHistory history, int startIndex)
    {
        for (var i = startIndex; i < history.Count; i++)
        {
            if (IsFunctionResult(history[i]))
            {
                return i;
            }
        }

        return -1;
    }
}
