using System.Text.Json;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Models.Common;
using CopilotBridge.Cli.Pipeline.Adapters.Codex;
using CopilotBridge.Cli.Pipeline.Routing;

namespace CopilotBridge.Cli.Pipeline.Strategies.Codex;

[Flags]
internal enum ResponsesRequestMutation
{
    None = 0,
    EffortCoerced = 1 << 0,
    ServiceTierStripped = 1 << 1,
    StoreTrueStripped = 1 << 2,
    ImageGenerationToolDropped = 1 << 3,
    CustomToolDropped = 1 << 4,
    InvalidMessageIdDropped = 1 << 5,
    RecursiveAgentToolDropped = 1 << 6,
    ProviderConflictDropped = 1 << 7,
}

/// <summary>
/// T2 — IR <see cref="MessagesRequest"/> → Copilot <c>/responses</c> wire bytes.
/// Rebuilds a Responses request from the IR (the inverse of T1), re-applies the
/// <c>ProviderExtensions["openai"]</c> bag verbatim, then applies the
/// probe-derived coercions (per-model effort clamp, strip <c>service_tier</c>,
/// drop the <c>image_generation</c> tool) from
/// <c>docs/codex-protocol-research.md</c> §4 / the change-2 contract snapshot.
/// </summary>
/// <remarks>
/// The bag is what makes the hub-IR round-trip lossless: everything Codex sent
/// that the Anthropic IR can't type (tools, store, include, prompt_cache_key,
/// text, tool_choice, client_metadata, reasoning extras, item metadata) was stashed by T1 and
/// is re-emitted here. The IR body supplies what it CAN type (model, system →
/// instructions, messages → input, effort → reasoning.effort).
/// </remarks>
internal static class ResponsesRequestBuilder
{
    internal static string FormatMutations(ResponsesRequestMutation mutations)
    {
        if (mutations == ResponsesRequestMutation.None) return "";
        var codes = new List<string>(8);
        if ((mutations & ResponsesRequestMutation.EffortCoerced) != 0) codes.Add("profile.effort");
        if ((mutations & ResponsesRequestMutation.ServiceTierStripped) != 0) codes.Add("profile.service_tier");
        if ((mutations & ResponsesRequestMutation.StoreTrueStripped) != 0) codes.Add("profile.store_true");
        if ((mutations & ResponsesRequestMutation.ImageGenerationToolDropped) != 0) codes.Add("profile.tool.image_generation");
        if ((mutations & ResponsesRequestMutation.CustomToolDropped) != 0) codes.Add("profile.tool.custom");
        if ((mutations & ResponsesRequestMutation.InvalidMessageIdDropped) != 0) codes.Add("protocol.message_id");
        if ((mutations & ResponsesRequestMutation.RecursiveAgentToolDropped) != 0) codes.Add("guard.recursive_agent");
        if ((mutations & ResponsesRequestMutation.ProviderConflictDropped) != 0) codes.Add("protocol.provider_conflict");
        return string.Join(',', codes);
    }

    /// <summary>
    /// Build the Responses wire body from the IR. Returns the serialized bytes,
    /// whether the request carries an image (→ Copilot-Vision-Request), and the
    /// effort actually written to the wire after per-model coercion (null when no
    /// effort was set). The caller (which holds a logger) compares
    /// <see cref="MessagesRequest.OutputConfig"/>'s inbound effort against this to
    /// WARN on a fallback and to log the honest outbound value.
    /// </summary>
    public static (byte[] Body, bool Vision, string? CoercedEffort) Build(
        MessagesRequest ir,
        CodexModelProfileCatalog profiles,
        bool filterRecursiveAgentTool = false) =>
        Build(ir, profiles, filterRecursiveAgentTool, out _, out _, out _);

    /// <summary>
    /// Build overload that reports whether the recursive-delegation guard actually
    /// removed an <c>Agent</c> tool. This lets the strategy log an operator-visible
    /// warning without making the pure wire builder depend on logging.
    /// </summary>
    public static (byte[] Body, bool Vision, string? CoercedEffort) Build(
        MessagesRequest ir,
        CodexModelProfileCatalog profiles,
        bool filterRecursiveAgentTool,
        out bool removedAgentTool) =>
        Build(ir, profiles, filterRecursiveAgentTool, out removedAgentTool, out _, out _);

    /// <summary>
    /// Build overload that also reports an image downgraded because the exact model's
    /// multimodal capability is UNKNOWN (not in the catalog) rather than probed-false.
    /// Those two cases look identical on the wire — both fall back to the string path —
    /// but only the first is a signal: it is what a Copilot-side model rename looks
    /// like, and without it the image simply never reaches the model while the request
    /// still returns 200.
    /// </summary>
    public static (byte[] Body, bool Vision, string? CoercedEffort) Build(
        MessagesRequest ir,
        CodexModelProfileCatalog profiles,
        bool filterRecursiveAgentTool,
        out bool removedAgentTool,
        out bool downgradedImageOnUnknownModel) =>
        Build(
            ir,
            profiles,
            filterRecursiveAgentTool,
            out removedAgentTool,
            out downgradedImageOnUnknownModel,
            out _);

