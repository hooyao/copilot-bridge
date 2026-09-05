using System.Net.ServerSentEvents;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotBridge.Cli.Pipeline.Adapters.Codex;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.UnitTests.Invariant;

/// <summary>
/// Optional four-file production-corpus gate for native Codex Responses fidelity.
/// It replays the exact captured request and raw upstream SSE through the current
/// translators, rather than comparing two artifacts produced by the old binary.
/// Set CODEX_RESPONSE_CORPUS_DIR plus optional FROM/TO trace ids to run locally.
/// </summary>
public class CodexResponseCorpusReplayTests
{
    private const string DirEnv = "CODEX_RESPONSE_CORPUS_DIR";
    private const string FromEnv = "CODEX_RESPONSE_CORPUS_FROM";
    private const string ToEnv = "CODEX_RESPONSE_CORPUS_TO";
    private const string MaxEnv = "CODEX_RESPONSE_CORPUS_MAX";
    private readonly ITestOutputHelper _output;

    public CodexResponseCorpusReplayTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void EveryCleanCapturedGpt56Turn_ReplaysWithoutUndeclaredWireDifference()
    {
        var directory = Environment.GetEnvironmentVariable(DirEnv);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            _output.WriteLine($"{DirEnv} not set or missing — skipping response corpus replay.");
            return;
        }

        var from = Environment.GetEnvironmentVariable(FromEnv) ?? "00000000-000000-0000";
        var to = Environment.GetEnvironmentVariable(ToEnv) ?? "99999999-999999-9999";
        var max = int.TryParse(Environment.GetEnvironmentVariable(MaxEnv), out var parsedMax)
            && parsedMax > 0 ? parsedMax : int.MaxValue;

        var failures = new List<string>();
        var targetTurns = 0;
        var completeFourFileTurns = 0;
        var requestReplays = 0;
        var streamingReplays = 0;
        var bufferedReplays = 0;
        var incompleteCaptureTurns = 0;
        var unterminatedCapturedStreams = 0;

