namespace CopilotBridge.Cli.Pipeline.Adapters.Codex;

/// A client-side fault in a Codex /responses request (e.g. malformed tool
/// arguments the model echoed back) — must surface as HTTP 400, NOT 502.
internal sealed class CodexBadRequestException(string message) : Exception(message);
