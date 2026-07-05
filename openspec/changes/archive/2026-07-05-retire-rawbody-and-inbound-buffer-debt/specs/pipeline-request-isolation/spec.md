# pipeline-request-isolation

## ADDED Requirements

### Requirement: The pipeline request context exposes IR state, not wire bytes

The per-request context the pipeline operates on SHALL expose only the
intermediate-representation (IR) form of the request that stages consume — the
typed body, the routing/header state, and request metadata — and SHALL NOT carry
the original inbound wire bytes as pipeline-visible state. Raw inbound bytes are a
boundary concern: they are consumed synchronously at the endpoint (deserialization
into the IR and the audit capture) and are not retained for the pipeline to read.

Rationale: the bridge translates each client's wire format into a shared IR at the
boundary (T1) and stages operate purely on that IR; exposing the wire bytes inside
the pipeline invites a stage to bypass the IR, and the inbound length in particular
is stale as soon as the first body-mutating stage runs, so it is not meaningful
pipeline state. Keeping raw bytes at the boundary also lets the pooled read buffer
be released within the endpoint's synchronous section rather than pinned for the
whole request.

#### Scenario: A stage cannot read the original inbound bytes from the context

- **WHEN** a request stage or detector runs during request processing
- **THEN** the request context offers no original-inbound-bytes member for it to
  read; only the typed IR body, headers, and request metadata are available

#### Scenario: Inbound size is observed at the boundary, not carried through the pipeline

- **WHEN** the inbound request size is logged for diagnostics
- **THEN** it is recorded at the endpoint boundary where the body is read, not by a
  pipeline component reading it from the request context

#### Scenario: Audit still records the exact inbound bytes

- **WHEN** tracing is enabled and a request is handled
- **THEN** the `inbound-req` audit artifact still contains the exact original client
  bytes, captured at the endpoint before the read buffer is released — unchanged by
  removing raw bytes from the pipeline context
