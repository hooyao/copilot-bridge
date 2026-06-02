using CopilotBridge.Cli.Pipeline.Routing;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Startup fail-fast rules in <see cref="RoutesValidator"/>: empty/ambiguous
/// matches, the empty-Use no-op, and the header allow-list. The empty-When
/// guard is the safety-critical one — it's what stops a nested-binding failure
/// (or a typo) from producing a location that silently matches every request.
/// </summary>
public class RoutesValidatorTests
{
    private static RoutesConfig One(MatchExpression when, LocationUse use) =>
        new() { Locations = [new RouteLocation { When = when, Use = use }] };

    private static readonly LocationUse SwapModel = new() { Model = "claude-opus-4.8" };

    [Fact]
    public void NestedLocation_Valid_Passes()
    {
        var cfg = One(
            new MatchExpression
            {
                AllOf =
                [
                    new MatchExpression { AnyOf = [new() { Model = "claude-opus-4.7" }, new() { Model = "claude-opus-4.8" }] },
                    new MatchExpression { Header = new HeaderMatch { Name = "anthropic-beta", Contains = "context-1m-2025-08-07" } },
                ],
            },
            new LocationUse { Model = "claude-opus-4.7-1m-internal", EffortMap = new() { ["max"] = "xhigh" } });

        RoutesValidator.Validate(cfg);   // must not throw
    }

    [Fact]
    public void EmptyWhen_Rejected_AntiMatchAll()
    {
        var cfg = One(new MatchExpression(), SwapModel);
        var ex = Assert.Throws<InvalidOperationException>(() => RoutesValidator.Validate(cfg));
        Assert.Contains("no conditions", ex.Message);
    }

    [Fact]
    public void EmptyNestedChild_Rejected()
    {
        // Simulates what a nested-binding failure would leave behind: an AllOf
        // whose child node has no conditions. Must be caught, not match-all.
        var cfg = One(
            new MatchExpression { AllOf = [new MatchExpression { Model = "claude-opus-4.7" }, new MatchExpression()] },
            SwapModel);
        Assert.Throws<InvalidOperationException>(() => RoutesValidator.Validate(cfg));
    }

    [Fact]
    public void EmptyAllOfArray_Rejected()
    {
        var cfg = One(new MatchExpression { AllOf = [] }, SwapModel);
        Assert.Throws<InvalidOperationException>(() => RoutesValidator.Validate(cfg));
    }

    [Fact]
    public void EmptyUse_Rejected()
    {
        var cfg = One(new MatchExpression { Model = "claude-opus-4.7" }, new LocationUse());
        var ex = Assert.Throws<InvalidOperationException>(() => RoutesValidator.Validate(cfg));
        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonWhitelistedHeaderSet_Rejected()
    {
        var cfg = One(
            new MatchExpression { Model = "claude-opus-4.7" },
            new LocationUse { Headers = new LocationHeaders { Set = new() { ["Authorization"] = "Bearer pwned" } } });
        var ex = Assert.Throws<InvalidOperationException>(() => RoutesValidator.Validate(cfg));
        Assert.Contains("allow-list", ex.Message);
    }

    [Fact]
    public void WhitelistedHeaderSet_Passes()
    {
        var cfg = One(
            new MatchExpression { Model = "claude-opus-4.7" },
            new LocationUse { Headers = new LocationHeaders { Set = new() { ["Editor-Version"] = "vscode/2.0.0" } } });
        RoutesValidator.Validate(cfg);   // must not throw
    }

    [Fact]
    public void Header_BothEqAndContains_Rejected()
    {
        var cfg = One(
            new MatchExpression { Header = new HeaderMatch { Name = "anthropic-beta", Eq = "a", Contains = "b" } },
            SwapModel);
        Assert.Throws<InvalidOperationException>(() => RoutesValidator.Validate(cfg));
    }

    [Fact]
    public void Header_MissingName_Rejected()
    {
        var cfg = One(
            new MatchExpression { Header = new HeaderMatch { Contains = "x" } },
            SwapModel);
        Assert.Throws<InvalidOperationException>(() => RoutesValidator.Validate(cfg));
    }
}
