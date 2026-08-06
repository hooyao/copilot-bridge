using System.Net.ServerSentEvents;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Adapters.ClaudeCode;
using CopilotBridge.Cli.Pipeline.Adapters.Codex;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Cli.Pipeline.Stages.Anthropic;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotBridge.UnitTests.Invariant;

/// <summary>
/// Reasoning state must survive Responses → IR → Claude Code → IR → Responses.
/// <para>The contract is layered: the SOURCE (T3) pushes the whole reasoning item into
/// the IR without knowing the downstream client; each DEST pulls what its own protocol
/// needs. The Claude edge folds the item into the one opaque string its wire can carry
/// (proven byte-faithful through a real 2.1.220 tool trajectory) and unfolds it on the
/// way back; T2 then pulls id/summary/content from the part bag it already reads —
/// gpt-5.6-sol live probes prove summary is required alongside encrypted_content.</para>
/// </summary>
public class ResponsesReasoningReplayTests
{
    private static readonly ClaudeCodeOutboundAdapter ClaudeEdge =
        new(NullLogger<ClaudeCodeOutboundAdapter>.Instance);

    [Fact]
    public void T3_PushesWholeReasoningItemIntoIr_WithoutKnowingTheClient()
    {
        var ir = RunT3(ReasoningThenToolStream());

        var starts = ir.Where(e => EventType(e) == "content_block_start").ToList();
        Assert.Equal(2, starts.Count);

        using var reasoningDoc = JsonDocument.Parse(starts[0].Data);
        var reasoningBlock = reasoningDoc.RootElement.GetProperty("content_block");
        Assert.Equal("redacted_thinking", reasoningBlock.GetProperty("type").GetString());
        Assert.Equal("opaque+/=bytes", reasoningBlock.GetProperty("data").GetString());
        // The whole item rides the marker so any edge can pull what it needs.
        var carried = reasoningBlock.GetProperty(ClaudeReasoningEnvelope.Marker);
        Assert.Equal("rs_contract", carried.GetProperty("id").GetString());
        Assert.Equal("[]", carried.GetProperty("summary").GetRawText());
        Assert.Equal(0, reasoningDoc.RootElement.GetProperty("index").GetInt32());

        using var toolDoc = JsonDocument.Parse(starts[1].Data);
        Assert.Equal("tool_use", toolDoc.RootElement.GetProperty("content_block")
            .GetProperty("type").GetString());
        Assert.Equal(1, toolDoc.RootElement.GetProperty("index").GetInt32());

        Assert.Equal(2, ir.Count(e => EventType(e) == "content_block_stop"));
    }

