## Context

0.5.26-beta uses `false` in both the option class and packaged configuration. The supplied desktop traces show an eligible stateless native Astra request with encrypted collaboration schemas reaching Copilot unchanged. The user confirms the flag was off and requires default-on behavior.

## Goals / Non-Goals

**Goals:** Enable the existing workaround for new/default and omitted-key configurations, retain explicit disablement, and verify real default-path execution through Native AOT at xhigh effort.

**Non-Goals:** Decrypt or discard existing history, alter model profiles, change other client protocols, or rewrite explicit user settings during updates.

## Decisions

- Set the option initializer and packaged `Enabled` value to true. Changing only the JSON would leave older configurations without the section disabled; changing only the initializer would leave the packaged false override active.
- Keep explicit false authoritative. For an existing 0.5.26-beta configuration containing false, operators must remove that override or set it true, restart, and start a fresh task. Update configuration migration keeps its existing user-setting preservation contract.
- Make the harness's collaboration override nullable: omission uses the packaged configuration, while true/false are explicit test overrides. Allow an explicit binary directory so the same real-client actuator can launch a published Native AOT executable without changing installed or development binaries.
- Add a default-path xhigh lifecycle case that exercises spawn, child-to-parent messaging, same-child follow-up, and matching actual tool outputs. Judge its client-owned SQLite log and run-specific traces, including projected upstream schemas and absence of new encrypted agent messages.

## Risks / Trade-offs

- [Existing encrypted tasks remain unrecoverable] → Preserve history and document fresh-task/restart requirements.
- [A test can accidentally force-enable the option] → The default-path case must pass no override, and unit contracts separately assert runtime and packaged defaults.
- [Testing a JIT executable misses distribution differences] → Use an explicit Native AOT binary directory and record binary version/hash alongside the execution evidence.
