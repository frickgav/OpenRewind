namespace OpenRewind;

public enum AiChatKind { User, Assistant, Tool, Error }

/// One row in the chat window. For Tool, `Detail` is exactly what was sent to the provider.
public sealed record AiChatItem(AiChatKind Kind, string Text, string Detail = "");

/// The conversation behind the chat window: asks the model, runs the lookups it asks for, repeats.
/// Same loop as AIChatModel on the Mac.
public sealed class AiConversation
{
    /// Lookups per question before giving up.
    public const int MaxRounds = 10;
    /// Characters of earlier tool answers kept in the conversation; older ones are shortened.
    public const int HistoryBudget = 60_000;

    readonly List<AiMessage> history = new();
    public IReadOnlyList<AiMessage> History => history;

    public void Clear() => history.Clear();

    /// `add` is called for every row to show, on the caller's context (the UI thread for a form).
    public async Task Ask(string question, IAiClient client, AiTools tools, Action<AiChatItem> add, CancellationToken ct = default)
    {
        add(new AiChatItem(AiChatKind.User, question));
        history.Add(new AiMessage { Role = AiRole.User, Text = question });
        try
        {
            for (int round = 0; round < MaxRounds; round++)
            {
                TrimHistory();
                var reply = await client.Send(SystemPrompt(DateTime.Now), history, AiTools.Specs, ct);
                ct.ThrowIfCancellationRequested();
                history.Add(new AiMessage { Role = AiRole.Assistant, Text = reply.Text, ToolCalls = reply.ToolCalls });
                if (reply.Text.Length > 0) add(new AiChatItem(AiChatKind.Assistant, reply.Text));
                if (reply.ToolCalls.Count == 0) return;
                foreach (var call in reply.ToolCalls)
                {
                    var result = await Task.Run(() => tools.Run(call), ct);
                    history.Add(new AiMessage { Role = AiRole.Tool, Text = result, ToolCallId = call.Id, ToolName = call.Name });
                    add(new AiChatItem(AiChatKind.Tool, AiTools.Describe(call), result));
                }
            }
            add(new AiChatItem(AiChatKind.Error, $"Stopped after {MaxRounds} lookups. Try asking something narrower."));
        }
        catch (Exception e)
        {
            var cancelled = e is OperationCanceledException;
            add(new AiChatItem(AiChatKind.Error, cancelled ? "Stopped." : e is AiException ? e.Message : $"Something went wrong: {e.Message}"));
            // Leave the history in a state every provider accepts: no unanswered tool calls.
            while (history.Count > 0 && history[^1].Role == AiRole.Tool) history.RemoveAt(history.Count - 1);
            if (history.Count > 0 && history[^1] is { Role: AiRole.Assistant, ToolCalls.Count: > 0 }) history.RemoveAt(history.Count - 1);
            if (history.Count > 0 && history[^1].Role == AiRole.User) history.RemoveAt(history.Count - 1);
        }
    }

    /// Long chats would send every earlier lookup again; shorten the oldest ones first.
    void TrimHistory()
    {
        var total = history.Where(m => m.Role == AiRole.Tool).Sum(m => m.Text.Length);
        foreach (var m in history)
        {
            if (total <= HistoryBudget) break;
            if (m.Role != AiRole.Tool || m.Text.Length <= 1500) continue;
            total -= m.Text.Length - 1500;
            m.Text = m.Text[..1500] + "\n[shortened]";
        }
    }

    public static string SystemPrompt(DateTime now) => $"""
        You are the assistant inside OpenRewind, an app that records this user's computer: text read from screenshots, app names, window titles, browser addresses, and transcripts of the microphone and system audio. Everything stays on their computer; you can read it only through the tools.

        Now: {now:yyyy-MM-dd HH:mm} local time ({TimeZoneInfo.Local.DisplayName}), {now:dddd d MMMM yyyy}.

        - Always look things up with the tools instead of guessing. For "what did I do / work on" questions, call activity_summary first, then search_screen or get_screen_text for details.
        - Times in tool answers are local. Write times the way people say them (2:15 PM).
        - When you mention a moment, link it with the link from the tool answer, e.g. [2:15 PM](openrewind://t/1760000000). The link opens that moment in the user's timeline.
        - Group work into tasks or projects instead of listing every window. Be concise.
        - If nothing is found, say so plainly. Screen text comes from OCR and can have errors.
        """;
}

/// The model answers in Markdown. WinForms shows plain text with clickable links, so turn
/// **bold**, `code` and headings into plain text and [text](url) into a link range.
public static class AiMarkdown
{
    public sealed record Link(int Start, int Length, string Url);

    static readonly System.Text.RegularExpressions.Regex LinkRx = new(@"\[([^\]]+)\]\(([^)\s]+)\)");

    public static (string Text, List<Link> Links) Plain(string md)
    {
        var lines = md.Replace("\r\n", "\n").Split('\n').Select(l =>
        {
            var t = l.TrimStart();
            if (t.StartsWith('#')) return t.TrimStart('#').TrimStart();
            if (t.StartsWith("- ") || t.StartsWith("* ")) return l[..(l.Length - t.Length)] + "• " + t[2..];
            return l;
        });
        var text = string.Join("\n", lines).Replace("**", "").Replace("__", "").Replace("`", "");
        var links = new List<Link>();
        var sb = new System.Text.StringBuilder();
        int last = 0;
        foreach (System.Text.RegularExpressions.Match m in LinkRx.Matches(text))
        {
            sb.Append(text, last, m.Index - last);
            links.Add(new Link(sb.Length, m.Groups[1].Length, m.Groups[2].Value));
            sb.Append(m.Groups[1].Value);
            last = m.Index + m.Length;
        }
        sb.Append(text, last, text.Length - last);
        return (sb.ToString(), links);
    }
}
