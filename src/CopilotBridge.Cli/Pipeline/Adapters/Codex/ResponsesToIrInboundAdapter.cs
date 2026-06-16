using System.Text.Json;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Models.Common;
using CopilotBridge.Cli.Models.Responses;
using CopilotBridge.Cli.Pipeline.Adapters;
using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Pipeline.Adapters.Codex;

/// <summary>
/// T1 — the Codex client-edge INBOUND translator: Codex
/// <see cref="ResponsesRequest"/> → IR <see cref="MessagesRequest"/> (Anthropic
/// shape). Real translator (not identity like Claude Code's), per
/// <c>docs/codex-implementation-design.md</c> §2/§4 and the §4 mapping table.
/// </summary>
/// <remarks>
/// <para>The mapping (design §4):</para>
/// <list type="bullet">
///   <item><c>instructions</c> → top-level <c>system</c>.</item>
///   <item><c>input[]</c> messages → <c>messages[]</c>; <c>developer</c> role →
///         <c>system</c> content folded into the system prompt; <c>input_text</c>/
///         <c>output_text</c> → <c>TextBlockParam</c>; <c>input_image</c> →
///         <c>ImageBlockParam</c> (data URL → base64 source).</item>
///   <item><c>function_call</c>/<c>function_call_output</c> → <c>ToolUseBlockParam</c>/
///         <c>ToolResultBlockParam</c> (call_id ↔ id, arguments STRING ↔ input
///         OBJECT, byte-faithful).</item>
///   <item><c>reasoning.effort</c> → <c>OutputConfig.Effort</c>; a reasoning item's
///         <c>encrypted_content</c> → <c>RedactedThinkingBlockParam.Data</c>.</item>
///   <item>Un-modeled knobs (<c>store</c>, <c>service_tier</c>, <c>include</c>,
///         <c>prompt_cache_key</c>, <c>text</c>, <c>parallel_tool_calls</c>,
///         <c>tools</c>, <c>tool_choice</c>, <c>client_metadata</c>,
///         <c>reasoning.summary</c>) → request-level
///         <c>ProviderExtensions["openai"]</c> verbatim, for T2 to re-apply.</item>
/// </list>
/// <para>The bag is what makes the hub-IR round-trip lossless: anything the
/// Anthropic IR body can't type rides it through and T2 re-emits it. T2 then
/// applies the probe-derived coercions (effort clamp, strip service_tier, drop
/// image_generation).</para>
/// </remarks>
internal sealed class ResponsesToIrInboundAdapter : IClientInboundAdapter<ResponsesRequest, MessagesRequest>
{
    /// <summary>The provider key the Codex knobs ride under in the IR bag.</summary>
    internal const string OpenAiProviderKey = "openai";

    private readonly ILogger<ResponsesToIrInboundAdapter> _log;

    public ResponsesToIrInboundAdapter(ILogger<ResponsesToIrInboundAdapter> log)
    {
        _log = log;
    }

    public string Name => "ResponsesToIrInbound(T1)";

