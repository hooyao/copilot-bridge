## Why

Long-running multimodal Codex sessions can legitimately exceed Kestrel's 30,000,000-byte default request-body limit because the client replays image-bearing tool results. The bridge currently inherits that limit implicitly and rejects such requests before its request pipeline can run.

## What Changes

- Add a `Server.MaxRequestBodySizeBytes` setting that controls Kestrel's request-body limit.
- Ship a 100 MiB default so image-heavy client conversations have practical headroom.
- Validate invalid configured limits during startup instead of accepting an unusable server configuration.
- Document the setting and verify both default and overridden values with contract-focused tests.

## Capabilities

### New Capabilities

- `request-body-size-limit`: Configurable bridge-wide inbound HTTP request-body capacity, its default, and startup validation.

### Modified Capabilities

None.

## Impact

The change affects server option binding, Kestrel startup configuration, the shipped `appsettings.json`, configuration documentation, and unit/startup contract tests. It does not change request translation or upstream Copilot payload semantics.
