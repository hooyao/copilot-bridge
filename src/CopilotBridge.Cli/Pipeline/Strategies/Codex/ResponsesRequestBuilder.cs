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
    /// Build the Responses wire body from the IR. Returns the serialized bytes
    /// and whether the request carries an image (→ Copilot-Vision-Request).
    /// </summary>
    public static (byte[] Body, bool Vision) Build(MessagesRequest ir, CodexModelProfileCatalog profiles)
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
            foreach (var msg in ir.Messages)
                WriteInputItem(w, msg, ref vision);
            w.WriteEndArray();

            // effort: from IR OutputConfig, clamped to what the model accepts.
            // summary: rode the bag as "reasoning_summary" (T1); re-emit it INSIDE
            // the reasoning object alongside effort so a Codex-sent reasoning.summary
            // survives. Emit a reasoning object if EITHER is present — if coercion
            // dropped effort but a summary exists, reasoning:{summary:…} still carries
            // it (WriteBagFields drops "reasoning_summary" at the top level).
            var effort = CoerceEffort(ir.OutputConfig?.Effort, profile);
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

            // Re-apply the bag's un-modeled knobs, with coercions.
            if (bag is { ValueKind: JsonValueKind.Object } bagObj)
                WriteBagFields(w, bagObj, profile, ref vision);

            w.WriteEndObject();
        }

        return (buffer.ToArray(), vision);
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
                    w.WriteString("name", tu.Name);
                    w.WriteString("arguments", tu.Input.GetRawText());
                    w.WriteEndObject();
                    break;
                case ToolResultBlockParam tr:
                    FlushMessage(w, msg.Role, textImageParts, ref vision);
                    textImageParts.Clear();
                    w.WriteStartObject();
                    w.WriteString("type", "function_call_output");
                    w.WriteString("call_id", tr.ToolUseId);
                    w.WritePropertyName("output");
                    if (tr.Content is { } content) content.WriteTo(w);
                    else w.WriteStringValue("");
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
    /// Clamp an inbound effort to what the resolved model accepts (research §2.2).
    /// Unknown profile → pass through (the model router already validated the id;
    /// a missing profile is a catalog gap surfaced elsewhere). Null effort → null.
    /// </summary>
    private static string? CoerceEffort(string? effort, CodexModelProfile? profile)
    {
        if (effort is null) return null;
        if (profile is null) return effort;
        if (profile.AcceptedEfforts.Contains(effort, StringComparer.OrdinalIgnoreCase))
            return effort;
        // Not accepted — clamp to the nearest accepted neighbor. The two profiles:
        //   large rejects "minimal" → map to "low".
        //   small rejects "none" → drop (return null); rejects "xhigh" → "high".
        // Neighbor lookups use OrdinalIgnoreCase to match the accept check above
        // (AcceptedEfforts values are lowercase today, but stay case-insensitive
        // for consistency so a future mixed-case profile entry can't silently miss).
        return effort.ToLowerInvariant() switch
        {
            "minimal" => profile.AcceptedEfforts.Contains("low", StringComparer.OrdinalIgnoreCase) ? "low" : null,
            "xhigh" => profile.AcceptedEfforts.Contains("high", StringComparer.OrdinalIgnoreCase) ? "high" : null,
            "none" => null,  // small models reject none and there's no neighbor
            _ => profile.AcceptedEfforts.Contains("medium", StringComparer.OrdinalIgnoreCase) ? "medium" : null,
        };
    }
}
