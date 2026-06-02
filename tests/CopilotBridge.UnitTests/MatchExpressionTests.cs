using CopilotBridge.Cli.Pipeline.Routing;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Pure logic coverage for <see cref="MatchExpression.Matches"/> — leaves,
/// composites, nesting, and the anthropic-beta token semantics. No config
/// binding here (see <see cref="RoutesBindingTests"/>); these construct the
/// tree directly so a failure points at the evaluator, not the binder.
/// </summary>
public class MatchExpressionTests
{
    // ── Leaves ────────────────────────────────────────────────────────────

    [Fact]
    public void Model_ExactMatch_CaseInsensitive()
    {
        var m = new MatchExpression { Model = "claude-opus-4.7" };
        Assert.True(m.Matches(TestCtx.Build("claude-opus-4.7")));
        Assert.True(m.Matches(TestCtx.Build("CLAUDE-OPUS-4.7")));
        Assert.False(m.Matches(TestCtx.Build("claude-opus-4.8")));
    }

    [Fact]
    public void Effort_ExactMatch()
    {
        var m = new MatchExpression { Effort = "max" };
        Assert.True(m.Matches(TestCtx.Build("x", effort: "max")));
        Assert.False(m.Matches(TestCtx.Build("x", effort: "high")));
        Assert.False(m.Matches(TestCtx.Build("x")));            // no effort field
    }

    [Fact]
    public void Header_AnthropicBeta_Contains_TokenAndWildcard()
    {
        var exact = new MatchExpression { Header = new HeaderMatch { Name = "anthropic-beta", Contains = "context-1m-2025-08-07" } };
        var wild  = new MatchExpression { Header = new HeaderMatch { Name = "anthropic-beta", Contains = "context-1m-*" } };

        var ctx = TestCtx.Build("x", betas: ["context-1m-2025-08-07", "other-beta"]);
        Assert.True(exact.Matches(ctx));
        Assert.True(wild.Matches(ctx));

        var without = TestCtx.Build("x", betas: ["other-beta"]);
        Assert.False(exact.Matches(without));
        Assert.False(wild.Matches(without));
    }

    [Fact]
    public void Header_AnthropicBeta_Eq_IsTokenPresence()
    {
        // Eq on anthropic-beta means "this exact token is in the set", NOT raw
        // whole-header equality (token order isn't part of the protocol).
        var m = new MatchExpression { Header = new HeaderMatch { Name = "anthropic-beta", Eq = "context-1m-2025-08-07" } };
        Assert.True(m.Matches(TestCtx.Build("x", betas: ["a", "context-1m-2025-08-07", "b"])));
        Assert.False(m.Matches(TestCtx.Build("x", betas: ["context-1m-2025-08-06"])));
    }

    [Fact]
    public void Header_NonBeta_MatchesAgainstRawHeaders()
    {
        var eq  = new MatchExpression { Header = new HeaderMatch { Name = "Editor-Version", Eq = "vscode/1.95.0" } };
        var sub = new MatchExpression { Header = new HeaderMatch { Name = "Editor-Version", Contains = "vscode/" } };

        var ctx = TestCtx.Build("x", headers: new Dictionary<string, string> { ["Editor-Version"] = "vscode/1.95.0" });
        Assert.True(eq.Matches(ctx));
        Assert.True(sub.Matches(ctx));
        Assert.False(eq.Matches(TestCtx.Build("x")));           // header absent
    }

    // ── Composites ────────────────────────────────────────────────────────

    [Fact]
    public void Flat_TopLevelFields_AreImplicitlyAnded()
    {
        var m = new MatchExpression
        {
            Model = "claude-opus-4.7",
            Header = new HeaderMatch { Name = "anthropic-beta", Contains = "context-1m-2025-08-07" },
        };
        Assert.True(m.Matches(TestCtx.Build("claude-opus-4.7", betas: ["context-1m-2025-08-07"])));
        Assert.False(m.Matches(TestCtx.Build("claude-opus-4.7")));                 // beta missing
        Assert.False(m.Matches(TestCtx.Build("claude-opus-4.8", betas: ["context-1m-2025-08-07"]))); // model wrong
    }

    [Fact]
    public void AllOf_RequiresEveryChild()
    {
        var m = new MatchExpression
        {
            AllOf =
            [
                new MatchExpression { Model = "claude-opus-4.7" },
                new MatchExpression { Effort = "max" },
            ],
        };
        Assert.True(m.Matches(TestCtx.Build("claude-opus-4.7", effort: "max")));
        Assert.False(m.Matches(TestCtx.Build("claude-opus-4.7", effort: "high")));
        Assert.False(m.Matches(TestCtx.Build("claude-opus-4.8", effort: "max")));
    }

    [Fact]
    public void AnyOf_RequiresAtLeastOneChild()
    {
        var m = new MatchExpression
        {
            AnyOf =
            [
                new MatchExpression { Effort = "max" },
                new MatchExpression { Header = new HeaderMatch { Name = "anthropic-beta", Contains = "context-1m-*" } },
            ],
        };
        Assert.True(m.Matches(TestCtx.Build("x", effort: "max")));
        Assert.True(m.Matches(TestCtx.Build("x", betas: ["context-1m-2025-08-07"])));
        Assert.True(m.Matches(TestCtx.Build("x", effort: "max", betas: ["context-1m-2025-08-07"])));
        Assert.False(m.Matches(TestCtx.Build("x", effort: "high")));
    }

    // ── Nesting — the user's exact shape ──────────────────────────────────

    /// <summary>opus-4.7 AND (1m-beta OR max) — the structure from the design discussion.</summary>
    private static MatchExpression UserShape() => new()
    {
        AllOf =
        [
            new MatchExpression { Model = "claude-opus-4.7" },
            new MatchExpression
            {
                AnyOf =
                [
                    new MatchExpression { Header = new HeaderMatch { Name = "anthropic-beta", Contains = "context-1m-*" } },
                    new MatchExpression { Effort = "max" },
                ],
            },
        ],
    };

    [Theory]
    [InlineData("claude-opus-4.7", "max", null, true)]                          // model ok, max branch
    [InlineData("claude-opus-4.7", null, "context-1m-2025-08-07", true)]        // model ok, beta branch
    [InlineData("claude-opus-4.7", "max", "context-1m-2025-08-07", true)]       // both inner branches
    [InlineData("claude-opus-4.7", "high", null, false)]                        // model ok, neither inner
    [InlineData("claude-opus-4.7", null, null, false)]                          // model ok, neither inner
    [InlineData("claude-opus-4.8", "max", "context-1m-2025-08-07", false)]      // wrong model — outer AND fails
    public void NestedAllOfAnyOf_EvaluatesCorrectly(string model, string? effort, string? beta, bool expected)
    {
        var ctx = TestCtx.Build(model, effort: effort, betas: beta is null ? null : [beta]);
        Assert.Equal(expected, UserShape().Matches(ctx));
    }
}
