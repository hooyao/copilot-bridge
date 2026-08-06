## MODIFIED Requirements

### Requirement: Private reasoning envelopes are strict and protocol-isolated

The bridge SHALL fold and unfold its reasoning envelope at the Claude client edge only — the same edge that mints it. The codec SHALL NOT be gated on the resolved backend vendor or the inbound route: it is one edge decoding what that edge encoded, so knowing the destination is not required and MUST NOT be consulted. Isolation from the native Codex edge SHALL be structural: that edge uses its own adapters, never mints a carrier, and MUST NOT reference the codec.

The bridge SHALL decode carriers bearing any discriminator it has ever emitted, and SHALL additionally recognise a reserved version-independent family discriminator carrying a version parsed from the payload. A carrier bearing a recognised discriminator whose version or payload this build cannot handle MUST fail closed with a bounded bridge-owned error. It MUST NOT be forwarded upstream as provider-native content, and MUST NOT be rendered as visible text.

The EMITTED discriminator SHALL remain stable across releases, and version evolution SHALL ride the payload rather than the discriminator. This is a downgrade-safety requirement, not a stylistic one: a client transcript outlives the build that wrote it, and self-update makes rollback routine. A reader that meets its own discriminator with an unreadable payload fails closed; a reader that meets an UNFAMILIAR discriminator treats the value as provider-native data and forwards it upstream — a silent corruption with a success status. Introducing a new discriminator therefore requires that read support ship first and reach users before anything emits it.

The envelope SHALL have a bounded size and strict field types. A carrier that is malformed, oversized, or lacks required fields MUST fail closed identically. A value without the family discriminator SHALL remain ordinary opaque provider data under existing behavior. Native Codex request/response fidelity SHALL remain unchanged.

#### Scenario: Native Codex lookalike stays opaque
- **WHEN** a native Codex request contains data resembling the private envelope
- **THEN** the Claude envelope decoder does not run and the native Codex contract is unchanged.

#### Scenario: Malformed private envelope fails closed
- **WHEN** a Claude request contains the family discriminator with invalid base64, JSON, version, or required fields
- **THEN** the request is rejected with a bounded non-secret error
- **AND** no encrypted/provider payload appears in the error or model-visible text.

#### Scenario: Provider-native redacted thinking is not mistaken for a carrier
- **WHEN** a Claude request contains a `redacted_thinking.data` value without the family discriminator
- **THEN** the bridge does not parse it as private envelope JSON.

#### Scenario: Unsupported newer version fails closed instead of reaching the upstream
- **WHEN** a client echoes a carrier bearing a recognised discriminator and a version this build does not support (written by a newer bridge, then replayed after a rollback)
- **THEN** the request fails with the bounded client-side error
- **AND** the carrier value does not appear anywhere in the outbound upstream request.

#### Scenario: The emitted discriminator does not drift
- **WHEN** the bridge folds a reasoning item for a Claude client
- **THEN** the carrier bears the same discriminator earlier releases emitted, so a build the user may roll back to still recognises it and fails closed on anything it cannot read, rather than forwarding it upstream.

#### Scenario: A previously emitted version stays readable
- **WHEN** a client echoes a carrier minted by an earlier shipped bridge version
- **THEN** the bridge decodes it and restores the reasoning item, so an in-flight conversation survives a bridge update.

## ADDED Requirements

### Requirement: Replayed reasoning state is bound to its originating model

The envelope SHALL record the model id of the turn that produced the reasoning item. On replay, the bridge SHALL compare that recorded origin against the model the request RESOLVED to, not the model the client named. Those routinely differ — on the Claude-to-Responses route the client names an Anthropic model while routing resolves a Responses one — so comparing against the client-named model would drop every valid carrier while still missing a later rewrite. The client edge therefore decodes the carrier and states its origin on the IR; the comparison happens after routing, where the resolved target is known. A mismatch SHALL be treated as a foreign carrier. The comparison is EXACT, not family-level: whether sibling models accept each other's encrypted reasoning state is a backend fact no live probe has established, and this project does not infer such facts from family names.

A carrier decoded from a form that predates origin recording names no producer. The bridge SHALL still govern it: such a carrier MAY be replayed only to a target of the backend kind that could have produced it (a Responses target), and MUST be dropped for any other target — otherwise a backend would receive encrypted content it never produced, together with bridge-internal bag keys. These carriers are the ones most likely to be replayed immediately after an upgrade, so exempting them would exempt exactly the population this requirement protects.

When the origin does not match the current target, the bridge SHALL drop the carrier and continue the turn without replayed reasoning state, and SHALL record the drop as an observable downgrade. It MUST NOT replay one model's encrypted reasoning state into a different model, and MUST NOT fail the request — a mid-session routing or model change is a legitimate user action, and a turn without reasoning state is recoverable while a turn carrying another model's state is not.

#### Scenario: Re-routed session drops stale reasoning state
- **WHEN** a conversation whose earlier turns were served by one Responses model is re-routed to a different model, and the client echoes a carrier minted by the first
- **THEN** the outbound request contains no restored reasoning item, and the foreign encrypted blob appears nowhere in it
- **AND** the turn completes normally, with the drop recorded as a downgrade.

#### Scenario: Reasoning state replays when the origin still matches
- **WHEN** a client echoes a carrier whose recorded origin matches the resolved target
- **THEN** the reasoning item is restored exactly as before, unchanged from the same-origin behavior
- **AND** no bridge-private origin key appears in the outbound upstream request.

#### Scenario: A client-named model differing from the resolved model is not a mismatch
- **WHEN** a client names one model, routing resolves a different one, and the echoed carrier was minted by the RESOLVED model
- **THEN** the reasoning item is replayed rather than dropped, because the comparison is against the resolved target.

#### Scenario: Origin-less legacy carrier replays only where it could have originated
- **WHEN** a carrier written before origin recording is replayed on a request resolved to a Responses target
- **THEN** the reasoning item is restored, and no bridge-internal bag key reaches the wire.

#### Scenario: Origin-less legacy carrier is dropped for a non-Responses target
- **WHEN** the same carrier is replayed on a request resolved to an Anthropic target
- **THEN** the block is dropped rather than forwarded as that backend's own encrypted content.

#### Scenario: Carrier rerouted to an Anthropic model is not sent as provider data
- **WHEN** a conversation carrying a bridge reasoning envelope is re-routed to a model other than the one that minted it, including an Anthropic-served model
- **THEN** the envelope is not forwarded to that backend as if it were provider-native encrypted content.
