# Verification

## Final real-client verdict

Both cases passed through real Codex app-server **0.153.4**, an isolated bridge
subprocess, and live Copilot. The client requested `gpt-5.6-sol` at `none`; every
upstream inference used **`gpt-6-astra` / `low`**. The final runs used the fixed
request builder and native `apply_patch` followed by a separate shell read.

| Case | Image-derived result | Execution and transport | Client SQLite |
| --- | --- | --- | --- |
| Native attachment | `354455`; top-left yellow, top-right green, bottom-left red, bottom-right blue | Native `localImage` input; image in the first of 3 inference requests; 2 matching custom exec call/output pairs for file creation and read-back | 216 rows; 0 router/dispatch fatals, 0 ERROR, 0 retry |
| `view_image` tool | `179193`; top-left yellow, top-right blue, bottom-left red, bottom-right green | Initial request has no image; actual client `imageView` event; image on the next 3 requests; 3 matching custom exec call/output pairs for view, file creation, and read-back | 256 rows; 0 router/dispatch fatals, 0 ERROR, 0 retry |

For both runs, the saved image was visually inspected. The client-written file,
shell read output, and completed final message matched all five expected fields.
Every image-bearing upstream request preserved the source PNG bytes and carried
`copilot-vision-request=true`. All upstream responses were 200; both client turns
completed with no abort or failed command.

Exact local manifests under the ignored `tests/behavior-runs/manifests/`:

- `codex-astra-attached-image-20260907-074919-798.json`
- `codex-astra-view-image-tool-20260907-075004-680.json`

Each manifest names its persistent visual evidence and exact isolated dispatch
database/window. `dispatch-log.txt` beside each visual evidence record was produced
with the skill's read-only SQLite reader and inspected for the verdict.

## Header inconsistency discovered by the new test

The initial `view_image` run correctly understood its image, but its native
`custom_tool_call_output.output` array bypassed the builder's image detector.
The trace and bridge log showed preserved image content with `vision=False`
and no vision header. Paired and standalone native function outputs had the same
gap. The fix observes native output content parts without rewriting their values.
Both image-delivery paths already produced correct visual answers before this
change. These observations establish a missing protocol flag, not a demonstrated
Astra recognition failure caused by its absence.

- Before the fix, four unit cases failed specifically on the missing vision flag:
  paired function output, standalone named function output, custom output, and the
  sanitized real Codex capture. Two text/metadata negative cases passed.
- The captured-byte endpoint replay also failed before the fix: the expected header
  was `true`, while the actual trace value was absent.
- After the fix, the full unit suite passed **1,776/1,776**, and the live endpoint
  replay preserved the captured native image output and reported the correct header.
- Successful replay trace: `tests/behavior-runs/serve-356c7246bdce431abdd4fad54a405ccd/`.
- The shared fixture `tests/Fixtures/Codex/native-vision-tool-output.json` retains
  the real tool call/output and generated image with the local path de-identified.

## Attachment negative control

Temporarily omitting the app-server's native image input produced
`codex-astra-attached-image-20260907-073510-631.json`. The real client searched
its scratch directory, executed `view_image`, and still returned the correct
reading. However, `localImageInputs=0` and the first request contained no image,
so this run was rejected as an attachment PASS. This demonstrates why an exact
answer and green actuator alone are insufficient. The omission was restored
before the final runs.

## Other checks

- `dotnet build CopilotBridge.slnx --no-restore`: zero warnings and errors.
- `dotnet test tests/CopilotBridge.UnitTests --no-build --no-restore`: 1,776 passed,
  including the mirrored-skill compatibility checks.
- Focused `AgentRepositoryCompatibilityTests`: 4 passed.
- Final Playground invocation: both real-client vision actuators and the live
  captured-byte replay passed (3 tests).
- `openspec validate add-codex-astra-vision-verification --strict`: valid.
- After archiving, `openspec validate --all --strict`: 34 passed, 0 failed.
- `git diff --check`: clean.

The runs above used local builds. The previously published `0.5.17-beta` build
does not contain the native tool-output vision-header correction.
