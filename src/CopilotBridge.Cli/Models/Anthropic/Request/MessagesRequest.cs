using System.Text.Json.Serialization;
using CopilotBridge.Cli.Models.Anthropic.Common;
using CopilotBridge.Cli.Models.Anthropic.Converters;
using CopilotBridge.Cli.Models.Common;

namespace CopilotBridge.Cli.Models.Anthropic.Request;

/// <summary>
/// Mirrors <c>MessageCreateParamsBase</c> (the body of <c>POST /v1/messages</c>).
/// Fields not present here (<c>betas</c>, <c>container</c>, <c>mcp_servers</c>,
/// <c>output_format</c>, <c>service_tier</c>, <c>speed</c>, <c>top_k</c>,
/// <c>top_p</c>, <c>user_profile_id</c>, <c>inference_geo</c>) are intentionally
/// dropped from the typed body — Claude Code does not emit them and Copilot
/// would reject several. When a NON-Anthropic client (Codex) sends such a knob,
/// it is not dropped: it rides <see cref="ProviderExtensions"/> verbatim through
/// the IR and is re-applied by the backend strategy. See
/// <c>docs/ir-definition-design.md</c> §3.
/// </summary>
internal sealed record MessagesRequest
{
    public required string Model { get; init; }
    public int MaxTokens { get; init; }
    public required IReadOnlyList<MessageParam> Messages { get; init; }

    [JsonConverter(typeof(TextBlockParamListConverter))]
    public IReadOnlyList<TextBlockParam>? System { get; init; }

    public IReadOnlyList<Tool>? Tools { get; init; }
    public ToolChoice? ToolChoice { get; init; }

    public ThinkingConfig? Thinking { get; init; }
    public OutputConfig? OutputConfig { get; init; }
    public ContextManagementConfig? ContextManagement { get; init; }

    public CacheControl? CacheControl { get; init; }
    public Metadata? Metadata { get; init; }

    public IReadOnlyList<string>? StopSequences { get; init; }
    public bool? Stream { get; init; }
    public double? Temperature { get; init; }

    /// <summary>
    /// Top-level <c>anthropic_beta</c> sometimes sent in the body (Claude Code
    /// also sends it as the <c>anthropic-beta</c> header). The bridge regenerates
    /// the header from a whitelist; this field is preserved for round-trip but
    /// not authoritative.
    /// </summary>
    public IReadOnlyList<string>? AnthropicBeta { get; init; }

    /// <summary>
    /// Request-level namespaced escape-hatch (<c>docs/ir-definition-design.md</c>
    /// §3). Carries provider-specific knobs the Anthropic-shape IR has no typed
    /// home for (Codex's <c>store</c>/<c>service_tier</c>/<c>include</c>/
    /// <c>prompt_cache_key</c>/<c>text.verbosity</c>). <c>null</c> for Claude
    /// Code — and the context-wide <c>WhenWritingNull</c> then omits it entirely,
    /// so the <c>/cc</c> hot path serializes byte-for-byte as before (H1).
    /// </summary>
    public ProviderExtensions? ProviderExtensions { get; init; }
}