    [Fact]
    public async Task ClaudeEdge_FoldsItemIntoData_AndScrubsTheMarker()
    {
        var claudeFacing = await ClaudeStreamAsync(RunT3(ReasoningThenToolStream()));

        var start = claudeFacing.Single(e =>
            EventType(e) == "content_block_start"
            && e.Data.Contains("redacted_thinking", StringComparison.Ordinal));
        using var doc = JsonDocument.Parse(start.Data);
        var block = doc.RootElement.GetProperty("content_block");

        Assert.False(block.TryGetProperty(ClaudeReasoningEnvelope.Marker, out _));
        var data = block.GetProperty("data").GetString()!;
        Assert.StartsWith(ClaudeReasoningEnvelope.LegacyV1Prefix, data, StringComparison.Ordinal);
        // The item's provider fields are folded into the opaque string, not exposed.
        Assert.DoesNotContain("rs_contract", start.Data, StringComparison.Ordinal);
        // No bridge-internal marker PROPERTY survives to the client on any event.
        foreach (var evt in claudeFacing)
            Assert.DoesNotContain(
                "\"" + ClaudeReasoningEnvelope.Marker + "\"", evt.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderNativeBlob_IsNotMistakenForACarrier()
    {
        const string nativeBlob = "provider-native-encrypted+/=";
        var original = RequestWithRedactedThinking(nativeBlob);

        var adapted = await UnfoldAsync(original);

        // Untouched instance: a non-prefixed value is ordinary provider data.
        Assert.Same(original, adapted);

        var wire = JsonNode.Parse(
            ResponsesRequestBuilder.Build(adapted, Catalog).Body)!.AsObject();
        var reasoning = wire["input"]!.AsArray()[0]!.AsObject();
        Assert.Equal(nativeBlob, reasoning["encrypted_content"]!.GetValue<string>());
        Assert.Null(reasoning["summary"]);
    }

    [Fact]
    public async Task UnfoldableReasoningItem_IsOmitted_NotLeftAsAPoisonBlob()
    {
        // An item with no summary cannot be replayed: live probes pin blob-only →
        // 400. The choice is therefore between STATELESS (no block at all) and
        // POISON (a bare blob the client faithfully echoes, which then reaches the
        // upstream as encrypted_content-without-summary and 400s the NEXT turn,
        // permanently — the client keeps replaying it). Stateless is the only
        // option that degrades; emitting the blob does not "stay safe", it breaks
        // the conversation later and further from the cause.
        var ir = RunT3(
        [
            Event("response.created",
                "{\"type\":\"response.created\",\"response\":{\"id\":\"r\",\"status\":\"in_progress\"}}"),
            Event("response.output_item.added",
                "{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"reasoning\",\"encrypted_content\":\"blob-without-summary\"}}"),
            Event("response.output_item.done",
                "{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"reasoning\",\"encrypted_content\":\"blob-without-summary\"}}"),
            Event("response.completed",
                "{\"type\":\"response.completed\",\"response\":{\"id\":\"r\",\"status\":\"completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}}"),
        ]);

        var claudeFacing = await ClaudeStreamAsync(ir);

        // Nothing unreplayable reaches the client at all.
        Assert.DoesNotContain(claudeFacing, e =>
            EventType(e) == "content_block_start"
            && e.Data.Contains("redacted_thinking", StringComparison.Ordinal));
        Assert.DoesNotContain(claudeFacing, e =>
            e.Data.Contains("blob-without-summary", StringComparison.Ordinal));
        foreach (var evt in claudeFacing)
            Assert.DoesNotContain(
                "\"" + ClaudeReasoningEnvelope.Marker + "\"", evt.Data, StringComparison.Ordinal);

        // And the block indices stay contiguous, so the omission cannot desync the
        // client's block bookkeeping.
        var starts = claudeFacing
            .Where(e => EventType(e) == "content_block_start")
            .Select(e => JsonDocument.Parse(e.Data).RootElement.GetProperty("index").GetInt32())
            .ToList();
        Assert.Equal(Enumerable.Range(0, starts.Count), starts);
    }

    [Fact]
    public void NativeCodexReasoningEcho_StillRoundTripsThroughItsOwnBag()
    {
        // The native Codex path fills the same part bag via T1; T2 pulls identically.
        const string requestJson = """
          {
            "model":"gpt-5.6-sol",
            "input":[
              {"type":"reasoning","id":"rs_native","encrypted_content":"NATIVE-BLOB",
               "summary":[{"type":"summary_text","text":"checked"}]},
              {"type":"message","role":"user","content":[{"type":"input_text","text":"go"}]}
            ],
            "stream":true
          }
          """;

        var emitted = CodexRoundTrip.RoundTrip(requestJson).AsObject()["input"]!.AsArray()[0]!.AsObject();

        Assert.Equal("rs_native", emitted["id"]!.GetValue<string>());
        Assert.Equal("NATIVE-BLOB", emitted["encrypted_content"]!.GetValue<string>());
        Assert.Equal(
            "[{\"type\":\"summary_text\",\"text\":\"checked\"}]",
            emitted["summary"]!.ToJsonString());
    }

    [Fact]
    public async Task UnfoldRestoresTheBagT2Reads()
    {
        // The edge decodes what the edge encoded, and hands the IR the same part bag
        // the native Codex path fills — so the request builder needs no knowledge of
        // the envelope, and pulls identically for either origin.
        var carrier = CarrierData(await ClaudeStreamAsync(RunT3(ReasoningThenToolStream())));

        var adapted = await UnfoldAsync(RequestWithRedactedThinking(carrier));

        var wire = JsonNode.Parse(
            ResponsesRequestBuilder.Build(adapted, Catalog).Body)!.AsObject();
        var reasoning = wire["input"]!.AsArray()[0]!.AsObject();
        Assert.Equal("rs_contract", reasoning["id"]!.GetValue<string>());
        Assert.Equal("opaque+/=bytes", reasoning["encrypted_content"]!.GetValue<string>());
        Assert.Equal("[]", reasoning["summary"]!.ToJsonString());
    }

    [Fact]
    public async Task MalformedCarrier_FailsClosed()
    {
        // The discriminator is this edge's own; a value bearing it that will not decode
        // is a client-side fault, not provider state to forward blindly.
        await Assert.ThrowsAsync<InvalidClaudeReasoningEnvelopeException>(async () =>
            await UnfoldAsync(RequestWithRedactedThinking(
                ClaudeReasoningEnvelope.LegacyV1Prefix + "not-valid-base64url!")));
    }

    [Fact]
    public void StaleAddedSnapshot_IsNotShipped_FinalSnapshotIs()
    {
        // Live capture shows encrypted_content and summary BOTH changing between the
        // reasoning item's `.added` and `.done` snapshots. Shipping the added one would
        // make the client replay stale state next turn.
        var ir = RunT3(
        [
            Event("response.created",
                "{\"type\":\"response.created\",\"response\":{\"id\":\"r\",\"status\":\"in_progress\"}}"),
            Event("response.output_item.added",
                "{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"reasoning\",\"id\":\"rs\",\"encrypted_content\":\"STALE\",\"summary\":[]}}"),
            Event("response.output_item.done",
                "{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"reasoning\",\"id\":\"rs\",\"encrypted_content\":\"FINAL\",\"summary\":[{\"type\":\"summary_text\",\"text\":\"done\"}]}}"),
            Event("response.completed",
                "{\"type\":\"response.completed\",\"response\":{\"id\":\"r\",\"status\":\"completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}}"),
        ]);

        var start = ir.Single(e =>
            EventType(e) == "content_block_start"
            && e.Data.Contains("redacted_thinking", StringComparison.Ordinal));
        using var doc = JsonDocument.Parse(start.Data);
        var block = doc.RootElement.GetProperty("content_block");

        Assert.Equal("FINAL", block.GetProperty("data").GetString());
        Assert.DoesNotContain("STALE", start.Data, StringComparison.Ordinal);
        var carried = block.GetProperty(ClaudeReasoningEnvelope.Marker);
        Assert.Equal(
            "[{\"type\":\"summary_text\",\"text\":\"done\"}]",
            carried.GetProperty("summary").GetRawText());
    }

    [Fact]
    public void ReasoningBeforeText_KeepsOutputOrder()
    {
        // The block position is reserved on `.added` and filled on `.done`, so a text
        // item that opens in between must not steal the reasoning block's index.
        var ir = RunT3(
        [
            Event("response.created",
                "{\"type\":\"response.created\",\"response\":{\"id\":\"r\",\"status\":\"in_progress\"}}"),
            Event("response.output_item.added",
                "{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"reasoning\",\"id\":\"rs\",\"encrypted_content\":\"BLOB\",\"summary\":[]}}"),
            Event("response.output_item.done",
                "{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"reasoning\",\"id\":\"rs\",\"encrypted_content\":\"BLOB\",\"summary\":[]}}"),
            Event("response.output_item.added",
                "{\"type\":\"response.output_item.added\",\"output_index\":1,\"item\":{\"type\":\"message\",\"id\":\"m\",\"role\":\"assistant\",\"status\":\"in_progress\",\"content\":[]}}"),
            Event("response.output_text.delta",
                "{\"type\":\"response.output_text.delta\",\"item_id\":\"m\",\"output_index\":1,\"delta\":\"hi\"}"),
            Event("response.output_item.done",
                "{\"type\":\"response.output_item.done\",\"output_index\":1,\"item\":{\"type\":\"message\",\"id\":\"m\",\"status\":\"completed\"}}"),
            Event("response.completed",
                "{\"type\":\"response.completed\",\"response\":{\"id\":\"r\",\"status\":\"completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}}"),
        ]);

        var indexes = ir
            .Where(e => EventType(e) == "content_block_start")
            .Select(e =>
            {
                using var d = JsonDocument.Parse(e.Data);
                return (
                    Type: d.RootElement.GetProperty("content_block").GetProperty("type").GetString(),
                    Index: d.RootElement.GetProperty("index").GetInt32());
            })
            .ToList();

        Assert.Equal(("redacted_thinking", 0), indexes[0]);
        Assert.Equal(("text", 1), indexes[1]);
        Assert.Equal(2, ir.Count(e => EventType(e) == "content_block_stop"));
    }

    [Fact]
    public async Task UnknownReasoningFields_SurviveTheClaudeRoundTrip()
    {
        // Responses reasoning items are an OPEN shape — gpt-5.6 keeps adding fields.
        // The bridge does not interpret them, so it must not DISCARD them either:
        // whatever the backend sent has to come back on the replay. Projecting the
        // item into a fixed known-field schema silently drops the rest, and the loss
        // only surfaces as an upstream failure a turn later, far from the cause.
        const string futureField = "\"future_reasoning\":\"keep-reasoning\"";
        var ir = RunT3(
        [
            Event("response.created",
                "{\"type\":\"response.created\",\"response\":{\"id\":\"r\",\"status\":\"in_progress\"}}"),
            Event("response.output_item.added",
                "{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"reasoning\",\"id\":\"rs_open\",\"encrypted_content\":\"BLOB\",\"summary\":[]}}"),
            Event("response.output_item.done",
                "{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"reasoning\",\"id\":\"rs_open\",\"encrypted_content\":\"BLOB\",\"summary\":[{\"type\":\"summary_text\",\"text\":\"s\"}],"
                + futureField + "}}"),
            Event("response.completed",
                "{\"type\":\"response.completed\",\"response\":{\"id\":\"r\",\"status\":\"completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}}"),
        ]);

        var carrier = CarrierData(await ClaudeStreamAsync(ir));
        Assert.NotEqual("", carrier);

        // Replay it exactly as Claude Code would, and read the wire T2 produces.
        var adapted = await UnfoldAsync(RequestWithRedactedThinking(carrier));
        var wire = JsonNode.Parse(
            ResponsesRequestBuilder.Build(adapted, Catalog).Body)!.AsObject();
        var reasoning = wire["input"]!.AsArray()
            .Select(i => i!.AsObject())
            .Single(i => i["type"]?.GetValue<string>() == "reasoning");

        Assert.Equal("keep-reasoning", reasoning["future_reasoning"]?.GetValue<string>());
    }


    [Fact]
    public void NativeCodexEdge_CannotSeeTheCarrierCodec()
    {
        // The isolation between the two client edges is STRUCTURAL, not a runtime
        // check: the Codex edge never mints a carrier, so it must never decode one.
        // Asserting that no Codex-side type references the codec is what keeps the
        // guarantee from silently becoming "whichever gate we remembered to write."
        var codexEdge = typeof(ResponsesToIrInboundAdapter).Assembly
            .GetTypes()
            .Where(t => t.FullName?.Contains(".Adapters.Codex.", StringComparison.Ordinal) == true
                || t.FullName?.Contains(".Strategies.Codex.", StringComparison.Ordinal) == true)
            .ToList();
        Assert.NotEmpty(codexEdge);

        var codecReferences = codexEdge
            .SelectMany(t => t.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => m.GetMethodBody() is not null)
            .Where(m => ReferencesEnvelopeCodec(m))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .ToList();

        Assert.Empty(codecReferences);
    }

    private static bool ReferencesEnvelopeCodec(MethodInfo method)
    {
        // A call to TryUnfold/TryFold compiles to a token operand naming the codec's
        // declaring type; scanning for that catches a future edit that wires the codec
        // into the Codex edge, which is exactly the leak this guards.
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null) return false;
        var module = method.Module;
        for (var i = 0; i + 4 < il.Length; i++)
        {
            // call (0x28) / callvirt (0x6F) — the only ways to reach the codec.
            if (il[i] is not (0x28 or 0x6F)) continue;
            var token = BitConverter.ToInt32(il, i + 1);
            MethodBase? target;
            try { target = module.ResolveMethod(token); }
            catch (ArgumentException) { continue; }
            if (target?.DeclaringType == typeof(ClaudeReasoningEnvelope)) return true;
        }
        return false;
    }

    /// <summary>
    /// The real production sequence for a replayed carrier: the client edge decodes
    /// it (before routing), then the origin stage judges it against the RESOLVED
    /// model (after routing). Testing them together is the point — the split between
    /// them is exactly what a client-edge-only comparison got wrong.
    /// </summary>
    private static async Task<MessagesRequest> UnfoldThenJudgeOriginAsync(
        MessagesRequest body, string resolvedModel)
    {
        var decoded = await UnfoldAsync(body);
        var ctx = ContextFor(decoded with { Model = resolvedModel }, BackendVendor.CopilotResponses);
        // Reproduce what the router leaves behind: the body now names the RESOLVED
        // model while OriginalRequestedModel still holds what the client asked for.
        // Without this, a stage reading the wrong one of the two would look correct.
        ctx.OriginalRequestedModel = body.Model;
        await new ClaudeReasoningOriginStage(
            ctx, NullLogger<ClaudeReasoningOriginStage>.Instance).ApplyAsync();
        return ctx.Request.Body;
    }

    private static BridgeContext<MessagesRequest> ContextFor(
        MessagesRequest body, BackendVendor vendor) =>
        new()
        {
            Request = new BridgeRequest<MessagesRequest>
            {
                Method = "POST",
                Path = "/cc/v1/messages",
                Body = body,
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            },
            Response = new BridgeResponse(),
            Target = new RouteTarget(
                vendor,
                vendor == BackendVendor.CopilotResponses ? "/responses" : "/v1/messages",
                body.Model),
        };

    /// <summary>Run the real Claude inbound edge — the unfold has no other entry point.</summary>
    private static async Task<MessagesRequest> UnfoldAsync(MessagesRequest body) =>
        await new ClaudeCodeInboundAdapter(NullLogger<ClaudeCodeInboundAdapter>.Instance)
            .AdaptAsync(body, EmptyHeaders, default);

    private static readonly Dictionary<string, string> EmptyHeaders =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly CodexModelProfileCatalog Catalog = new();

    private static List<SseItem<string>> RunT3(IReadOnlyList<SseItem<string>> upstream)
    {
        var t3 = new ResponsesToAnthropicStream("gpt-5.6-sol");
        var ir = new List<SseItem<string>>();
        foreach (var evt in upstream) ir.AddRange(t3.Translate(evt));
        ir.AddRange(t3.Flush());
        return ir;
    }

    private static async Task<List<SseItem<string>>> ClaudeStreamAsync(List<SseItem<string>> ir)
    {
        var result = new List<SseItem<string>>();
        await foreach (var evt in ClaudeEdge.AdaptStreamAsync(ToAsync(ir), default))
            result.Add(evt);
        return result;
    }

    private static async IAsyncEnumerable<SseItem<string>> ToAsync(List<SseItem<string>> items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.Yield();
        }
    }

