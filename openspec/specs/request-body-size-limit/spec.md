# request-body-size-limit Specification

## Purpose
Define the bridge-wide inbound HTTP request-body ceiling so image-heavy client conversations have a finite, configurable capacity that is applied consistently by Kestrel and rejected early when misconfigured.

## Requirements
### Requirement: Configurable inbound request-body limit
The bridge SHALL bind `Server.MaxRequestBodySizeBytes` as a positive byte count and SHALL apply that value as Kestrel's maximum request-body size for every HTTP endpoint before accepting connections.

#### Scenario: Configured limit is applied
- **WHEN** `Server.MaxRequestBodySizeBytes` is configured to `125829120`
- **THEN** Kestrel's maximum request-body size is `125829120` bytes

#### Scenario: Limit applies bridge-wide
- **WHEN** the bridge starts with a valid configured maximum request-body size
- **THEN** the same Kestrel limit governs the Codex, Claude Code, and auxiliary HTTP endpoints

### Requirement: Default request-body capacity
The bridge SHALL use a maximum request-body size of 104,857,600 bytes when `Server.MaxRequestBodySizeBytes` is not supplied, and the shipped configuration SHALL expose that same default.

#### Scenario: Setting is omitted
- **WHEN** configuration contains no `Server.MaxRequestBodySizeBytes` value
- **THEN** the bound server option and Kestrel maximum request-body size are both `104857600` bytes

#### Scenario: Shipped configuration is inspected
- **WHEN** the distributed `appsettings.json` is loaded
- **THEN** `Server.MaxRequestBodySizeBytes` is `104857600`

### Requirement: Invalid request-body limits fail startup
The bridge MUST reject a configured maximum request-body size below one byte before the server begins listening, and the startup error MUST identify the invalid setting and its positive-value requirement.

#### Scenario: Zero-byte limit is configured
- **WHEN** `Server.MaxRequestBodySizeBytes` is `0`
- **THEN** server configuration fails with an error naming `MaxRequestBodySizeBytes` and requiring a value greater than zero

#### Scenario: Negative limit is configured
- **WHEN** `Server.MaxRequestBodySizeBytes` is negative
- **THEN** server configuration fails with an error naming `MaxRequestBodySizeBytes` and requiring a value greater than zero
