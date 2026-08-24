## MODIFIED Requirements

### Requirement: Context uplift respects total and prompt limits

The bridge SHALL treat Copilot `max_context_window_tokens` as the total context ceiling and `max_prompt_tokens` as the distinct input ceiling. A valid uplift SHALL set `context_window` and `max_context_window` no higher than the total ceiling and SHALL set an explicit `auto_compact_token_limit` no higher than 85 percent of total context, rounded down to a whole thousand tokens, and strictly below the maximum prompt with a non-zero safety reserve. Missing, non-positive, or internally inconsistent live limits MUST NOT raise the exact official Codex baseline.

#### Scenario: Current 1M-class model receives the 85-percent uplift

- **WHEN** Copilot reports a bridge-routable model with total context 1,050,000 and maximum prompt 922,000
- **THEN** Codex receives a total/max context no greater than 1,050,000
- **AND** its explicit auto-compaction threshold is 892,000 under the documented rounding policy.

#### Scenario: Prompt ceiling remains an independent guard

- **WHEN** 85 percent of total context is not strictly below the validated maximum prompt with a non-zero reserve
- **THEN** the bridge uses the lower prompt-derived safety limit rather than the total-context percentage.

#### Scenario: Total context is not mistaken for prompt capacity

- **WHEN** Copilot's maximum prompt is smaller than its maximum context
- **THEN** the bridge does not configure Codex to postpone compaction until the total-context ceiling.

#### Scenario: Inconsistent capability fails closed

- **WHEN** the live capability omits a required limit, contains a non-positive value, or reports maximum prompt greater than total context
- **THEN** the returned entry retains the exact official baseline limits and the bridge logs why no uplift was applied.
