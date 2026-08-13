## ADDED Requirements

### Requirement: Configured timeout durations are exact and representable

The bridge SHALL enforce every positive `FirstByteTimeoutSeconds`,
`StreamIdleTimeoutSeconds`, and `KeepAliveIntervalSeconds` value as the exact
configured duration on the phase that option governs. It SHALL NOT add a margin,
clamp the value, substitute a fallback, or use it to rewrite any client setting.
Zero or negative SHALL retain the existing documented disabled meaning.

At startup the bridge SHALL validate that each positive duration can be represented
by the actual .NET timer API used on its path (`CancelAfter` or `Task.Delay`). An
unrepresentable value SHALL fail startup with the option name, raw value, allowed
range, and correction guidance, rather than throwing only after a request arrives.

#### Scenario: Positive first-byte value is exact

- **WHEN** `FirstByteTimeoutSeconds` is 240
- **THEN** each bridge upstream send receives exactly a 240-second response-header budget
- **AND** no client configuration is changed from that value.

#### Scenario: Positive stream-idle value is exact

- **WHEN** `StreamIdleTimeoutSeconds` is 240
- **THEN** each complete parsed upstream SSE event gap is bounded at exactly 240 seconds
- **AND** downstream pings do not alter that duration.

#### Scenario: Disabled value remains disabled

- **WHEN** a timeout option is zero or negative
- **THEN** that bridge phase has no bound
- **AND** no finite substitute value is generated anywhere.

#### Scenario: Unrepresentable timer value fails before serving

- **WHEN** a positive configured duration exceeds the supported timer range
- **THEN** bridge startup fails before binding the port
- **AND** the diagnostic names the exact option/value and supported range.

#### Scenario: Keepalive remains an interval, not a budget

- **WHEN** `KeepAliveIntervalSeconds` is positive and representable
- **THEN** the bridge uses that exact interval only for eligible downstream ping scheduling
- **AND** never includes it in a client timeout derivation.
