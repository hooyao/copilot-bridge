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
        // ── passthrough items: opaque input[] items the bridge forwards verbatim,
        // never interprets — additional_tools (gpt-5.6 harness tool-registration
        // preamble), agent_message (inter-agent messages), and any UNKNOWN type. They
        // must keep their ORDER relative to the conversation messages (and each other),
        // so each records the count of IR messages emitted BEFORE it (afterMessageIndex);
        // T2 re-inserts each at that position via a single ordered mechanism. ──
        var passthroughItems = new List<(int AfterMessageIndex, JsonElement Raw)>();
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
                            ProviderExtensions = BuildFunctionCallPartBag(fcGrammarText, fc.Namespace),
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

                case ResponsesAdditionalToolsItem addTools:
                    // Harness tool-registration preamble (gpt-5.6+) — opaque, not
                    // conversation content, re-emitted verbatim. It rides the SAME
                    // ordered passthrough mechanism as unknown items (recording its input
                    // position via messages.Count) so true input order is preserved even
                    // if it is not input[0] — every capture shows it first, but the
                    // Responses schema doesn't guarantee that, and hoisting it ahead of a
                    // preceding unknown item would reorder the array.
                    passthroughItems.Add((messages.Count, SerializeAdditionalTools(addTools)));
                    break;

                case ResponsesUnknownItem unknown:
                    // An input[] type the bridge doesn't model — a gpt-5.6 inter-agent
                    // `agent_message`, or a future feature (tool_search_call, compaction,
                    // …). Forward it VERBATIM, in order — never reject it, never lose a
                    // field (the whole item rides as unknown.Raw, encrypted_content and
                    // all). This is the universal escape hatch that ends the per-type
                    // whack-a-mole.
                    passthroughItems.Add((messages.Count, unknown.Raw));
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
    private static ProviderExtensions? BuildFunctionCallPartBag(bool grammarText, string? ns)
    {
        var hasNamespace = !string.IsNullOrEmpty(ns);
        if (!grammarText && !hasNamespace) return null;

        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            if (grammarText)
                w.WriteBoolean("grammar_text_arguments", true);
            if (hasNamespace)
                w.WriteString("namespace", ns);
            w.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(buffer.ToArray());
        return new ProviderExtensions
        {
            ByProvider = new Dictionary<string, JsonElement> { [OpenAiProviderKey] = doc.RootElement.Clone() },
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
        IReadOnlyList<(int AfterMessageIndex, JsonElement Raw)> passthroughItems)
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
            || req.ClientMetadata is not null
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
                foreach (var (after, raw) in passthroughItems)
                {
                    w.WriteStartObject();
                    w.WriteNumber("after", after);
                    w.WritePropertyName("raw");
                    w.WriteRawValue(raw.GetRawText());
                    w.WriteEndObject();
                }
                w.WriteEndArray();
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
    /// Re-serialize an <see cref="ResponsesAdditionalToolsItem"/> to a byte-faithful
    /// <c>{type:"additional_tools", role?, tools}</c> element for the ordered passthrough.
    /// <c>tools</c> is written with <c>WriteRawValue(GetRawText())</c> — NOT
    /// <c>WriteTo</c> — so the original lexical bytes survive (Copilot's reserved
    /// <c>collaboration.*</c> schemas ride here). T2 re-emits it into <c>input[]</c> at
    /// its recorded position.
    /// </summary>
    private static JsonElement SerializeAdditionalTools(ResponsesAdditionalToolsItem addTools)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("type", "additional_tools");
            if (addTools.Role is { } role)
                w.WriteString("role", role);
            w.WritePropertyName("tools");
            w.WriteRawValue(addTools.Tools.GetRawText());
            w.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(buffer.ToArray());
        return doc.RootElement.Clone();
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