    public ValueTask<MessagesRequest> AdaptAsync(
        ResponsesRequest clientBody,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken ct)
    {
        // ── system: instructions + any developer-role messages ──
        var systemParts = new List<TextBlockParam>();
        if (!string.IsNullOrEmpty(clientBody.Instructions))
            systemParts.Add(new TextBlockParam { Text = clientBody.Instructions });

        // ── messages: input[] items → MessageParam[] ──
        var messages = new List<MessageParam>();
        foreach (var item in clientBody.Input)
        {
            switch (item)
            {
                case ResponsesMessageItem msg:
                    if (string.Equals(msg.Role, "developer", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(msg.Role, "system", StringComparison.OrdinalIgnoreCase))
                    {
                        // developer/system preamble folds into the IR system prompt
                        // (Anthropic has no mid-array system; the IR top-level system
                        // is where harness instructions belong).
                        foreach (var part in msg.Content)
                            if (part is ResponsesInputTextPart t)
                                systemParts.Add(new TextBlockParam { Text = t.Text });
                        break;
                    }
                    messages.Add(new MessageParam
                    {
                        Role = NormalizeRole(msg.Role),
                        Content = MapContentParts(msg.Content),
                    });
                    break;

                case ResponsesFunctionCallItem fc:
                    // Assistant tool call → an assistant message carrying a tool_use block.
                    messages.Add(new MessageParam
                    {
                        Role = Role.Assistant,
                        Content = [new ToolUseBlockParam
                        {
                            Id = fc.CallId,
                            Name = fc.Name,
                            Input = ParseArgumentsToElement(fc.Arguments, fc.CallId),
                        }],
                    });
                    break;

                case ResponsesFunctionCallOutputItem fco:
                    // Tool result → a user message carrying a tool_result block.
                    messages.Add(new MessageParam
                    {
                        Role = Role.User,
                        Content = [new ToolResultBlockParam
                        {
                            ToolUseId = fco.CallId,
                            Content = fco.Output,
                        }],
                    });
                    break;

                case ResponsesReasoningItem reasoning:
                    // Encrypted reasoning echo → a redacted-thinking block on an
                    // assistant message (opaque blob slot). The item's id rides the
                    // part-level ProviderExtensions bag so multi-turn reasoning
                    // identity survives the round trip; summary/content are still
                    // dropped (no typed home, not echoed by Codex on the way out).
                    // If there's no blob, skip — a redacted_thinking block needs
                    // Data, and an id-only reasoning item carries nothing forwardable.
                    if (!string.IsNullOrEmpty(reasoning.EncryptedContent))
                    {
                        messages.Add(new MessageParam
                        {
                            Role = Role.Assistant,
                            Content = [new RedactedThinkingBlockParam
                            {
                                Data = reasoning.EncryptedContent,
                                ProviderExtensions = BuildReasoningPartBag(reasoning.Id),
                            }],
                        });
                    }
                    break;
            }
        }

        // ── reasoning.effort → OutputConfig.Effort ──
        OutputConfig? outputConfig = clientBody.Reasoning?.Effort is { Length: > 0 } effort
            ? new OutputConfig { Effort = effort }
            : null;

        // ── un-modeled knobs → ProviderExtensions["openai"] verbatim ──
        var bag = BuildOpenAiBag(clientBody);

        var ir = new MessagesRequest
        {
            Model = clientBody.Model,
            // Codex always streams; honor the inbound flag. max_tokens is not in
            // Codex's 13 fields (it sends max_output_tokens rarely) — default 0,
            // the Responses backend supplies its own cap.
            MaxTokens = clientBody.MaxOutputTokens ?? 0,
            Messages = messages,
            System = systemParts.Count > 0 ? systemParts : null,
            OutputConfig = outputConfig,
            Stream = clientBody.Stream,
            ProviderExtensions = bag,
        };

        _log.LogDebug(
            "adapter {Name}: model={Model} messages={Messages} system_parts={Sys} effort={Effort} bag_keys={BagKeys}",
            Name, ir.Model, messages.Count, systemParts.Count, outputConfig?.Effort ?? "<none>",
            bag?.ByProvider.Count ?? 0);

        return ValueTask.FromResult(ir);
    }

    private static IReadOnlyList<ContentBlockParam> MapContentParts(IReadOnlyList<ResponsesContentPart> parts)
    {
        var blocks = new List<ContentBlockParam>(parts.Count);
        foreach (var part in parts)
        {
            switch (part)
            {
                case ResponsesInputTextPart t:
                    blocks.Add(new TextBlockParam { Text = t.Text });
                    break;
                case ResponsesOutputTextPart ot:
                    blocks.Add(new TextBlockParam { Text = ot.Text });
                    break;
                case ResponsesInputImagePart img:
                    blocks.Add(MapImage(img));
                    break;
            }
        }
        return blocks;
    }

    private static ImageBlockParam MapImage(ResponsesInputImagePart img)
    {
        // image_url is a data URL: data:image/png;base64,XXXX
        var url = img.ImageUrl;
        const string prefix = "data:";
        if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var comma = url.IndexOf(',');
            var semi = url.IndexOf(';');
            if (comma > 0 && semi > prefix.Length && semi < comma)
            {
                var mediaType = url[prefix.Length..semi];
                var data = url[(comma + 1)..];
                return new ImageBlockParam
                {
                    Source = new Base64ImageSource { Data = data, MediaType = mediaType },
                };
            }
        }
        // Not a data URL — carry as a URL source.
        return new ImageBlockParam { Source = new UrlImageSource { Url = url } };
    }

