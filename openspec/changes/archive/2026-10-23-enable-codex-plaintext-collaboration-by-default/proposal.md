## Why

The released 0.5.26-beta shipped plaintext collaboration disabled despite the requested default-on behavior. A real desktop Astra child request therefore retained encrypted collaboration schemas and agent messages and failed with Copilot's decryption error after upgrading.

## What Changes

- Enable plaintext collaboration in both the runtime option default and packaged configuration, including configurations that omit the option.
- Preserve an explicit `false` as an operator override and keep the existing stateless/native-route eligibility rules.
- Update configuration contracts, operational documentation, and the real-client harness so a published/native executable can be verified without replacing the user's installed bridge.
- Verify the default-on path with a real current Codex/Astra parent and child at `xhigh`, using a Native AOT executable and client-owned execution evidence.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `codex-plaintext-collaboration`: Make the recognized plaintext collaboration workaround enabled by default, including omitted configuration, while retaining explicit disablement.

## Impact

Changes the Codex option initializer, packaged appsettings, documentation, contract tests, and integration harness. No dependencies, model profiles, encryption algorithms, or credential flows change. Existing encrypted history is still preserved and cannot be repaired by changing this default.