    public static (byte[] Body, bool Vision, string? CoercedEffort) Build(
        MessagesRequest ir,
        CodexModelProfileCatalog profiles,
        bool filterRecursiveAgentTool,
        out bool removedAgentTool,
        out bool downgradedImageOnUnknownModel,
        out ResponsesRequestMutation mutations)
    {
        removedAgentTool = false;
        downgradedImageOnUnknownModel = false;
        mutations = ResponsesRequestMutation.None;
        // Exact profile, or the nearest known one (best-effort fallback for a
        // Codex model newer than this build's catalog — the router already
        // WARN-logged the fuzzy match and let the request through; here we just
        // borrow the closest model's effort-clamp + custom-tool-drop rules). Only
        // a below-floor id yields null → the existing unclamped passthrough.
        var exactProfile = profiles.Get(ir.Model);
        var profile = exactProfile ?? profiles.GetNearest(ir.Model, out _, out _);
        // Pull the openai bag (un-modeled knobs T1 stashed). Absent → empty.
        JsonElement? bag = null;
        if (ir.ProviderExtensions?.ByProvider.TryGetValue(
                ProviderExtensions.OpenAiNamespace, out var b) == true)
            bag = b;

        // A positive wire-shape capability is not a defensive coercion: never borrow
        // it from a nearest profile. T2 asks only "does THIS target accept structured
        // multimodal function output?" and then pulls whatever the IR actually holds —
        // it does not ask who produced the IR. A source that means its tool output to
        // stay opaque marks it as such on the block (see IsOpaqueToolOutput).
        // THREE-valued, not boolean. A boolean conflates "probed and cannot" with
        // "never heard of this model", and only the second needs an operator signal:
        // it is what a Copilot-side rename looks like from in here. Exact matching is
        // unchanged — a positive wire capability is never borrowed from a neighbour.
        var multimodalCapability = exactProfile is null
            ? MultimodalOutputCapability.Unknown
            : exactProfile.SupportsMultimodalFunctionOutput
                ? MultimodalOutputCapability.Supported
                : MultimodalOutputCapability.Unsupported;
        var structuredMultimodalOutput =
            multimodalCapability == MultimodalOutputCapability.Supported;
        var sawUnknownModelImage = false;

        var vision = false;
        string? effort = null;   // coerced effort actually written to the wire; hoisted so the return below (outside the writer's using) can report it
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("model", ir.Model);

            // system → instructions. Blocks projected from Responses input-level
            // developer/system items remain semantic for stages but are restored to
            // input[] from provider records below, so they are excluded here.
            if (ir.System is { Count: > 0 })
            {
                var sb = new System.Text.StringBuilder();
                foreach (var s in ir.System)
                {
                    if (TryGetResponsesSystemGroup(s.ProviderExtensions, out _, out _))
                        continue;
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(s.Text);
                }
                if (sb.Length > 0 || ir.System.Any(s =>
                        !TryGetResponsesSystemGroup(s.ProviderExtensions, out _, out _)))
                    w.WriteString("instructions", sb.ToString());
            }

            // messages → input[]
            w.WritePropertyName("input");
            w.WriteStartArray();
            // Opaque passthrough items (additional_tools harness preamble, agent_message,
            // and any unknown type) are re-inserted IN ORDER, each at the point in the
            // message flow T1 recorded (`after` = the number of IR messages that preceded
            // it). Emit any with after==0 first (they preceded every message — e.g. the
            // additional_tools preamble at input[0]), then interleave the rest as messages
            // emit. One ordered mechanism for all opaque kinds preserves true input order.
            var passthrough = ReadPassthroughItems(bag);
            var systemGroups = ReadResponsesSystemGroups(ir.System);
            var ptIdx = 0;
            var emittedMsgs = 0;
            ptIdx = WritePassthroughUpTo(
                w, passthrough, systemGroups, ptIdx, emittedMsgs, ref mutations);
            foreach (var msg in ir.Messages)
            {
                WriteInputItem(
                    w,
                    msg,
                    structuredMultimodalOutput,
                    ref vision,
                    ref sawUnknownModelImage,
                    ref mutations);
                emittedMsgs++;
                ptIdx = WritePassthroughUpTo(
                    w, passthrough, systemGroups, ptIdx, emittedMsgs, ref mutations);
            }
            // Any remaining passthrough items whose `after` exceeds the message count
            // (e.g. trailing agent_message) — emit them at the end. Raw-value, not
            // WriteTo — preserve the exact bytes (WriteTo reserializes the DOM and can
            // re-escape, e.g. an encrypted_content blob; GetRawText keeps them verbatim).
            while (ptIdx < passthrough.Count)
            {
                WritePassthroughItem(w, passthrough[ptIdx], systemGroups, ref mutations);
                ptIdx++;
            }
            w.WriteEndArray();

            // effort: from IR OutputConfig, clamped to what the model accepts.
            // summary: rode the bag as "reasoning_summary" (T1); re-emit it INSIDE
            // the reasoning object alongside effort so a Codex-sent reasoning.summary
            // survives. Emit a reasoning object if EITHER is present — if coercion
            // dropped effort but a summary exists, reasoning:{summary:…} still carries
            // it (WriteBagFields drops "reasoning_summary" at the top level).
            effort = CoerceEffort(ir.OutputConfig?.Effort, profile);
            if (ir.OutputConfig?.Effort is { } inboundEffort
                && effort is { } outboundEffort
                && !string.Equals(inboundEffort, outboundEffort, StringComparison.OrdinalIgnoreCase))
                mutations |= ResponsesRequestMutation.EffortCoerced;
            var reasoningSummary = TryGetBagString(bag, "reasoning_summary");
            var reasoningContext = TryGetBagValue(bag, "reasoning_context");
            var reasoningPresent = TryGetBagBoolean(bag, "reasoning_present");
            var reasoningExtras = TryGetBagObject(bag, "reasoning_extra");
            if (effort is not null
                || reasoningSummary is not null
                || reasoningContext is not null
                || reasoningPresent
                || reasoningExtras is not null)
            {
                w.WritePropertyName("reasoning");
                w.WriteStartObject();
                if (effort is not null)
                    w.WriteString("effort", effort);
                if (reasoningSummary is not null)
                    w.WriteString("summary", reasoningSummary);
                if (reasoningContext is { } contextValue)
                {
                    w.WritePropertyName("context");
                    contextValue.WriteTo(w);
                }
                WriteObjectProperties(
                    w, reasoningExtras, ProviderExtraScope.Reasoning, ref mutations);
                w.WriteEndObject();
            }
            // Anthropic's top-level thinking configuration is a control for the
            // Anthropic backend, not replayable Responses input. Responses uses
            // reasoning.effort above; without an output_config.effort the count
            // shape deliberately emits no reasoning field. Historical plaintext
            // thinking blocks are likewise explicitly dropped in WriteInputItem.

            // max_output_tokens: T1 maps the IR's MaxTokens from Codex's
            // max_output_tokens (default 0 when Codex omits it). Emit only when set
            // (> 0) so current Codex traffic — which omits it — round-trips with no
            // added field, while a future Codex that sends it survives.
            if (ir.MaxTokens > 0)
                w.WriteNumber("max_output_tokens", ir.MaxTokens);

            if (ir.Stream is { } stream)
                w.WriteBoolean("stream", stream);

            // Re-apply the bag's un-modeled knobs, with coercions. Track whether
            // the bag supplied tools / tool_choice so the IR-derived emit below
            // does NOT double-write them for a Codex request (whose bag wins).
            var bagHasTools = false;
            var bagHasToolChoice = false;
            if (bag is { ValueKind: JsonValueKind.Object } bagObj)
            {
                bagHasTools = bagObj.TryGetProperty("tools", out _);
                bagHasToolChoice = bagObj.TryGetProperty("tool_choice", out _);
                WriteBagFields(w, bagObj, profile, ref vision, ref mutations);
            }

            // Claude Code path: the request carries typed Anthropic tools /
            // tool_choice on the IR body and has NO openai bag (bag == null). The
            // Codex round-trip stashes tools INSIDE the bag (T1 → WriteBagFields),
            // but a Claude Code request never had one — so without this the tools
            // are silently dropped and gpt-5.5 can talk but never call a tool
            // (the reported "complex tasks fail on gpt-5.5"). Emit them from the
            // IR, but only when the bag didn't already supply them: a real Codex
            // request's bag still wins, keeping that path byte-identical.
            var irToolSurvivors = new HashSet<string>(StringComparer.Ordinal);
            if (!bagHasTools)
            {
                irToolSurvivors = WriteIrTools(
                    w, ir.Tools, filterRecursiveAgentTool, out removedAgentTool);
                if (removedAgentTool)
                    mutations |= ResponsesRequestMutation.RecursiveAgentToolDropped;
            }
            // Only emit tool_choice when tools are actually on the wire — a
            // tool_choice of "required" or {function,name} with no tools array is a
            // Responses 400. Tools are present iff the bag supplied them
            // (bagHasTools) or WriteIrTools emitted at least one. For a forced tool
            // ({type:"tool",name:X}), also require X to have SURVIVED the drop
            // filter — otherwise tool_choice would name a tool absent from tools[]
            // (also a 400); WriteIrToolChoice downgrades that to "auto".
            if (!bagHasToolChoice && (bagHasTools || irToolSurvivors.Count > 0))
                WriteIrToolChoice(w, ir.ToolChoice, bagHasTools ? null : irToolSurvivors);

            w.WriteEndObject();
        }

