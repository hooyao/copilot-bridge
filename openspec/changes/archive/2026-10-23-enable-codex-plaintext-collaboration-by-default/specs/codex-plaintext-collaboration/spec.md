## MODIFIED Requirements

### Requirement: Plaintext collaboration is explicit and narrowly recognized

The bridge SHALL expose `Codex:PlaintextCollaboration:Enabled`, default true in both runtime options and packaged configuration. An omitted section or key SHALL retain that enabled default, and an explicit false SHALL disable the workaround. When enabled for native Codex traffic routed to Copilot Responses, it SHALL activate only for a `collaboration` namespace containing a recognized message action (`spawn_agent`, `send_message`, `followup_task`) whose message schema is an encrypted string. Disabled, unrelated, or other-client requests MUST retain their existing behavior.

#### Scenario: Default configuration enables recognized collaboration
- **WHEN** a fresh installation uses the packaged configuration and a native stateless request advertises encrypted collaboration message tools
- **THEN** plaintext collaboration projection and explicit plaintext dispatch are active without a configuration edit.

#### Scenario: Older configuration omits the section or key
- **WHEN** the configuration lacks `Codex:PlaintextCollaboration` or its `Enabled` key
- **THEN** recognized native collaboration requests use the enabled runtime default.

#### Scenario: Disabled option preserves original requests
- **WHEN** the option is explicitly false and a native request advertises encrypted collaboration tools
- **THEN** no plaintext projection or dispatch metadata is introduced.

#### Scenario: Similar application tools remain unchanged
- **WHEN** a namespace named `collaboration` has no recognized encrypted string message action
- **THEN** the adapter remains inactive and preserves the request.

## ADDED Requirements

### Requirement: Default-path execution is verified at the distributed native boundary

Acceptance SHALL run a real current Codex client through a Native AOT bridge with the workaround configuration unforced and xhigh Astra reasoning. The task SHALL exercise a complete parent/child collaboration lifecycle and multiple real tool executions. The verdict SHALL inspect client-owned dispatch logs and matching run-specific call/output evidence.

#### Scenario: Native default completes an xhigh collaboration lifecycle
- **WHEN** the real client starts a fresh Astra task using the native bridge's default configuration without a test enablement override
- **THEN** spawn, child-to-parent message, same-child follow-up, and independently verified file operations complete
- **AND** recognized upstream message schemas are projected and new agent messages contain no encrypted task parts
- **AND** the nonempty client-owned dispatch log contains zero router/dispatch fatals.
