# NEXT — pick up 1M context routing

> Handoff written **2026-05-21** for the next Claude Code session on a
> different machine. Read [`docs/pipeline-design.md`](docs/pipeline-design.md)
> §7.5 first — that section has the design (configuration grammar,
> empirical Copilot model table, file-by-file touch points). This file
> captures **how to execute it**: order of steps, the open decision that
> blocks final implementation, and a few sharp edges that aren't
> documented anywhere else.

## Why this work matters

Today the bridge silently drops the inbound `anthropic-beta:
context-1m-2025-08-07` header. Claude Code users who flip the "1M context"
toggle hit a wall: the 200k cap on `claude-opus-4.7` is never lifted
because Copilot serves 1M as a **dedicated model id** (`claude-opus-4.7-1m-internal`),
not a beta. Same story for `claude-opus-4.6-1m`. The fix is in the routing
layer, not in the protocol code — it's a configuration extension.

## Before you write any code

### Step 0 — get a working bridge on this machine

The bridge persists its GitHub OAuth token via **Windows DPAPI scoped to
CurrentUser**. Tokens copied from another machine **will not decrypt** —
DPAPI uses the local Windows account's master key. On a fresh checkout:

```pwsh
dotnet publish src/CopilotBridge.Cli -c Release -r win-x64
.\publish\copilot-bridge.exe serve
# Prints a device-code URL + user code; complete it in your browser.
# The token lands at .\publish\github_token.dat (and is picked up on
# subsequent starts).
```

`dotnet publish` produces `<repo>/publish/copilot-bridge.exe` (the
`PublishDir` in the csproj overrides the default `bin/.../publish/` path).
AOT publish requires the VS 2022 C++ Build Tools workload + Windows SDK.
On this machine you may also need `vswhere.exe` on PATH — if AOT linking
fails with `'vswhere.exe' is not recognized`, prepend
`C:\Program Files (x86)\Microsoft Visual Studio\Installer` to PATH and
retry.

To run the playground (which calls Copilot through your token, no bridge
in the middle), the test fixture reads the token from the test binary's
`BaseDirectory` or `~/github_token.dat`. Copy the token from
`publish/github_token.dat` to one of those locations after the device-code
flow completes:

```pwsh
copy .\publish\github_token.dat $env:USERPROFILE\github_token.dat
```

### Step 1 — close the open design question with empirical data

`docs/pipeline-design.md` §7.5 — see the "Inbound beta default behavior"
subsection — calls out an open decision: **does Copilot accept, reject, or
silently ignore the various `anthropic-beta` tokens that Claude Code can
send?** The right default for the bridge (forward whitelist vs. forward
everything vs. forward nothing) hinges on this fact, and the project is
fact-driven — no guessing.

Extend `tests/CopilotBridge.Playground/BetaAcceptanceTests.cs`. The class
already probes 6 beta tokens. Add at minimum:

| Beta token                          | Where it comes from                                       |
|-------------------------------------|-----------------------------------------------------------|
| `context-1m-2025-08-07`             | Claude Code's "1M context" toggle                         |
| `extended-cache-ttl-2025-04-11`     | Anthropic SDK; CC may send it                             |
| `output-128k-2025-02-19`            | Anthropic SDK                                             |
| `fine-grained-tool-streaming-2025-05-14` | Anthropic SDK                                        |
| `token-efficient-tools-2025-02-19`  | Anthropic SDK                                             |

The existing test already calls each token through the bridge and dumps
status + body without asserting acceptance. Extend the data set; **also
add a second probe** that sends each token through Anthropic Direct (your
own Anthropic API key in `appsettings.local.json::AnthropicApiKey`) so the
matrix distinguishes:

- 200 from both → fine
- 200 from Anthropic, 400 from Copilot → strip it before forwarding
- 200 from Anthropic, 200 from Copilot but **upstream behaves differently**
  → can't catch this here, but worth a comment

Run:

```pwsh
dotnet test tests/CopilotBridge.Playground `
  --filter "FullyQualifiedName~BetaAcceptance" `
  --logger "console;verbosity=detailed"
```

Copy the raw status/body matrix into a new subsection in §7.5
("Inbound beta default behavior — empirical results"). Then pick the
default. My guess based on existing project posture: **default-strip
unknown betas, explicit whitelist via a new `Rewrite.PassThroughBetas` (or
a top-level `Routing.BetaPassThrough`)** — but the data may surprise you.