    private static string NormalizeRole(string role) =>
        string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) ? Role.Assistant : Role.User;

    /// <summary>
    /// Parse the Responses <c>arguments</c> STRING into a JSON object element
    /// (the IR <c>tool_use.input</c> is a JsonElement object). Byte-faithful: the
    /// underlying JSON text is parsed once; T2 reserializes it back to a string.
    /// </summary>
    /// <remarks>
    /// <c>arguments</c> is client data — prior tool calls the MODEL produced and
    /// Codex echoed back — so a malformed value is a client fault, not an upstream
    /// one (upstream is never contacted at T1). Surface it as
    /// <see cref="CodexBadRequestException"/> → HTTP 400, naming the offending
    /// <c>call_id</c>; letting the raw <see cref="JsonException"/> escape would hit
    /// the endpoint's generic catch and mis-report a 502. A non-object value (a
    /// JSON scalar or array) parses fine but violates the IR contract that
    /// <c>tool_use.input</c> is an object, so it is rejected the same way. Empty or
    /// whitespace arguments → <c>{}</c> (a valid empty-input tool call).
    /// </remarks>
    private static JsonElement ParseArgumentsToElement(string arguments, string callId)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new CodexBadRequestException(
                $"function_call '{callId}' has malformed JSON arguments: {ex.Message}");
        }
        if (root.ValueKind != JsonValueKind.Object)
            throw new CodexBadRequestException(
                $"function_call '{callId}' arguments must be a JSON object, got {root.ValueKind}.");
        return root;
    }

    /// <summary>
    /// Collect every Responses field with no typed home in the Anthropic IR into
    /// a single <c>openai</c> JSON object, carried verbatim through the IR. T2
    /// reads it back out. Only includes fields that were actually present.
    /// Written with <see cref="Utf8JsonWriter"/> (no <c>JsonNode</c> generic
    /// <c>Add</c>, which trips IL2026/IL3050) so it stays AOT-clean.
    /// </summary>
    private static ProviderExtensions? BuildOpenAiBag(ResponsesRequest req)
    {
        var hasAny =
            req.Tools is not null
            || req.ToolChoice is not null
            || req.ParallelToolCalls is not null
            || req.Store is not null
            || req.Include is { Count: > 0 }
            || !string.IsNullOrEmpty(req.PromptCacheKey)
            || !string.IsNullOrEmpty(req.ServiceTier)
            || req.Text is not null
            || req.Reasoning?.Summary is { Length: > 0 }
            || req.ClientMetadata is not null;
        if (!hasAny) return null;

        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            if (req.Tools is { } tools)
            {
                w.WritePropertyName("tools");
                tools.WriteTo(w);
            }
            if (req.ToolChoice is { } tc)
            {
                w.WritePropertyName("tool_choice");
                tc.WriteTo(w);
            }
            if (req.ParallelToolCalls is { } ptc)
                w.WriteBoolean("parallel_tool_calls", ptc);
            if (req.Store is { } store)
                w.WriteBoolean("store", store);
            if (req.Include is { Count: > 0 })
            {
                w.WriteStartArray("include");
                foreach (var inc in req.Include) w.WriteStringValue(inc);
                w.WriteEndArray();
            }
            if (!string.IsNullOrEmpty(req.PromptCacheKey))
                w.WriteString("prompt_cache_key", req.PromptCacheKey);
            if (!string.IsNullOrEmpty(req.ServiceTier))
                w.WriteString("service_tier", req.ServiceTier);
            if (req.Text is { } text)
            {
                w.WritePropertyName("text");
                JsonSerializer.Serialize(w, text, JsonContext.Default.TextControls);
            }
            if (req.Reasoning?.Summary is { Length: > 0 } summary)
                w.WriteString("reasoning_summary", summary);
            if (req.ClientMetadata is { } cm)
            {
                w.WritePropertyName("client_metadata");
                cm.WriteTo(w);
            }
            w.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(buffer.ToArray());
        return new ProviderExtensions
        {
            ByProvider = new Dictionary<string, JsonElement> { [OpenAiProviderKey] = doc.RootElement.Clone() },
        };
    }

    /// <summary>
    /// Build the part-level <c>openai</c> bag for a reasoning item, carrying its
    /// <c>id</c> so multi-turn reasoning identity survives T1→IR→T2 (T2 has no
    /// other place to recover it from a <see cref="RedactedThinkingBlockParam"/>).
    /// Null when there's no id — keeps the block's bag <c>null</c> so it stays
    /// inert for every Claude Code block (H1). Same AOT-clean
    /// <see cref="Utf8JsonWriter"/> style as <see cref="BuildOpenAiBag"/>.
    /// </summary>
    private static ProviderExtensions? BuildReasoningPartBag(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;

        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("reasoning_id", id);
            w.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(buffer.ToArray());
        return new ProviderExtensions
        {
            ByProvider = new Dictionary<string, JsonElement> { [OpenAiProviderKey] = doc.RootElement.Clone() },
        };
    }
}
