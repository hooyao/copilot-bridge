namespace CopilotBridge.Cli.Models.Copilot;

/// <summary>
/// Response from <c>GET {capiBaseURL}/models</c> on Copilot. Schema mirrors what
/// Microsoft's official @vscode/copilot-api package consumes.
/// </summary>
internal sealed record CopilotModelsResponse
{
    public string? Object { get; init; }
    public IReadOnlyList<CopilotModel> Data { get; init; } = [];
}

internal sealed record CopilotModel
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? Object { get; init; }
    public string? Vendor { get; init; }
    public string? Version { get; init; }
    public bool Preview { get; init; }
    public bool ModelPickerEnabled { get; init; }
    public CopilotModelCapabilities? Capabilities { get; init; }

    /// <summary>
    /// Subset of <c>{"/v1/messages", "/responses", "/chat/completions"}</c> indicating
    /// which CAPI endpoints accept this model. M1 routes only on <c>/v1/messages</c>.
    /// </summary>
    public IReadOnlyList<string>? SupportedEndpoints { get; init; }

    public CopilotModelPolicy? Policy { get; init; }
}

internal sealed record CopilotModelCapabilities
{
    public string? Family { get; init; }
    public string? Type { get; init; }
    public string? Tokenizer { get; init; }
    public string? Object { get; init; }
    public CopilotModelLimits? Limits { get; init; }
    public CopilotModelSupports? Supports { get; init; }
}

internal sealed record CopilotModelLimits
{
    public int? MaxContextWindowTokens { get; init; }
    public int? MaxOutputTokens { get; init; }
    public int? MaxPromptTokens { get; init; }
    public int? MaxInputs { get; init; }
}

internal sealed record CopilotModelSupports
{
    public bool? ToolCalls { get; init; }
    public bool? ParallelToolCalls { get; init; }
    public bool? Streaming { get; init; }
    public bool? StructuredOutputs { get; init; }
    public bool? Vision { get; init; }
    public bool? Dimensions { get; init; }
    public bool? AdaptiveThinking { get; init; }
    public IReadOnlyList<string>? ReasoningEffort { get; init; }
    public int? MaxThinkingBudget { get; init; }
    public int? MinThinkingBudget { get; init; }
}

internal sealed record CopilotModelPolicy
{
    public string? State { get; init; }
    public string? Terms { get; init; }
}
