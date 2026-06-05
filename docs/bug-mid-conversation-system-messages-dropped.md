# Bug Report: Mid-conversation `role: "system"` messages silently dropped, losing all Claude Code queued user messages

**Status**: confirmed root cause, evidence captured, reproducer trivial
**Severity**: high (silent data loss of user instructions)
**Reporter**: Hu Yao (via investigation done from the upstream Claude Code session)
**Date**: 2026-06-05

## TL;DR

When the user types a message while Claude Code is mid-execution (running tools, no yield yet), Claude Code internally captures it and **injects it into the next API request as a message with `role: "system"`**, immediately before the next assistant turn. This is **not** an error in Claude Code — it's how the TUI's "queue while busy" feature delivers messages.

`MessagesSanitizeStage` (or a stage upstream of it in the request pipeline) is silently discarding **all** mid-conversation `role: "system"` messages on their way to Copilot. The result: every queued user message gets dropped, plus all of Claude Code's other mid-conversation system reminders (task reminders, plan-mode notes, MCP/skill instructions, etc.). The upstream model never sees them and behaves as if they were never sent.

This matches the upstream-LLM behavior the user has been reporting for the last several turns ("I queue messages while you're running tools and you ignore them") — those messages literally never reached the model.

## Evidence

All traces are in `C:\Users\HuYao\Desktop\copilot-bridge\request-traces\`, captured by `BridgeIoSink`. The naming convention is `<utc>-<seq>-<inbound|upstream>-<req|resp>.json`. 227 inbound-req files exist for the recent debugging session.

### Aggregate pattern (last 10 turns)

```
trace                                             user  asst   sys
inbound  20260605-120110-0037-inbound-req.json   338   337    72
upstream 20260605-120110-0037-upstream-req.json  338   337     0
inbound  20260605-120219-0038-inbound-req.json   339   338    72
upstream 20260605-120219-0038-upstream-req.json  339   338     0
inbound  20260605-120227-0039-inbound-req.json   340   339    72
upstream 20260605-120227-0039-upstream-req.json  340   339     0
... (pattern continues for every trace)
```

**Inbound has 72-73 `role: "system"` messages mixed in throughout the messages array; upstream has 0. user and assistant counts are preserved.** This is 100 % reproducible across every recent trace.

### Categorization of the 72 dropped system messages in trace 0038

| Category | Count | What it is |
|---|---|---|
| **Queued user messages** | **21** | `"The user sent a new message while you were working: <text>\n\nIMPORTANT: After completing your current task, you MUST address the user's message above. Do not ignore it."` |
| Task reminders | 41 | `"The task tools haven't been used recently. If you're working on tasks that would benefit from tracking progress..."` |
| MCP / skill instructions | 1 | `"# MCP Server Instructions\n\nThe following MCP servers have provided instructions..."` |
| Other (plan-mode notes, etc) | 9 | misc |

### Sample queued user messages that got dropped

These are real user instructions from this session that the upstream LLM never received. Pick any of them as a reproducer for the "user feels ignored" symptom:

- `[147] "不过每次执行操作前都给我确认下"`
- `[152] "你用就是了"`
- `[177] "你先验证下这个能用" + "再去操作"`
- `[237] "hugging face token是 hf_REDACTED..."`  ← user shared a credential, model never saw it, user wondered why nothing happened
- `[280] "对了今天结束之前你看看能不能把nvcr.io 的镜像用nvcr.m.daocloud.io加速" + "还有把我们这个repo在github上建个private repo推上去"`  ← two separate instructions queued together
- `[421] "还有我想看下有没有ecc报错"`
- `[505] "这是个submodule"`
- `[521] "我今晚睡觉前想开始数学refresh，你帮我开始一下"`
- `[610] "弄好了别忘更新文档和索引"`
- `[710] "我觉得这个指令太极端了" + "会让我们的对话充满摩擦，但是想法是对的"`  ← two judgments back-to-back
- `[741] "...这里边有非常完整的message trace, 你帮忙分析下"`  ← the message that triggered this investigation

### What an inbound `role: "system"` queued-user-message looks like (exact JSON shape)

From `20260605-120219-0038-inbound-req.json`, message `[741]`:

```json
{
  "role": "system",
  "content": "The user sent a new message while you were working:\nC:\\Users\\HuYao\\Desktop\\copilot-bridge\\request-traces 这里边有非常完整的message trace, 你帮忙分析下\n\nIMPORTANT: After completing your current task, you MUST address the user's message above. Do not ignore it."
}
```

Note `content` is a plain string in this case (not an array of content blocks). Other system messages in the same array also use the plain-string form. The role is the discriminator.

### What ends up at upstream

Same trace, upstream-req: the entire `[741]` slot does not exist. The messages array is contiguous user / assistant / user / assistant with no gap and no replacement marker. The model has no signal that anything was elided.

## Why this is a Copilot bridge issue, not a Claude Code issue

It is a known and intentional Claude Code behavior that queued-while-busy user messages get marshaled into the next request via `role: "system"` content. This is also how the upcoming **Anthropic mid-conversation system messages** feature (released with Opus 4.8) is meant to be used:

> "Claude Opus 4.8 accepts `role: \"system\"` messages **immediately after a user turn** in the messages array (subject to placement rules). This lets you append updated instructions later in a long-running conversation without restating the full system prompt..."
> — https://platform.claude.com/docs/en/about-claude/models/whats-new-claude-4-8

