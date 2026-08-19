## Context

Kestrel defaults `Limits.MaxRequestBodySize` to 30,000,000 bytes. The bridge does not currently override that value, so a Codex request containing a long history of base64 image tool results is rejected while `InboundBody` copies the HTTP request stream, before audit capture, deserialization, or pipeline processing. The generic endpoint exception handler then surfaces the local rejection as a 502.

The bridge already binds listener settings through `BridgeServerOptions` and applies them in `KestrelOptionsConfigurator`, making that the existing ownership boundary for this limit. Request bodies are read into bounded recyclable buffers; raising the acceptance ceiling does not preallocate the ceiling size, although an accepted large request still consumes memory proportional to its actual size.

## Goals / Non-Goals

**Goals:**

- Make the bridge-wide inbound request-body limit configurable under the existing `Server` section.
- Default the limit to 100 MiB (104,857,600 bytes).
- Apply the configured value directly to Kestrel before the listener starts.
- Reject non-positive configured values at startup with an actionable error.
- Cover option binding and Kestrel application from the external configuration contract.

**Non-Goals:**

- Compacting or removing image history from Codex requests.
- Adding endpoint-specific limits or an unlimited mode.
- Changing the upstream Copilot request-size limit.
- Changing the endpoint's generic exception-to-status mapping as part of this change.

## Decisions

### Put the value in `Server.MaxRequestBodySizeBytes`

The setting is a byte count so its unit is unambiguous and it lives beside `Server.Port`, whose options object already feeds Kestrel. A `long` matches Kestrel's limit type and avoids introducing a second configuration surface. Alternatives such as a megabyte-valued setting or a top-level Kestrel section would either introduce unit rounding or bypass the bridge's established options contract.

### Use an explicit 100 MiB default in both code and shipped configuration

The options initializer preserves the default when a custom configuration source omits the key; the shipped `appsettings.json` makes the operational default discoverable and allows the self-update config merger to carry it into existing installations. Tests pin both representations to prevent drift.

### Validate and apply in `KestrelOptionsConfigurator`

The configurator already fail-fast validates the port and mutates `KestrelServerOptions`. It will reject values below one byte with `BridgeStartupException`, then assign `options.Limits.MaxRequestBodySize`. This keeps validation adjacent to the consuming boundary and guarantees the effective Kestrel value is the bound bridge option.

## Risks / Trade-offs

- **Accepted requests can consume more memory than before** → retain a finite default, preserve the existing pooled reader, and allow operators to lower the limit.
- **100 MiB only postpones unbounded client-history growth** → document that the setting is capacity rather than compaction; context management remains a separate client/protocol concern.
- **Existing installations retain merged configuration across updates** → the code initializer supplies 100 MiB when the new key is absent, while the normal config merger introduces the documented key from the new template.