    [Fact]
    public async Task UnknownCarrierVersion_FailsClosed_AndNeverReachesTheUpstream()
    {
        // The carrier is DURABLE CLIENT DATA: Claude Code persists transcripts and
        // replays them across --resume, compaction, and a bridge downgrade. So a
        // build WILL meet a carrier written by a newer build. That is bridge-owned
        // state this build cannot interpret — it must fail closed, exactly as a
        // corrupt carrier does. Forwarding it upstream as if it were provider-native
        // encrypted content is the one outcome that is silently wrong: the backend
        // sees another build's private JSON in place of its own reasoning blob.
        var future = ClaudeReasoningEnvelope.FamilyPrefix + "999:" + FutureVersionPayload();

        await Assert.ThrowsAsync<InvalidClaudeReasoningEnvelopeException>(async () =>
            await UnfoldAsync(RequestWithRedactedThinking(future)));
    }

    [Fact]
    public async Task UnknownCarrierVersion_ContentNeverAppearsInTheOutboundRequest()
    {
        // Assert on the outbound BYTES, not on a status code. The failure mode this
        // guards produced a perfectly healthy 200 while the upstream received the
        // wrong bytes — a status assertion would have stayed green through it.
        var future = ClaudeReasoningEnvelope.FamilyPrefix + "999:" + FutureVersionPayload();
        MessagesRequest? adapted = null;
        try
        {
            adapted = await UnfoldAsync(RequestWithRedactedThinking(future));
        }
        catch (InvalidClaudeReasoningEnvelopeException)
        {
            return; // Failed closed before any upstream body could be built.
        }

        var wire = ResponsesRequestBuilder.Build(adapted!, Catalog).Body;
        Assert.DoesNotContain(future, System.Text.Encoding.UTF8.GetString(wire), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyV1Carrier_StillDecodes()
    {
        // A carrier in the exact shape v0.4.29-beta emitted. Frozen as a literal
        // rather than regenerated, because a carrier produced by the CURRENT encoder
        // would prove nothing about reading the ones already sitting in users'
        // transcripts — and those outlive any single build.
        var adapted = await UnfoldAsync(RequestWithRedactedThinking(LegacyV1Carrier));

        var wire = JsonNode.Parse(
            ResponsesRequestBuilder.Build(adapted, Catalog).Body)!.AsObject();
        var reasoning = wire["input"]!.AsArray()
            .Select(i => i!.AsObject())
            .Single(i => i["type"]?.GetValue<string>() == "reasoning");
        Assert.Equal("LEGACY-BLOB", reasoning["encrypted_content"]!.GetValue<string>());
        Assert.Equal("rs_legacy", reasoning["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task CarrierFromAnotherModel_IsDropped_AndTheTurnStillRuns()
    {
        // Encrypted reasoning state is private to the model that produced it. After a
        // mid-session model change the transcript still carries the old model's
        // carrier — replaying it hands model B a blob only model A can read. Drop it:
        // a turn missing hidden state recovers, a turn carrying foreign state does
        // not. Version validation cannot catch this; the version is correct.
        var carrier = CarrierData(await ClaudeStreamAsync(RunT3(ReasoningThenToolStream())));
        // Routing has already resolved the target by the time origin is judged, so
        // the comparison runs against the RESOLVED model, not what the client asked
        // for. On CC→gpt those differ by design (client: claude-opus-5, resolved:
        // gpt-5.6-sol) — judging at the client edge would drop every valid carrier.
        var adapted = await UnfoldThenJudgeOriginAsync(
            RequestWithRedactedThinking(carrier), resolvedModel: "gpt-5.6-luna");

        var wire = JsonNode.Parse(
            ResponsesRequestBuilder.Build(adapted, Catalog).Body)!.AsObject();
        var input = wire["input"]!.AsArray();
        Assert.DoesNotContain(input, i =>
            i!.AsObject()["type"]?.GetValue<string>() == "reasoning");
        // The foreign blob must not survive in any form.
        Assert.DoesNotContain("opaque+/=bytes", wire.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CarrierFromTheSameModel_StillReplays()
    {
        // The control: origin binding must not cost the normal case anything.
        var carrier = CarrierData(await ClaudeStreamAsync(RunT3(ReasoningThenToolStream())));

        var adapted = await UnfoldThenJudgeOriginAsync(
            RequestWithRedactedThinking(carrier), resolvedModel: "gpt-5.6-sol");

        var wire = JsonNode.Parse(
            ResponsesRequestBuilder.Build(adapted, Catalog).Body)!.AsObject();
        var reasoning = wire["input"]!.AsArray()
            .Select(i => i!.AsObject())
            .Single(i => i["type"]?.GetValue<string>() == "reasoning");
        Assert.Equal("opaque+/=bytes", reasoning["encrypted_content"]!.GetValue<string>());
        // The bridge-private origin key must not ride out onto the wire.
        Assert.DoesNotContain(
            ClaudeReasoningEnvelope.OriginBagKey, wire.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClientRequestedModelDiffersFromResolved_CarrierStillReplays()
    {
        // The CC→gpt shape that a client-edge comparison would have broken: the
        // client names claude-opus-5, routing resolves gpt-5.6-sol, and the carrier
        // was minted by gpt-5.6-sol. This must REPLAY, not drop.
        var carrier = CarrierData(await ClaudeStreamAsync(RunT3(ReasoningThenToolStream())));
        var asClientSentIt = RequestWithRedactedThinking(carrier) with { Model = "claude-opus-5" };

        var adapted = await UnfoldThenJudgeOriginAsync(asClientSentIt, resolvedModel: "gpt-5.6-sol");

        var wire = JsonNode.Parse(
            ResponsesRequestBuilder.Build(adapted, Catalog).Body)!.AsObject();
        Assert.Contains(wire["input"]!.AsArray(),
            i => i!.AsObject()["type"]?.GetValue<string>() == "reasoning");
    }

    [Fact]
    public async Task CarrierOriginIsTheUpstreamProducer_NotTheRequestedModel()
    {
        // Copilot may resolve an alias or fall back, so the model that PRODUCED the
        // reasoning is not necessarily the one requested. The carrier must record the
        // producer: stamping the requested id would either replay the state to a model
        // that cannot read it, or drop it when routed back to the real producer.
        var ir = RunT3(
        [
            Event("response.created",
                "{\"type\":\"response.created\",\"response\":{\"id\":\"r\",\"status\":\"in_progress\",\"model\":\"gpt-5.6-actual\"}}"),
            Event("response.output_item.added",
                "{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"reasoning\",\"id\":\"rs\",\"encrypted_content\":\"BLOB\",\"summary\":[]}}"),
            Event("response.output_item.done",
                "{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"reasoning\",\"id\":\"rs\",\"encrypted_content\":\"BLOB\",\"summary\":[]}}"),
            Event("response.completed",
                "{\"type\":\"response.completed\",\"response\":{\"id\":\"r\",\"status\":\"completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}}"),
        ]);

        var start = ir.Single(e =>
            EventType(e) == "content_block_start"
            && e.Data.Contains("redacted_thinking", StringComparison.Ordinal));
        using var doc = JsonDocument.Parse(start.Data);
        Assert.Equal("gpt-5.6-actual", doc.RootElement.GetProperty("content_block")
            .GetProperty(ClaudeReasoningEnvelope.OriginMarker).GetString());
    }

    [Fact]
    public async Task OriginMarker_NeverReachesTheClient()
    {
        var claudeFacing = await ClaudeStreamAsync(RunT3(ReasoningThenToolStream()));

        foreach (var evt in claudeFacing)
            Assert.DoesNotContain(
                "\"" + ClaudeReasoningEnvelope.OriginMarker + "\"",
                evt.Data, StringComparison.Ordinal);
    }

    [Fact]
    public void EmittedPrefixStaysV1_SoAnOlderReaderFailsClosed()
    {
        // Measured against the real v0.4.29-beta decoder, both directions:
        //   `_v1:` + unknown payload  → Invalid (bounded 400, safe)
        //   a NEW prefix              → Absent  (forwarded upstream, silent corruption)
        // Its HasOnlyKnownFields accepts only "v"/"item", so even the `origin` field
        // this build adds makes an older reader fail CLOSED. The hazard therefore
        // comes from changing the PREFIX, never from evolving the payload — so the
        // emitted prefix must not drift, and payload versioning carries evolution.
        using var doc = JsonDocument.Parse(
            "{\"type\":\"reasoning\",\"encrypted_content\":\"BLOB\",\"summary\":[]}");
        Assert.True(ClaudeReasoningEnvelope.TryFold(doc.RootElement, "gpt-5.6-sol", out var emitted));

        Assert.StartsWith(ClaudeReasoningEnvelope.LegacyV1Prefix, emitted, StringComparison.Ordinal);
        Assert.DoesNotContain(
            ClaudeReasoningEnvelope.FamilyPrefix + "1:", emitted, StringComparison.Ordinal);
    }

    [Fact]
    public void ReservedFamilyPrefixIsReadable_SoAFutureSwitchIsSafe()
    {
        // Reading it today is what would let a LATER build adopt it without the
        // rollback hazard. Read support costs nothing; emitting it is the risk.
        using var doc = JsonDocument.Parse(
            "{\"type\":\"reasoning\",\"encrypted_content\":\"BLOB\",\"summary\":[]}");
        Assert.True(ClaudeReasoningEnvelope.TryFold(doc.RootElement, out var v1));
        var payload = v1[ClaudeReasoningEnvelope.LegacyV1Prefix.Length..];

        var verdict = ClaudeReasoningEnvelope.TryUnfold(
            ClaudeReasoningEnvelope.FamilyPrefix + "1:" + payload, out var enc, out _, out _);

        Assert.Equal(ClaudeReasoningUnfold.Valid, verdict);
        Assert.Equal("BLOB", enc);
    }

    [Fact]
    public void FrozenLegacyFixture_MatchesWhatThisBuildEmits()
    {
        // Guards the frozen literal against going quietly stale. Asserting only the
        // prefix would leave this green while a change to `item`, `v`, or field
        // serialization made the fixture represent no real carrier at all — so the
        // compatibility test above would pass vacuously. Compare the WHOLE carrier.
        using var doc = JsonDocument.Parse(LegacyFixtureItemJson);

        Assert.True(ClaudeReasoningEnvelope.TryFold(doc.RootElement, out var emitted));

        Assert.Equal(LegacyV1Carrier, emitted);
    }

    /// <summary>The exact source item the frozen carrier below was minted from.</summary>
    private const string LegacyFixtureItemJson =
        "{\"type\":\"reasoning\",\"id\":\"rs_legacy\",\"encrypted_content\":\"LEGACY-BLOB\","
        + "\"summary\":[{\"type\":\"summary_text\",\"text\":\"legacy\"}]}";

    /// <summary>
    /// A carrier in the exact shape v0.4.29-beta emitted. Frozen as a literal rather
    /// than regenerated: a carrier produced by the CURRENT encoder proves nothing
    /// about reading the ones already sitting in users' transcripts, and those
    /// outlive any single build.
    /// </summary>
    private const string LegacyV1Carrier =
        "cbridge_rr_7f3a9d2c_v1:eyJ2IjoxLCJpdGVtIjp7InR5cGUiOiJyZWFzb25pbmciLCJpZCI6InJzX2"
        + "xlZ2FjeSIsImVuY3J5cHRlZF9jb250ZW50IjoiTEVHQUNZLUJMT0IiLCJzdW1tYXJ5IjpbeyJ0eXBlIjo"
        + "ic3VtbWFyeV90ZXh0IiwidGV4dCI6ImxlZ2FjeSJ9XX19";

    /// <summary>A well-formed envelope body bearing a version this build cannot know.</summary>
    private static string FutureVersionPayload()
    {
        var json = "{\"v\":999,\"item\":{\"type\":\"reasoning\",\"encrypted_content\":\"FUTURE\","
            + "\"summary\":[],\"field_from_a_later_build\":true}}";
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string CarrierData(List<SseItem<string>> claudeFacing)
    {
        var start = claudeFacing.Single(e =>
            EventType(e) == "content_block_start"
            && e.Data.Contains("redacted_thinking", StringComparison.Ordinal));
        using var doc = JsonDocument.Parse(start.Data);
        return doc.RootElement.GetProperty("content_block").GetProperty("data").GetString()!;
    }

    private static MessagesRequest RequestWithRedactedThinking(string data) =>
        new()
        {
            Model = "gpt-5.6-sol",
            Messages =
            [
                new MessageParam
                {
                    Role = Role.Assistant,
                    Content = [new RedactedThinkingBlockParam { Data = data }],
                },
            ],
        };

    private static IReadOnlyList<SseItem<string>> ReasoningThenToolStream() =>
    [
        Event("response.created",
            "{\"type\":\"response.created\",\"response\":{\"id\":\"resp_reasoning_contract\",\"status\":\"in_progress\"}}"),
        Event("response.output_item.added",
            "{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"reasoning\",\"id\":\"rs_contract\",\"encrypted_content\":\"opaque+/=bytes\",\"summary\":[],\"content\":[{\"type\":\"reasoning_text\",\"text\":\"opaque\"}]}}"),
        Event("response.output_item.done",
            "{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"reasoning\",\"id\":\"rs_contract\",\"encrypted_content\":\"opaque+/=bytes\",\"summary\":[],\"content\":[{\"type\":\"reasoning_text\",\"text\":\"opaque\"}]}}"),
        Event("response.output_item.added",
            "{\"type\":\"response.output_item.added\",\"output_index\":1,\"item\":{\"type\":\"function_call\",\"id\":\"fc_contract\",\"call_id\":\"call_reasoning_tool\",\"name\":\"Bash\",\"arguments\":\"\",\"status\":\"in_progress\"}}"),
        Event("response.function_call_arguments.done",
            "{\"type\":\"response.function_call_arguments.done\",\"item_id\":\"fc_contract\",\"output_index\":1,\"arguments\":\"{\\\"command\\\":\\\"echo ok\\\"}\"}"),
        Event("response.output_item.done",
            "{\"type\":\"response.output_item.done\",\"output_index\":1,\"item\":{\"type\":\"function_call\",\"id\":\"fc_contract\",\"call_id\":\"call_reasoning_tool\",\"name\":\"Bash\",\"arguments\":\"{\\\"command\\\":\\\"echo ok\\\"}\",\"status\":\"completed\"}}"),
        Event("response.completed",
            "{\"type\":\"response.completed\",\"response\":{\"id\":\"resp_reasoning_contract\",\"status\":\"completed\",\"usage\":{\"input_tokens\":10,\"output_tokens\":5,\"total_tokens\":15}}}"),
    ];

    private static SseItem<string> Event(string type, string data) => new(data, type);

    private static string EventType(SseItem<string> item)
    {
        using var doc = JsonDocument.Parse(item.Data);
        return doc.RootElement.TryGetProperty("type", out var type)
            ? type.GetString() ?? item.EventType ?? ""
            : item.EventType ?? "";
    }
}
