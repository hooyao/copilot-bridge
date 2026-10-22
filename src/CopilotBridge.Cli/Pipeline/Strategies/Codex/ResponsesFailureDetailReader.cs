using System.Net.ServerSentEvents;
using System.Text.Json;
using CopilotBridge.Cli.Copilot;

namespace CopilotBridge.Cli.Pipeline.Strategies.Codex;

/// <summary>One bounded diagnostic read after an already-failed native response.</summary>
internal static class ResponsesFailureDetailReader
{
    internal static readonly TimeSpan DiagnosticBudget = TimeSpan.FromMilliseconds(250);

    internal static bool TryReadFailed(in SseItem<string> evt, out UpstreamResponseFailedException failure)
    {
        failure = null!;
        if (evt.EventType != "response.failed"
            && !evt.Data.Contains("\"response.failed\"", StringComparison.Ordinal)) return false;
        try
        {
            using var doc = JsonDocument.Parse(evt.Data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String || type.GetString() != "response.failed") return false;
            failure = UpstreamResponseFailedException.FromEvent(root);
            return true;
        }
        catch (JsonException) { return false; }
    }

    internal static async Task<UpstreamResponseFailedException> EnrichAsync(
        IAsyncEnumerator<SseItem<string>> enumerator,
        CancellationTokenSource readCts,
        UpstreamResponseFailedException original,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var move = enumerator.MoveNextAsync();
            bool moved;
            if (move.IsCompleted) moved = await move;
            else
            {
                var pending = move.AsTask();
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var deadline = Task.Delay(DiagnosticBudget, delayCts.Token);
                var winner = await Task.WhenAny(pending, deadline);
                if (winner != pending && !pending.IsCompleted)
                {
                    readCts.Cancel();
                    try { await pending; } catch { /* observe the cancelled read */ }
                    ct.ThrowIfCancellationRequested();
                    return original;
                }
                delayCts.Cancel();
                moved = await pending;
            }
            ct.ThrowIfCancellationRequested();
            if (!moved || enumerator.Current.Data.Length > 16_384) return original;
            using var doc = JsonDocument.Parse(enumerator.Current.Data);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String && type.GetString() == "error"
                ? UpstreamResponseFailedException.FromEvent(root) : original;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // The explicit failed terminal remains authoritative if its optional
            // detail tail is malformed, reset, or otherwise unreadable.
            return original;
        }
    }
}