        // Only UNKNOWN is reportable: a probed-unsupported model taking the string
        // path is the recorded expectation for that model, not news.
        downgradedImageOnUnknownModel =
            sawUnknownModelImage && multimodalCapability == MultimodalOutputCapability.Unknown;
        return (buffer.ToArray(), vision, effort);
    }

    /// <summary>
    /// Read the ordered passthrough items T1 stashed in the bag (agent_message +
    /// unknown input[] types). Each is <c>{after:int, raw:object}</c> where <c>after</c>
    /// is the number of IR messages that preceded it. Returns an empty list when the
    /// bag has none (every Claude Code / plain Codex request).
    /// </summary>
    private readonly record struct PassthroughItem(
        int After,
        JsonElement Raw,
        int? SystemGroup);

    private static IReadOnlyList<PassthroughItem> ReadPassthroughItems(JsonElement? bag)
    {
        if (bag is not { ValueKind: JsonValueKind.Object } obj
            || !obj.TryGetProperty("passthrough_items", out var items)
            || items.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<PassthroughItem>();
        foreach (var item in items.EnumerateArray())
        {
            // T1 always writes {after:int, raw:object}. These guards are purely
            // defensive against a corrupted bag (which can't happen in the normal
            // in-process flow). Rather than fail silently in a WRONG direction, they
            // degrade to the least-surprising behavior:
            //  - a malformed entry (not an object, or no `raw`) is skipped — it carries
            //    no forwardable payload, so there's nothing to preserve;
            //  - a missing/non-int `after` defaults to int.MaxValue (append at the END),
            //    NOT 0 (which would silently HOIST the item to the front of the turn and
            //    reorder the conversation). Appending is the safer failure mode.
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("raw", out var raw))
                continue;
            var after = item.TryGetProperty("after", out var a) && a.TryGetInt32(out var n) ? n : int.MaxValue;
            int? systemGroup = item.TryGetProperty("system_group", out var g)
                && g.TryGetInt32(out var group)
                    ? group
                    : null;
            list.Add(new PassthroughItem(after, raw, systemGroup));
        }
        return list;
    }

    /// <summary>
    /// Emit every passthrough item whose <c>after</c> ≤ <paramref name="emittedMsgs"/>
    /// and not yet written, starting at <paramref name="ptIdx"/>. VERBATIM via
    /// <c>WriteRawValue(GetRawText())</c> — NOT <c>WriteTo</c>, which reserializes the
    /// DOM and can re-escape the bytes (e.g. an <c>encrypted_content</c> blob). Returns
    /// the advanced index. The list is in inbound order, so a simple forward walk
    /// preserves ordering among passthrough items too.
    /// </summary>
    private static int WritePassthroughUpTo(
        Utf8JsonWriter w,
        IReadOnlyList<PassthroughItem> passthrough,
        IReadOnlyDictionary<int, IReadOnlyList<string>> systemGroups,
        int ptIdx,
        int emittedMsgs,
        ref ResponsesRequestMutation mutations)
    {
        while (ptIdx < passthrough.Count && passthrough[ptIdx].After <= emittedMsgs)
        {
            WritePassthroughItem(w, passthrough[ptIdx], systemGroups, ref mutations);
            ptIdx++;
        }
        return ptIdx;
    }

    private static void WritePassthroughItem(
        Utf8JsonWriter w,
        PassthroughItem item,
        IReadOnlyDictionary<int, IReadOnlyList<string>> systemGroups,
        ref ResponsesRequestMutation mutations)
    {
        if (item.SystemGroup is not { } group
            || !systemGroups.TryGetValue(group, out var texts))
        {
            WriteRawPassthroughItem(w, item.Raw, ref mutations);
            return;
        }
        WriteSystemSourceItem(w, item.Raw, texts, ref mutations);
    }

    private static IReadOnlyDictionary<int, IReadOnlyList<string>> ReadResponsesSystemGroups(
        IReadOnlyList<TextBlockParam>? system)
    {
        if (system is not { Count: > 0 })
            return new Dictionary<int, IReadOnlyList<string>>();

        var groups = new Dictionary<int, SortedDictionary<int, string>>();
        foreach (var part in system)
        {
            if (!TryGetResponsesSystemGroup(part.ProviderExtensions, out var group, out var index))
                continue;
            if (!groups.TryGetValue(group, out var values))
                groups[group] = values = new SortedDictionary<int, string>();
            values[index] = part.Text;
        }
        return groups.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.Values.ToList());
    }

    private static void WriteSystemSourceItem(
        Utf8JsonWriter w,
        JsonElement raw,
        IReadOnlyList<string> texts,
        ref ResponsesRequestMutation mutations)
    {
        if (raw.ValueKind != JsonValueKind.Object)
        {
            raw.WriteTo(w);
            return;
        }

        var messageItem = IsResponsesMessageItem(raw);
        w.WriteStartObject();
        foreach (var property in raw.EnumerateObject())
        {
            if (messageItem && ShouldDropMessageId(property))
            {
                mutations |= ResponsesRequestMutation.InvalidMessageIdDropped;
                continue;
            }
            if (property.Name != "content" || property.Value.ValueKind != JsonValueKind.Array)
            {
                property.WriteTo(w);
                continue;
            }

            w.WriteStartArray("content");
            var textIndex = 0;
            foreach (var content in property.Value.EnumerateArray())
            {
                if (content.ValueKind != JsonValueKind.Object
                    || !content.TryGetProperty("type", out var type)
                    || type.GetString() != "input_text"
                    || textIndex >= texts.Count)
                {
                    content.WriteTo(w);
                    continue;
                }

                w.WriteStartObject();
                foreach (var contentProperty in content.EnumerateObject())
                {
                    if (contentProperty.Name == "text")
                        w.WriteString("text", texts[textIndex]);
                    else
                        contentProperty.WriteTo(w);
                }
                w.WriteEndObject();
                textIndex++;
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    private static void WriteRawPassthroughItem(
        Utf8JsonWriter writer,
        JsonElement raw,
        ref ResponsesRequestMutation mutations)
    {
        if (!IsResponsesMessageItem(raw))
        {
            writer.WriteRawValue(raw.GetRawText());
            return;
        }

        writer.WriteStartObject();
        foreach (var property in raw.EnumerateObject())
        {
            if (ShouldDropMessageId(property))
            {
                mutations |= ResponsesRequestMutation.InvalidMessageIdDropped;
                continue;
            }
            property.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    private static bool IsResponsesMessageItem(JsonElement raw) =>
        raw.ValueKind == JsonValueKind.Object
        && raw.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String
        && type.GetString() == "message";

    private static bool ShouldDropMessageId(JsonProperty property) =>
        property.Name == "id"
        && property.Value.ValueKind == JsonValueKind.String
        && property.Value.GetString() is { } id
        && !id.StartsWith("msg", StringComparison.Ordinal);

    private static void WriteInputItem(
        Utf8JsonWriter w,
        MessageParam msg,
        bool structuredMultimodalOutput,
        ref bool vision,
        ref bool sawImageOnCompatibilityPath,
        ref ResponsesRequestMutation mutations)
    {
        // An IR message maps back to one or more Responses input items. Tool-use
        // and tool-result blocks become their own function_call/function_call_output
        // items; text/image blocks become a message item.
        var textImageParts = new List<ContentBlockParam>();
        foreach (var block in msg.Content)
        {
            switch (block)
            {
                case ToolUseBlockParam tu:
                    FlushMessage(w, msg, textImageParts, ref vision, ref mutations);
                    textImageParts.Clear();
                    w.WriteStartObject();
                    w.WriteString("type", "function_call");
                    w.WriteString("call_id", tu.Id);
                    // namespace: a NON-default-namespace tool (gpt-5.6 collaboration/MCP,
                    // e.g. collaboration.list_agents) MUST round-trip its namespace on
                    // echo, or Copilot 400s the next turn with "Missing namespace for
                    // function_call" (live-replayed, NamespaceRealReplayProbe). T1 stashed
                    // it in the part bag; re-emit it here. Absent for plain default-
                    // namespace tools → field omitted, byte-identical to before.
                    if (TryGetToolNamespace(tu.ProviderExtensions, out var toolNs))
                        w.WriteString("namespace", toolNs);
                    w.WriteString("name", tu.Name);
                    // arguments: a plain function tool's input is a JSON object →
                    // GetRawText() (byte-faithful). A CUSTOM (grammar) tool's input is
                    // raw text T1 wrapped as a JSON string + marked grammar_text_arguments
                    // (Codex's `exec` echoed back). Re-emit THAT as the raw string value
                    // (GetString()), not GetRawText() — the latter would double-encode
                    // the already-quoted string. Copilot accepts a function_call with
                    // raw-text arguments (live-probed 200, CustomToolEchoProbe).
                    w.WriteString("arguments",
                        IsGrammarTextArgs(tu.ProviderExtensions) && tu.Input.ValueKind == JsonValueKind.String
                            ? tu.Input.GetString()
                            : tu.Input.GetRawText());
                    WriteProviderItemExtras(
                        w, tu.ProviderExtensions, validateMessageId: false, ref mutations);
                    w.WriteEndObject();
                    break;
                case ToolResultBlockParam tr:
                    FlushMessage(w, msg, textImageParts, ref vision, ref mutations);
                    textImageParts.Clear();
                    w.WriteStartObject();
                    w.WriteString("type", "function_call_output");
                    w.WriteString("call_id", tr.ToolUseId);
                    w.WritePropertyName("output");
                    // Responses function output is string | content-items[]. T2 pulls
                    // what the IR holds: a block the source marked opaque keeps the
                    // established string/verbatim contract; otherwise an all-supported
                    // text/image array may become input_text/input_image when THIS
                    // target is live-proven to accept it. Unknown blocks fall back as a
                    // whole so nothing is partially lost.
                    var sourceWantsOpaque = IsOpaqueToolOutput(tr.ProviderExtensions);
                    if (sourceWantsOpaque)
                    {
                        if (tr.Content is { } nativeOutput)
                            nativeOutput.WriteTo(w);
                        else
                            w.WriteStringValue("");
                    }
                    else
                    {
                        WriteToolResultOutput(
                            w,
                            tr.Content,
                            structuredMultimodalOutput,
                            reportImageDowngrade: true,
                            ref vision,
                            ref sawImageOnCompatibilityPath);
                    }
                    WriteProviderItemExtras(
                        w, tr.ProviderExtensions, validateMessageId: false, ref mutations);
                    w.WriteEndObject();
                    break;
                case RedactedThinkingBlockParam rt:
                    FlushMessage(w, msg, textImageParts, ref vision, ref mutations);
                    textImageParts.Clear();
                    w.WriteStartObject();
                    w.WriteString("type", "reasoning");
                    // PULL the reasoning item's identity/summary/content from the
                    // part-level openai bag whatever source filled it — native Codex
                    // T1, or the Claude inbound edge unfolding what its client could
                    // only carry inside `data`. T2 does not know or ask which.
                    if (TryGetReasoningId(rt.ProviderExtensions, out var reasoningId))
                        w.WriteString("id", reasoningId);
                    w.WriteString("encrypted_content", rt.Data);
                    WriteReasoningOpaqueFields(w, rt.ProviderExtensions, ref mutations);
                    w.WriteEndObject();
                    break;
                case ThinkingBlockParam:
                    // DROP — plain (unencrypted) Anthropic thinking has no Responses
                    // equivalent, and gpt-5.5 HARD-REJECTS it: a message content part
                    // {type:"thinking"} → 400 "Invalid value: 'thinking'. Supported
                    // values are: input_text, input_image, output_text, refusal,
                    // input_file, computer_screenshot, summary_text,
                    // tether_browsing_display" (live-probed 2026-07-04). It is
                    // model-internal scratch Anthropic itself never replays as visible
                    // content, so dropping is both mandatory and harmless (the
                    // assistant's sibling text block still carries the turn's output —
                    // conversation stays coherent, live-probed). Handled EXPLICITLY (not
                    // via the default catch-all) so the drop is intentional and a future
                    // ThinkingBlockParam case in FlushMessage can't silently forward it
                    // and reintroduce the 400.
                    break;
                default:
                    textImageParts.Add(block);
                    break;
            }
        }
        FlushMessage(w, msg, textImageParts, ref vision, ref mutations);
    }

    private static void FlushMessage(
        Utf8JsonWriter w,
        MessageParam message,
        List<ContentBlockParam> parts,
        ref bool vision,
        ref ResponsesRequestMutation mutations)
    {
        if (parts.Count == 0) return;
        w.WriteStartObject();
        w.WriteString("type", "message");
        w.WriteString("role", message.Role);
        WriteProviderItemExtras(
            w, message.ProviderExtensions, validateMessageId: true, ref mutations);
        w.WritePropertyName("content");
        w.WriteStartArray();
        foreach (var p in parts)
        {
            switch (p)
            {
                case TextBlockParam t:
                    w.WriteStartObject();
                    // user text → input_text; assistant text → output_text.
                    w.WriteString("type", message.Role == Role.Assistant ? "output_text" : "input_text");
                    w.WriteString("text", t.Text);
                    WriteProviderContentExtras(w, t.ProviderExtensions, ref mutations);
                    w.WriteEndObject();
                    break;
                case ImageBlockParam img:
                    vision = true;
                    w.WriteStartObject();
                    w.WriteString("type", "input_image");
                    w.WriteString("image_url", ImageToDataUrl(img.Source));
                    WriteProviderContentExtras(w, img.ProviderExtensions, ref mutations);
                    w.WriteEndObject();
                    break;
            }
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static string ImageToDataUrl(ImageSource source) => source switch
    {
        Base64ImageSource b => $"data:{b.MediaType};base64,{b.Data}",
        UrlImageSource u => u.Url,
        _ => "",
    };

    /// <summary>
    /// Write the <c>tools</c> array from the IR's typed Anthropic tools
    /// (<see cref="MessagesRequest.Tools"/>) into the Responses <c>function</c>
    /// shape: <c>{ "type":"function", "name", "description", "parameters",
    /// "strict":false }</c>. Called only on the Claude Code path — a Codex request
    /// carries its tools in the openai bag and re-emits them via
    /// <see cref="WriteToolsWithDrops"/>, so this is skipped there. Returns the
    /// SET OF SURVIVING TOOL NAMES (empty when nothing survived filtering), so the
    /// caller can decide whether a <c>tool_choice</c> is meaningful AND whether a
    /// forced <c>tool_choice</c> names a tool that actually made it to the wire.
    /// </summary>
    /// <remarks>
    /// Every Claude Code tool is a custom function tool (no <c>type</c> field,
    /// carries <c>input_schema</c>) — verified against the live capture corpus.
    /// Anthropic's <c>input_schema</c> is renamed to Responses' <c>parameters</c>
    /// (a JSON-Schema object). Two kinds of tool are dropped so this path matches
    /// what the /cc→Anthropic path sends (the Anthropic-only sanitize stages —
    /// including <c>ToolsSanitizeStage</c> — are gated off for a Responses target,
    /// so their drops must be reproduced here):
    /// <list type="bullet">
    ///   <item><b>Server tools</b> (<c>Type</c> starts with <c>web_search_</c>) —
    ///         the /cc endpoint already 400s them; T2 must never put one on the
    ///         /responses wire either.</item>
    ///   <item><b>IDE-only <c>mcp__ide__executeCode</c></b> without
    ///         <c>defer_loading=true</c> — Copilot has no IDE execution channel, so
    ///         <c>ToolsSanitizeStage</c> drops it on the /cc path; drop it here too
    ///         rather than forward a tool gpt-5.5 could call but the client can't
    ///         service.</item>
    /// </list>
    /// An empty / all-dropped tool list emits no <c>tools</c> key at all (Copilot
    /// rejects an empty tools array on some models, and an absent key is the correct
    /// "no tools" signal).
    /// </remarks>
    private static HashSet<string> WriteIrTools(
        Utf8JsonWriter w,
        IReadOnlyList<Tool>? tools,
        bool filterRecursiveAgentTool,
        out bool removedAgentTool)
    {
        removedAgentTool = false;
        var survivors = new HashSet<string>(StringComparer.Ordinal);
        if (tools is not { Count: > 0 }) return survivors;

        // Materialize the kept set first so we don't open a "tools":[] array for a
        // request whose only tools are dropped (server / IDE-only).
        var kept = new List<Tool>(tools.Count);
        foreach (var t in tools)
        {
            // Claude Code exposes Agent to sub-agents, but a GPT backend treats it
            // as an ordinary repeatable function and can recursively fan out a wide
            // agent tree. The strategy supplies this flag only for a configured
            // `/cc` sub-agent → Responses translation. Exact ordinal name matching
            // avoids removing an unrelated user-defined tool.
            if (filterRecursiveAgentTool && t.Name == "Agent")
            {
                removedAgentTool = true;
                continue;
            }
            if (t.Type is { Length: > 0 } typ
                && typ.StartsWith("web_search_", StringComparison.OrdinalIgnoreCase))
                continue; // server tool — never reaches Copilot /responses
            // Mirror ToolsSanitizeStage: the IDE-execution tool is a no-op on a
            // non-IDE backend unless the client explicitly defer-loaded it.
            if (t.Name == "mcp__ide__executeCode" && t.DeferLoading != true)
                continue;
            kept.Add(t);
        }
        if (kept.Count == 0) return survivors;

        w.WritePropertyName("tools");
        w.WriteStartArray();
        foreach (var t in kept)
        {
            w.WriteStartObject();
            w.WriteString("type", "function");
            w.WriteString("name", t.Name);
            if (t.Description is { } desc)
                w.WriteString("description", desc);
            w.WritePropertyName("parameters");
            WriteInputSchema(w, t.InputSchema);
            // Anthropic tools are not strict-mode; mirror what the successful Codex
            // function tools send (strict:false) so gpt-5.5 doesn't enforce a
            // stricter schema than the tool author intended.
            w.WriteBoolean("strict", false);
            w.WriteEndObject();
            survivors.Add(t.Name);
        }
        w.WriteEndArray();
        return survivors;
    }

    /// <summary>
    /// Serialize the IR's lossy <see cref="InputSchema"/> as a JSON-Schema object
    /// under the Responses <c>parameters</c> key. The IR models only
    /// <c>type</c>/<c>properties</c>/<c>required</c> (the rest was dropped at
    /// deserialize — see docs). A null schema (server tools omit it, though those
    /// are already skipped) still needs a valid empty object so gpt-5.5 doesn't
    /// reject a parameter-less function.
    /// </summary>
    private static void WriteInputSchema(Utf8JsonWriter w, InputSchema? schema)
    {
        w.WriteStartObject();
        w.WriteString("type", schema?.Type ?? "object");
        if (schema?.Properties is { } props)
        {
            w.WritePropertyName("properties");
            props.WriteTo(w);
        }
        else
        {
            // No properties → an empty object, not an absent key: a Responses
            // function schema with type:object and no properties is a valid
            // no-argument tool.
            w.WritePropertyName("properties");
            w.WriteStartObject();
            w.WriteEndObject();
        }
        if (schema?.Required is { Count: > 0 } required)
        {
            w.WriteStartArray("required");
            foreach (var r in required) w.WriteStringValue(r);
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    /// <summary>
    /// Write <c>tool_choice</c> from the IR's typed <see cref="ToolChoice"/>:
    /// <c>auto</c>→<c>"auto"</c>, <c>any</c>→<c>"required"</c>,
    /// <c>none</c>→<c>"none"</c>, <c>tool{name}</c>→<c>{type:"function",name}</c>.
    /// Called only on the Claude Code path (Codex re-emits its own from the bag).
    /// Null → omit (Responses defaults to auto).
    /// </summary>
    /// <param name="survivingToolNames">
    /// The names of tools that actually reached the wire (from <see cref="WriteIrTools"/>).
    /// Used ONLY for a forced <c>{type:"tool",name:X}</c> choice: if X was dropped
    /// (server / IDE-only) it is absent from <c>tools[]</c>, and naming it in
    /// <c>tool_choice</c> is a Responses 400 — so the choice is downgraded to
    /// <c>"auto"</c> (the model still runs, just not forced onto a tool that isn't
    /// there). Null means "don't validate" (the bag supplied the tools, so this
    /// builder didn't filter them and can't know the survivor set).
    /// </param>
    private static void WriteIrToolChoice(Utf8JsonWriter w, ToolChoice? choice, HashSet<string>? survivingToolNames)
    {
        switch (choice)
        {
            case null:
                return;
            case ToolChoiceAuto:
                w.WriteString("tool_choice", "auto");
                break;
            case ToolChoiceAny:
                // Anthropic "any" (must use a tool) maps to Responses "required".
                w.WriteString("tool_choice", "required");
                break;
            case ToolChoiceNone:
                w.WriteString("tool_choice", "none");
                break;
            case ToolChoiceTool tool:
                // Forced tool: only legal if the tool survived to tools[]. If it was
                // dropped, forcing it 400s — fall back to "auto" so the request still
                // succeeds (the dropped tool is unusable anyway).
                if (survivingToolNames is not null && !survivingToolNames.Contains(tool.Name))
                {
                    w.WriteString("tool_choice", "auto");
                    break;
                }
                w.WritePropertyName("tool_choice");
                w.WriteStartObject();
                w.WriteString("type", "function");
                w.WriteString("name", tool.Name);
                w.WriteEndObject();
                break;
        }
    }

    /// <summary>
    /// Write a Responses <c>function_call_output.output</c> from Anthropic
    /// <c>tool_result.content</c> (<see cref="ToolResultBlockParam.Content"/>), or a
    /// native Codex round-trip's opaque output:
    /// <list type="bullet">
    ///   <item>An exact live-proven target may receive an image-bearing array made
    ///         entirely of valid Anthropic <c>text</c>/<c>image</c> blocks as ordered
    ///         Responses <c>input_text</c>/<c>input_image</c> content items.</item>
    ///   <item>Every other semantic Anthropic array retains the established newline
    ///         compatibility fallback. Native Codex arrays never reach this helper:
    ///         their opaque marker makes the caller write the original JSON directly.</item>
    ///   <item>String/object/scalar values are written verbatim; null becomes an empty
    ///         string.</item>
    /// </list>
    /// </summary>
    private static void WriteToolResultOutput(
        Utf8JsonWriter w,
        JsonElement? content,
        bool structuredMultimodalOutput,
        bool reportImageDowngrade,
        ref bool vision,
        ref bool sawImageOnCompatibilityPath)
    {
        if (content is not { } c)
        {
            w.WriteStringValue("");
            return;
        }
        if (c.ValueKind == JsonValueKind.Array)
        {
            if (structuredMultimodalOutput && IsSupportedMultimodalToolResult(c))
            {
                WriteMultimodalToolResult(w, c);
                vision = true;
                return;
            }

            // Record that an image took the string path. The caller decides whether
            // that is expected (probed-unsupported) or worth reporting (unknown model).
            if (reportImageDowngrade && !structuredMultimodalOutput
                && IsSupportedMultimodalToolResult(c))
                sawImageOnCompatibilityPath = true;

            WriteFlattenedToolResult(w, c);
            return;
        }
        // String / object / scalar: verbatim. Byte-identical to the previous
        // content.WriteTo for the common string case, and keeps a Codex structured
        // output object intact.
        c.WriteTo(w);
    }

    private static bool IsSupportedMultimodalToolResult(JsonElement content)
    {
        var sawImage = false;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object
                || !block.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String)
                return false;

            switch (type.GetString())
            {
                case "text":
                    if (!block.TryGetProperty("text", out var text)
                        || text.ValueKind != JsonValueKind.String)
                        return false;
                    break;
                case "image":
                    if (!TryGetImageUrl(block, out _)) return false;
                    sawImage = true;
                    break;
                default:
                    return false;
            }
        }
        return sawImage;
    }

    private static void WriteMultimodalToolResult(Utf8JsonWriter w, JsonElement content)
    {
        w.WriteStartArray();
        foreach (var block in content.EnumerateArray())
        {
            var type = block.GetProperty("type").GetString();
            w.WriteStartObject();
            if (type == "text")
            {
                w.WriteString("type", "input_text");
                w.WriteString("text", block.GetProperty("text").GetString());
            }
            else
            {
                _ = TryGetImageUrl(block, out var imageUrl);
                w.WriteString("type", "input_image");
                w.WriteString("image_url", imageUrl);
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static bool TryGetImageUrl(JsonElement block, out string imageUrl)
    {
        imageUrl = "";
        if (!block.TryGetProperty("source", out var source)
            || source.ValueKind != JsonValueKind.Object
            || !source.TryGetProperty("type", out var sourceType)
            || sourceType.ValueKind != JsonValueKind.String)
            return false;

        switch (sourceType.GetString())
        {
            case "base64" when source.TryGetProperty("media_type", out var mediaType)
                               && mediaType.ValueKind == JsonValueKind.String
                               && source.TryGetProperty("data", out var data)
                               && data.ValueKind == JsonValueKind.String:
                // A data URL is only usable if BOTH halves are well formed. An empty or
                // malformed media type, or a payload that is not base64, would produce
                // something like `data:;base64,not-base64` — which the contract says
                // must fall back for the whole array, not be shipped (and must not
                // claim vision).
                if (mediaType.GetString() is not { Length: > 0 } mediaTypeValue
                    || !IsImageMediaType(mediaTypeValue)
                    || data.GetString() is not { Length: > 0 } dataValue
                    || !IsBase64(dataValue))
                    return false;
                imageUrl = $"data:{mediaTypeValue};base64,{dataValue}";
                return true;
            case "url" when source.TryGetProperty("url", out var url)
                            && url.ValueKind == JsonValueKind.String:
                // Require an absolute http(s) or data URL: a relative or opaque string
                // is not something the backend can fetch, so it belongs in the fallback.
                return url.GetString() is { Length: > 0 } urlValue
                    && IsUsableImageUrl(urlValue)
                    && (imageUrl = urlValue).Length > 0;
            default:
                return false;
        }
    }

    /// <summary>A <c>type/subtype</c> image media type, e.g. <c>image/png</c>.</summary>
    private static bool IsImageMediaType(string value)
    {
        const string prefix = "image/";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || value.Length <= prefix.Length)
            return false;
        foreach (var c in value.AsSpan(prefix.Length))
        {
            // Subtype charset per RFC 6838 restricted-name (no parameters — a
            // tool-result image block carries a bare media type).
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '+' or '-'))
                return false;
        }
        return true;
    }

    private static bool IsBase64(string value)
    {
        // Length and alphabet only — cheap, allocation-free, and enough to reject the
        // "not-base64" class the contract cares about.
        if (value.Length % 4 != 0) return false;
        var padding = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '=')
            {
                // Padding is legal only in the final one or two positions.
                if (i < value.Length - 2) return false;
                padding++;
                continue;
            }
            if (padding > 0) return false;
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '/')) return false;
        }
        return padding <= 2;
    }

    private static bool IsUsableImageUrl(string value) =>
        value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);

    private static void WriteFlattenedToolResult(Utf8JsonWriter w, JsonElement content)
    {
        var sb = new System.Text.StringBuilder();
        var wroteBlock = false;
        foreach (var block in content.EnumerateArray())
        {
            if (wroteBlock) sb.Append('\n');
            wroteBlock = true;
            if (block.ValueKind == JsonValueKind.Object
                && block.TryGetProperty("type", out var bt)
                && bt.ValueKind == JsonValueKind.String
                && bt.GetString() is "text" or "input_text" or "output_text"
                && block.TryGetProperty("text", out var txt)
                && txt.ValueKind == JsonValueKind.String)
            {
                sb.Append(txt.GetString());
            }
            else
            {
                sb.Append(block.GetRawText());
            }
        }
        w.WriteStringValue(sb.ToString());
    }

    /// <summary>
    /// Read a top-level string property out of the openai bag, or null if the bag
    /// is absent, not an object, or lacks a string value at <paramref name="name"/>.
    /// Used to lift <c>reasoning_summary</c> back into the reasoning object.
    /// </summary>
    private static string? TryGetBagString(JsonElement? bag, string name) =>
        bag is { ValueKind: JsonValueKind.Object } obj
        && obj.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static bool TryGetBagBoolean(JsonElement? bag, string name) =>
        bag is { ValueKind: JsonValueKind.Object } obj
        && obj.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static JsonElement? TryGetBagObject(JsonElement? bag, string name) =>
        bag is { ValueKind: JsonValueKind.Object } obj
        && obj.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static JsonElement? TryGetBagValue(JsonElement? bag, string name) =>
        bag is { ValueKind: JsonValueKind.Object } obj
        && obj.TryGetProperty(name, out var value)
            ? value
            : null;

    private enum ProviderExtraScope { Request, Reasoning }

    private static void WriteObjectProperties(
        Utf8JsonWriter writer,
        JsonElement? value,
        ProviderExtraScope scope,
        ref ResponsesRequestMutation mutations)
    {
        if (value is not { ValueKind: JsonValueKind.Object } obj) return;
        foreach (var property in obj.EnumerateObject())
        {
            if (IsReservedProviderExtra(scope, property.Name))
            {
                mutations |= ResponsesRequestMutation.ProviderConflictDropped;
                continue;
            }
            property.WriteTo(writer);
        }
    }

    private static bool IsReservedProviderExtra(ProviderExtraScope scope, string name) =>
        scope switch
        {
            ProviderExtraScope.Request => name is
                "model" or "instructions" or "input" or "tools" or "tool_choice"
                or "parallel_tool_calls" or "reasoning" or "store" or "stream"
                or "include" or "prompt_cache_key" or "service_tier" or "text"
                or "client_metadata" or "max_output_tokens",
            ProviderExtraScope.Reasoning => name is "effort" or "summary" or "context",
            _ => false,
        };

    private static bool TryGetOpenAiBag(
        Models.Common.ProviderExtensions? extensions,
        out JsonElement bag)
    {
        if (extensions?.ByProvider.TryGetValue(
                ProviderExtensions.OpenAiNamespace, out bag) == true
            && bag.ValueKind == JsonValueKind.Object)
            return true;
        bag = default;
        return false;
    }

    private static void WriteProviderItemExtras(
        Utf8JsonWriter writer,
        Models.Common.ProviderExtensions? extensions,
        bool validateMessageId,
        ref ResponsesRequestMutation mutations)
    {
        if (!TryGetOpenAiBag(extensions, out var bag)
            || !bag.TryGetProperty("responses_item_extra", out var extras)
            || extras.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in extras.EnumerateObject())
        {
            if (property.Name is "type" or "role" or "content" or "call_id"
                or "name" or "namespace" or "arguments" or "output"
                or "encrypted_content" or "summary")
            {
                mutations |= ResponsesRequestMutation.ProviderConflictDropped;
                continue;
            }
            if (validateMessageId && property.Name == "id")
            {
                if (ShouldDropMessageId(property))
                    mutations |= ResponsesRequestMutation.InvalidMessageIdDropped;
                else
                    property.WriteTo(writer);
                continue;
            }
            property.WriteTo(writer);
        }
    }

    private static void WriteProviderContentExtras(
        Utf8JsonWriter writer,
        Models.Common.ProviderExtensions? extensions,
        ref ResponsesRequestMutation mutations)
    {
        if (!TryGetOpenAiBag(extensions, out var bag)
            || !bag.TryGetProperty("responses_content_extra", out var extras)
            || extras.ValueKind != JsonValueKind.Object)
            return;
        foreach (var property in extras.EnumerateObject())
        {
            if (property.Name is "type" or "text" or "image_url")
            {
                mutations |= ResponsesRequestMutation.ProviderConflictDropped;
                continue;
            }
            property.WriteTo(writer);
        }
    }

    private static bool TryGetResponsesSystemGroup(
        Models.Common.ProviderExtensions? extensions,
        out int group,
        out int part)
    {
        group = 0;
        part = 0;
        return TryGetOpenAiBag(extensions, out var bag)
            && bag.TryGetProperty("responses_system_group", out var groupNode)
            && groupNode.TryGetInt32(out group)
            && bag.TryGetProperty("responses_system_part", out var partNode)
            && partNode.TryGetInt32(out part);
    }

    /// <summary>
    /// Pull the reasoning item's <c>id</c> back out of a redacted-thinking block's
    /// part-level <c>openai</c> bag (where T1 stashed it as <c>reasoning_id</c>).
    /// Returns false when the block carries no bag or no id — every Claude Code
    /// block has a null bag, so this is inert on the hot path.
    /// </summary>
    private static bool TryGetReasoningId(Models.Common.ProviderExtensions? ext, out string id)
    {
        id = "";
        if (ext?.ByProvider.TryGetValue(
                ProviderExtensions.OpenAiNamespace, out var bag) == true
            && bag.ValueKind == JsonValueKind.Object
            && bag.TryGetProperty("reasoning_id", out var rid)
            && rid.ValueKind == JsonValueKind.String)
        {
            id = rid.GetString() ?? "";
            return id.Length > 0;
        }
        return false;
    }

    /// <summary>
    /// True when the SOURCE marked this tool-result content as an opaque provider
    /// payload (Codex T1's own <c>function_call_output.output</c>). T2 then re-emits it
    /// verbatim instead of reading it as Anthropic content blocks. Absent on every
    /// Claude Code block, so those stay interpretable — no client identity is consulted.
    /// </summary>
    private static bool IsOpaqueToolOutput(Models.Common.ProviderExtensions? ext) =>
        ext?.ByProvider.TryGetValue(
            ProviderExtensions.OpenAiNamespace, out var bag) == true
        && bag.ValueKind == JsonValueKind.Object
        && bag.TryGetProperty("opaque_tool_output", out var opaque)
        && opaque.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Re-emit opaque Responses reasoning fields T1 stored on the redacted-thinking
    /// block. These have no Anthropic IR equivalent but can be required by Copilot
    /// on the tool-result echo turn (live: detailed summary without an item id).
    /// </summary>
    private static void WriteReasoningOpaqueFields(
        Utf8JsonWriter writer,
        Models.Common.ProviderExtensions? ext,
        ref ResponsesRequestMutation mutations)
    {
        if (ext?.ByProvider.TryGetValue(
                ProviderExtensions.OpenAiNamespace, out var bag) != true
            || bag.ValueKind != JsonValueKind.Object)
            return;

        if (bag.TryGetProperty("reasoning_summary", out var summary))
        {
            writer.WritePropertyName("summary");
            summary.WriteTo(writer);
        }
        if (bag.TryGetProperty("reasoning_content", out var content))
        {
            writer.WritePropertyName("content");
            content.WriteTo(writer);
        }
        // Fields the backend sent that this build does not model. The reasoning item
        // is an open shape; replaying only what we understand would silently narrow
        // the state the backend gets back, and the loss would surface as an upstream
        // failure a turn later rather than here.
        if (bag.TryGetProperty("reasoning_extra", out var extra)
            && extra.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in extra.EnumerateObject())
            {
                if (property.Name is "type" or "id" or "encrypted_content"
                    or "summary" or "content")
                {
                    mutations |= ResponsesRequestMutation.ProviderConflictDropped;
                    continue;
                }
                property.WriteTo(writer);
            }
        }
    }

    /// <summary>
    /// True when a tool_use block's part-level <c>openai</c> bag marks its input as
    /// grammar text (raw, non-JSON) — set by T1 when it carried a custom (grammar)
    /// tool's raw-text arguments (Codex `exec`). Tells the <c>function_call</c> emit
    /// to write the raw string (<c>GetString()</c>) instead of <c>GetRawText()</c>.
    /// Every Claude Code / JSON function-tool block has no such marker, so this is
    /// inert there and the arguments emit is byte-identical.
    /// </summary>
    private static bool IsGrammarTextArgs(Models.Common.ProviderExtensions? ext) =>
        ext?.ByProvider.TryGetValue(
            ProviderExtensions.OpenAiNamespace, out var bag) == true
        && bag.ValueKind == JsonValueKind.Object
        && bag.TryGetProperty("grammar_text_arguments", out var g)
        && g.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Pull a tool_use block's <c>namespace</c> back out of its part-level
    /// <c>openai</c> bag (where T1 stashed a NON-default namespace off an echoed
    /// gpt-5.6 collaboration/MCP <c>function_call</c>). Returns false when the block
    /// carries no bag or no namespace — every Claude Code / default-namespace block
    /// has none, so the <c>function_call</c> emit stays byte-identical there.
    /// </summary>
    private static bool TryGetToolNamespace(Models.Common.ProviderExtensions? ext, out string ns)
    {
        ns = "";
        if (ext?.ByProvider.TryGetValue(
                ProviderExtensions.OpenAiNamespace, out var bag) == true
            && bag.ValueKind == JsonValueKind.Object
            && bag.TryGetProperty("namespace", out var n)
            && n.ValueKind == JsonValueKind.String)
        {
            ns = n.GetString() ?? "";
            return ns.Length > 0;
        }
        return false;
    }

    /// <summary>
    /// Re-emit the bag's un-modeled knobs, applying the three uniform coercions:
    /// strip <c>service_tier</c> (Copilot 400s it), strip <c>store:true</c>
    /// (Codex sends false; harmless), and drop the <c>image_generation</c> tool
    /// (Copilot 400s it). Everything else passes through verbatim.
    /// </summary>
    private static void WriteBagFields(
        Utf8JsonWriter w,
        JsonElement bag,
        CodexModelProfile? profile,
        ref bool vision,
        ref ResponsesRequestMutation mutations)
    {
        foreach (var prop in bag.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "service_tier":
                    // STRIP — uniform coercion (research §2.3).
                    if (CodexModelProfileCatalog.StripsServiceTier)
                    {
                        mutations |= ResponsesRequestMutation.ServiceTierStripped;
                        continue;
                    }
                    prop.WriteTo(w);
                    break;
                case "store":
                    // Strip only when true (Q3). Codex sends false → keep.
                    if (CodexModelProfileCatalog.StripsStoreTrue &&
                        prop.Value.ValueKind == JsonValueKind.True)
                    {
                        mutations |= ResponsesRequestMutation.StoreTrueStripped;
                        continue;
                    }
                    prop.WriteTo(w);
                    break;
                case "tools":
                    WriteToolsWithDrops(w, prop.Value, profile, ref mutations);
                    break;
                case "reasoning_present":
                case "reasoning_summary":
                case "reasoning_context":
                case "reasoning_extra":
                    // Re-emitted inside the reasoning object, never as top-level keys.
                    continue;
                case "passthrough_items":
                    // Opaque input[] items (additional_tools preamble, agent_message,
                    // unknown types) — re-emitted INTO input[] in order (see Build), not
                    // as a top-level field. Skip here.
                    continue;
                case "request_extra":
                    // Unknown envelope fields retained by the Responses source. They
                    // cannot collide with typed semantic keys because STJ puts only
                    // unmapped properties in this object.
                    WriteObjectProperties(
                        w, prop.Value, ProviderExtraScope.Request, ref mutations);
                    continue;
                default:
                    // tool_choice, parallel_tool_calls, include, prompt_cache_key,
                    // text, client_metadata — verbatim.
                    prop.WriteTo(w);
                    break;
            }
        }
    }

    /// <summary>
    /// Re-emit the tools array, dropping <c>image_generation</c> (uniform 400),
    /// and — for <c>mai-code-1-flash-internal</c> — dropping <c>custom</c> tools
    /// (that model 500s on them, profile flag).
    /// </summary>
    private static void WriteToolsWithDrops(
        Utf8JsonWriter w,
        JsonElement tools,
        CodexModelProfile? profile,
        ref ResponsesRequestMutation mutations)
    {
        if (tools.ValueKind != JsonValueKind.Array)
        {
            w.WritePropertyName("tools");
            tools.WriteTo(w);
            return;
        }
        w.WritePropertyName("tools");
        w.WriteStartArray();
        foreach (var tool in tools.EnumerateArray())
        {
            var type = tool.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "image_generation" && CodexModelProfileCatalog.DropsImageGenerationTool)
            {
                mutations |= ResponsesRequestMutation.ImageGenerationToolDropped;
                continue;
            }
            if (type == "custom" && profile?.RejectsCustomTools == true)
            {
                mutations |= ResponsesRequestMutation.CustomToolDropped;
                continue;
            }
            tool.WriteTo(w);
        }
        w.WriteEndArray();
    }

    /// <summary>
    /// Coerce an inbound effort to what the resolved model accepts. Three cases:
    /// <list type="number">
    ///   <item>null → null (no effort set; nothing to write).</item>
    ///   <item>accepted (case-insensitive) → returned as-is.</item>
    ///   <item>not accepted → the model's <see cref="CodexModelProfile.DefaultEffort"/>.
    ///         E.g. Anthropic's <c>max</c> lands here for the "large"/"small"
    ///         profiles that don't accept it — but the "xlarge" profile (gpt-5.6)
    ///         DOES accept <c>max</c>, so there it's returned as-is by the case
    ///         above, not coerced.</item>
    /// </list>
    /// No nearest-neighbor guessing — the fallback is a deliberate per-model choice
    /// on the profile. This is the FACT layer; an operator can override per location
    /// with a routing <c>EffortMap</c> that runs earlier (research §2.2,
    /// <c>docs/routing.md</c>). Unknown profile → pass through (the model router
    /// already validated the id; a missing profile is a catalog gap surfaced
    /// elsewhere). The caller WARN-logs when the returned value differs from the
    /// inbound one.
    /// </summary>
    private static string? CoerceEffort(string? effort, CodexModelProfile? profile)
    {
        if (effort is null) return null;
        if (profile is null) return effort;
        if (profile.AcceptedEfforts.Contains(effort, StringComparer.OrdinalIgnoreCase))
            return effort;
        // Not accepted — fall back to the model's deliberate default (never a guess).
        return profile.DefaultEffort;
    }
}
