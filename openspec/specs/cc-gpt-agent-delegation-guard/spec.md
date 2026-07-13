# cc-gpt-agent-delegation-guard Specification

## Purpose

Prevent recursively expanding Claude Code agent trees when `/cc` traffic is translated to a GPT Responses backend, while preserving root delegation, native client behavior, and an explicit operator escape hatch.

## Requirements
### Requirement: Recursive Claude Code delegation is filtered at the Responses translation boundary

When enabled, the bridge SHALL omit the exact `Agent` tool from the upstream Responses request if and only if the downstream request came from a Claude Code sub-agent and routing selected a Responses backend. The bridge SHALL classify a request as a sub-agent from a non-empty inbound `x-claude-code-agent-id` header without requiring a parent-agent header. The bridge SHALL NOT mutate the shared IR tool collection.

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
