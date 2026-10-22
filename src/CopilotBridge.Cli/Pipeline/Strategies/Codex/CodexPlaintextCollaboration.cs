using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Pipeline.Adapters.Codex;

namespace CopilotBridge.Cli.Pipeline.Strategies.Codex;

/// <summary>
/// Request-local producer-side projection, following LiteLLM PR #46315 and
/// Codex PR #35845's explicit plaintext dispatch contract. Never decrypts or
/// deletes old messages. The strategy owns opt-in/native-route eligibility.
/// </summary>
internal sealed class CodexPlaintextCollaboration
{
    internal const string ClientNamespace = "collaboration";
    internal const string UpstreamNamespace = "bridge_plaintext_collaboration";
    private static readonly string[] IdentityKeys = ["type", "namespace", "name", "call_id"];
    private readonly HashSet<string> _actions = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Dictionary<string, string>> _identities = [];

    internal bool Active => _actions.Count > 0;

    internal byte[] PrepareRequest(byte[] body)
    {
        _actions.Clear();
        _identities.Clear();
        var root = JsonNode.Parse(body)!.AsObject();
        foreach (var tool in Inventory(root))
        {
            if (Text(tool["type"]) != "namespace" || Text(tool["name"]) != ClientNamespace
                || tool["tools"] is not JsonArray children) continue;
            foreach (var child in children.OfType<JsonObject>())
                if (EncryptedAction(child) is { } action) _actions.Add(action);
        }
        if (!Active) return body;
        if (root["previous_response_id"] is not null || root["conversation"] is not null)
            throw new CodexBadRequestException("Plaintext collaboration requires stateless Responses requests; previous_response_id and conversation are unsupported.");
        // Qualified ordinary function names share the response mapper's identity
        // space, so they collide just as the bare namespace does.
        if (Inventory(root).Any(tool => Text(tool["name"]) is { } name
            && (name == UpstreamNamespace || QualifiedSuffix(name, UpstreamNamespace) is not null)))
            throw new CodexBadRequestException("The bridge_plaintext_collaboration tool namespace is reserved by the bridge.");

        foreach (var tool in Inventory(root)) ProjectTool(tool, reverse: false);
        if (root["input"] is JsonArray input)
        {
            foreach (var item in input.OfType<JsonObject>())
                if (Text(item["type"]) is "function_call" or "custom_tool_call"
                    or "function_call_output" or "custom_tool_call_output")
                    MapIdentity(item, ClientNamespace, UpstreamNamespace, response: false);
        }
        ProjectChoice(root["tool_choice"], ClientNamespace, UpstreamNamespace, response: false);
        root["store"] = false;
        return Bytes(root);
    }

    internal byte[] RestoreResponse(byte[] body)
    {
        if (!Active) return body;
        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch (JsonException) { throw new UpstreamResponseFailedException("invalid_buffered_response"); }
        if (root is not JsonObject response) return body;
        RestoreResponse(response);
        return Bytes(response);
    }

    internal SseItem<string> RestoreEvent(in SseItem<string> evt)
    {
        if (!Active || string.IsNullOrWhiteSpace(evt.Data)) return evt;
        JsonNode? node;
        try { node = JsonNode.Parse(evt.Data); }
        catch (JsonException) { return evt; }
        if (node is not JsonObject root) return evt;
        var index = Index(root["output_index"]);
        if (root["item"] is JsonObject item) RestoreItem(item, index);
        if (root["response"] is JsonObject response) RestoreResponse(response);
        switch (Text(root["type"]))
        {
            case "response.function_call_arguments.done":
                Remember(root, index, item: false);
                RestoreIdentity(root, messageAction: true);
                break;
            case "response.function_call_arguments.delta":
                MapIdentity(root, UpstreamNamespace, ClientNamespace, response: true);
                break;
        }
        return new SseItem<string>(Encoding.UTF8.GetString(Bytes(root)), evt.EventType);
    }

    private static IEnumerable<JsonObject> Inventory(JsonObject root)
    {
        if (root["tools"] is JsonArray tools)
            foreach (var tool in tools.OfType<JsonObject>()) yield return tool;
        if (root["input"] is not JsonArray input) yield break;
        foreach (var item in input.OfType<JsonObject>())
        {
            if (Text(item["type"]) is not ("additional_tools" or "tool_search_output")
                || item["tools"] is not JsonArray nested) continue;
            foreach (var tool in nested.OfType<JsonObject>()) yield return tool;
        }
    }

    private static string? EncryptedAction(JsonObject tool)
    {
        var name = Text(tool["name"]);
        if (Text(tool["type"]) != "function" || !IsMessageAction(name)) return null;
        var message = (tool["parameters"] as JsonObject)?["properties"] as JsonObject;
        return message?["message"] is JsonObject schema
            && Text(schema["type"]) == "string"
            && schema["encrypted"] is JsonValue encrypted
            && encrypted.TryGetValue<bool>(out var enabled) && enabled ? name : null;
    }

