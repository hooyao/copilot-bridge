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
///         semantic <c>system</c> content plus an ordered provider source record; <c>input_text</c>/
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
///         <c>reasoning.summary/context</c>, future envelope/item siblings) → request/message/part-level
///         <c>ProviderExtensions["openai"]</c> verbatim, for T2 to re-apply.</item>
/// </list>
/// <para>The bag is what makes the hub-IR round-trip lossless: anything the
/// Anthropic IR body can't type rides it through and T2 re-emits it. T2 then
/// applies the probe-derived coercions (effort clamp, strip service_tier, drop
/// image_generation).</para>
/// </remarks>
internal sealed class ResponsesToIrInboundAdapter : IClientInboundAdapter<ResponsesRequest, MessagesRequest>
{
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
        if (clientBody.Instructions is not null)
            systemParts.Add(new TextBlockParam { Text = clientBody.Instructions });

        // ── messages: input[] items → MessageParam[] ──
        var messages = new List<MessageParam>();
        // ── passthrough items: opaque input[] items the bridge forwards verbatim,
        // never interprets — additional_tools (gpt-5.6 harness tool-registration
        // preamble), agent_message (inter-agent messages), standalone named function
        // outputs (external tool events with no preceding call), and any UNKNOWN type.
        // They must keep their ORDER relative to the conversation messages (and each
        // other), so each records the count of IR messages emitted BEFORE it
        // (afterMessageIndex); T2 re-inserts each at that position via a single ordered
        // mechanism. ──
        var passthroughItems = new List<(int AfterMessageIndex, JsonElement Raw, int? SystemGroup)>();
        var nextSystemGroup = 0;
        foreach (var item in clientBody.Input)
        {
            switch (item)
            {
                case ResponsesMessageItem msg:
                    if (string.Equals(msg.Role, "developer", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(msg.Role, "system", StringComparison.OrdinalIgnoreCase))
                    {
                        // Keep the content visible to semantic system stages while the
                        // provider record retains the original Responses item/position.
                        // T2 pulls the record and suppresses only these marked blocks
                        // from top-level instructions. No destination is consulted here.
                        var systemGroup = nextSystemGroup++;
                        var systemPartIndex = 0;
                        foreach (var part in msg.Content)
                            if (part is ResponsesInputTextPart t)
                                systemParts.Add(new TextBlockParam
                                {
                                    Text = t.Text,
                                    ProviderExtensions = BuildSystemSourceBag(
                                        systemGroup, systemPartIndex++, t.ExtensionData),
                                });
                        passthroughItems.Add((
                            messages.Count,
                            SerializeKnownInputItem(msg),
                            systemGroup));
                        break;
                    }
                    messages.Add(new MessageParam
                    {
                        Role = NormalizeRole(msg.Role),
                        Content = MapContentParts(msg.Content),
                        ProviderExtensions = BuildMessageBag(msg),
                    });
                    break;

                case ResponsesFunctionCallItem fc:
                    // Assistant tool call → an assistant message carrying a tool_use block.
                    // A plain function tool's arguments are a JSON object; a CUSTOM
                    // (grammar) tool's — Codex's `exec` echoed back — are raw TEXT
                    // (JavaScript), which is not a JSON object. Carry either: JSON
                    // objects become the tool_use.input element directly; raw text is
                    // wrapped as a JSON string element + a block marker so T2 re-emits
                    // it as the raw `arguments` string (Copilot accepts a function_call
                    // with raw-text arguments — live-probed 200, CustomToolEchoProbe).
                    // A NON-default `namespace` (gpt-5.6 collaboration/MCP tool) rides
                    // the same part bag so T2 re-emits it on echo — dropping it 400s
                    // the next turn (live-replayed, NamespaceRealReplayProbe).
                    var (fcInput, fcGrammarText) = ParseArgumentsToElement(fc.Arguments);
                    messages.Add(new MessageParam
                    {
                        Role = Role.Assistant,
                        Content = [new ToolUseBlockParam
                        {
                            Id = fc.CallId,
                            Name = fc.Name,
                            Input = fcInput,
                            ProviderExtensions = BuildFunctionCallPartBag(fcGrammarText, fc),
                        }],
                    });
                    break;

                case ResponsesFunctionCallOutputItem fco
                    when fco.CallId is { Length: > 0 } callId:
                    // Tool result → a user message carrying a tool_result block. The
                    // `output` value is Codex's OWN Responses payload (string, object,
                    // or Responses content items) — it is NOT an Anthropic content-block
                    // array, even when its JSON happens to resemble one. Mark it opaque
                    // on the part bag so T2 re-emits it verbatim instead of reading it
                    // as Anthropic blocks. A Claude Code tool_result carries no such
                    // marker, so T2 is free to interpret its blocks.
                    messages.Add(new MessageParam
                    {
                        Role = Role.User,
                        Content = [new ToolResultBlockParam
                        {
                            ToolUseId = callId,
                            Content = fco.Output,
                            ProviderExtensions = BuildOpaqueToolOutputBag(fco),
                        }],
                    });
                    break;

                case ResponsesFunctionCallOutputItem standalone:
                    // Codex 0.153.3+ supports a second, deliberately UNPAIRED shape for
                    // external tool events injected through thread/inject_items:
                    // {type:function_call_output,name,namespace?,output}, with no
                    // call_id because the model never made a preceding call. The app-
                    // server contract says this retains tool-tier authority. The frozen
                    // Anthropic IR has no honest semantic representation for it, so carry
                    // the complete item through the ordered OpenAI provider bag. Turning
                    // it into a user message would lower authority; inventing a call id
                    // is rejected by Copilot as "No tool call found".
                    passthroughItems.Add((
                        messages.Count,
                        SerializeKnownInputItem(standalone),
                        null));
                    break;

                case ResponsesReasoningItem reasoning:
                    // Encrypted reasoning echo → a redacted-thinking block on an
                    // assistant message (opaque blob slot). The item's id rides the
                    // part-level ProviderExtensions bag so multi-turn reasoning
                    // identity plus opaque summary/content ride the part-level bag
                    // so a detailed-reasoning echo remains a valid Responses item.
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
                                ProviderExtensions = BuildReasoningPartBag(reasoning),
                            }],
                        });
                    }
                    else
                    {
                        // No semantic redacted-thinking representation exists, but the
                        // provider item still belongs at its original position.
                        passthroughItems.Add((
                            messages.Count,
                            SerializeKnownInputItem(reasoning),
                            null));
                    }
                    break;

                case ResponsesUnknownItem unknown:
                    // An input[] type the bridge doesn't model — the gpt-5.6 harness
                    // `additional_tools` tool-registration preamble, an inter-agent
                    // `agent_message`, or a future feature (tool_search_call, compaction,
                    // …). Forward it VERBATIM, in order — never reject it, never lose a
                    // field (the WHOLE item rides as unknown.Raw, every sibling field and
                    // the encrypted_content blob included). This is the universal escape
                    // hatch that ends the per-type whack-a-mole. Order is preserved via
                    // the recorded IR-message count, so an item that precedes another opaque
                    // item (e.g. an unknown before the additional_tools preamble) keeps its
                    // place.
                    passthroughItems.Add((messages.Count, unknown.Raw, null));
                    break;
            }
        }

        // ── reasoning.effort → OutputConfig.Effort ──
        OutputConfig? outputConfig = clientBody.Reasoning?.Effort is { Length: > 0 } effort
            ? new OutputConfig { Effort = effort }
            : null;

        // ── un-modeled knobs → ProviderExtensions["openai"] verbatim ──
        var bag = BuildOpenAiBag(clientBody, passthroughItems);

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
                    blocks.Add(new TextBlockParam
                    {
                        Text = t.Text,
                        ProviderExtensions = BuildContentPartBag(t),
                    });
                    break;
                case ResponsesOutputTextPart ot:
                    blocks.Add(new TextBlockParam
                    {
                        Text = ot.Text,
                        ProviderExtensions = BuildContentPartBag(ot),
                    });
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
                    ProviderExtensions = BuildContentPartBag(img),
                };
            }
        }
        // Not a data URL — carry as a URL source.
        return new ImageBlockParam
        {
            Source = new UrlImageSource { Url = url },
            ProviderExtensions = BuildContentPartBag(img),
        };
    }

    private static string NormalizeRole(string role) =>
        string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) ? Role.Assistant : Role.User;

    /// <summary>
    /// Turn the Responses <c>arguments</c> STRING into the IR <c>tool_use.input</c>
    /// element, returning whether it is <b>grammar text</b> (raw, non-JSON) rather
    /// than a JSON object. Two shapes reach here:
    /// <list type="bullet">
    ///   <item>A plain FUNCTION tool's arguments — a JSON object. Parsed to that
    ///         object element; <c>grammarText=false</c>. T2 re-emits via
    ///         <c>GetRawText()</c>, byte-faithful.</item>
    ///   <item>A CUSTOM (grammar) tool's arguments — raw text (Codex's `exec`
    ///         echoing back JavaScript). NOT a JSON object; parsing it as JSON is the
    ///         old bug (<c>ExpectedStartOfValueNotFound</c> → 400). Wrapped as a JSON
    ///         STRING element and returned with <c>grammarText=true</c> so T2 re-emits
    ///         the raw string as <c>arguments</c> (Copilot accepts a function_call with
    ///         raw-text arguments — live-probed 200).</item>
    /// </list>
    /// Empty/whitespace → <c>{}</c> (a valid empty-input tool call), not grammar text.
    /// </summary>
    /// <remarks>
    /// The old contract "<c>tool_use.input</c> MUST be a JSON object, else 400" was
    /// wrong for custom tools: their input is legitimately non-JSON, and Copilot round-
    /// trips it fine. We no longer reject a non-JSON / non-object value — we carry it
    /// as grammar text. A JSON <em>scalar/array</em> (rare; a malformed function tool)
    /// also lands in the grammar-text path rather than 400ing — carried through as its
    /// raw text, which is the least-surprising, lossless behavior.
    /// </remarks>
    private static (JsonElement Input, bool GrammarText) ParseArgumentsToElement(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            using var empty = JsonDocument.Parse("{}");
            return (empty.RootElement.Clone(), false);
        }
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                return (doc.RootElement.Clone(), false);
            // Valid JSON but not an object (scalar/array) — treat as grammar text so
            // it round-trips losslessly rather than being rejected.
        }
        catch (JsonException)
        {
            // Not JSON at all — a custom (grammar) tool's raw-text arguments.
        }
        // Wrap the raw arguments as a JSON string element; mark the block grammar-text.
        return (WrapAsStringElement(arguments), true);
    }

    /// <summary>Wrap a raw string as a JSON string <see cref="JsonElement"/>.</summary>
    private static JsonElement WrapAsStringElement(string raw)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
            w.WriteStringValue(raw);
        using var doc = JsonDocument.Parse(buffer.ToArray());
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Part-level <c>openai</c> bag for a tool_use block echoed from a Codex
    /// <c>function_call</c>, carrying the two markers T2 needs to re-emit it faithfully:
    /// <list type="bullet">
    ///   <item><c>grammar_text_arguments:true</c> when the block's <c>input</c> is
    ///   grammar text (raw, non-JSON) — so T2 writes <c>arguments</c> as the raw string,
    ///   not a JSON-serialized string (Codex's <c>exec</c>).</item>
    ///   <item><c>namespace:"&lt;ns&gt;"</c> when the tool belongs to a NON-default
    ///   namespace (gpt-5.6 collaboration/MCP) — so T2 re-emits <c>"namespace"</c> on the
    ///   echoed function_call, without which the next turn 400s
    ///   (<c>Missing namespace for function_call</c>).</item>
    /// </list>
    /// Returns <c>null</c> when NEITHER applies (every plain Claude Code / default-namespace
    /// JSON function tool) so the block's bag stays null and H1 remains byte-identical.
    /// Same AOT-clean <see cref="Utf8JsonWriter"/> style as the reasoning-id bag.
    /// </summary>
    private static ProviderExtensions? BuildFunctionCallPartBag(
        bool grammarText,
        ResponsesFunctionCallItem item)
    {
        var hasNamespace = !string.IsNullOrEmpty(item.Namespace);
        var hasExtras = item.Id is not null
            || item.Status is not null
            || item.ExtensionData is { Count: > 0 };
        if (!grammarText && !hasNamespace && !hasExtras) return null;

        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            if (grammarText)
                w.WriteBoolean("grammar_text_arguments", true);
            if (hasNamespace)
                w.WriteString("namespace", item.Namespace);
            WriteItemExtras(w, item.Id, item.Status, phase: null, item.ExtensionData);
            w.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(buffer.ToArray());
        return new ProviderExtensions
        {
            ByProvider = new Dictionary<string, JsonElement> { [ProviderExtensions.OpenAiNamespace] = doc.RootElement.Clone() },
        };
    }

    /// <summary>
    /// Collect every Responses field with no typed home in the Anthropic IR into
    /// a single <c>openai</c> JSON object, carried verbatim through the IR. T2
    /// reads it back out. Only includes fields that were actually present.
    /// Written with <see cref="Utf8JsonWriter"/> (no <c>JsonNode</c> generic
    /// <c>Add</c>, which trips IL2026/IL3050) so it stays AOT-clean.
    /// </summary>
    private static ProviderExtensions? BuildOpenAiBag(
        ResponsesRequest req,
        IReadOnlyList<(int AfterMessageIndex, JsonElement Raw, int? SystemGroup)> passthroughItems)
    {
        var hasAny =
            req.Tools is not null
            || req.ToolChoice is not null
            || req.ParallelToolCalls is not null
            || req.Store is not null
            || req.Include is not null
            || req.PromptCacheKey is not null
            || req.ServiceTier is not null
            || req.Text is not null
            || req.Reasoning is not null
            || req.ClientMetadata is not null
            || req.ExtensionData is { Count: > 0 }
            || passthroughItems.Count > 0;
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
            if (req.Include is not null)
            {
                w.WriteStartArray("include");
                foreach (var inc in req.Include) w.WriteStringValue(inc);
                w.WriteEndArray();
            }
            if (req.PromptCacheKey is not null)
                w.WriteString("prompt_cache_key", req.PromptCacheKey);
            if (req.ServiceTier is not null)
                w.WriteString("service_tier", req.ServiceTier);
            if (req.Text is { } text)
            {
                w.WritePropertyName("text");
                JsonSerializer.Serialize(w, text, JsonContext.Default.TextControls);
            }
            if (req.Reasoning is { } reasoning)
            {
                w.WriteBoolean("reasoning_present", true);
                if (reasoning.Summary is not null)
                    w.WriteString("reasoning_summary", reasoning.Summary);
                if (reasoning.Context.ValueKind != JsonValueKind.Undefined)
                {
                    w.WritePropertyName("reasoning_context");
                    reasoning.Context.WriteTo(w);
                }
                WriteExtraObject(w, "reasoning_extra", reasoning.ExtensionData);
            }
            if (req.ClientMetadata is { } cm)
            {
                w.WritePropertyName("client_metadata");
                cm.WriteTo(w);
            }
            // passthrough_items → an ORDERED array of {after, raw}: each is an
            // agent_message (gpt-5.6 inter-agent) or an UNKNOWN input[] item the bridge
            // doesn't model, carried VERBATIM (raw bytes via WriteRawValue, so an
            // encrypted_content blob is byte-faithful). `after` is the count of IR
            // messages that preceded it, so T2 re-inserts it at the right point in the
            // conversation flow. This + the unknown-item converter is the universal
            // escape hatch that ends the per-type 400 whack-a-mole.
            if (passthroughItems.Count > 0)
            {
                w.WriteStartArray("passthrough_items");
                foreach (var (after, raw, systemGroup) in passthroughItems)
                {
                    w.WriteStartObject();
                    w.WriteNumber("after", after);
                    if (systemGroup is { } group)
                        w.WriteNumber("system_group", group);
                    w.WritePropertyName("raw");
                    w.WriteRawValue(raw.GetRawText());
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            WriteExtraObject(w, "request_extra", req.ExtensionData);
            w.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(buffer.ToArray());
        return new ProviderExtensions
        {
            ByProvider = new Dictionary<string, JsonElement> { [ProviderExtensions.OpenAiNamespace] = doc.RootElement.Clone() },
        };
    }

    /// <summary>
    /// Build the part-level <c>openai</c> bag for a reasoning item, carrying every
    /// opaque field the frozen IR cannot type: <c>id</c>, <c>summary</c>, and
    /// <c>content</c>. A detailed-reasoning tool turn can omit id while summary is
    /// required on echo, so the bag must not be gated on id alone. Null only when
    /// all three are absent — keeping every Claude Code block inert (H1). Same AOT-clean
    /// <see cref="Utf8JsonWriter"/> style as <see cref="BuildOpenAiBag"/>.
    /// </summary>
    private static ProviderExtensions? BuildReasoningPartBag(ResponsesReasoningItem item)
    {
        if (item.Id is null
            && item.Summary.ValueKind == JsonValueKind.Undefined
            && item.Content.ValueKind == JsonValueKind.Undefined
            && item.ExtensionData is not { Count: > 0 })
            return null;

        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            if (item.Id is not null)
                w.WriteString("reasoning_id", item.Id);
            if (item.Summary.ValueKind != JsonValueKind.Undefined)
            {
                w.WritePropertyName("reasoning_summary");
                item.Summary.WriteTo(w);
            }
            if (item.Content.ValueKind != JsonValueKind.Undefined)
            {
                w.WritePropertyName("reasoning_content");
                item.Content.WriteTo(w);
            }
            WriteExtraObject(w, "reasoning_extra", item.ExtensionData);
            w.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(buffer.ToArray());
        return new ProviderExtensions
        {
            ByProvider = new Dictionary<string, JsonElement> { [ProviderExtensions.OpenAiNamespace] = doc.RootElement.Clone() },
        };
    }

    /// <summary>
    /// Mark a tool-result block's content as an OPAQUE provider payload — Codex's own
    /// Responses <c>function_call_output.output</c>, which must be re-emitted verbatim
    /// rather than interpreted as Anthropic content blocks. This is a source-side
    /// statement of fact pushed into the IR: T2 pulls it and knows not to reinterpret,
    /// without needing to know which client produced the request. A Claude Code
    /// tool_result never carries it, so its blocks stay interpretable.
    /// </summary>
    private static ProviderExtensions BuildOpaqueToolOutputBag(
        ResponsesFunctionCallOutputItem item)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteBoolean("opaque_tool_output", true);
            // Codex 0.153.3 widened the same item shape with optional name/namespace.
            // A paired output may still carry them, so keep them outside
            // responses_item_extra (whose reserved-name filter intentionally blocks
            // semantic keys) and let T2 restore them explicitly.
            if (item.Name is not null)
                w.WriteString("function_output_name", item.Name);
            if (item.Namespace is not null)
                w.WriteString("function_output_namespace", item.Namespace);
            WriteItemExtras(w, item.Id, item.Status, phase: null, item.ExtensionData);
            w.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(buffer.ToArray());
        return new ProviderExtensions
        {
            ByProvider = new Dictionary<string, JsonElement> { [ProviderExtensions.OpenAiNamespace] = doc.RootElement.Clone() },
        };
    }

    private static ProviderExtensions? BuildMessageBag(ResponsesMessageItem item)
    {
        if (item.Id is null
            && item.Phase is null
            && item.Status is null
            && item.ExtensionData is not { Count: > 0 })
            return null;

        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            WriteItemExtras(w, item.Id, item.Status, item.Phase, item.ExtensionData);
            w.WriteEndObject();
        }
        return WrapOpenAiBag(buffer);
    }

    private static ProviderExtensions BuildSystemSourceBag(
        int group,
        int part,
        IReadOnlyDictionary<string, JsonElement>? contentExtras)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("responses_system_group", group);
            w.WriteNumber("responses_system_part", part);
            WriteExtraObject(w, "responses_content_extra", contentExtras);
            w.WriteEndObject();
        }
        return WrapOpenAiBag(buffer)!;
    }

    private static ProviderExtensions? BuildContentPartBag(ResponsesContentPart part)
    {
        JsonElement? annotations = null;
        string? detail = null;
        IReadOnlyDictionary<string, JsonElement>? extras = null;
        switch (part)
        {
            case ResponsesInputTextPart input:
                extras = input.ExtensionData;
                break;
            case ResponsesOutputTextPart output:
                annotations = output.Annotations;
                extras = output.ExtensionData;
                break;
            case ResponsesInputImagePart image:
                detail = image.Detail;
                extras = image.ExtensionData;
                break;
        }
        if (annotations is null && detail is null && extras is not { Count: > 0 })
            return null;

        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteStartObject("responses_content_extra");
            if (annotations is { } annotationValue)
            {
                w.WritePropertyName("annotations");
                annotationValue.WriteTo(w);
            }
            if (detail is not null)
                w.WriteString("detail", detail);
            WriteExtensionFields(w, extras);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return WrapOpenAiBag(buffer);
    }

    private static JsonElement SerializeKnownInputItem(ResponsesInputItem item) =>
        JsonSerializer.SerializeToElement(item, JsonContext.Default.ResponsesInputItem);

    private static void WriteItemExtras(
        Utf8JsonWriter w,
        string? id,
        string? status,
        string? phase,
        IReadOnlyDictionary<string, JsonElement>? extras)
    {
        if (id is null && status is null && phase is null && extras is not { Count: > 0 })
            return;
        w.WriteStartObject("responses_item_extra");
        if (id is not null) w.WriteString("id", id);
        if (phase is not null) w.WriteString("phase", phase);
        if (status is not null) w.WriteString("status", status);
        WriteExtensionFields(w, extras);
        w.WriteEndObject();
    }

    private static void WriteExtraObject(
        Utf8JsonWriter w,
        string name,
        IReadOnlyDictionary<string, JsonElement>? extras)
    {
        if (extras is not { Count: > 0 }) return;
        w.WriteStartObject(name);
        WriteExtensionFields(w, extras);
        w.WriteEndObject();
    }

    private static void WriteExtensionFields(
        Utf8JsonWriter w,
        IReadOnlyDictionary<string, JsonElement>? extras)
    {
        if (extras is null) return;
        foreach (var (name, value) in extras)
        {
            w.WritePropertyName(name);
            value.WriteTo(w);
        }
    }

    private static ProviderExtensions? WrapOpenAiBag(MemoryStream buffer)
    {
        using var doc = JsonDocument.Parse(buffer.ToArray());
        return new ProviderExtensions
        {
            ByProvider = new Dictionary<string, JsonElement>
            {
                [ProviderExtensions.OpenAiNamespace] = doc.RootElement.Clone(),
            },
        };
    }
}
