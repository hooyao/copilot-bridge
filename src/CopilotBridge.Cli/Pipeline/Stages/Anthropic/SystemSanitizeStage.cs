using System.Text;
using CopilotBridge.Cli.Models.Anthropic.Request;

using Serilog;

namespace CopilotBridge.Cli.Pipeline.Stages.Anthropic;

/// <summary>
/// Strips Claude Code's <c># currentDate\nToday's date is YYYY-MM-DD.\n</c>
/// block from the volatile portion of the request — wherever it lands. Claude
/// Code injects this in the first user message inside a <c>&lt;system-reminder&gt;</c>
/// wrapper; the date changes daily and was making prompt cache hits impossible.
/// Scans both <c>system[]</c> and every user message's text blocks. Other
/// volatile blocks (<c># claudeMd</c>, <c># userEmail</c>) are stable per-user
/// and cache-friendly, so we leave them.
/// </summary>
internal sealed class SystemSanitizeStage : IRequestStage<MessagesRequest>
{
    private const string Marker = "# currentDate\n";

    public string Name => "SystemSanitize";

    public Task ApplyAsync(BridgeContext<MessagesRequest> ctx)
    {
        var stripped = 0;

        // Scan the top-level system field first.
        IReadOnlyList<TextBlockParam>? newSystem = ctx.Request.Body.System;
        if (newSystem is { Count: > 0 })
        {
            var rebuilt = new List<TextBlockParam>(newSystem.Count);
            var systemChanged = false;
            foreach (var s in newSystem)
            {
                var (text, hits) = StripCurrentDate(s.Text);
                if (hits > 0)
                {
                    rebuilt.Add(s with { Text = text });
                    stripped += hits;
                    systemChanged = true;
                }
                else
                {
                    rebuilt.Add(s);
                }
            }
            if (systemChanged) newSystem = rebuilt;
        }

        // Scan every user message's text blocks.
        var newMessages = ctx.Request.Body.Messages;
        var messagesChanged = false;
        var rebuiltMessages = new List<MessageParam>(newMessages.Count);
        foreach (var msg in newMessages)
        {
            if (msg.Role != Role.User)
            {
                rebuiltMessages.Add(msg);
                continue;
            }
            var newContent = new List<ContentBlockParam>(msg.Content.Count);
            var contentChanged = false;
            foreach (var block in msg.Content)
            {
                if (block is TextBlockParam textBlock)
                {
                    var (newText, hits) = StripCurrentDate(textBlock.Text);
                    if (hits > 0)
                    {
                        newContent.Add(textBlock with { Text = newText });
                        stripped += hits;
                        contentChanged = true;
                        continue;
                    }
                }
                newContent.Add(block);
            }
            if (contentChanged)
            {
                rebuiltMessages.Add(msg with { Content = newContent });
                messagesChanged = true;
            }
            else
            {
                rebuiltMessages.Add(msg);
            }
        }

        if (stripped > 0)
        {
            ctx.Request.Body = ctx.Request.Body with
            {
                System = newSystem,
                Messages = messagesChanged ? rebuiltMessages : ctx.Request.Body.Messages,
            };
        }

        Log.Debug($"stage {Name}: stripped {stripped} currentDate occurrences");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Removes <c># currentDate\nToday's date is YYYY-MM-DD.\n</c> + any
    /// trailing blank lines. Returns the new text and the number of
    /// occurrences removed (typically 0 or 1 — Claude Code injects it once).
    /// </summary>
    private static (string Text, int Hits) StripCurrentDate(string input)
    {
        if (input.Length == 0 || input.IndexOf(Marker, StringComparison.Ordinal) < 0)
        {
            return (input, 0);
        }

        var sb = new StringBuilder(input.Length);
        var hits = 0;
        var i = 0;
        while (i < input.Length)
        {
            var idx = input.IndexOf(Marker, i, StringComparison.Ordinal);
            if (idx < 0)
            {
                sb.Append(input, i, input.Length - i);
                break;
            }
            sb.Append(input, i, idx - i);
            // Skip the marker
            var skipFrom = idx + Marker.Length;
            // Skip the date line (everything up to the next \n inclusive)
            var dateLineEnd = input.IndexOf('\n', skipFrom);
            if (dateLineEnd < 0)
            {
                // Marker exists but no terminating newline after the date — bail and keep the marker.
                sb.Append(input, idx, input.Length - idx);
                break;
            }
            i = dateLineEnd + 1;
            // Eat any immediately-following blank lines so we don't leave a hole.
            while (i < input.Length && input[i] == '\n') i++;
            hits++;
        }
        return (sb.ToString(), hits);
    }
}
