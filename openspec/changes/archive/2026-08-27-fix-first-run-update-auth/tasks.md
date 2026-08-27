## 1. Contract Tests

- [x] 1.1 Add update-activation tests proving authentication is untouched before Ready, resumes after a successful Ready send, and cannot terminate the serving bridge on failure.
- [x] 1.2 Add credential-store tests proving empty lookup and first unified save do not create the historical v2 lock while legacy migration still uses and retains it.
- [x] 1.3 Mutation-check the new tests against the pre-fix behavior.

## 2. Implementation

- [x] 2.1 Sequence deferred authentication after the updater readiness send and make post-readiness failures actionable but non-fatal.
- [x] 2.2 Make historical-lock acquisition conditional on observable legacy state while preserving unified-first lock ordering and stable existing lock files.

## 3. Documentation and Verification

- [x] 3.1 Update auto-update and token-storage documentation to describe post-Ready authentication and fresh-install lock behavior.
- [x] 3.2 Run focused update/auth tests and the solution non-integration suite.
- [x] 3.3 Run the subprocess startup/update coverage appropriate to the changed lifecycle.
- [x] 3.4 Run a real headless client through the bridge and verify the client-owned execution evidence.
