## 1. Versioned Store and Migration Contracts

- [x] 1.1 Add failing contracts for version-1 full legacy and version-2 direct
  source-generated serialization, round-trip encryption, and unknown-version fail-closed.
- [x] 1.2 Add failing migration contracts for v2 precedence, raw fallback, complete
  field preservation, atomic readback verification, and deletion of both old files.
- [x] 1.3 Mutation-check failure ordering: inject write/readback failure and prove old
  files survive; inject concurrent migration and prove one committed result.
- [x] 1.4 Prove interrupted post-verification cleanup is retried on a later load until
  no legacy credential file remains.

## 2. Independent CredentialService

- [x] 2.1 Implement the single-file store and immutable versioned credential lease.
- [x] 2.2 Implement transactional migration from both exact old files under one stable
  lock, deleting them only after verified commit.
- [x] 2.3 Move Device Flow, refresh, rejection identity, status, and logout ownership into
  CredentialService; callers receive no paths/protectors/records.

## 3. AuthService Integration

- [x] 3.1 Refactor AuthService to consume CredentialService only and remove direct store,
  migration, refresh, and login knowledge.
- [x] 3.2 Preserve version-1 exchange/refresh behavior until typed terminal rejection.
- [x] 3.3 Make explicit login write version 2 and retain direct CAPI lease behavior.
- [x] 3.4 Preserve first-use login single-flight so concurrent callers share one Device
  Flow and one committed credential.
- [x] 3.5 Classify a late direct-CAPI 401 by persisted credential identity so a newer
  lease generation carrying the same rejected bearer cannot be replayed.
- [x] 3.6 Keep stale credential rejections from overwriting the terminal identity of the
  current persisted credential.

## 4. CLI, Documentation, and Cleanup

- [x] 4.1 Refactor auth status/login/logout and debug composition through the service.
- [x] 4.2 Remove runtime creation/reading of `github_credentials.v2.dat` and
  `github_token.dat`; retain only migration readers and explicit logout cleanup.
- [x] 4.3 Update README, token-storage, pipeline/API research, design, and size history.
- [x] 4.4 Make logout delete current and legacy credential files without first parsing
  potentially unreadable credential bytes.

## 5. Verification

- [x] 5.1 Run focused migration/service/auth tests and mutation checks, then all unit and
  non-integration tests.
- [x] 5.2 Run a real-client version-1 migration/exchange case and read client-owned evidence.
- [x] 5.3 Run a real-client version-2 direct case and read client-owned evidence.
- [x] 5.4 Publish bridge/updater Native AOT, inspect warnings/size, and audit for secrets,
  stale credential artifacts, and unrelated changes.
