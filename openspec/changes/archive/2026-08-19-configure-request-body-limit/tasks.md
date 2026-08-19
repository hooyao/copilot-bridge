## 1. Contract Tests

- [x] 1.1 Add option-binding tests for the 100 MiB default and a configured byte override.
- [x] 1.2 Add Kestrel configuration tests that require the effective limit to match the bound value and reject zero or negative values.
- [x] 1.3 Mutation-check the new tests against the unchanged product code and confirm they fail for the missing behavior.

## 2. Server Configuration

- [x] 2.1 Add `MaxRequestBodySizeBytes` to `BridgeServerOptions` with the 104,857,600-byte default.
- [x] 2.2 Validate and apply the bound value through `KestrelOptionsConfigurator`.
- [x] 2.3 Add the discoverable default to the shipped `appsettings.json` and cover template/default consistency.

## 3. Documentation

- [x] 3.1 Document the setting, default, scope, and memory/context-growth trade-off in the user configuration reference.
- [x] 3.2 Update the pipeline architecture documentation for the configured inbound-body ceiling.

## 4. Verification

- [x] 4.1 Run the focused configuration tests and the full unit-test suite.
- [x] 4.2 Verify a request above Kestrel's former 30,000,000-byte default reaches endpoint processing under the default configuration.
- [x] 4.3 Run a complex real Codex client task through a bridge subprocess and inspect the client's own dispatch log for a successful tool round-trip and zero router fatals.