### Step 2 — confirm the 1M model id is still the same

The empirical table in §7.5 was taken from a single `/models` dump.
Re-run `DumpClaudeModelsAndCapabilities` to confirm `claude-opus-4.7-1m-internal`
is still on your account and its `max_context_window_tokens` is still
1000000. Copilot's catalog changes; the table in the doc could be stale
by the time you read this.

```pwsh
dotnet test tests/CopilotBridge.Playground `
  --filter "FullyQualifiedName~DumpClaudeModelsAndCapabilities" `
  --logger "console;verbosity=detailed"
```

If the model id has been renamed, update §7.5 + the rule examples below
before coding.

## Implementation order

The order matters — earlier steps unblock later ones, and pipeline
plumbing changes will break tests until the final rule wires through.

### Step 3 — IR field for inbound betas

Pick one of two homes for the parsed beta set:

- **Option A**: `[JsonIgnore] IReadOnlyList<string>? InboundBetaHeader`
  on `MessagesRequest`. Wire-invisible, traveling with the request body
  through the pipeline. **Risk**: someone forgets `[JsonIgnore]` and the
  field leaks onto the upstream JSON.
- **Option B**: a property on `BridgeContext<TBody>`, populated by the
  inbound endpoint before the pipeline runs. Doesn't touch the DTO at all.
  **Risk**: response stages already have access to context but
  request-shape stages currently only touch `ctx.Request.Body` — adding
  another channel widens the surface.

**My recommendation**: Option B. The bridge already uses
`ctx.DroppedEvents` for the same "side-channel that the wire shape
doesn't carry" pattern (added in this commit). Stay consistent.

```csharp
// Pipeline/BridgeContext.cs
public IReadOnlySet<string> InboundBetas { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
```

`ClaudeCodeInboundAdapter` populates it from the headers dictionary it
already receives. CSV split, trim each token, lowercase or keep
ordinal-ignore-case — match whatever you put on `RouteMatch.InboundBeta`'s
comparison.

### Step 4 — extend `RoutesConfig`

```csharp
// Pipeline/Routing/RoutesConfig.cs

public sealed class RouteMatch
{
    public string? InboundModel    { get; init; }
    public string? InboundEffort   { get; init; }
    public string? InboundThinking { get; init; }
    public string? InboundBeta     { get; init; }  // NEW
}

public sealed class RuleRewrite
{
    public string?            Model     { get; init; }
    public string?            Effort    { get; init; }
    public ThinkingRewrite?   Thinking  { get; init; }
    public bool               DeriveBudgetFromEffort { get; init; }
    public bool               DeriveEffortFromBudget { get; init; }
    public IReadOnlyList<string>? StripBetas { get; init; }  // NEW
}
```

Add the corresponding `JsonSerializable` lines if needed (POCOs bound via
`builder.Configuration.GetSection("Routing")` usually don't need source-gen
entries — the configuration binder is its own thing — but verify after
the first build).

### Step 5 — extend `ModelRouteResolver.Matches` and `ApplyRewrite`

```csharp
// Matches() — add after the existing InboundThinking check:
if (match.InboundBeta is not null
    && !ctx.InboundBetas.Contains(match.InboundBeta))
{
    return false;
}

// ApplyRewrite() — accumulate strip directives on the context so the
// HeadersOutboundStage can see them later. Add a new collection on the
// context for this, or push them through Rewrite return value — pick
// whatever feels less invasive.
if (r.StripBetas is { Count: > 0 } stripBetas)
{
    ctx.PendingBetaStrips.AddRange(stripBetas);
}
```

Note that `Matches()` currently takes `(RouteMatch, MessagesRequest)`.
You'll need to thread the context through too — change the signature to
`Matches(RouteMatch, BridgeContext<MessagesRequest>)` and update the
caller. Small mechanical change.

### Step 6 — extend `HeadersOutboundStage`

Currently this stage **clears all inbound headers** and rebuilds the
outbound list from scratch (`adaptive-thinking`, `context-management`,
`tool-search`). After the changes:

1. Compute the outbound betas as today (the three derived ones).
2. Add any inbound betas that pass the forward whitelist (Step 1
   decides what the whitelist is). For now, simplest behavior: **also
   forward inbound betas verbatim**, then run the strip filter.
3. Apply `StripBetas` patterns. Each pattern can end in `*` —
   trailing-wildcard match against each token, remove matches.
4. Join the final list, write `anthropic-beta` header.

