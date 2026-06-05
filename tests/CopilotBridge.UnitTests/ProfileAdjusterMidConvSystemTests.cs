using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Models.Anthropic.Common;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Routing;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// <see cref="ProfileAdjuster.Apply"/>'s handling of mid-conversation
/// <c>role:"system"</c> messages. Two code paths, both critical:
/// <list type="bullet">
///   <item>When the target profile has
///         <see cref="ModelProfile.AcceptsMidConversationSystem"/> = <c>false</c>
///         (every model except opus-4.8 today), every mid-conv system message
///         must be converted to <c>role:"user"</c> with the
///         <c>[Claude Code injected]</c> marker prefix. The previous "fold
///         into top-level system[]" behavior is what
///         <c>docs/bug-mid-conversation-system-messages-dropped.md</c>
///         post-mortems: it lost the temporal anchor of user-queued messages
///         and broke prompt cache by growing the system field every turn.</item>
///   <item>When the target profile accepts mid-conv system (opus-4.8), keep
///         each entry in place only if its placement is legal under the 4.8
///         rule (predecessor=user; successor=assistant or end-of-array) —
///         convert the rest. Placement legality is judged against the
///         original message array so converting one entry can never change
///         the predecessor seen by a later one.</item>
/// </list>
/// </summary>
public class ProfileAdjusterMidConvSystemTests
{
    private const string Marker = "[Claude Code injected]\n";

    // ─── Profile fixtures ────────────────────────────────────────────────
    //
    // Minimal profiles that exercise only the mid-conv-system code path —
    // empty effort list (Strip on miss), AdaptiveOnly thinking, default
    // budget. Both profiles match the canonical id of the body model so
    // ApplyEffort's lookup is a no-op (effort/thinking aren't set on the
    // test bodies). Catalog is empty because no variant-routing is
    // exercised here.

    private static readonly ModelProfile RejectsMidConvSystem = new()
    {
        CanonicalId = "test-model-rejects-system",
        AcceptedEfforts = [],
        EffortOnUnsupported = EffortHandling.Strip,
        Thinking = ThinkingPolicy.AdaptiveOnly,
        MaxThinkingBudget = 32000,
        AcceptsMidConversationSystem = false,
    };

    private static readonly ModelProfile AcceptsMidConvSystem = new()
    {
        CanonicalId = "test-model-accepts-system",
        AcceptedEfforts = [],
        EffortOnUnsupported = EffortHandling.Strip,
        Thinking = ThinkingPolicy.AdaptiveOnly,
        MaxThinkingBudget = 32000,
        AcceptsMidConversationSystem = true,
    };

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static MessageParam Msg(string role, string text) => new()
    {
        Role = role,
        Content = [new TextBlockParam { Text = text }],
    };

    private static MessageParam SystemMsg(string text, CacheControl? cc = null) => new()
    {
        Role = "system",
        Content = [new TextBlockParam { Text = text, CacheControl = cc }],
    };

    private static BridgeContext<MessagesRequest> CtxWith(params MessageParam[] msgs)
    {
        var body = new MessagesRequest
        {
            Model = "test-model",
            Messages = msgs,
        };
        var req = new BridgeRequest<MessagesRequest>
        {
            Method = "POST",
            Path = "/cc/v1/messages",
            Body = body,
        };
        return new BridgeContext<MessagesRequest>
        {
            Request = req,
            Response = new BridgeResponse(),
            Ct = default,
        };
    }

    private static string TextOf(MessageParam m, int blockIndex = 0)
    {
        var block = m.Content[blockIndex];
        Assert.IsType<TextBlockParam>(block);
        return ((TextBlockParam)block).Text;
    }

    private static readonly ModelProfileCatalog EmptyCatalog = new(Array.Empty<ModelProfile>());

    // ─── Profile rejects mid-conv-system (default path) ───────────────────

