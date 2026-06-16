using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotBridge.Cli.Models.Responses;

/// <summary>
/// The Codex CLI's <c>POST /responses</c> request body — the OpenAI Responses
/// API shape. Models exactly the 13 fields Codex actually sends
/// (<c>docs/codex-protocol-research.md</c> §3.2, sourced from
/// <c>codex-rs ResponsesApiRequest</c> / <c>build_responses_request</c> and
/// confirmed against the real captures in <c>docs/scratch/codex-capture/</c>);
/// the full OpenAI <c>ResponseCreateParamsBase</c> has dozens more fields Codex
/// never emits, intentionally not modeled (AOT size + we only translate what
/// Codex sends).
/// </summary>
/// <remarks>
/// <b>Fully typed, no <c>JsonElement</c> passthrough of the envelope</b>
/// (decision Q2): T1 reads these to build the IR and T2 rewrites them from the
/// IR, and AOT requires source-gen serialization — so every field Codex sends is
/// a real property. The only <c>JsonElement</c> here is for genuinely opaque
/// leaves (tool-call arguments, output schema, the Codex-internal
/// <c>client_metadata</c> map) that the bridge carries verbatim.
/// </remarks>
internal sealed record ResponsesRequest
{
    /// <summary>Model id (e.g. <c>gpt-5.3-codex</c>). Codex sends the dotted slug already.</summary>
    public required string Model { get; init; }

    /// <summary>Top-level system prompt (maps to IR <c>system</c>). Omitted by Codex if empty.</summary>
    public string? Instructions { get; init; }

    /// <summary>The conversation items (maps to IR <c>messages</c>).</summary>
    public required IReadOnlyList<ResponsesInputItem> Input { get; init; }

    /// <summary>
    /// The Responses <c>tools[]</c> array, held opaque as a <see cref="JsonElement"/>.
    /// Codex emits six concrete tool shapes (function / custom / namespace /
    /// tool_search / web_search / image_generation) whose variant-specific bodies
    /// (apply_patch's grammar, tool_search's execution, …) have no lossless
    /// Anthropic equivalent — so the whole array rides the IR
    /// <c>ProviderExtensions["openai"]</c> bag verbatim and T2 re-emits it,
    /// dropping only <c>image_generation</c> (uniform 400) and — for
    /// <c>mai-code-1-flash-internal</c> — <c>custom</c> tools (that model 500s).
    /// Kept opaque (not a typed polymorphic union) because the bridge never reads
    /// a tool's internals, only its <c>type</c> for the drop filter — and a typed
    /// union with <c>[JsonExtensionData]</c> can't bind through a record ctor
    /// under STJ anyway.
    /// </summary>
    public JsonElement? Tools { get; init; }

    /// <summary>Codex hardcodes <c>"auto"</c> (research §3.2).</summary>
    public JsonElement? ToolChoice { get; init; }

    public bool? ParallelToolCalls { get; init; }

    public Reasoning? Reasoning { get; init; }

    /// <summary><c>false</c> for Copilot/bridge (non-Azure) — no server persistence.</summary>
    public bool? Store { get; init; }

    /// <summary>Codex hardcodes <c>true</c>. The bridge honors the inbound flag.</summary>
    public bool? Stream { get; init; }

    /// <summary><c>["reasoning.encrypted_content"]</c> iff reasoning is on (research §3.2).</summary>
    public IReadOnlyList<string>? Include { get; init; }

    public string? PromptCacheKey { get; init; }

    /// <summary>Copilot rejects this (400) — T2 strips it (research §2.3).</summary>
    public string? ServiceTier { get; init; }

    /// <summary>Verbosity + output schema controls.</summary>
    public TextControls? Text { get; init; }

    /// <summary>
    /// Codex-internal metadata map — opaque to the bridge, carried verbatim
    /// (rides the IR <c>ProviderExtensions["openai"]</c> bag through T1/T2).
    /// </summary>
    public JsonElement? ClientMetadata { get; init; }

    /// <summary>
    /// Forward-looking output-token cap. Codex doesn't send it today, but the
    /// Responses API defines it; modeled so a future Codex that does send it
    /// round-trips. Maps to IR <c>max_tokens</c>.
    /// </summary>
    public int? MaxOutputTokens { get; init; }
}

/// <summary>
/// Mirrors the Responses <c>reasoning</c> object. <c>effort</c> is the one the
/// bridge translates (IR <c>thinking</c>/<c>output_config.effort</c> ↔ this) and
/// clamps per the Codex model profile; <c>summary</c> rides the bag.
/// </summary>
internal sealed record Reasoning
{
    /// <summary>One of <c>minimal | none | low | medium | high | xhigh</c> (Codex vocabulary, research §2.2).</summary>
    public string? Effort { get; init; }

    /// <summary><c>"auto"</c> etc. — carried verbatim through the bag, not interpreted.</summary>
    public string? Summary { get; init; }
}

/// <summary>
/// Mirrors the Responses <c>text</c> controls. <c>verbosity</c> rides the bag
/// (Q1: the bridge does not touch it); <c>format</c> carries a JSON-schema for
/// structured output, held opaque.
/// </summary>
internal sealed record TextControls
{
    public string? Verbosity { get; init; }

    public JsonElement? Format { get; init; }
}