    private static bool IsMessageAction(string? name) =>
        name is "spawn_agent" or "send_message" or "followup_task";

    private static void ProjectTool(JsonObject tool, bool reverse)
    {
        var source = reverse ? UpstreamNamespace : ClientNamespace;
        if (Text(tool["type"]) != "namespace" || Text(tool["name"]) != source) return;
        tool["name"] = reverse ? ClientNamespace : UpstreamNamespace;
        if (reverse || tool["tools"] is not JsonArray children) return;
        foreach (var child in children.OfType<JsonObject>())
        {
            if (EncryptedAction(child) is null) continue;
            var properties = (JsonObject)child["parameters"]!["properties"]!;
            ((JsonObject)properties["message"]!).Remove("encrypted");
        }
    }

    private static void ProjectChoice(JsonNode? value, string source, string target, bool response)
    {
        if (value is not JsonObject choice) return;
        if (Text(choice["type"]) == "namespace" && Text(choice["name"]) == source)
        {
            choice["name"] = target;
            return;
        }
        if (Text(choice["type"]) == "allowed_tools")
        {
            if (choice["tools"] is JsonArray tools)
                foreach (var tool in tools) ProjectChoice(tool, source, target, response);
            return;
        }
        // Qualified tool-choice names are a distinct legal wire form. Keep that
        // form when no separate namespace was supplied.
        var name = Text(choice["name"]);
        var namespacePresent = choice.ContainsKey("namespace");
        MapIdentity(choice, source, target, response);
        if (!namespacePresent && name is not null && QualifiedSuffix(name, source) is { } suffix)
        {
            choice.Remove("namespace");
            choice["name"] = target + suffix.Separator + suffix.Name;
        }
    }

    private void RestoreResponse(JsonObject response)
    {
        if (response["output"] is JsonArray output)
            for (var i = 0; i < output.Count; i++)
                if (output[i] is JsonObject item) RestoreItem(item, i);
        if (response["tools"] is JsonArray tools)
            foreach (var tool in tools.OfType<JsonObject>()) ProjectTool(tool, reverse: true);
        ProjectChoice(response["tool_choice"], UpstreamNamespace, ClientNamespace, response: true);
    }

    private void RestoreItem(JsonObject item, int? index)
    {
        var type = Text(item["type"]);
        if (type is not ("function_call" or "custom_tool_call")
            && (index is null || !_identities.ContainsKey(index.Value))) return;
        Remember(item, index, item: true);
        RestoreIdentity(item, messageAction: Text(item["type"]) == "function_call");
    }

    private void Remember(JsonObject value, int? index, bool item)
    {
        MapIdentity(value, UpstreamNamespace, UpstreamNamespace, response: true);
        if (index is null) return;
        if (!_identities.TryGetValue(index.Value, out var known))
            _identities[index.Value] = known = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in IdentityKeys)
        {
            if (!item && key == "type") continue;
            if (Text(value[key]) is not { Length: > 0 } incoming) continue;
            if (known.TryGetValue(key, out var old) && old != incoming)
                throw new UpstreamResponseFailedException("plaintext_collaboration_identity_conflict");
            known[key] = incoming;
        }
        foreach (var (key, identity) in known)
            if (item || key != "type") value[key] = identity;
    }

    private void RestoreIdentity(JsonObject value, bool messageAction)
    {
        var sourceNamespace = Text(value["namespace"]);
        MapIdentity(value, UpstreamNamespace, ClientNamespace, response: true);
        if (!messageAction || sourceNamespace != UpstreamNamespace
            || Text(value["name"]) is not { } name || !_actions.Contains(name)) return;
        if (value.ContainsKey("encrypted_function_args")
            && value["encrypted_function_args"] is not JsonArray { Count: 0 })
            throw new UpstreamResponseFailedException("plaintext_collaboration_encrypted_arguments");
        value["encrypted_function_args"] = new JsonArray();
    }

    private static void MapIdentity(JsonObject value, string source, string target, bool response)
    {
        var ns = Text(value["namespace"]);
        if (Text(value["name"]) is { } name && QualifiedSuffix(name, source) is { } suffix)
        {
            if (!string.IsNullOrEmpty(ns) && ns != source)
            {
                if (response) throw new UpstreamResponseFailedException("plaintext_collaboration_identity_conflict");
                throw new CodexBadRequestException("Conflicting plaintext collaboration namespace and function name.");
            }
            value["namespace"] = target;
            value["name"] = suffix.Name;
        }
        else if (ns == source) value["namespace"] = target;
    }

    private static (string Separator, string Name)? QualifiedSuffix(string name, string source)
    {
        foreach (var separator in new[] { ".", "__" })
            if (name.StartsWith(source + separator, StringComparison.Ordinal))
                return (separator, name[(source.Length + separator.Length)..]);
        return null;
    }

    private static int? Index(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<int>(out var index) && index >= 0 ? index : null;

    private static string? Text(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;

    private static byte[] Bytes(JsonNode value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) value.WriteTo(writer);
        return buffer.ToArray();
    }
}