    [Fact]
    public void Rejects_SingleMidConvSystem_ConvertedToUser_WithMarkerPrefix()
    {
        var ctx = CtxWith(
            Msg("user", "hi"),
            SystemMsg("From now on, respond in pirate-speak."),
            Msg("user", "say hello"));

        ProfileAdjuster.Apply(ctx, RejectsMidConvSystem, EmptyCatalog);

        var msgs = ctx.Request.Body.Messages;
        Assert.Equal(3, msgs.Count);
        Assert.Equal("user", msgs[0].Role);
        Assert.Equal("user", msgs[1].Role);   // ← role rewritten
        Assert.Equal("user", msgs[2].Role);
        Assert.Equal(Marker + "From now on, respond in pirate-speak.", TextOf(msgs[1]));
    }

    [Fact]
    public void Rejects_TopLevelSystem_LeftUntouched()
    {
        // Whatever's in the top-level system field must stay there — the
        // bug we're fixing is precisely that the old fold path was growing
        // this field by appending mid-conv system text. CONVERT must not.
        var ctx = CtxWith(
            Msg("user", "hi"),
            SystemMsg("injected"),
            Msg("user", "go"));
        ctx.Request.Body = ctx.Request.Body with
        {
            System = [new TextBlockParam { Text = "original system prompt" }],
        };

        ProfileAdjuster.Apply(ctx, RejectsMidConvSystem, EmptyCatalog);

        var sys = ctx.Request.Body.System;
        Assert.NotNull(sys);
        Assert.Single(sys);
        Assert.Equal("original system prompt", sys[0].Text);
    }

    [Fact]
    public void Rejects_MultipleMidConvSystems_AllConvertedInPlace_OrderPreserved()
    {
        var ctx = CtxWith(
            Msg("user", "hi"),
            SystemMsg("first reminder"),
            Msg("assistant", "ok"),
            SystemMsg("second reminder"),
            Msg("user", "go"),
            SystemMsg("third reminder"));

        ProfileAdjuster.Apply(ctx, RejectsMidConvSystem, EmptyCatalog);

        var msgs = ctx.Request.Body.Messages;
        Assert.Equal(6, msgs.Count);
        // Order preserved
        Assert.All(new[] { 0, 1, 2, 3, 4, 5 }, i => Assert.NotEqual("system", msgs[i].Role));
        Assert.Equal(Marker + "first reminder", TextOf(msgs[1]));
        Assert.Equal(Marker + "second reminder", TextOf(msgs[3]));
        Assert.Equal(Marker + "third reminder", TextOf(msgs[5]));
        // Roles
        Assert.Equal("user", msgs[1].Role);
        Assert.Equal("user", msgs[3].Role);
        Assert.Equal("user", msgs[5].Role);
    }

    [Fact]
    public void Rejects_CacheControl_PreservedOnConvert()
    {
        // Claude Code does not set cache_control on mid-conv-system text
        // blocks in practice — but the convert path must keep the invariant
        // clean so that if the input shape grows one, it survives.
        var cc = new CacheControl { Type = "ephemeral" };
        var ctx = CtxWith(
            Msg("user", "hi"),
            SystemMsg("with cc", cc),
            Msg("user", "go"));

        ProfileAdjuster.Apply(ctx, RejectsMidConvSystem, EmptyCatalog);

        var converted = ctx.Request.Body.Messages[1];
        var block = Assert.IsType<TextBlockParam>(converted.Content[0]);
        Assert.NotNull(block.CacheControl);
        Assert.Equal("ephemeral", block.CacheControl!.Type);
    }

    [Fact]
    public void Rejects_StringContentSystem_ConvertedToBlockArray_WithMarker()
    {
        // The on-wire shape Claude Code uses for mid-conv-system is
        // string-content (not array) — see the canonical trace
        // 20260605-120219-0038. ContentBlockParamListConverter normalizes
        // both shapes to a one-element TextBlockParam[] during deserialize,
        // so by the time ProfileAdjuster sees it, content is always an
        // array. This test asserts that the convert path handles a single
        // text block (the deserialized form of a string) correctly.
        var ctx = CtxWith(
            Msg("user", "hi"),
            SystemMsg("a string-form system msg"),   // would be content: "a string-form..." on the wire
            Msg("user", "go"));

        ProfileAdjuster.Apply(ctx, RejectsMidConvSystem, EmptyCatalog);

        var converted = ctx.Request.Body.Messages[1];
        Assert.Equal("user", converted.Role);
        Assert.Single(converted.Content);
        var block = Assert.IsType<TextBlockParam>(converted.Content[0]);
        Assert.Equal(Marker + "a string-form system msg", block.Text);
    }

