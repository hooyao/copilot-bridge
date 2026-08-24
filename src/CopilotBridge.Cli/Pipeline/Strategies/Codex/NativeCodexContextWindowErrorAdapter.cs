using System.Net.ServerSentEvents;
using CopilotBridge.Cli.Models;

namespace CopilotBridge.Cli.Pipeline.Strategies.Codex;

/// <summary>
/// Converts Copilot's confirmed pre-stream context rejection into the native
/// Responses failed terminal that Codex classifies as ContextWindowExceeded.
/// </summary>
internal static class NativeCodexContextWindowErrorAdapter
{
    public static bool TryCreate(
        string path,
        BackendVendor vendor,
        int status,
        bool streaming,
        byte[] body,
        string model,
        out SseItem<string> failed)
    {
        failed = default;
        if (!streaming
            || !string.Equals(path, "/codex/responses", StringComparison.OrdinalIgnoreCase)
            || !ResponsesContextWindowErrorClassifier.IsConfirmed(vendor, status, body))
            return false;

        var modelJson = CodexJson.EncodeString(model);
        failed = new SseItem<string>(
            "{\"type\":\"response.failed\",\"sequence_number\":0,\"response\":{"
            + "\"id\":\"resp_bridge_context_window\",\"object\":\"response\","
            + "\"status\":\"failed\",\"model\":" + modelJson + ",\"output\":[],"
            + "\"error\":{\"code\":\"context_length_exceeded\","
            + "\"message\":\"the model context window was exceeded\"},\"usage\":null}}",
            "response.failed");
        return true;
    }
}
