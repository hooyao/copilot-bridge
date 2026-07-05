# observability

## ADDED Requirements

### Requirement: Trace capture is zero-overhead when disabled

When tracing is disabled (`Tracing.Enabled=false`), the bridge SHALL NOT perform
any work whose only purpose is to produce a trace artifact. Specifically, while
handling a request with tracing off, the bridge SHALL NOT allocate a raw-response
capture buffer, SHALL NOT allocate a per-event capture list, SHALL NOT serialize
or copy a request/response body solely for auditing, and SHALL NOT emit any
bridge-IO audit record. The request's observable wire behaviour (status, headers,
body bytes, SSE events) SHALL be identical to the tracing-on path.

Rationale: real Claude Code requests routinely exceed 4 MB; audit-only
serialization or body copies on the hot path are a per-request tax paid in
production, where tracing is off. Gating every capture and emission behind a
single per-request seam makes "off ⇒ no extra work" structural rather than a
property each call site must individually remember.

#### Scenario: Off-trace request emits no audit records

- **WHEN** a request is handled end-to-end with `Tracing.Enabled=false`
- **THEN** no `inbound-req`, `inbound-resp`, `upstream-req`, or `upstream-resp`
  audit record is produced, and no raw-capture buffer or per-event list is
  allocated for that request

#### Scenario: Off-trace does not re-serialize the upstream body

- **WHEN** a passthrough request (claude-* → `/v1/messages`) is handled with
  `Tracing.Enabled=false`
- **THEN** the request body is serialized exactly once — the copy POSTed upstream
  — and no second serialization is performed for the audit

#### Scenario: Toggling tracing does not change wire output

- **WHEN** the same request is handled once with tracing on and once with tracing
  off
- **THEN** the bytes and SSE events returned to the client are byte-identical
  across the two runs

### Requirement: The upstream-request audit records the exact bytes POSTed upstream

When tracing is enabled, the `upstream-req` audit artifact SHALL contain the exact
byte sequence the bridge handed to the Copilot client for that request — the
translated Responses body on a gpt-* (Codex/Responses) route, or the serialized
Anthropic body on a claude-* passthrough route — and SHALL NOT contain a body
that was re-derived independently of what was actually sent.

Rationale: the audit exists to show what the bridge really sent. Recording the
exact POSTed bytes (rather than a second serialization of the in-memory request)
makes `upstream-req` a faithful witness and removes a redundant serialization from
the request path.

#### Scenario: Passthrough upstream-req equals the POSTed Anthropic bytes

- **WHEN** a claude-* request is forwarded to Copilot's `/v1/messages` with
  tracing on
- **THEN** the `upstream-req` body equals the exact bytes the bridge POSTed to the
  Copilot client, not a separately re-serialized copy of the request

#### Scenario: Codex upstream-req equals the POSTed Responses bytes

- **WHEN** a gpt-* request is translated (T2) and forwarded to Copilot's
  `/responses` with tracing on
- **THEN** the `upstream-req` body equals the exact translated Responses bytes the
  bridge POSTed, not the untranslated Anthropic-shape request

### Requirement: Each audit artifact records the wire bytes of its boundary, never the internal IR

When tracing is enabled, each of the four artifacts SHALL record the real bytes
that crossed its boundary in the native protocol of the side that owns that
boundary, and SHALL NOT record the bridge's internal Anthropic-shaped IR:
`inbound-req` is the client's original request captured before inbound
translation; `upstream-req` is the exact body POSTed to Copilot; `upstream-resp`
is Copilot's response captured before response-side translation or any response
stage; `inbound-resp` is the exact body returned to the client. This holds for
both client edges (`/cc` Claude Code and `/codex` Codex).

Rationale: the IR is an internal hub; auditing it instead of the wire would
misrepresent what actually crossed the boundary. On the Codex path a request is
translated twice (client→IR, IR→Copilot), so `inbound-req` and `upstream-req` are
both Responses-shaped yet intentionally not byte-identical — keeping them as the
real bytes on each side is what lets an operator diff the translation.

#### Scenario: Codex inbound-req is the untranslated client request

- **WHEN** a Codex request is handled with tracing on
- **THEN** the `inbound-req` body equals the original Responses bytes the Codex
  client sent, captured before the inbound adapter builds the IR, and is not the
  IR representation

#### Scenario: Codex inbound-req and upstream-req are both wire, neither the IR

- **WHEN** a Codex request is handled with tracing on
- **THEN** `inbound-req` holds the client's original Responses bytes and
  `upstream-req` holds the T2-produced Copilot Responses bytes, and neither holds
  the internal Anthropic IR (whose required `max_tokens` field is absent from both
  Responses bodies); when the translation reshapes the body the two differ, and
  when T1∘T2 is an identity for a trivial request they may coincide — the invariant
  is that each is the wire on its side, not that they always differ

#### Scenario: An early-return request still audits the boundary it reached

- **WHEN** a request fails before reaching the upstream backend (malformed body,
  unsupported server tool, or an unknown model) with tracing on
- **THEN** `inbound-req` records the original inbound bytes and `inbound-resp`
  records the error response actually returned, and no `upstream-req`/`upstream-resp`
  is fabricated for a POST that never happened

#### Scenario: A truncated upstream response is not audited as a clean success

- **WHEN** the upstream stream faults mid-response with tracing on
- **THEN** `upstream-resp` records the partial bytes received before the fault and
  carries the fault in its error field, rather than recording an empty or
  clean-200 artifact