    [Fact]
    public void Rejects_NoSystemMessages_BodyUntouched()
    {
        // Pure pass-through: if there's nothing to do, don't allocate a new
        // list. The reference-equality check below would fail if the
        // adjuster rebuilt the messages array unnecessarily.
        var ctx = CtxWith(
            Msg("user", "hi"),
            Msg("assistant", "hello"),
            Msg("user", "go"));
        var originalMessages = ctx.Request.Body.Messages;

        ProfileAdjuster.Apply(ctx, RejectsMidConvSystem, EmptyCatalog);

        Assert.Same(originalMessages, ctx.Request.Body.Messages);
    }

    // ─── Profile accepts mid-conv-system (opus-4.8 path) ─────────────────

    [Fact]
    public void Accepts_LegalPlacement_UserPredAssistantSucc_KeptInPlace()
    {
        // U·S·A — predecessor=user, successor=assistant. Legal under the
        // 4.8 placement rule (probed in
        // ModelProfileProbe.Opus48_MidConversationSystem_PlacementRules).
        var ctx = CtxWith(
            Msg("user", "hi"),
            SystemMsg("system in place"),
            Msg("assistant", "ok"));

        ProfileAdjuster.Apply(ctx, AcceptsMidConvSystem, EmptyCatalog);

        var msgs = ctx.Request.Body.Messages;
        Assert.Equal(3, msgs.Count);
        Assert.Equal("system", msgs[1].Role);   // ← kept untouched
        Assert.Equal("system in place", TextOf(msgs[1]));
    }

    [Fact]
    public void Accepts_LegalPlacement_UserPredEndOfArray_KeptInPlace()
    {
        // U·S — predecessor=user, successor=end-of-array. Legal under 4.8.
        var ctx = CtxWith(
            Msg("user", "hi"),
            SystemMsg("trailing system"));

        ProfileAdjuster.Apply(ctx, AcceptsMidConvSystem, EmptyCatalog);

        var msgs = ctx.Request.Body.Messages;
        Assert.Equal(2, msgs.Count);
        Assert.Equal("system", msgs[1].Role);
    }

    [Fact]
    public void Accepts_IllegalPlacement_AssistantPredecessor_Converted()
    {
        // U·A·S — predecessor=assistant. Illegal under 4.8 (Copilot 400s
        // with "system must follow a 'user' message or an 'assistant'
        // message ending in a server tool result"). Convert.
        var ctx = CtxWith(
            Msg("user", "hi"),
            Msg("assistant", "hello"),
            SystemMsg("after-assistant"));

        ProfileAdjuster.Apply(ctx, AcceptsMidConvSystem, EmptyCatalog);

        var msgs = ctx.Request.Body.Messages;
        Assert.Equal("user", msgs[2].Role);
        Assert.Equal(Marker + "after-assistant", TextOf(msgs[2]));
    }

    [Fact]
    public void Accepts_IllegalPlacement_UserSuccessor_Converted()
    {
        // U·S·U — predecessor=user, successor=user. Illegal under 4.8
        // (successor must be assistant or end-of-array; another user
        // breaks the rule). Convert.
        var ctx = CtxWith(
            Msg("user", "hi"),
            SystemMsg("between-users"),
            Msg("user", "go"));

        ProfileAdjuster.Apply(ctx, AcceptsMidConvSystem, EmptyCatalog);

        Assert.Equal("user", ctx.Request.Body.Messages[1].Role);
        Assert.Equal(Marker + "between-users", TextOf(ctx.Request.Body.Messages[1]));
    }

    [Fact]
    public void Accepts_IllegalPlacement_FirstMessage_Converted()
    {
        // [S, ...] — no predecessor at all. Illegal (Claude Code never
        // sends this shape since the actual system prompt goes in the
        // top-level field, but the adjuster must still handle it sanely).
        var ctx = CtxWith(
            SystemMsg("at-the-start"),
            Msg("user", "hi"));

        ProfileAdjuster.Apply(ctx, AcceptsMidConvSystem, EmptyCatalog);

        Assert.Equal("user", ctx.Request.Body.Messages[0].Role);
        Assert.Equal(Marker + "at-the-start", TextOf(ctx.Request.Body.Messages[0]));
    }

