## Context

The existing Astra API probe understands a solid-red image delivered as a function output. The real Codex acceptance case exercises computation and shell tools only. The app-server harness already captures client notifications, isolated SQLite logs, and bridge traces, but `turn/start.input` currently contains only text.

## Goals / Non-Goals

**Goals:** Verify attached images and real `view_image` output through `gpt-5.6-sol -> gpt-6-astra`, including a later tool round-trip that consumes the visual answer. Keep replayable visual evidence and explicit client-owned PASS criteria.

**Non-Goals:** Model-catalog changes, an image-quality benchmark, or new production dependencies.

## Decisions

- Extend the invocation with optional local image paths for the first turn. The official [Codex app-server documentation](https://developers.openai.com/codex/app-server#turns) defines `{ "type": "localImage", "path": "..." }`; the real client converts these into its own Responses payload. Later turns remain text-only unless explicitly extended in future work.
- Add two `Kind=ClientBehavior` cases using the existing stock-route subprocess scenario and reviewed Codex build. One attaches a PNG through `turn/start`; the other supplies only its path and requires Codex to execute `view_image`. Both require native `apply_patch` followed by a separate shell read and a final report of the observed content. This follows Codex's own file-editing instructions; the initial shell-write prompt made the client wrap `apply_patch` in PowerShell and recover from avoidable quoting errors.
- Use a small dependency-free raster challenge: four shuffled color tiles plus a random six-digit code rendered with bitmap glyphs. Prompts and filenames contain no expected answer. The expected answer is held by the harness and persisted only after the client exits, outside its working directory.
- Save the original PNG, its digest, the observed output file, and expected text in a separate visual-evidence record referenced by the run manifest. Retain this evidence after scratch cleanup.
- Keep xUnit assertions limited to harness health. The verification skill checks the actual client final answer and tool completion, image-bearing inbound/upstream requests, Astra routing and vision header, and a nonempty client SQLite log with no dispatch fatal. An actuator passing is not a semantic PASS.
- The first real `view_image` run preserved its `custom_tool_call_output.output` image but logged `vision=False` and omitted the vision header. Native function outputs also bypass the translated-image detector. Detect native `input_image` content at these output-writing boundaries without rewriting the opaque content, and cover both paired and standalone output forms. Literal JSON in text and unrelated nested metadata must not activate the flag. Bank the sanitized real custom-output capture as a shared regression fixture and replay it through the endpoint.

## Risks / Trade-offs

- OCR/model variability -> Use large high-contrast digits and unambiguous colors; record all evidence and report a failed reading honestly.
- An alternate image-reading path could hide a missing target path -> Require an image in the first request for the attachment case and an actual `view_image` call with image-bearing continuation for the tool case.
- A client can exit cleanly after a failed tool -> Require matching tool outputs, final answer, and client-owned log review.
- Evidence loss on scratch cleanup -> Persist the image and answer files under the existing ignored behavior-evidence root before disposing the scratch directory.
- Live authentication can expire -> Use the existing explicitly selected credential-staging contract; no rotating installed credentials are copied.
