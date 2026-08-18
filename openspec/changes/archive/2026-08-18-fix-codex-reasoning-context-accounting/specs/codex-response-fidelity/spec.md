## ADDED Requirements

### Requirement: Native Codex receives authoritative reasoning accounting

For a request received on `/codex/responses` and resolved to `BackendVendor.CopilotResponses`, the bridge SHALL add `X-Reasoning-Included: true` to every 2xx downstream HTTP response before any buffered body byte or SSE event is written. This client-edge signal declares that Copilot's reported input usage already accounts for replayed encrypted reasoning, so Codex MUST NOT need to add its fallback historical-reasoning estimate to that usage.

The bridge MUST NOT add the signal to the Copilot request, MUST NOT fabricate it in the raw `upstream-resp` audit when Copilot omitted it, and MUST NOT add it to `/cc`, a non-Responses destination, or a non-2xx upstream result. The `inbound-resp` audit SHALL record the signal exactly when the downstream Codex response carries it.

#### Scenario: Streaming Responses success carries the signal before SSE
- **WHEN** native Codex traffic resolves to Copilot Responses and Copilot returns HTTP 200 with an event stream
- **THEN** the downstream HTTP response contains `X-Reasoning-Included: true` before the first SSE event
- **AND** every authorized SSE event retains the existing native-response fidelity contract.

#### Scenario: Buffered Responses success carries the signal
- **WHEN** native Codex traffic resolves to Copilot Responses and Copilot returns a successful buffered Responses object
- **THEN** the downstream HTTP response contains `X-Reasoning-Included: true` before the body
- **AND** the body and usage retain their existing values.

#### Scenario: Raw and downstream audits tell different truthful facts
- **WHEN** Copilot returns a successful Responses result without `X-Reasoning-Included`
- **THEN** `upstream-resp` records no such header because Copilot did not send it
- **AND** `inbound-resp` records `X-Reasoning-Included: true` because the bridge supplied the Codex compatibility signal.

#### Scenario: Failure and cross-client paths remain isolated
- **WHEN** the request uses `/cc`, resolves to a non-Responses backend, or receives a non-2xx upstream HTTP result
- **THEN** the bridge does not synthesize `X-Reasoning-Included` for that response.

### Requirement: Backend reasoning-accounting fact is independently guarded

Acceptance SHALL include a live paired Copilot Responses probe in which the only material input difference is replayed reasoning state from a prior response. The request retaining replayed reasoning SHALL report a positive `input_tokens` delta over the request omitting it. The bridge-header regression test alone MUST NOT be treated as proof of this backend fact.

#### Scenario: Live Copilot usage charges replayed reasoning
- **WHEN** a live probe sends equivalent follow-up requests with and without complete reasoning items emitted by the same prior Copilot response
- **THEN** both requests complete successfully
- **AND** the with-reasoning response reports more input tokens than the without-reasoning response.

### Requirement: Real Codex proves the signal prevents false post-sampling compaction

Acceptance SHALL drive a real headless Codex client through a bridge subprocess for two turns on one thread. The first turn SHALL create historical encrypted reasoning while keeping the next pre-turn check below the configured compact limit. The second turn's tool-bearing sampling response SHALL report active usage below that limit while adding the first turn's fallback reasoning estimate would cross it. The client SHALL receive the accounting signal, execute the requested tools, and complete without issuing a false post-sampling compaction request.

The verdict SHALL use the run's bridge trace and Codex's own structured dispatch log. It SHALL require matching tool calls/results, no execution abort, and zero router/dispatch fatals. Bridge HTTP 200, a synthetic response replay without a real client, or a final-text canary alone is insufficient. A known Codex release that clears the signal before a later user-turn boundary is an external client limitation and is not evidence that the bridge omitted the signal during the completed turn.

#### Scenario: Second real-Codex turn stays below the false boundary
- **WHEN** a deterministic Responses upstream creates historical reasoning in turn one and returns a below-limit tool-bearing response in turn two
- **THEN** the bridge trace shows `X-Reasoning-Included: true` on successful client responses and no such header in raw upstream responses
- **AND** Codex completes the tool trajectory without a compact request before completion
- **AND** Codex's own log contains no aborted tool or router/dispatch fatal.
