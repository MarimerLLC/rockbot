using System.Text;
using Microsoft.Extensions.AI;

namespace RockBot.Llm.Copilot;

/// <summary>
/// Converts <see cref="ChatMessage"/> sequences into the system prompt + user prompt
/// pair required by the Copilot SDK (which has no conversation history injection API).
/// </summary>
internal static class MessageFormatter
{
    /// <summary>
    /// Extracts system messages and formats conversation history into a single prompt pair.
    /// </summary>
    public static (string SystemPrompt, string UserPrompt) Format(IEnumerable<ChatMessage> messages)
    {
        var systemParts = new List<string>();
        var conversationMessages = new List<ChatMessage>();

        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                if (!string.IsNullOrEmpty(message.Text))
                    systemParts.Add(message.Text);
            }
            else
            {
                conversationMessages.Add(message);
            }
        }

        var systemPrompt = string.Join("\n\n", systemParts);

        // Optimization: if only a single user message with no prior history, pass directly.
        if (conversationMessages.Count == 1 && conversationMessages[0].Role == ChatRole.User)
            return (systemPrompt, conversationMessages[0].Text ?? string.Empty);

        if (conversationMessages.Count == 0)
            return (systemPrompt, string.Empty);

        // Multi-turn: format prior messages in <conversation_history> tags,
        // with the final user message outside the block.
        var sb = new StringBuilder();
        var lastIndex = conversationMessages.Count - 1;
        var priorMessages = conversationMessages.Take(lastIndex).ToList();

        if (priorMessages.Count > 0)
        {
            sb.AppendLine("<conversation_history>");
            foreach (var msg in priorMessages)
                AppendMessage(sb, msg);
            sb.AppendLine("</conversation_history>");
            sb.AppendLine();
        }

        // Final message (usually user) goes outside the history block.
        var finalMessage = conversationMessages[lastIndex];
        sb.Append(finalMessage.Text ?? string.Empty);

        return (systemPrompt, sb.ToString());
    }

    private static void AppendMessage(StringBuilder sb, ChatMessage message)
    {
        var role = message.Role == ChatRole.Assistant ? "assistant"
            : message.Role == ChatRole.User ? "user"
            : message.Role == ChatRole.Tool ? "tool_result"
            : message.Role.Value;

        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case FunctionCallContent fc:
                    sb.AppendLine($"[tool_call]: {fc.Name}({fc.Arguments?.ToString() ?? "{}"})");
                    break;
                case FunctionResultContent fr:
                    sb.AppendLine($"[tool_result]: {fr.Result?.ToString() ?? ""}");
                    break;
                case TextContent tc when !string.IsNullOrEmpty(tc.Text):
                    sb.AppendLine($"[{role}]: {tc.Text}");
                    break;
            }
        }

        // Fallback: if no structured content, use the Text property.
        if (message.Contents.Count == 0 && !string.IsNullOrEmpty(message.Text))
            sb.AppendLine($"[{role}]: {message.Text}");
    }
}
