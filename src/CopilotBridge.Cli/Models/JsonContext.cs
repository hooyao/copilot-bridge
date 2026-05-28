using System.Text.Json.Serialization;
using CopilotBridge.Cli.Models.Anthropic.Models;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Models.Anthropic.Response;
using CopilotBridge.Cli.Models.Anthropic.Stream;
using CopilotBridge.Cli.Models.Copilot;
using CopilotBridge.Cli.Models.GitHub;

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
internal partial class JsonContext : JsonSerializerContext;
