## MODIFIED Requirements

### Requirement: Every non-cancelled Codex stream has exactly one terminal

After any Responses bytes have been accepted for a native Codex request, the bridge SHALL emit exactly one of `response.completed`, `response.incomplete`, or `response.failed`. A clean completed or incomplete upstream terminal SHALL be forwarded value-faithfully. An upstream failed terminal SHALL retain its bounded machine code and a bounded bridge-owned message; the exact confirmed encrypted-function-output diagnostic SHALL select a specific reviewed message. Arbitrary upstream-generated error text MUST NOT cross the client or log boundary. An empty failed terminal followed by a structured error SHALL be enriched through at most one bounded diagnostic read. Premature EOF, malformed terminal state, upstream exception, detector abort, or stream-idle timeout SHALL produce exactly one Codex-native `response.failed` terminal after any safe prefix. The bridge MUST NOT fabricate a successful tool-input `.done`, output-item completion, or successful response terminal from an incomplete upstream stream.

If the downstream request cancellation token is already cancelled, the bridge SHALL stop writing and record client cancellation; it need not and generally cannot write a terminal after the client has disconnected.

#### Scenario: Upstream failed detail is bounded
- **WHEN** Copilot emits `response.failed` with a machine code and generated error message
- **THEN** Codex receives exactly one `response.failed` carrying a bounded bridge-owned message
- **AND** the generated upstream message does not cross the client edge.

#### Scenario: Confirmed decryption error follows an empty failed terminal
- **WHEN** Copilot emits an empty `response.failed` followed within the diagnostic bound by `error` with `invalid_request_body` and the exact confirmed encrypted-function-output message
- **THEN** Codex receives one failed terminal retaining `invalid_request_body` and the reviewed decryption diagnostic
- **AND** the summary records the same bounded failure rather than generic success.

#### Scenario: Failed stream does not provide more detail
- **WHEN** an empty failed terminal is followed by EOF, an unreadable tail, a different event, or silence beyond the diagnostic bound
- **THEN** the bridge emits one bounded failed terminal promptly without waiting for the normal stream-idle budget.

#### Scenario: Completed terminal never waits for diagnostics
- **WHEN** Copilot emits a completed or incomplete terminal and leaves the connection open
- **THEN** that terminal completes normally without any diagnostic lookahead.

#### Scenario: Premature EOF after custom-tool deltas
- **WHEN** Copilot emits custom-tool input deltas and closes without the tool-input done event, output-item done event, or a response terminal
- **THEN** the bridge does not synthesize successful tool completion
- **AND** Codex receives exactly one `response.failed` terminal unless Codex cancelled first.

#### Scenario: Upstream exception after visible output
- **WHEN** an upstream read throws after the bridge has relayed a safe event prefix and the downstream client remains connected
- **THEN** the safe prefix is retained and followed by exactly one `response.failed` terminal.

#### Scenario: Client cancels first
- **WHEN** Codex cancels the request before an upstream terminal is available
- **THEN** the bridge records the request as client-cancelled and does not claim that a complete response was delivered.