    [Fact]
    public void Accepts_MixedPlacements_KeepLegalConvertIllegal()
    {
        // Index-by-index legality (pred=predecessor role, succ=successor role):
        //   [0] U
        //   [1] S — pred=U, succ=A → LEGAL
        //   [2] A
        //   [3] S — pred=A, succ=U → ILLEGAL (predecessor not user)
        //   [4] U
        //   [5] S — pred=U, succ=A → LEGAL
        //   [6] A
        //   [7] U
        //   [8] S — pred=U, succ=EOA → LEGAL
        var ctx = CtxWith(
            Msg("user", "U0"),
            SystemMsg("S1-legal"),
            Msg("assistant", "A2"),
            SystemMsg("S3-illegal"),
            Msg("user", "U4"),
            SystemMsg("S5-legal"),
            Msg("assistant", "A6"),
            Msg("user", "U7"),
            SystemMsg("S8-legal"));

        ProfileAdjuster.Apply(ctx, AcceptsMidConvSystem, EmptyCatalog);

        var msgs = ctx.Request.Body.Messages;
        Assert.Equal(9, msgs.Count);
        Assert.Equal("system", msgs[1].Role);
        Assert.Equal("user", msgs[3].Role);   // converted
        Assert.Equal(Marker + "S3-illegal", TextOf(msgs[3]));
        Assert.Equal("system", msgs[5].Role);
        Assert.Equal("system", msgs[8].Role);
        // legal ones kept their original text
        Assert.Equal("S1-legal", TextOf(msgs[1]));
        Assert.Equal("S5-legal", TextOf(msgs[5]));
        Assert.Equal("S8-legal", TextOf(msgs[8]));
    }

    [Fact]
    public void Accepts_PlacementJudgedAgainstOriginalArray_NotProgressivelyRewritten()
    {
        // Two consecutive system messages: [U, S, S, A]. The legality of
        // the second S depends on whether we look at the ORIGINAL array
        // (its predecessor is S → illegal) or a PROGRESSIVELY rewritten
        // array (if the first S were converted to user, the second's
        // predecessor would become user → legal). The correct behavior is
        // to look at the original — converting the first changes nothing
        // about how Copilot sees the second.
        var ctx = CtxWith(
            Msg("user", "U0"),
            SystemMsg("S1"),
            SystemMsg("S2"),
            Msg("assistant", "A3"));

        ProfileAdjuster.Apply(ctx, AcceptsMidConvSystem, EmptyCatalog);

        var msgs = ctx.Request.Body.Messages;
        // S1: pred=U, succ=S → illegal (succ is system, not assistant) → convert
        // S2: pred=S, succ=A → illegal (pred is system, not user) → convert
        Assert.Equal("user", msgs[1].Role);
        Assert.Equal("user", msgs[2].Role);
    }

    [Fact]
    public void Accepts_AllSystemsLegal_NoMessagesRebuild()
    {
        // When every system message is legally placed, the rebuild branch
        // shouldn't run — body.Messages reference should survive unchanged.
        var originalMessages = new[]
        {
            Msg("user", "U0"),
            SystemMsg("legal-S"),
            Msg("assistant", "A2"),
        };
        var ctx = CtxWith(originalMessages);
        var beforeRef = ctx.Request.Body.Messages;

        ProfileAdjuster.Apply(ctx, AcceptsMidConvSystem, EmptyCatalog);

        Assert.Same(beforeRef, ctx.Request.Body.Messages);
        Assert.Equal("system", ctx.Request.Body.Messages[1].Role);
    }

    [Fact]
    public void Accepts_NoSystemMessages_BodyUntouched()
    {
        var ctx = CtxWith(
            Msg("user", "hi"),
            Msg("assistant", "hello"),
            Msg("user", "go"));
        var originalMessages = ctx.Request.Body.Messages;

        ProfileAdjuster.Apply(ctx, AcceptsMidConvSystem, EmptyCatalog);

        Assert.Same(originalMessages, ctx.Request.Body.Messages);
    }
}
