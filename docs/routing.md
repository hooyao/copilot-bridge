# Routing configuration

> Practical reference for `appsettings.json` → `Routing.Locations`, the
> nginx-style match/rewrite layer in front of the model-profile system. For the
> framework-level contract (where this sits in the pipeline, the profile
> catalog, the adjuster) see [`pipeline-design.md` §7](pipeline-design.md). This
> doc is the config-author's view: syntax, examples, validation, and why it's
> shaped this way.

## Two layers: location = preference, profile = fact

What model id + body shape reaches Copilot is decided by two layers, in order:

1. **`Routing.Locations`** (this doc) — **operator preference**, editable in
   `appsettings.json`. "When a request looks like X, rewrite it to Y." Used to
   paper over a stale catalog, pick a deliberate fallback, or normalize client
   vocabulary Copilot rejects.
2. **`ModelProfileCatalog`** (C#, playground-derived) — **Copilot's actual wire
   contract**: which efforts/thinking shapes each model accepts, which betas to
   strip, etc. Not user-editable, because it's a fact about Copilot, not a
   preference. See [`pipeline-design.md` §7.2](pipeline-design.md).

A location fires first (rewriting model / effort / headers); then the resolved
target's profile coerces the body to what Copilot's gateway will actually
accept. **Locations express what you want; profiles enforce what's possible.**

## Location shape

```jsonc
{
  "When": { /* MatchExpression — see below */ },
  "Use":  { /* change-set — see below */ },
  "Note": "free-form; surfaced in debug logs and error messages"
}
```

Locations are scanned **top-to-bottom, first-match-wins**. No chain, no
fall-through: a request matches at most one location, applies its `Use`, and
moves on to the profile layer.

## `When` — the match expression

A small JSON-native tree (`Pipeline/Routing/MatchExpression.cs`). No custom DSL.

### Leaves

| Leaf | Form | Matches when… |
| --- | --- | --- |
| `Model` | `"claude-opus-4.8"` | the canonical (post-normalize) model id equals this, case-insensitive |
| `Effort` | `"max"` | `output_config.effort` equals this |
| `Header` | `{ "Name": "...", "Eq" \| "Contains": "..." }` | the named header matches (see below) |

**`anthropic-beta` is special.** It's a comma-token list, and routing asks
"does the client carry this beta?", not "does the raw header string equal
this". So for `Name: "anthropic-beta"`:
- `Contains: "context-1m-2025-08-07"` — that token is present.
- `Contains: "context-1m-*"` — a token with that prefix is present (trailing
  `*` wildcard).
- `Eq: "context-1m-2025-08-07"` — that exact token is present (same as
  `Contains` without a wildcard; token-set membership, not whole-string).

For any other header name, `Eq`/`Contains` match the raw header value
(case-insensitive equality / substring).

### Composites

| Node | Form | Semantics |
| --- | --- | --- |
| `AllOf` | `[ … ]` | AND — every child must match |
| `AnyOf` | `[ … ]` | OR — at least one child must match |

Composites nest arbitrarily. **Top-level `Model`/`Effort`/`Header` are
implicitly AND-ed** — the common case needs no `AllOf` wrapper. There is **no
`Not`**: negation is a readability/correctness footgun, so write two locations
instead.

### Examples

Simplest — model + a beta (implicit AND):

```jsonc
"When": {
  "Model": "claude-opus-4.6",
  "Header": { "Name": "anthropic-beta", "Contains": "context-1m-2025-08-07" }
}
```

Two models share one rule (OR on model, AND with the beta):

```jsonc
"When": {
  "AllOf": [
    { "AnyOf": [ { "Model": "claude-opus-4.7" }, { "Model": "claude-opus-4.8" } ] },
    { "Header": { "Name": "anthropic-beta", "Contains": "context-1m-2025-08-07" } }
  ]
}
```

Match on either of two conditions for one model — `opus-4.7 AND (1M-beta OR max-effort)`:

```jsonc
"When": {
  "AllOf": [
    { "Model": "claude-opus-4.7" },
    { "AnyOf": [
        { "Header": { "Name": "anthropic-beta", "Contains": "context-1m-*" } },
        { "Effort": "max" }
    ]}
  ]
}
```

## `Use` — the change-set

What to do when the location matches. At least one field is required (an empty
`Use` is rejected at startup as a silent no-op).

| Field | Effect |
| --- | --- |
| `Model` | Replace `body.model` with this canonical id. |
| `EffortMap` | `{ inbound: outbound }` — remap `output_config.effort` **after** the model swap, so the mapping is scoped to the resolved target (e.g. `{"max":"xhigh"}` only makes sense on an id that accepts xhigh). A value not in the map is left unchanged. |
| `Headers.Set` | `{ name: value }` — set/replace a whitelisted header. For `anthropic-beta`, the value is split into tokens and merged into the outbound set. |
| `Headers.Remove` | `[ "X-Foo", "anthropic-beta:context-1m-*" ]` — a plain entry drops the whole header; the `name:pattern` form drops matching `anthropic-beta` tokens (trailing `*` wildcard). |

### Header allow-list

`Headers.Set` / `Headers.Remove` only accept these names; anything else fails
startup validation:

- `anthropic-beta`
- `Editor-Version`
- `Editor-Plugin-Version`
- `Copilot-Integration-Id`
- `X-GitHub-Api-Version`

Bridge-internal protocol headers (`Authorization`, the VS Code session /
machine / device ids, the vision flag) are deliberately **off-limits** — a
config typo there would produce silent 401s or wrong-routing. Identity-header
overrides thread through `BridgeContext.CopilotHeaderOverrides` into
`CopilotHeaderFactory`; `anthropic-beta` add/remove flows through
`HeadersOutboundStage`.

## Validation (fail-fast at startup)

`RoutesValidator` runs before the listening socket opens. Any of these aborts
startup (exit code 2) with a message naming the offending `Routing.Locations[i]`:

- **Empty `When`** (no `AllOf`/`AnyOf`/`Model`/`Effort`/`Header` at all) —
  rejected. An empty match routes *every* request through the location; this
  guard also catches a nested composite that silently failed to bind (which
  would otherwise become a match-all that swallows all traffic).
- **Empty `AllOf`/`AnyOf` array** — rejected (ambiguous; likely a typo).
- **Empty `Use`** — rejected (the location would silently no-op).
- **Non-whitelisted header** in `Set`/`Remove` — rejected.
- **`Header` missing `Name`, or setting both `Eq` and `Contains`** — rejected.

## Shipped configuration

The bundled `appsettings.json` ships an **empty** active location list
(`"Locations": []`) — no rewrites by default. It also carries a **disabled**
example under `_Locations_disabled`, a key the config binder ignores (the same
leading-underscore convention used for `_comment` fields). To enable it, rename
`_Locations_disabled` to `Locations` (and rename the active empty `Locations` to
something else — exactly one `Locations` key may be active):

```jsonc
"_Locations_disabled": [
  {
    "When": { "Model": "claude-opus-4.8" },
    "Use": { "Model": "gpt-5.6-sol", "EffortMap": { "max": "xhigh" } },
    "Note": "Route Claude Code's claude-opus-4.8 traffic to Copilot gpt-5.6-sol (the newest Codex model). EffortMap max->xhigh is an OPTIONAL down-tier here: unlike gpt-5.5, gpt-5.6-sol accepts 'max' natively, so without the map Claude Code's 'max' passes through verbatim; the map caps it at xhigh instead. Drop the EffortMap to send 'max' through unchanged. Still a cross-model substitution — enable only when you intend Copilot's gpt-5.6-sol to serve Claude Code traffic."
  }
]
```

> Earlier releases shipped an active `gpt-5.5-1m -> gpt-5.5` Codex context-window
> alias here; it was removed when the active list was emptied. The `-1m` alias
> was a Codex-side trick (name the model `gpt-5.5-1m` with
> `model_context_window=1000000` to sidestep a client-side context cap Codex
> applies to the literal `gpt-5.5`, then map it back so Copilot's natively-1M
> `gpt-5.5` is used); if you still drive gpt-5.5 from Codex you can re-add it as a
> location.

### Retired: the opus 1M redirects

Earlier releases shipped two `"opus 4.x + 1M beta → dedicated 1M model id"`
redirects (opus-4.7/4.8 → `claude-opus-4.7-1m-internal`, opus-4.6 →
`claude-opus-4.6-1m`). Both were **removed in the 2026 model reconciliation**:

- Copilot **retired** the `claude-opus-4.7-1m-internal` and
  `claude-opus-4.6-1m` ids — a request for either now 400s with *"not available
  for integrator"* (verified by `ModelProfileProbe.RetiredCandidate_LivenessProbe`).
  A redirect to a retired target would be worse than no redirect.
- The opus-4.6 / opus-4.7 **base** ids now serve 1M context **natively** — a
  >600k-token prompt returns 200 with and without the `context-1m-2025-08-07`
  beta (`ModelProfileProbe.OpusBase_LargePrompt_ProbeOneMillionContextSupport`),
  exactly like opus-4.8 and sonnet-5. So there is nothing to unlock: the 1M beta
  passes through and the base model handles it.

The net effect for a client is unchanged — opus-4.6/4.7/4.8 + 1M beta all get
1M context — but now by identity passthrough rather than an id swap. If Copilot
ever re-introduces a dedicated 1M variant that the base doesn't cover, add the
redirect back as a location (the mechanism is unchanged).


## Why nginx-style

The previous design was a flat `Routing.Rules` list of `Match → Rewrite.Model`
pairs, where related behavior had to be spread across several rules that
composed implicitly across the list (and an effort remap was a separate rule
from the model redirect it depended on). That was hard to read and easy to get
wrong — a rule's effect depended on what other rules did before it.

The location model borrows nginx's `location { … }` idea: **each entry is a
self-contained closure**. Everything that should happen for "this kind of
request" — the match, the model swap, the effort remap, the header tweaks —
lives in one block. First-match-wins, no cross-rule composition to reason
about. One location holds the whole story (e.g. `opus-4.8 + 1M → 1m-internal`
**and** `max→xhigh`, together).

## Testing

- **Unit** (`tests/CopilotBridge.UnitTests`, no network/Copilot, ~90 ms):
  `MatchExpressionTests` (leaves, composites, the nested shapes above),
  `RoutesBindingTests` (nested `AllOf`/`AnyOf` binds from JSON to non-null
  lists), `RoutesValidatorTests` (the fail-fast rules, incl. the empty-When
  guard), `ModelRouteResolverTests` (the `Use` change-set mutates the context).
  Run: `dotnet test tests/CopilotBridge.UnitTests`.
- **AOT binding**: the JIT unit tests can't prove the AOT source-generated
  config binder handles the recursive `List<MatchExpression>`. That's verified
  by publishing the exe and confirming startup logs `Routes: N user locations`
  (a nested-binding failure would trip the empty-When guard and abort startup).
- **End-to-end** (`OneMillionContextRoutingTests`, in the Playground harness,
  needs a live Copilot login): drives real requests through the bridge and
  asserts the upstream model/effort/beta.

> **For contributors / agents:** Playground tests are tagged
> `[Trait("Category", "Integration")]` so CI can skip them
> (`dotnet test --filter "Category!=Integration"`) — they require live Copilot +
> DPAPI. **Any new test in `tests/CopilotBridge.Playground` must carry that
> trait**; pure-logic tests with no external dependency belong in
> `tests/CopilotBridge.UnitTests` instead.
