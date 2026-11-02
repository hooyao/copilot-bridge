## MODIFIED Requirements

### Requirement: Context uplift respects total and prompt limits

The bridge SHALL treat Copilot `max_context_window_tokens` as the total context ceiling and `max_prompt_tokens` as the distinct input ceiling. A valid uplift SHALL set `context_window` and `max_context_window` no higher than the total ceiling and SHALL set an explicit `auto_compact_token_limit` no higher than 85 percent of total context, rounded down to a whole thousand tokens, and strictly below the maximum prompt with a non-zero safety reserve. Missing, non-positive, or internally inconsistent live limits MUST NOT raise the exact official Codex baseline. When maximum prompt minus maximum output minus 16,000 permits at least 1,000 tokens, the compaction threshold SHALL additionally be capped by that growth budget. Otherwise the existing percentage-based threshold SHALL remain in effect without changing model availability.

#### Scenario: Current 1M-class model reserves one response of history growth

- **WHEN** Copilot reports a bridge-routable model with total context 1,050,000, maximum prompt 922,000, and maximum output 128,000
- **THEN** Codex receives total/max context 1,050,000 and an explicit auto-compaction threshold of 778,000.

#### Scenario: Prompt ceiling remains an independent guard

- **WHEN** 85 percent of total context is not strictly below the validated maximum prompt with a non-zero reserve
- **THEN** the bridge uses the lower prompt-derived safety limit rather than the total-context percentage, also applying the output-growth ceiling whenever it permits a positive whole-thousand threshold.

#### Scenario: Total context is not mistaken for prompt capacity

- **WHEN** Copilot's maximum prompt is smaller than its maximum context
- **THEN** the bridge does not configure Codex to postpone compaction until the total-context ceiling.

#### Scenario: Inconsistent capability fails closed

- **WHEN** the live capability omits a required limit, contains a non-positive value, or reports maximum prompt greater than total context
- **THEN** the returned entry retains the exact official baseline limits and the bridge logs why no uplift was applied.

#### Scenario: Output-heavy small window keeps its existing positive threshold

- **WHEN** coherent capacities report total context 256,000, maximum prompt 128,000, and maximum output 128,000
- **THEN** the bridge keeps the existing 124,000 compaction threshold and the model's availability, without claiming to reserve a maximum-size output.

#### Scenario: Output reserve uses the resolved alias target

- **WHEN** an invariant alias targets coherent capacities of 1,000,000 total, 872,000 maximum prompt, and 128,000 maximum output
- **THEN** the alias keeps its source instructions and receives total/max context 1,000,000 with a compaction threshold of 728,000.

#### Scenario: Real Codex compacts using the projected resource

- **WHEN** a real headless Codex client fetches the projected 778,000 threshold without local context or compaction overrides and then observes authoritative context usage above 778,000 but below 892,000
- **THEN** the client automatically compacts, continues a multi-operation tool task, and its own dispatch evidence contains no router fatal.
