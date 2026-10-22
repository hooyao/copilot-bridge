## ADDED Requirements

### Requirement: Plaintext collaboration is explicit and narrowly recognized

The bridge SHALL expose `Codex:PlaintextCollaboration:Enabled`, default false. When enabled for native Codex traffic routed to Copilot Responses, it SHALL activate only for a `collaboration` namespace containing a recognized message action (`spawn_agent`, `send_message`, `followup_task`) whose message schema is an encrypted string. Disabled, unrelated, or other-client requests MUST retain their existing behavior.

#### Scenario: Disabled option preserves original requests
- **WHEN** the option is false and a native request advertises encrypted collaboration tools
- **THEN** no plaintext projection or dispatch metadata is introduced.

#### Scenario: Similar application tools remain unchanged
- **WHEN** a namespace named `collaboration` has no recognized encrypted string message action
- **THEN** the adapter remains inactive and preserves the request.

### Requirement: Producer-side projection preserves complete task semantics

Active adaptation SHALL rename the recognized collaboration namespace to a reserved ordinary upstream namespace and remove only the recognized message encryption annotations. It SHALL consistently project top-level and historical tool inventories, replayed function calls and named outputs (including standalone outputs without a call id), and explicit/allowed tool choices while preserving every unrelated value, argument string, call id, item position, and existing ciphertext. Active requests MUST be stateless, force `store:false`, and reject reserved-name collisions, conflicting identities, or stateful continuation before upstream sending.

#### Scenario: Incremental inventory and replay retain their meaning
- **WHEN** a native request includes encrypted message tools in `additional_tools` or `tool_search_output`, saved collaboration calls, and an explicit tool choice
- **THEN** all relevant identities target the same ordinary upstream namespace
- **AND** unrelated schemas, calls, reasoning, and complete task text remain unchanged.

#### Scenario: Stateful request is rejected
- **WHEN** adaptation would activate on a request carrying non-null `previous_response_id` or `conversation`
- **THEN** the bridge returns a bounded HTTP 400 before making an upstream request.

#### Scenario: Qualified reserved function names are rejected
- **WHEN** an active request declares an ordinary function whose name uses either the dotted or double-underscore qualified reserved namespace in any supported inventory
- **THEN** the bridge rejects it before upstream sending rather than risk restoring that unrelated function as a native collaboration action.

#### Scenario: Named function outputs retain consistent replay identity
- **WHEN** active replay contains paired or standalone named function outputs
- **THEN** their names and namespaces use the same projected identities as their declarations and calls
- **AND** output values, unrelated metadata, and the presence or absence of a call id remain unchanged.

#### Scenario: Unrelated flat names retain their declared identity
- **WHEN** an active inventory declares an ordinary function named `collaboration.lookup` or `collaboration__lookup` without a matching collaboration child
- **THEN** its explicit/allowed choices and replayed calls/outputs retain that flat name without acquiring a namespace
- **AND** a flat name that aliases a declared collaboration child is rejected as ambiguous before upstream sending.

#### Scenario: Existing ciphertext is never discarded
- **WHEN** historical `agent_message` or reasoning items contain ciphertext
- **THEN** those fields retain their original values and ordering
- **AND** the adapter does not claim to have recovered the hidden task.

### Requirement: Returned calls select Codex plaintext dispatch

For active adaptation, streaming and buffered Responses copies of projected message-action calls SHALL restore the original collaboration identity and contain `encrypted_function_args: []`. Sparse lifecycle and argument events SHALL recover identity from the same output index without overwriting nonempty conflicts. Unrelated calls, non-message collaboration controls, ids, arguments, and future fields SHALL retain their values. A conflicting encrypted marker or identity MUST fail explicitly rather than create a false plaintext call.

#### Scenario: Sparse lifecycle retains the plaintext marker
- **WHEN** an added item identifies a projected `spawn_agent` and later done/terminal copies omit its namespace or name
- **THEN** each client-visible call has its canonical collaboration identity and empty encrypted-argument array
- **AND** the complete argument string remains unchanged.

#### Scenario: Mixed encrypted and plain message schemas use plaintext dispatch
- **WHEN** an encrypted recognized message action activates projection and another recognized string-message action in the projected inventories is already plain
- **THEN** streaming and buffered calls for both actions restore their collaboration identities and contain `encrypted_function_args: []`
- **AND** an inventory containing only plain message actions does not activate the workaround.

#### Scenario: Nonempty encrypted marker is rejected
- **WHEN** Copilot returns a projected message action with a nonempty `encrypted_function_args`
- **THEN** the bridge emits a bounded failure and never labels that action as plaintext.

#### Scenario: Buffered response stages preserve dispatch metadata
- **WHEN** an enabled native buffered request is routed to another model and response stages reconstruct the response
- **THEN** the client receives its original requested model, restored collaboration identity, and explicit empty dispatch marker
- **AND** a model-only rewrite preserves original native item ids and all other native response values
- **AND** the internal buffered dispatch carrier never appears at the native or Claude client boundary
- **AND** calls without explicit encryption metadata do not acquire a plaintext marker.

#### Scenario: Malformed buffered success remains a typed failure
- **WHEN** adaptation is active and an upstream HTTP 200 buffered body is truncated or not JSON
- **THEN** the native client receives a bounded failed response with `invalid_buffered_response`
- **AND** JSON parser text and the arbitrary upstream body do not cross the client boundary.

### Requirement: Real native collaboration proves task delivery and execution

Acceptance SHALL drive a current real Codex client through a bridge subprocess with the option enabled and a live Astra target. The task SHALL exercise spawn, a message from child to parent, a follow-up to the same child, and multiple real tool executions with matching outputs. The verdict SHALL read the client's own dispatch log and the run-specific trace, rather than infer success from HTTP status or a prompt-embedded canary.

#### Scenario: Astra child completes a multi-step task and follow-up
- **WHEN** the real parent starts an Astra child on a fresh task through the enabled bridge
- **THEN** the child executes the assigned tool operations and returns independently obtained results
- **AND** parent/child messaging and same-child follow-up complete with zero dispatch/router fatals
- **AND** new task messages carry readable content rather than encrypted task blocks.