        foreach (var inboundPath in Directory.EnumerateFiles(directory, "*-inbound-req.json")
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(inboundPath);
            const string suffix = "-inbound-req.json";
            if (!name.EndsWith(suffix, StringComparison.Ordinal)) continue;
            var traceId = name[..^suffix.Length];
            if (string.CompareOrdinal(traceId, from) < 0 || string.CompareOrdinal(traceId, to) > 0)
                continue;

            JsonObject inboundEnvelope;
            try
            {
                inboundEnvelope = JsonNode.Parse(File.ReadAllText(inboundPath))!.AsObject();
            }
            catch (Exception ex)
            {
                failures.Add($"{traceId}: inbound envelope {ex.GetType().Name}");
                continue;
            }
            if (inboundEnvelope["target"]?.GetValue<string>() != "/codex/responses"
                || inboundEnvelope["body"] is not JsonObject inboundBody
                || inboundBody["model"]?.GetValue<string>() is not { } model
                || !model.StartsWith("gpt-5.6", StringComparison.Ordinal))
                continue;

            if (targetTurns >= max) break;
            targetTurns++;

            var upstreamRequestPath = Path.Combine(directory, traceId + "-upstream-req.json");
            var upstreamResponsePath = Path.Combine(directory, traceId + "-upstream-resp.json");
            var inboundResponsePath = Path.Combine(directory, traceId + "-inbound-resp.json");
            if (!File.Exists(upstreamRequestPath)
                || !File.Exists(upstreamResponsePath)
                || !File.Exists(inboundResponsePath))
            {
                incompleteCaptureTurns++;
                continue;
            }
            completeFourFileTurns++;

            try
            {
                var actualRequest = CodexRoundTrip.RoundTrip(inboundBody.ToJsonString());
                var expectedRequest = inboundBody.DeepClone();
                ApplyExpectedRequestMutations(expectedRequest);
                if (!JsonNode.DeepEquals(actualRequest, expectedRequest))
                {
                    var difference = FirstNodeDifference(expectedRequest, actualRequest);
                    failures.Add(
                        $"{traceId}: T1/T2 request differs at {difference} "
                        + RequestItemContext(expectedRequest, actualRequest, difference));
                }
                else
                    requestReplays++;

                var upstreamEnvelope = JsonNode.Parse(File.ReadAllText(upstreamResponsePath))!.AsObject();
                var inboundResponseEnvelope = JsonNode.Parse(File.ReadAllText(inboundResponsePath))!.AsObject();
                if (upstreamEnvelope["body"] is JsonValue upstreamBodyValue
                    && upstreamBodyValue.TryGetValue<string>(out var upstreamSse))
                {
                    var originalEvents = ParseSse(upstreamSse);
                    if (!HasCleanTerminal(originalEvents))
                    {
                        unterminatedCapturedStreams++;
                        continue;
                    }
                    var emitted = NativeRoundTrip(originalEvents, model);
                    NormalizeExpectedNativeProtocolIds(originalEvents);
                    var difference = FirstDifference(originalEvents, emitted);
                    if (difference is not null)
                        failures.Add($"{traceId}: response {difference}");
                    else
                        streamingReplays++;
                }
                else
                {
                    var capturedInboundBody = inboundResponseEnvelope["body"];
                    if (!JsonNode.DeepEquals(upstreamEnvelope["body"], capturedInboundBody))
                        failures.Add($"{traceId}: buffered upstream/inbound response values differ");
                    else
                        bufferedReplays++;
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{traceId}: replay threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        _output.WriteLine(
            $"target={targetTurns} complete4={completeFourFileTurns} request={requestReplays} "
            + $"stream={streamingReplays} buffered={bufferedReplays} "
            + $"incomplete4={incompleteCaptureTurns} unterminated={unterminatedCapturedStreams}");
        foreach (var failure in failures.Take(100)) _output.WriteLine("FAIL " + failure);

        Assert.True(targetTurns > 0, "selected corpus contained no gpt-5.6 /codex/responses turns");
        Assert.Equal(targetTurns, completeFourFileTurns + incompleteCaptureTurns);
        Assert.Equal(completeFourFileTurns, requestReplays);
        Assert.Equal(
            completeFourFileTurns,
            streamingReplays + bufferedReplays + unterminatedCapturedStreams);
        Assert.Empty(failures);
    }

    private static List<SseItem<string>> NativeRoundTrip(
        IReadOnlyList<SseItem<string>> original,
        string model)
    {
        var ledger = new NativeResponsesEventLedger();
        var t3 = new ResponsesToAnthropicStream(
            model, preserveNativeEvents: true, nativeLedger: ledger);
        var ir = new List<SseItem<string>>();
        foreach (var item in original) ir.AddRange(t3.Translate(item));
        ir.AddRange(t3.Flush());

        var t4 = new AnthropicToResponsesStream(model, nativeLedger: ledger);
        var emitted = new List<SseItem<string>>();
        foreach (var item in ir) emitted.AddRange(t4.Translate(item));
        emitted.AddRange(t4.Flush());
        Assert.Equal(0, ledger.Count);
        return emitted;
    }

    private static bool HasCleanTerminal(IReadOnlyList<SseItem<string>> events) =>
        events.Any(item => item.EventType is "response.completed" or "response.incomplete");

    private static void NormalizeExpectedNativeProtocolIds(List<SseItem<string>> events)
    {
        var states = new Dictionary<int, (string CallId, string Synthesized, string? Stable)>();
        var byCallId = new Dictionary<string, (string Synthesized, string? Stable)>(StringComparer.Ordinal);
        var messageIds = new Dictionary<int, string>();
        var responseId = "";
        for (var i = 0; i < events.Count; i++)
        {
            var root = JsonNode.Parse(events[i].Data)!.AsObject();
            var type = root["type"]?.GetValue<string>();
            if (type == "response.created"
                && root["response"]?["id"]?.GetValue<string>() is { Length: > 0 } createdId)
                responseId = createdId;
            if (type == "response.output_item.added"
                && root["output_index"]?.GetValue<int>() is { } addedIndex
                && root["item"] is JsonObject added)
            {
                if (added["type"]?.GetValue<string>() == "custom_tool_call"
                    && added["call_id"]?.GetValue<string>() is { Length: > 0 } callId)
                {
                    var synth = SynthesizeCtcId(callId);
                    states[addedIndex] = (callId, synth, null);
                    byCallId[callId] = (synth, null);
                    added["id"] = synth;
                }
                else if (added["type"]?.GetValue<string>() == "message")
                {
                    var canonical = added["id"]?.GetValue<string>() is { Length: > 0 } id
                        ? id
                        : SynthesizeMessageId(responseId, addedIndex, events[i].Data);
                    messageIds[addedIndex] = canonical;
                    added["id"] = canonical;
                }
            }
            else if (type is "response.custom_tool_call_input.delta" or "response.custom_tool_call_input.done"
                && root["output_index"]?.GetValue<int>() is { } inputIndex
                && states.TryGetValue(inputIndex, out var inputState))
            {
                var stable = root["item_id"]?.GetValue<string>() is { Length: > 0 } observed
                    && observed.StartsWith("ctc", StringComparison.Ordinal)
                    ? observed : inputState.Stable;
                inputState = (inputState.CallId, inputState.Synthesized, stable);
                states[inputIndex] = inputState;
                byCallId[inputState.CallId] = (inputState.Synthesized, stable);
                root["item_id"] = stable ?? inputState.Synthesized;
            }
            else if (type == "response.output_item.done"
                && root["output_index"]?.GetValue<int>() is { } doneIndex
                && root["item"] is JsonObject done)
            {
                if (done["type"]?.GetValue<string>() == "custom_tool_call"
                    && states.TryGetValue(doneIndex, out var doneState))
                    done["id"] = doneState.Stable ?? doneState.Synthesized;
                else if (done["type"]?.GetValue<string>() == "message"
                         && messageIds.TryGetValue(doneIndex, out var messageId))
                    done["id"] = messageId;
            }

            if (root["output_index"]?.GetValue<int>() is { } eventIndex
                && messageIds.TryGetValue(eventIndex, out var eventMessageId)
                && root.ContainsKey("item_id"))
                root["item_id"] = eventMessageId;

            if (type is "response.completed" or "response.incomplete" or "response.failed"
                && root["response"]?["output"] is JsonArray output)
            {
                for (var outputIndex = 0; outputIndex < output.Count; outputIndex++)
                {
                    if (output[outputIndex] is not JsonObject item
                        || item["type"]?.GetValue<string>() is not { } itemType)
                        continue;
                    if (itemType == "custom_tool_call"
                        && item["call_id"]?.GetValue<string>() is { } terminalCallId
                        && byCallId.TryGetValue(terminalCallId, out var terminalState))
                        item["id"] = terminalState.Stable ?? terminalState.Synthesized;
                    else if (itemType == "message"
                             && messageIds.TryGetValue(outputIndex, out var terminalMessageId))
                        item["id"] = terminalMessageId;
                }
            }
            events[i] = new SseItem<string>(root.ToJsonString(), events[i].EventType);
        }
    }

    private static string SynthesizeCtcId(string callId) =>
        "ctc_" + (callId.StartsWith("call_", StringComparison.Ordinal) ? callId[5..] : callId);

    private static string SynthesizeMessageId(string responseId, int outputIndex, string addedEvent)
    {
        var material = $"{responseId}\u001f{outputIndex}\u001f{addedEvent}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return "item_" + Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static string? FirstDifference(
        IReadOnlyList<SseItem<string>> expected,
        IReadOnlyList<SseItem<string>> actual)
    {
        if (expected.Count != actual.Count) return $"event count {expected.Count} != {actual.Count}";
        for (var i = 0; i < expected.Count; i++)
        {
            if (!string.Equals(expected[i].EventType, actual[i].EventType, StringComparison.Ordinal))
                return $"event[{i}] type {expected[i].EventType} != {actual[i].EventType}";
            using var expectedJson = JsonDocument.Parse(expected[i].Data);
            using var actualJson = JsonDocument.Parse(actual[i].Data);
            if (!JsonElement.DeepEquals(expectedJson.RootElement, actualJson.RootElement))
                return $"event[{i}] {expected[i].EventType} JSON value differs";
        }
        return null;
    }

    private static string FirstNodeDifference(JsonNode? expected, JsonNode? actual, string path = "$")
    {
        if (expected is null || actual is null)
            return expected is null && actual is null ? "<none>" : path + " (null/type)";
        if (expected.GetValueKind() != actual.GetValueKind())
            return path + $" (kind {expected.GetValueKind()} != {actual.GetValueKind()})";
        if (expected is JsonObject expectedObject && actual is JsonObject actualObject)
        {
            foreach (var property in expectedObject)
                if (!actualObject.ContainsKey(property.Key)) return path + "." + property.Key + " (missing actual)";
            foreach (var property in actualObject)
                if (!expectedObject.ContainsKey(property.Key)) return path + "." + property.Key + " (extra actual)";
            foreach (var property in expectedObject)
            {
                var child = FirstNodeDifference(property.Value, actualObject[property.Key], path + "." + property.Key);
                if (child != "<none>") return child;
            }
            return "<none>";
        }
        if (expected is JsonArray expectedArray && actual is JsonArray actualArray)
        {
            if (expectedArray.Count != actualArray.Count)
                return path + $" (count {expectedArray.Count} != {actualArray.Count})";
            for (var i = 0; i < expectedArray.Count; i++)
            {
                var child = FirstNodeDifference(expectedArray[i], actualArray[i], $"{path}[{i}]");
                if (child != "<none>") return child;
            }
            return "<none>";
        }
        if (JsonNode.DeepEquals(expected, actual)) return "<none>";
        return path + $" (value {NodeSummary(expected)} != {NodeSummary(actual)})";
    }

    private static string NodeSummary(JsonNode node)
    {
        var json = node.ToJsonString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..12];
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
            return $"string:length={text.Length}:sha={hash}";
        return $"{node.GetValueKind()}:json-length={json.Length}:sha={hash}";
    }

    private static string RequestItemContext(JsonNode expected, JsonNode actual, string difference)
    {
        const string marker = "$.input[";
        if (!difference.StartsWith(marker, StringComparison.Ordinal)) return "";
        var close = difference.IndexOf(']', marker.Length);
        if (close < 0 || !int.TryParse(difference.AsSpan(marker.Length, close - marker.Length), out var index))
            return "";
        if (expected["input"] is not JsonArray expectedInput
            || actual["input"] is not JsonArray actualInput
            || index >= expectedInput.Count || index >= actualInput.Count
            || expectedInput[index] is not JsonObject expectedItem
            || actualInput[index] is not JsonObject actualItem)
            return "";
        static string Field(JsonObject item, string key) =>
            item[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "<none>";
        return $"[expected type={Field(expectedItem, "type")} call_id={Field(expectedItem, "call_id")}; "
            + $"actual type={Field(actualItem, "type")} call_id={Field(actualItem, "call_id")}]";
    }

    /// <summary>
    /// Apply only destination mutations established independently by the live
    /// contract. The 2026-08-07 paired probe proved Copilot accepts valid msg ids,
    /// phases, function-output ids, all-turns reasoning, developer placement, and
    /// native output arrays, but rejects a message id such as item_0 because it does
    /// not begin with msg. Everything else is required to remain value-identical.
    /// </summary>
    private static void ApplyExpectedRequestMutations(JsonNode expected)
    {
        if (expected["input"] is not JsonArray input) return;
        foreach (var node in input)
            if (node is JsonObject item
                && item["type"]?.GetValue<string>() == "message"
                && item["id"]?.GetValue<string>() is { Length: > 0 } id
                && !id.StartsWith("msg", StringComparison.Ordinal))
                item.Remove("id");
    }

    private static List<SseItem<string>> ParseSse(string wire)
    {
        var events = new List<SseItem<string>>();
        string? eventType = null;
        var data = new StringBuilder();
        foreach (var line in wire.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("event:", StringComparison.Ordinal))
                eventType = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line[5..].TrimStart());
            }
            else if (line.Length == 0 && (eventType is not null || data.Length > 0))
            {
                events.Add(new SseItem<string>(data.ToString(), eventType));
                eventType = null;
                data.Clear();
            }
        }
        if (eventType is not null || data.Length > 0)
            events.Add(new SseItem<string>(data.ToString(), eventType));
        return events;
    }
}
