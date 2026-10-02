using System.Text.Json.Nodes;
using CopilotBridge.Playground.Contract;
using Xunit;

namespace CopilotBridge.Playground;

public partial class ResponsesProbe
{
    /// <summary>
    /// Replays a real streaming Codex request that already targets GPT-5 mini.
    /// Hold the captured model, input history, tool definitions, and stream flag
    /// fixed while varying one axis at a time: low to minimal, then search off.
    /// This guards the backend fact behind T2's otherwise silent effort raise.
    /// </summary>
    [Fact]
    public async Task Gpt5Mini_RealCodexBytes_MinimalSearchBoundary()
    {
        const string model = "gpt-5-mini";
        var (capturePath, captured) = LoadNewestRealCodexBodyForModel(model);
        Assert.Equal(model, captured["model"]?.GetValue<string>());
        Assert.Equal(true, captured["stream"]?.GetValue<bool>());
        var tools = captured["tools"]?.AsArray()
            ?? throw new InvalidDataException($"Captured request has no tools: {capturePath}");
        Assert.Contains(tools, tool => tool?["type"]?.GetValue<string>() == "web_search");
        var reasoning = captured["reasoning"]?.AsObject()
            ?? throw new InvalidDataException($"Captured request has no reasoning object: {capturePath}");
        Assert.Equal("low", reasoning["effort"]?.GetValue<string>());

        using var client = new PlaygroundClient();
        var (baselineStatus, baselineBody) = await client.TryPostResponsesAsync(captured.ToJsonString());
        Assert.True(
            WireAcceptance.IsAccepted(baselineStatus, baselineBody, $"{model} captured low+search"),
            WireAcceptance.ErrorMessage(baselineBody));

        reasoning["effort"] = "minimal";
        var (rejectedStatus, rejectedBody) = await client.TryPostResponsesAsync(captured.ToJsonString());
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, rejectedStatus);
        Assert.Contains("web_search", rejectedBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reasoning.effort 'minimal'", rejectedBody, StringComparison.OrdinalIgnoreCase);

        var removed = 0;
        for (var i = tools.Count - 1; i >= 0; i--)
        {
            if (tools[i]?["type"]?.GetValue<string>() != "web_search") continue;
            tools.RemoveAt(i);
            removed++;
        }
        Assert.Equal(1, removed);
        var (acceptedStatus, acceptedBody) = await client.TryPostResponsesAsync(captured.ToJsonString());
        Assert.True(
            WireAcceptance.IsAccepted(acceptedStatus, acceptedBody, $"{model} captured minimal without search"),
            WireAcceptance.ErrorMessage(acceptedBody));

        _output.WriteLine(
            $"[{model}] real Codex bytes low+search -> {(int)baselineStatus}; "
            + $"minimal+search -> {(int)rejectedStatus}; "
            + $"minimal without search -> {(int)acceptedStatus}; capture={capturePath}");
    }
}
