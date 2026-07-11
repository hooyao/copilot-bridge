using System.Text.Json;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline.Adapters.Codex;
using CopilotBridge.Cli.Pipeline.Routing;

namespace CopilotBridge.Cli.Pipeline.Strategies.Codex;

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
/// text, tool_choice, client_metadata, reasoning.summary) was stashed by T1 and
/// is re-emitted here. The IR body supplies what it CAN type (model, system →
/// instructions, messages → input, effort → reasoning.effort).
/// </remarks>
internal static class ResponsesRequestBuilder
{
    /// <summary>
    /// Build the Responses wire body from the IR. Returns the serialized bytes,
    /// whether the request carries an image (→ Copilot-Vision-Request), and the
    /// effort actually written to the wire after per-model coercion (null when no
    /// effort was set). The caller (which holds a logger) compares
    /// <see cref="MessagesRequest.OutputConfig"/>'s inbound effort against this to
    /// WARN on a fallback and to log the honest outbound value.
    /// </summary>
    public static (byte[] Body, bool Vision, string? CoercedEffort) Build(MessagesRequest ir, CodexModelProfileCatalog profiles)
    {
        // Exact profile, or the nearest known one (best-effort fallback for a
        // Codex model newer than this build's catalog — the router already
        // WARN-logged the fuzzy match and let the request through; here we just
        // borrow the closest model's effort-clamp + custom-tool-drop rules). Only
        // a below-floor id yields null → the existing unclamped passthrough.
        var profile = profiles.Get(ir.Model) ?? profiles.GetNearest(ir.Model, out _, out _);

        // Pull the openai bag (un-modeled knobs T1 stashed). Absent → empty.
        JsonElement? bag = null;
        if (ir.ProviderExtensions?.ByProvider.TryGetValue(
                ResponsesToIrInboundAdapter.OpenAiProviderKey, out var b) == true)
            bag = b;

        var vision = false;
        string? effort = null;   // coerced effort actually written to the wire; hoisted so the return below (outside the writer's using) can report it
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("model", ir.Model);

            // system → instructions
            if (ir.System is { Count: > 0 })
            {
                var sb = new System.Text.StringBuilder();
                foreach (var s in ir.System)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(s.Text);
                }
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
            var ptIdx = 0;
            var emittedMsgs = 0;
            ptIdx = WritePassthroughUpTo(w, passthrough, ptIdx, emittedMsgs);
            foreach (var msg in ir.Messages)
            {
                WriteInputItem(w, msg, ref vision);
                emittedMsgs++;
                ptIdx = WritePassthroughUpTo(w, passthrough, ptIdx, emittedMsgs);
            }
            // Any remaining passthrough items whose `after` exceeds the message count
            // (e.g. trailing agent_message) — emit them at the end. Raw-value, not
            // WriteTo — preserve the exact bytes (WriteTo reserializes the DOM and can
            // re-escape, e.g. an encrypted_content blob; GetRawText keeps them verbatim).
            while (ptIdx < passthrough.Count)
            {
                w.WriteRawValue(passthrough[ptIdx].Raw.GetRawText());
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
            var reasoningSummary = TryGetBagString(bag, "reasoning_summary");
            if (effort is not null || reasoningSummary is not null)
            {
                w.WritePropertyName("reasoning");
                w.WriteStartObject();
                if (effort is not null)
                    w.WriteString("effort", effort);
                if (reasoningSummary is not null)
                    w.WriteString("summary", reasoningSummary);
                w.WriteEndObject();
            }

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
                WriteBagFields(w, bagObj, profile, ref vision);
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
                irToolSurvivors = WriteIrTools(w, ir.Tools);
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

        return (buffer.ToArray(), vision, effort);
    }

    /// <summary>
    /// Read the ordered passthrough items T1 stashed in the bag (agent_message +
    /// unknown input[] types). Each is <c>{after:int, raw:object}</c> where <c>after</c>
    /// is the number of IR messages that preceded it. Returns an empty list when the
    /// bag has none (every Claude Code / plain Codex request).
    /// </summary>
    private static IReadOnlyList<(int After, JsonElement Raw)> ReadPassthroughItems(JsonElement? bag)
    {
        if (bag is not { ValueKind: JsonValueKind.Object } obj
            || !obj.TryGetProperty("passthrough_items", out var items)
            || items.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<(int, JsonElement)>();
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
            list.Add((after, raw));
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
        Utf8JsonWriter w, IReadOnlyList<(int After, JsonElement Raw)> passthrough, int ptIdx, int emittedMsgs)
    {
        while (ptIdx < passthrough.Count && passthrough[ptIdx].After <= emittedMsgs)
        {
            w.WriteRawValue(passthrough[ptIdx].Raw.GetRawText());
            ptIdx++;
        }
        return ptIdx;
    }

    private static void WriteInputItem(Utf8JsonWriter w, MessageParam msg, ref bool vision)
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
                    FlushMessage(w, msg.Role, textImageParts, ref vision);
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
                    w.WriteEndObject();
                    break;
                case ToolResultBlockParam tr:
                    FlushMessage(w, msg.Role, textImageParts, ref vision);
                    textImageParts.Clear();
                    w.WriteStartObject();
                    w.WriteString("type", "function_call_output");
                    w.WriteString("call_id", tr.ToolUseId);
                    w.WritePropertyName("output");
                    // Responses' function_call_output.output is a STRING. A Codex
                    // round-trip already carries a string here (T1 stored the
                    // opaque Output element), so a string passes through verbatim.
                    // But a Claude Code tool_result.content can be an ARRAY of
                    // content blocks ([{type:text,text:...}, ...]) — gpt-5.5 400s
                    // on a non-string output — so flatten an array to its
                    // concatenated text. Null → empty string.
                    WriteToolResultOutput(w, tr.Content);
                    w.WriteEndObject();
                    break;
                case RedactedThinkingBlockParam rt:
                    FlushMessage(w, msg.Role, textImageParts, ref vision);
                    textImageParts.Clear();
                    w.WriteStartObject();
                    w.WriteString("type", "reasoning");
                    // Recover the reasoning item's id from the part-level openai bag
                    // T1 stashed it in, so multi-turn reasoning identity survives the
                    // round trip. Absent → omit (Codex tolerates a blob-only item).
                    if (TryGetReasoningId(rt.ProviderExtensions, out var reasoningId))
                        w.WriteString("id", reasoningId);
                    w.WriteString("encrypted_content", rt.Data);
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
        FlushMessage(w, msg.Role, textImageParts, ref vision);
    }

    private static void FlushMessage(Utf8JsonWriter w, string role, List<ContentBlockParam> parts, ref bool vision)
    {
        if (parts.Count == 0) return;
        w.WriteStartObject();
        w.WriteString("type", "message");
        w.WriteString("role", role);
        w.WritePropertyName("content");
        w.WriteStartArray();
        foreach (var p in parts)
        {
            switch (p)
            {
                case TextBlockParam t:
                    w.WriteStartObject();
                    // user text → input_text; assistant text → output_text.
                    w.WriteString("type", role == Role.Assistant ? "output_text" : "input_text");
                    w.WriteString("text", t.Text);
                    w.WriteEndObject();
                    break;
                case ImageBlockParam img:
                    vision = true;
                    w.WriteStartObject();
                    w.WriteString("type", "input_image");
                    w.WriteString("image_url", ImageToDataUrl(img.Source));
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
    private static HashSet<string> WriteIrTools(Utf8JsonWriter w, IReadOnlyList<Tool>? tools)
    {
        var survivors = new HashSet<string>(StringComparer.Ordinal);
        if (tools is not { Count: > 0 }) return survivors;

        // Materialize the kept set first so we don't open a "tools":[] array for a
        // request whose only tools are dropped (server / IDE-only).
        var kept = new List<Tool>(tools.Count);
        foreach (var t in tools)
        {
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
    /// Write a Responses <c>function_call_output.output</c> from an Anthropic
    /// <c>tool_result.content</c> (<see cref="ToolResultBlockParam.Content"/>),
    /// which is <c>string | Array&lt;block&gt; | null</c>, OR a Codex round-trip's
    /// raw <c>JsonElement</c> output (string / object / scalar):
    /// <list type="bullet">
    ///   <item><b>Array</b> (Claude Code content blocks like
    ///         <c>[{type:text,text:…}]</c>) → the <c>text</c> fields concatenated
    ///         with newlines; a non-text block is kept as compact JSON so nothing
    ///         is lost. gpt-5.5 can't read the Anthropic block shape as a Responses
    ///         output-content array, so it MUST be flattened to a string.</item>
    ///   <item><b>Anything else</b> (string, object, scalar) → written verbatim.
    ///         This preserves two contracts at once: the common string case is
    ///         byte-identical to the old <c>content.WriteTo</c>, and a Codex
    ///         structured output object (<c>{"rows":[1,2],"ok":true}</c>) survives
    ///         as an object rather than being stringified
    ///         (<c>StructuredToolOutput_RoundTripsThroughT1T2</c>).</item>
    ///   <item><b>null</b> → empty string.</item>
    /// </list>
    /// </summary>
    private static void WriteToolResultOutput(Utf8JsonWriter w, JsonElement? content)
    {
        if (content is not { } c)
        {
            w.WriteStringValue("");
            return;
        }
        if (c.ValueKind == JsonValueKind.Array)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var block in c.EnumerateArray())
            {
                if (sb.Length > 0) sb.Append('\n');
                if (block.ValueKind == JsonValueKind.Object
                    && block.TryGetProperty("type", out var bt)
                    && bt.ValueKind == JsonValueKind.String
                    && bt.GetString() == "text"
                    && block.TryGetProperty("text", out var txt)
                    && txt.ValueKind == JsonValueKind.String)
                {
                    sb.Append(txt.GetString());
                }
                else
                {
                    // Non-text block (image, etc.): preserve it as compact JSON
                    // rather than dropping it — the model at least sees the shape.
                    sb.Append(block.GetRawText());
                }
            }
            w.WriteStringValue(sb.ToString());
            return;
        }
        // String / object / scalar: verbatim. Byte-identical to the previous
        // content.WriteTo for the common string case, and keeps a Codex structured
        // output object intact.
        c.WriteTo(w);
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
                ResponsesToIrInboundAdapter.OpenAiProviderKey, out var bag) == true
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
    /// True when a tool_use block's part-level <c>openai</c> bag marks its input as
    /// grammar text (raw, non-JSON) — set by T1 when it carried a custom (grammar)
    /// tool's raw-text arguments (Codex `exec`). Tells the <c>function_call</c> emit
    /// to write the raw string (<c>GetString()</c>) instead of <c>GetRawText()</c>.
    /// Every Claude Code / JSON function-tool block has no such marker, so this is
    /// inert there and the arguments emit is byte-identical.
    /// </summary>
    private static bool IsGrammarTextArgs(Models.Common.ProviderExtensions? ext) =>
        ext?.ByProvider.TryGetValue(
            ResponsesToIrInboundAdapter.OpenAiProviderKey, out var bag) == true
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
                ResponsesToIrInboundAdapter.OpenAiProviderKey, out var bag) == true
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
    /// Re-emit the bag's un-modeled knobs, applying the two uniform coercions:
    /// strip <c>service_tier</c> (Copilot 400s it), drop the
    /// <c>image_generation</c> tool (Copilot 400s it). <c>store</c> is only
    /// stripped when <c>true</c> (Codex sends false; harmless). Everything else
    /// passes through verbatim.
    /// </summary>
    private static void WriteBagFields(Utf8JsonWriter w, JsonElement bag, CodexModelProfile? profile, ref bool vision)
    {
        foreach (var prop in bag.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "service_tier":
                    // STRIP — uniform coercion (research §2.3).
                    continue;
                case "store":
                    // Strip only when true (Q3). Codex sends false → keep.
                    if (prop.Value.ValueKind == JsonValueKind.True) continue;
                    prop.WriteTo(w);
                    break;
                case "tools":
                    WriteToolsWithDrops(w, prop.Value, profile);
                    break;
                case "reasoning_summary":
                    // This was reasoning.summary — re-emitted INSIDE the reasoning
                    // object (see Build), not at the top level. Skip here so it isn't
                    // also written as a stray top-level key.
                    continue;
                case "passthrough_items":
                    // Opaque input[] items (additional_tools preamble, agent_message,
                    // unknown types) — re-emitted INTO input[] in order (see Build), not
                    // as a top-level field. Skip here.
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
    private static void WriteToolsWithDrops(Utf8JsonWriter w, JsonElement tools, CodexModelProfile? profile)
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
            if (type == "image_generation") continue;                       // uniform drop
            if (type == "custom" && profile?.RejectsCustomTools == true) continue; // flash drop
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
