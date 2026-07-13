## ADDED Requirements

### Requirement: Recursive Claude Code delegation is filtered at the Responses translation boundary

When enabled, the bridge SHALL omit the exact `Agent` tool from the upstream Responses request if and only if the downstream request came from a Claude Code sub-agent and routing selected a Responses backend. The bridge SHALL classify a request as a sub-agent from a non-empty inbound `x-claude-code-agent-id` header without requiring a parent-agent header. The bridge SHALL NOT mutate the shared IR tool collection.

Each request from which the bridge actually removes `Agent` SHALL emit a warning that identifies `Pipeline:CcToResponses:PreventRecursiveAgentDelegation=false` as the recovery setting when the removal is considered incorrect. The bridge SHALL NOT emit this warning merely because a request is classified as a sub-agent when no `Agent` tool was removed.

#### Scenario: First-generation sub-agent cannot delegate recursively

- **WHEN** a `/cc` request carries `x-claude-code-agent-id`, carries no `x-claude-code-parent-agent-id`, includes `Agent` plus another tool, and is routed to `/responses` with the guard enabled
- **THEN** the upstream Responses `tools[]` omits `Agent` and retains the other tool
- **AND** the original IR still contains `Agent`.

#### Scenario: Deeper sub-agent cannot delegate recursively

- **WHEN** a `/cc` request carries both agent-id and parent-agent-id headers and is routed to `/responses` with the guard enabled
- **THEN** the upstream Responses `tools[]` omits `Agent`.

#### Scenario: Root Claude Code agent retains delegation

- **WHEN** a root `/cc` request has no agent-id header, includes `Agent`, and is routed to `/responses` with the guard enabled
- **THEN** the upstream Responses `tools[]` retains `Agent`.

#### Scenario: Native client paths are unchanged

- **WHEN** a native `/cc` request is routed to the Anthropic upstream or a native `/codex` request contains a function named `Agent`
- **THEN** this guard does not remove or rewrite that tool.

#### Scenario: Actual removal is operator-visible

- **WHEN** the enabled guard removes `Agent` from a translated sub-agent request
- **THEN** the bridge logs a warning that advises setting `Pipeline:CcToResponses:PreventRecursiveAgentDelegation=false` and restarting the bridge if the removal is incorrect.

#### Scenario: No removal produces no guard warning

- **WHEN** the request is a root request, the guard is disabled, or the translated typed tool set contains no `Agent`
- **THEN** the bridge does not log the recursive-delegation removal warning.

### Requirement: Recursive delegation guard is configurable

The bridge SHALL expose `Pipeline:CcToResponses:PreventRecursiveAgentDelegation` as a startup-bound boolean configuration option. The option SHALL default to `true`. Setting it to `false` SHALL restore the prior CC-to-Responses behavior for sub-agent requests.

#### Scenario: Default configuration prevents recursive delegation

- **WHEN** the option is omitted from operator configuration and a Claude Code sub-agent request is translated to Responses
- **THEN** the bridge omits `Agent` from the upstream tools.

#### Scenario: Operator disables the guard

- **WHEN** the option is set to `false` and a Claude Code sub-agent request is translated to Responses
- **THEN** the bridge retains `Agent` in the upstream tools.

### Requirement: Tool choice remains valid after filtering

If filtering removes `Agent`, the bridge SHALL NOT emit a forced Responses tool choice naming the absent tool. Other tool choices SHALL retain their existing translation.

#### Scenario: Removed forced Agent choice downgrades safely

- **WHEN** an eligible sub-agent request forces the `Agent` tool and the guard removes that tool
- **THEN** the upstream Responses request emits `tool_choice` as `auto`.

#### Scenario: Forced surviving tool remains forced

- **WHEN** an eligible sub-agent request forces a tool other than `Agent` and that tool survives all filters
- **THEN** the upstream Responses request retains the forced function choice.
