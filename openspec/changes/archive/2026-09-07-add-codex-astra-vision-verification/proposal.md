## Why

Astra's live API probes demonstrate basic image understanding, but the shipped real Codex acceptance task never sends an image. We need repeatable client evidence for both attached images and images returned by Codex's own `view_image` tool through the stock GPT-5.6-to-Astra route.

Both paths recognized images before the header change. The real-client tests also exposed a missing Copilot vision header on native tool-image output; the runtime correction aligns the existing protocol flag with the image content. Current Astra was not observed to fail without that header.

## What Changes

- Add native local-image input support to the real Codex app-server test driver.
- Add two integration behavior cases: an attached image and a real `view_image` tool result, each followed by native `apply_patch` and a separate file-read tool call.
- Generate a fresh visual challenge for each run, with randomized color positions and a numeric code rendered only in PNG pixels. Save the image and expected answer outside the client's working directory for verdict review.
- Record the image evidence alongside the run manifest and document the required client, trace, and SQLite checks in both skill mirrors.
- Fix the missing vision flag found by the real `view_image` run: native Responses tool-output images must activate the existing Copilot vision header while retaining their original content.

## Capabilities

### New Capabilities

- `codex-astra-vision-verification`: Real Codex verification of attached-image and image-tool-result understanding through the Astra compatibility route.

### Modified Capabilities

None.

## Impact

Changes cover the Playground harness, native tool-output image detection in the Responses request builder, regression tests, verification documentation, and OpenSpec artifacts. The tests use the existing Copilot authentication staging, isolated bridge subprocess, and real Codex app-server. No new runtime dependency or production routing change is planned.