Wildcard implementation: `pattern.EndsWith("*") ? token.StartsWith(pattern[..^1], OrdinalIgnoreCase) : token.Equals(pattern, OrdinalIgnoreCase)`.

### Step 7 — extend `RoutesValidator`

- Validate `Match.InboundBeta` is a non-empty token (no commas, no spaces).
- Validate `Rewrite.StripBetas` patterns are non-empty.
- Validate `Match` constrains at least one dimension (this rule already
  exists — make sure the new `InboundBeta` counts).

### Step 8 — appsettings.json — ship the two rules

```json
{
  "Routing": {
    "Rules": [
      // ... existing rules ...
      {
        "Match":   { "InboundModel": "claude-opus-4.7", "InboundBeta": "context-1m-2025-08-07" },
        "Rewrite": { "Model": "claude-opus-4.7-1m-internal", "StripBetas": ["context-1m-*"] },
        "Note":    "1M context: Copilot exposes it as a dedicated model id, not a beta"
      },
      {
        "Match":   { "InboundModel": "claude-opus-4.6", "InboundBeta": "context-1m-2025-08-07" },
        "Rewrite": { "Model": "claude-opus-4.6-1m", "StripBetas": ["context-1m-*"] }
      }
    ]
  }
}
```

### Step 9 — verification

Two layers:

1. **Unit-ish**: a new headless test in
   `tests/CopilotBridge.Playground/Headless/` that drives `claude.exe`
   with the 1M effort toggle enabled, then asserts the resulting
   `logs/<utc>-<seq>-upstream-req.json` contains
   `body.model == "claude-opus-4.7-1m-internal"` and the headers section
   does **not** contain `context-1m-*`. `EffortRoutingTests.cs` is the
   precedent — same pattern.
2. **Smoke**: open Claude Code with `ANTHROPIC_BASE_URL=http://localhost:8765/cc`,
   flip the 1M toggle, send a deliberately-large prompt (>200k tokens
   worth of context). Without the fix Copilot will return an error
   about exceeding context window; with the fix it should succeed.

## Sharp edges you might hit

- **Effort on the 1M variant**: Copilot does **not** expose
  `claude-opus-4.7-1m-internal-high` / `-xhigh`. So when a 1M routing
  rule fires and the inbound request had `effort=max`, the existing
  capability layer (`CopilotModelCatalog`) will not find a variant to
  route to. Check what `ApplyEffortRouting` does for an unknown variant
  — current behavior is "strip effort" for unknown models, which should
  be the right default here too. Confirm by reading
  `CopilotModelCatalog.DecideEffortRouting`.
- **Adapter vs endpoint for inbound beta parsing**:
  `ClaudeCodeInboundAdapter` currently sees the headers dictionary the
  endpoint built. Parsing belongs there (adapters are the inbound
  translation seam). But if you put it in the endpoint instead, the
  pipeline starts depending on something the endpoint did rather than
  on the adapter — slightly murkier seam.
- **The bridge runs in a single OS account**: anyone testing this needs
  their own `auth login`. Don't share `github_token.dat` files across
  machines.
- **`anthropic-beta` header is comma-separated**, but the Anthropic SDK
  also accepts multiple `anthropic-beta` headers. Test both forms in the
  inbound parser; Claude Code's actual output is in
  `logs/<utc>-<seq>-inbound-req.json` after one real request.
- **CountTokens currently hardcodes the upstream URL** in
  `ClaudeCodeCountTokensEndpoint.cs` for the audit (`ICopilotClient`
  doesn't expose the real URL). Not in scope for routing, but if you're
  in there anyway, this is the smallest possible follow-up.

## Where I'd start if I were you

1. `git pull` this branch (`pipeline-and-gap-analysis`), `dotnet publish`,
   `auth login`, copy token to `~/github_token.dat`.
2. Extend `BetaAcceptanceTests.cs`, run it, paste the matrix into
   `docs/pipeline-design.md` §7.5.
3. Re-run `DumpClaudeModelsAndCapabilities` to confirm `1m-internal` is
   still there.
4. Decide forward-whitelist policy; record the decision + the data in the
   doc.
5. Implement Steps 3-8 in order. Each step compiles standalone — push a
   commit after each. The build won't actually exercise the new path
   until Step 8 (appsettings rule) lands.
6. Write the headless test in Step 9 and run it against a real Copilot
   token.

Ping the original author if any of the design decisions look wrong once
you have the empirical data — the design is provisional, not load-bearing.