So:

- Anthropic Messages API on Opus 4.8 **explicitly accepts** mid-conversation `role: "system"` entries.
- Claude Code uses this same mechanism to inject queued user messages, task reminders, and skill instructions.
- The proxy is dropping all of them on the way to Copilot.

If Copilot's upstream cannot accept `role: "system"` outside the top-of-conversation position (likely, since GitHub Copilot's adapter is OpenAI-shaped underneath), the right fix is **conversion, not deletion**.

## Where the bug is (high-confidence guess; please verify)

Suspect chain in `src/CopilotBridge.Cli/Pipeline/Stages/Anthropic/`:

- `MessagesSanitizeStage.cs` — currently only drops `"Tool loaded."` markers and appends `"Please continue."` on trailing-assistant. **Does NOT filter `role: "system"` — so the drop is happening elsewhere.**
- `AssistantThinkingFilterStage.cs` — only touches `role: "assistant"`. Innocent.
- `SystemSanitizeStage.cs` — **prime suspect**. Did not read it during the investigation. Likely culprit is here: a stage named `SystemSanitize` would naturally collapse / strip system entries to fit a "single top-level system prompt" target shape.

Also worth checking:

- `CopilotMessagesPassthroughStrategy.cs` — confirmed byte-passthrough, does not modify body. Innocent.
- `ClaudeCodeInboundAdapter.cs` — identity adapter, returns `clientBody` unchanged. Innocent.

So the drop is between the inbound adapter and the upstream forwarder. Among the request stages, `SystemSanitizeStage` is the obvious place.

## Suggested fix

Two acceptable shapes:

### Option A — convert mid-conversation `role: "system"` to `role: "user"` with a marker prefix

For every `role: "system"` message that is NOT at messages[0] (i.e. is a mid-conversation injection, not the actual top-of-conversation system prompt — though note: Claude Code does not put the top system prompt in messages[], it goes in the top-level `system` field):

```csharp
// Pseudocode
foreach (var msg in messages) {
    if (msg.Role == Role.System) {
        msg.Role = Role.User;
        // Prefix so the LLM can tell this is an injected context message, not a user utterance:
        msg.Content = "[Claude Code injected]\n" + msg.Content;
    }
}
```

Pros: zero data loss. Upstream LLM sees everything, including queued user messages. The `[Claude Code injected]` prefix gives the model a hint that this is harness-generated, not user-typed (so it can ack appropriately without being confused).

