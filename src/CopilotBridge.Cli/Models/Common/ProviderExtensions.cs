using System.Text.Json;

namespace CopilotBridge.Cli.Models.Common;

/// <summary>
/// The IR's namespaced escape-hatch — stolen from the Vercel AI SDK's
/// <c>providerOptions</c>/<c>providerMetadata</c> pattern (see
/// <c>docs/ir-definition-design.md</c> §1, §3). The bridge IR is the
/// Anthropic Messages shape, which has no typed home for the provider-specific
/// knobs other clients/backends send (Codex/Responses sends <c>store</c>,
/// <c>service_tier</c>, <c>include</c>, <c>prompt_cache_key</c>,
/// <c>text.verbosity</c>, …). Rather than grow the IR body with per-provider
/// fields, those un-modeled knobs ride here, keyed by provider name, as opaque
/// JSON the pipeline never interprets.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rules</b> (<c>docs/ir-definition-design.md</c> §3.3): namespaced by
/// provider (a single IR can hold <c>openai</c> AND <c>anthropic</c> keys; each
/// emitter reads only its own and ignores the rest); opaque to the core (values
/// are <see cref="JsonElement"/> copied verbatim, never parsed); every converter
/// MUST copy it through (the Vercel drop-the-bag bug #5942/#9731 is our
/// mandatory survival test, A3).
/// </para>
/// <para>
/// <b>Wire shape</b>: an explicit <c>by_provider</c> dictionary property (design
/// §3.3 / F1 — "explicit property in JsonContext", chosen over
/// <c>[JsonExtensionData]</c>). The flattened-extension-data form is impossible
/// here anyway: STJ forbids <c>[JsonExtensionData]</c> on a record whose
/// dictionary also binds a primary-constructor parameter
/// (<c>ThrowInvalidOperationException_ExtensionDataCannotBindToCtorParam</c>) —
/// so an explicit property is both the design's lean AND the only AOT-clean
/// option. The type serializes as
/// <c>"provider_extensions":{"by_provider":{"openai":{…}}}</c>. This field never
/// appears on any real upstream wire: on the <c>/cc</c> Claude Code path it is
/// always <c>null</c> (omitted by the context-wide <c>WhenWritingNull</c>,
/// keeping the hot path byte-identical — H1); on the Codex path T2 reads the
/// knobs back out and re-applies them to the Responses request, discarding this
/// envelope. So the wrapper segment is a free, internal-only detail.
/// </para>
/// <para>
/// <b>AOT</b>: a <c>Dictionary&lt;string,JsonElement&gt;</c> property is
/// source-gen supported and reflection-free; registered explicitly in
/// <c>JsonContext</c>.
/// </para>
/// </remarks>
internal sealed record ProviderExtensions
{
    /// <summary>
    /// provider-name → opaque JSON object the bridge never interprets. e.g.
    /// <c>["openai"] = {"store":false,"service_tier":"default",
    /// "include":["reasoning.encrypted_content"],"text":{"verbosity":"low"}}</c>.
    /// </summary>
    public Dictionary<string, JsonElement> ByProvider { get; init; } = new();
}
