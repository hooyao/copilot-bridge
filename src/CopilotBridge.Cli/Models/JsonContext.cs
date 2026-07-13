using System.Text.Json.Serialization;
using CopilotBridge.Cli.Models.Anthropic.Errors;
using CopilotBridge.Cli.Models.Anthropic.Models;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Models.Anthropic.Response;
using CopilotBridge.Cli.Models.Anthropic.Stream;
using CopilotBridge.Cli.Models.Common;
using CopilotBridge.Cli.Models.Copilot;
using CopilotBridge.Cli.Models.GitHub;
using CopilotBridge.Cli.Models.Responses;

namespace CopilotBridge.Cli.Models;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    // Claude Code emits objects with `type` at the END of the property list (e.g.
    // `{"budget_tokens":31999,"type":"enabled"}` for `thinking`); STJ's polymorphic
    // deserializer otherwise requires the discriminator first and would 400 the
    // request. .NET 9+ buffers properties until the discriminator is found.
    AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(DeviceCodeRequest))]
[JsonSerializable(typeof(DeviceCodeResponse))]
[JsonSerializable(typeof(AccessTokenRequest))]
[JsonSerializable(typeof(AccessTokenResponse))]
[JsonSerializable(typeof(GitHubUser))]
// Public GitHub Releases REST surface — read anonymously by the startup
// auto-update discovery client. A List<GitHubRelease> is the page shape; the
// element and its assets are reachable from it. SnakeCaseLower maps
// tag_name/browser_download_url/digest/published_at automatically.
[JsonSerializable(typeof(List<GitHubRelease>))]
[JsonSerializable(typeof(GitHubRelease))]
[JsonSerializable(typeof(GitHubReleaseAsset))]
[JsonSerializable(typeof(CopilotTokenResponse))]
[JsonSerializable(typeof(CopilotTokenEndpoints))]
[JsonSerializable(typeof(CopilotModelsResponse))]
// Anthropic surface — registered top-levels are reachable from each request /
// response shape; polymorphic derived types (ContentBlockParam variants,
// ContentBlock variants, ImageSource variants, ToolChoice variants, ThinkingConfig
// variants, ContextEdit variants, StreamEvent variants, ContentBlockDelta variants)
// are auto-discovered via [JsonDerivedType] attributes and don't need separate
// entries.
[JsonSerializable(typeof(MessagesRequest))]
[JsonSerializable(typeof(AnthropicMessage))]
[JsonSerializable(typeof(StreamEvent))]
[JsonSerializable(typeof(AnthropicModelsResponse))]
// The converter-driven properties (MessageParam.Content,
// MessagesRequest.System) need direct TypeInfo lookups for their element
// types at runtime via options.GetTypeInfo(...).
[JsonSerializable(typeof(ContentBlockParam))]
[JsonSerializable(typeof(TextBlockParam))]
// The IR's namespaced provider-extensions escape-hatch
// (docs/ir-definition-design.md §3). Reachable from MessagesRequest /
// ContentBlockParam, but registered explicitly so the source generator emits
// metadata for the [JsonExtensionData] Dictionary<string,JsonElement> shape
// directly (AOT-clean — JsonElement values copied verbatim, no reflection).
[JsonSerializable(typeof(ProviderExtensions))]
// Codex Responses-API surface (docs/codex-implementation-design.md §8). The
// /codex/responses endpoint deserializes ResponsesRequest (T1) and the strategy
// re-serializes it (T2); the streaming translators read/write the SSE event
// shapes. Polymorphic derived types (input items, content parts, tool variants,
// stream events) are auto-discovered via [JsonDerivedType]; only the top-level
// bases need explicit registration. Fully typed (no JsonElement envelope) per Q2.
[JsonSerializable(typeof(ResponsesRequest))]
[JsonSerializable(typeof(ResponsesInputItem))]
// ResponsesUnknownItem carries no [JsonDerivedType] discriminator (it's built by
// ResponsesInputItemListConverter for any unmodeled input[] type, and re-emitted via
// its Raw JsonElement, not source-gen metadata) — but register it so the generator
// emits its TypeInfo for the rare direct-serialize path (tests / the converter's Write).
[JsonSerializable(typeof(ResponsesUnknownItem))]
[JsonSerializable(typeof(ResponsesContentPart))]
[JsonSerializable(typeof(TextControls))]
[JsonSerializable(typeof(ResponsesStreamEvent))]
// Bridge-generated error responses (e.g. unknown model → 400).
[JsonSerializable(typeof(ErrorResponse))]
// Usage-probe envelopes — minimal POCOs read by UsageProbe to extract token
// counts off (a) the non-streaming response body, (b) the count_tokens
// response body, (c) message_start / message_delta SSE events. None of these
// participate in the wire shape; they exist solely so the per-request INFO
// summary can report input/output/cache token counts.
[JsonSerializable(typeof(UsageProbeEnvelope))]
[JsonSerializable(typeof(CountTokensResponse))]
[JsonSerializable(typeof(MessageStartUsageEnvelope))]
[JsonSerializable(typeof(MessageDeltaUsageEnvelope))]
internal partial class JsonContext : JsonSerializerContext;