Cons: the model sees these messages with elevated salience (as user role). For task reminders that's fine; for queued user messages that's exactly what we want. The injected MCP / skill list is also harmless to elevate.

### Option B — collapse all mid-conversation `role: "system"` into a single appended `role: "user"` block before the trailing assistant/user pair

```csharp
var systemMessages = messages.Where(m => m.Role == Role.System).ToList();
if (systemMessages.Any()) {
    var merged = string.Join("\n\n---\n\n",
        systemMessages.Select(m => m.Content));
    // Insert as a user message immediately before the most recent user turn,
    // or append after the last assistant turn, depending on placement.
    messages.Insert(...);
}
messages.RemoveAll(m => m.Role == Role.System);
```

Pros: only one extra user message; cleaner request shape.

Cons: temporal ordering is lost (the "you sent X while I was working" loses its position relative to the assistant's tool calls). For "queued user message" specifically the temporal position matters less than the fact that the LLM sees it at all, so this is acceptable.

**Recommendation: Option A.** It preserves temporal ordering and is one-line trivial. The `[Claude Code injected]` marker is enough for the LLM to handle these correctly.

### What NOT to do

Don't silently drop. That's the current behavior and the root cause of this bug.

Don't try to map them to the top-level `system` field. That's not addressable per-position and would either overwrite the actual system prompt or grow it unboundedly across turns.

Don't use `role: "system"` in the upstream body if Copilot's adapter rejects it (which I haven't verified but suspect is why the current stage strips them). Just convert to `user`.

## Reproduction

Trivially reproducible in any current session:

1. Start a Claude Code session against the bridge with any tool-using prompt that triggers >5 seconds of tool calls.
2. While the assistant is mid-execution (status indicator shows running), type a message and press Enter. The TUI shows "queued".
3. When the assistant yields, observe its response. It will not reference the queued message at all.
4. Inspect the corresponding `inbound-req.json` — there will be a `role: "system"` message with content starting with `"The user sent a new message while you were working:"`.
5. Inspect the corresponding `upstream-req.json` — the `role: "system"` message is gone.

## Test coverage suggestion

Add an integration test that:

1. Constructs an inbound request with a `role: "system"` message mid-conversation (content: `"The user sent a new message while you were working: hello"`).
2. Runs it through the request pipeline.
3. Asserts the upstream body contains exactly one message with `role: "user"` whose content includes `"hello"` (either with or without a marker prefix, depending on which option above is implemented).

Without this test, the current behavior will silently regress again the next time anyone refactors message sanitization.

## Additional findings worth mentioning to the upstream LLM

Once this is fixed, the upstream LLM (Claude Opus 4.8) will start receiving the queued user messages. It should be told that:

- These will arrive with a `[Claude Code injected]` prefix (or whatever marker the fix uses).
- The format is `"The user sent a new message while you were working:\n<message text>\n\nIMPORTANT: After completing your current task, you MUST address the user's message above. Do not ignore it."`
- The LLM should explicitly acknowledge each such message at the start of its next assistant turn (e.g. with a "Re your queued messages: ..." preamble) so the user gets visible confirmation.

This is a model-side / system-prompt concern, not a bridge concern, but it's worth documenting in this report so whoever sets up the upstream behavior knows the bridge is now correctly delivering these messages and is no longer the bottleneck.

## Why this took so long to find

The user reported "I queue messages and you ignore them" multiple times over several days. Each time the upstream LLM (correctly, given what it was seeing) attributed it to either:

- (a) Claude Code TUI not delivering the messages
- (b) Anthropic harness deprioritizing the messages relative to tool_result content
- (c) Its own attention not being allocated to mid-conversation system reminders

None of these were correct. The bridge was silently dropping the messages before they ever reached the model. The investigation found this in ~10 minutes once the trace data was inspected — but for those 10 minutes the upstream LLM had no reason to suspect the bridge, because every other aspect of the pipeline was working perfectly (tool calls, responses, streaming, history continuity).

**Operational lesson for the bridge**: when a stage drops messages, log a structured event with the dropped count and category, not just a debug-level "dropped X" line. This would have surfaced the issue the first time it bit a user.

## Files referenced

- `C:\Users\HuYao\Desktop\copilot-bridge\request-traces\20260605-120219-0038-inbound-req.json` — the canonical evidence trace
- `C:\Users\HuYao\Desktop\copilot-bridge\request-traces\20260605-120219-0038-upstream-req.json` — matching upstream with system messages stripped
- `src/CopilotBridge.Cli/Pipeline/Stages/Anthropic/SystemSanitizeStage.cs` — prime suspect for the drop
- `src/CopilotBridge.Cli/Pipeline/Stages/Anthropic/MessagesSanitizeStage.cs` — confirmed innocent (only drops `"Tool loaded."`)
- `src/CopilotBridge.Cli/Pipeline/Adapters/ClaudeCode/ClaudeCodeInboundAdapter.cs` — confirmed innocent (identity)
- `src/CopilotBridge.Cli/Pipeline/Strategies/Anthropic/CopilotMessagesPassthroughStrategy.cs` — confirmed innocent (byte passthrough)

---

## Update / Playground & cache findings (2026-06-05, follow-up RCA)

The "where it drops" diagnosis above was off by one stage. **Nothing in `Pipeline/Stages/Anthropic/` was filtering `role:"system"`** — the actual transform was `ProfileAdjuster.FoldMidConversationSystem` in `src/CopilotBridge.Cli/Pipeline/Routing/ProfileAdjuster.cs:149`, which moves mid-conv system messages **into the top-level `system[]` field** instead of dropping them. So they did reach upstream, just in the wrong slot — which is functionally close to dropping (loss of temporal anchor, model treats them as global static context, etc. — see the original report).

A round of Playground probes (added to `tests/CopilotBridge.Playground/ModelProfileProbe.cs`) and a cache prototype (`docs/scratch/midconv-cache-prototype.py`) reshaped the fix.

### Finding 1 — opus-4.8 already accepts mid-conv `role:"system"` on Copilot

The catalog comment that justified the universal fold —

> `// empirical: Copilot rejects role:"system" mid-conv even on 4.8`

— was a misread of a single-placement probe. Re-probing with multiple placements separates "role unsupported" from "role supported, position wrong":

| Model | Behavior |
|---|---|
| 4.5 / 4.6 / sonnet-4.6 / haiku-4.5 / opus-4.7 / opus-4.7-1m-internal | 400 `Unexpected role "system". The Messages API accepts a top-level system parameter, not "system" as an input message role.` (role unrecognized everywhere) |
| **claude-opus-4.8** | Accepts `role:"system"` — error surface changed to **placement-specific** messages |

4.8 placement matrix (S = system, U = user, A = assistant):

| Position | Result |
|---|---|
| `U·S` (end after user) | **200 OK** ✅ |
| `U·A·U·S` (end after user with prior assistant) | **200 OK** ✅ |
| `U·S·U` | 400 `must precede an 'assistant' message or end the array` |
| `U·A·S` | 400 `must follow a 'user' message or an 'assistant' message ending in a server tool result` |
| `U·A·S·U` | 400 same as above |
| `U·A·S·A` | 400 same as above |

Two placement rules (conjunction):

1. **Predecessor**: must be a `user` (Claude Code never emits the other allowed predecessor, "assistant ending in a server tool result").
2. **Successor**: must be `assistant` OR end-of-array.

The 4.8 mid-conv-system feature is **on by default** — sending the `mid-conversation-system-2025-XX-XX` beta header changes nothing; not sending it is also fine. (So the `Pipeline.OutboundBeta.GlobalStrip` entry for `mid-conversation-system-*` is unnecessary and was removed in this fix.)

### Finding 2 — Copilot accepts consecutive `user` messages

Probed `U·U` and `U·A·U·A·U` against haiku-4.5, sonnet-4.6, opus-4.7, opus-4.7-1m-internal, opus-4.8 — all **200 OK**. So the alternative shape (convert `role:"system"` → `role:"user"` with an injected-context marker) is wire-legal on every Copilot model, including 4.7 fallbacks where 4.8 placement rules don't apply.

### Finding 3 — the current FOLD strategy is the worst cache option of the three considered

This was the surprise. The `system[]` field is supposed to be the cache-friendly slot, but FOLD breaks cache in a way CONVERT and PLACEMENT_FIX do not.

The prototype (`docs/scratch/midconv-cache-prototype.py`) replays 9 consecutive turns of the bug-window traces (0035 → 0044), simulates each strategy, and computes Anthropic-style prefix-bytes equality at every `cache_control` breakpoint (with `cache_control` fields themselves stripped from the hash, as Anthropic's implementation must do or append-only history could never hit cache).

| Strategy | System bp hits | Last message bp hits |
|---|---|---|
| **FOLD** (current) | 18/18 | **7/9** |
| **CONVERT** (system→user with marker) | 18/18 | **9/9** |
| **PLACEMENT_FIX** (keep S in legal 4.8 positions, else CONVERT) | 18/18 | **9/9** |

FOLD failure mode is mechanical: every time Claude Code injects a new task-reminder system message mid-conversation, FOLD appends it to `system[]`. That makes the **system field N+1 ≠ system field N** for any breakpoint located in `messages[]` — because Anthropic hashes `tools + system + messages-prefix` together. The cache bottoms out at the deepest system breakpoint (system[2]) instead of the much deeper last-tool_result breakpoint at messages[~750].

CONVERT keeps the system field byte-stable across turns (system messages stay in `messages[]`, in append-only positions) and is therefore strictly cache-better than FOLD. PLACEMENT_FIX is **byte-identical** to CONVERT in cache behavior — its only difference (keeping legal-placement S in place) doesn't reshuffle prior messages, so the prefix is unchanged.

### The fix

Two-layer rewrite of `ProfileAdjuster.FoldMidConversationSystem`, keyed off `profile.AcceptsMidConversationSystem`:

- **Layer A — convert (fallback for every non-4.8 profile)**:
  Each mid-conv `role:"system"` becomes a `role:"user"` whose first text block is prefixed `"[Claude Code injected]\n"`. Top-level `system[]` untouched. `CacheControl` on the original text block is preserved. Plain-string content gets normalized to a single-text-block array as part of the convert.

- **Layer B — placement-fix (opus-4.8)**:
  For each mid-conv `role:"system"`: if its predecessor is `user` AND its successor is `assistant` or end-of-array → keep as-is. Otherwise → convert (Layer A path). `AcceptsMidConversationSystem` flips to `true` for `claude-opus-4.8` only.

Also fixed in this change:

- `ModelProfileCatalog.cs` opus-4.8 comment updated; cross-cutting note in the class doc no longer claims "no model — including opus-4.8 — accepts mid-conv system".
- `appsettings.json` `Pipeline.OutboundBeta.GlobalStrip` loses the `mid-conversation-system-*` entry.
- `ProfileAdjuster` emits a structured **INFO** log per call with `{convertedCount, keptInPlaceCount}` — answering the original report's "operational lesson" so the next regression surfaces in production logs without Debug.
- New unit tests in `tests/CopilotBridge.UnitTests` for: convert-when-not-accepted, multi-system convert, keep-in-place when 4.8 placement legal, convert when 4.8 placement illegal, string-content system, `CacheControl` preservation, no-op when no system messages.

### What was NOT changed

- The `[Claude Code injected]\n` marker text — the original report's recommendation. The model can use it to distinguish harness-injected context from a user utterance.
- Mid-conv-system deduplication. Across turns, Claude Code re-sends the same queued-user-message system entries every turn (history is append-only), so they'll accumulate. The cache stability of CONVERT means this growth is amortized over the cache lifetime; if it becomes a problem, in-request dedup is a follow-up.

