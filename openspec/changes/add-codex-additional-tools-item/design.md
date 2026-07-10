# Design — carry `additional_tools` through the IR verbatim

## Context

Codex (gpt-5.6) sends a new `input[]` item the bridge doesn't model, and the
bridge 400s on it at inbound deserialization. Live probe proves Copilot accepts
the item natively (200), so the fix is faithful carriage. The only real design
question is **where in the IR the item rides** and **how T2 puts it back**.

## The captured item (verbatim)

Always `input[0]`, `role: "developer"`, a `tools` array of four entries:

| nested `type` | `name` | shape |
|---|---|---|
| `custom` | `exec` | + `format:{type:grammar,syntax:lark,definition:…}` |
| `function` | `wait` | + `strict`, `parameters` (JSON-Schema) |
| `function` | `request_user_input` | + `strict`, `parameters` |
| `namespace` | `collaboration` | + nested `tools:[spawn_agent, send_message, …]` |

`collaboration.*` are **reserved built-in tools** — Copilot has a fixed schema for
them and 400s if the client sends a mismatched one (observed when a hand-written
stub was probed). The real Codex client sends the exact expected schema, so the
verbatim capture 200s. **The bridge must therefore never rewrite the nested tools
— byte fidelity is load-bearing, not just nice-to-have.**

## Decision 1 — carry it in `ProviderExtensions["openai"]`, not the messages array

The alternatives:

- **(A) Fold into IR `messages[]`** (like a `developer`-role `message` item, which
  T1 folds into the IR system prompt). **Rejected.** `additional_tools` is not
  conversation content — it is a tool-registration preamble. Folding its `tools`
  JSON into a system text block would (a) lose its structure, (b) be impossible to
  reconstruct as an `additional_tools` item at T2, and (c) risk the model reading
  a giant tool-schema blob as prose. It also has no text to fold — `content` isn't
  even a field on this item.
- **(B) Add a typed field to the IR `MessagesRequest`.** **Rejected.** The IR is
  the frozen Anthropic shape; it deliberately has no home for provider-specific
  knobs — that is exactly what `ProviderExtensions` exists for
  (`docs/ir-definition-design.md` §3). A new typed IR field for an OpenAI-only
  concept violates the freeze.
- **(C) Ride the request-level `ProviderExtensions["openai"]` bag verbatim.**
  **Chosen.** This is the established pattern for every un-modeled Codex knob
  (`tools`, `tool_choice`, `store`, `include`, …). T1 stashes it, T2 reads it
  back. Byte-faithful, no interpretation, AOT-clean (`JsonElement`).

Carriage shape in the bag:

```json
"openai": {
  "additional_tools": [ { "role": "developer", "tools": [ …verbatim… } ],
  "tools": …, "store": …, …
}
```

Modeled as an **array** to be forward-safe: every capture shows exactly one
`additional_tools` item at `input[0]`, but the Responses schema does not forbid
more, and an array carries N (including the observed N=1) without a second code
path. Each element records the item's `role` and opaque `tools`. Position is
implicitly "before the conversation messages" — see Decision 2.

## Decision 2 — T2 re-emits it first in `input[]`

Every observed capture puts `additional_tools` at `input[0]`, before any
conversation message. T2 already writes `input[]` by iterating `ir.Messages`;
the fix writes the carried `additional_tools` element(s) **first**, then the
messages. This reproduces the observed order (harness preamble → conversation),
which is also the only order the probe validated as 200.

Rationale for "first" rather than threading an exact index: the IR messages array
does not preserve the interleaving of a non-message input item among messages, and
no capture ever interleaves one. Emitting all carried `additional_tools` up front
is faithful to 100% of observed traffic and keeps T2 a single forward pass. If a
future Codex interleaves them, that is a new capture and a new, grounded change —
not a speculative code path now (the repo's "probe, don't guess" rule).

## Decision 3 — no coercion on the carried item

The request-level `tools[]` path drops `image_generation` (uniform 400) and, for
`mai-code-1-flash-internal`, `custom` tools (500). Those coercions are
**model-specific facts about the top-level tools array**. The `additional_tools`
item is a different field that probed 200 verbatim on gpt-5.6-sol (the only model
that sends it today). Applying the `tools[]` drops to it would be an ungrounded
guess and could corrupt the reserved `collaboration` schema. So T2 writes the
carried item **verbatim, no drops**. If a future model that sends `additional_tools`
rejects a nested shape, that is a fresh probe + profile flag — not pre-emptive here.

## Contract-derived tests

Per the repo's highest-priority testing directive — assert the *required
behaviour*, derived from the probe truth, not the implementation:

1. **Round-trip fidelity (unit, CI-safe).** Given the committed capture's
   `additional_tools` item, after `T1` then `T2` the outbound `input[]` contains
   an `additional_tools` item whose `tools` array is **byte-identical** to the
   input and appears **before** the conversation messages. Contract: "the bridge
   carries the item Copilot accepts, unchanged." Mutation check: break T2 to drop
   the item → test goes red.
2. **`/cc` hot path unaffected (unit).** A Claude Code request produces an IR with
   **no** `additional_tools` bag key and T2-for-Anthropic is untouched — the
   `/cc` serialization is byte-identical to before (H1). Contract: "the fix is
   Codex-only."
3. **Deserialization no longer throws (unit).** The exact failing capture body
   deserializes to a `ResponsesRequest` without a `JsonException`. Contract: "the
   discriminator is now recognized." Mutation check: remove the derived-type
   attribute → red.
4. **Live acceptance (integration, already run).**
   `ResponsesProbe.AdditionalToolsVerbatim` — the real item round-trips 200 on
   Copilot. This is the ground truth every unit test is derived from; kept as a
   living probe, not asserted in CI (needs live Copilot).

## Out of scope

- Translating `additional_tools` into top-level `tools[]` (unnecessary — native
  200; and the `namespace`/`collaboration` reserved schema makes hoisting fragile).
- `x-initiator` semantics for a `developer`-role preamble item (the header isn't
  set on this path today; unchanged).
- Any gpt-5.6 effort/profile work (already shipped in the prior gpt-5.6 change).
