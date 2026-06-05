# Bug report: opus-4.8 (and sonnet-4.6) intermittently omit a **required** tool-call input field when streaming via the native Anthropic `/v1/messages` endpoint

> **Filing note.** GitHub Copilot has no public issue tracker; feedback goes to
> GitHub Community Discussions (`github.com/orgs/community/discussions`,
> *Copilot Conversations* category). That repo **only accepts discussions
> created through the web UI using its templates** — a discussion created via
> the GraphQL/`gh` API is auto-closed by a bot ("We currently only accept
> discussions created through the GitHub UI using our provided discussion
> templates"). So this must be filed manually in the browser:
> `https://github.com/orgs/community/discussions/new?category=copilot-conversations`
> — Discussion Type = **Bug**, Topic Area = **Copilot in GitHub**, title from the
> H1 below, body = everything under it.

## Summary

When streaming a tool call from `claude-opus-4.8` through Copilot's native
Anthropic endpoint (`POST /v1/messages`, `Accept: text/event-stream`), the
reassembled `tool_use.input` **intermittently omits a field that the tool's
`input_schema` declares as `required`**. The `input_json_delta.partial_json`
fragments that Copilot streams concatenate into JSON that is *syntactically
valid* but *schema-invalid* — a required property is simply absent.

For a tool like Anthropic's `AskUserQuestion` (each `questions[]` item requires
`question`, `header`, `options`, `multiSelect`), the streamed input comes back
with the `question` field missing from every question object, so any strict
schema validator on the client rejects the tool call ("Invalid tool
parameters") and the turn fails.

This is non-deterministic: the same request shape succeeds most of the time and
fails a minority of the time, which points at the model/gateway streaming path
rather than a client bug.

## Affected surface

- **Endpoint:** `POST https://api.<enterprise|business|individual>.githubcopilot.com/v1/messages`
- **Header:** `Accept: text/event-stream` (streaming); also reproduced via the standard VS Code editor headers
- **Models observed:** `claude-opus-4.8` (primary). The same family streams tool calls the same way, so `claude-sonnet-4.6` is likely also affected.
- **Not** observed on non-streaming (`stream:false`) responses, where `tool_use.input` arrives as a single complete object.

## What I expected

The concatenation of all `input_json_delta.partial_json` fragments for a
`tool_use` content block should reproduce a JSON object that satisfies the
tool's declared `input_schema`, including every `required` property — exactly as
the first-party Anthropic API does.

## What actually happens

The concatenated fragments are valid JSON but **missing a required field**.
Example (real capture, reassembled from the streamed `partial_json` fragments;
the tool was `AskUserQuestion`):

```json
{"questions":[
  {"header":"Database","multiSelect":false,"options":[ ... ]},
  {"header":"Deploy target","multiSelect":false,"options":[ ... ]}
]}
```

Each `questions[]` item is missing its `question` property (required by the
tool's schema). The fragment boundaries show the gap directly — two consecutive
`input_json_delta` events were:

```
partial_json = "{\"questi"
partial_json = "ons\": [{\"he"     ←  jumps straight from `[{"`  to  `"he`(ader)
```

i.e. the `"question":"…",` key/value pair that should sit between `[{` and
`"header"` is absent from the stream itself.

## Frequency (measured)

Across 6 consecutive `AskUserQuestion` tool calls in one session:

| call | questions in call | questions WITH required `question` field |
|------|-------------------|------------------------------------------|
| 1 | 3 | 2  *(one missing)* |
| 2 | 3 | 3 |
| 3 | 1 | 1 |
| 4 | 1 | 1 |
| 5 | 2 | **0**  *(all missing → turn rejected)* |
| 6 | 2 | 2 |

So roughly 1–2 in 6 calls were affected. A tighter, isolated reproduction (below)
came back clean for 8/8 on one batch — consistent with a low-frequency,
non-deterministic omission rather than a deterministic failure.

## Reproduction

Force a tool call with `tool_choice` so the model must emit `AskUserQuestion`,
stream the response, and concatenate the `partial_json` fragments. Repeat ~10–20
times; a fraction will be missing the `question` field.

```bash
curl -N https://api.githubcopilot.com/v1/messages \
  -H "Authorization: Bearer $COPILOT_TOKEN" \
  -H "Content-Type: application/json" \
  -H "Accept: text/event-stream" \
  -H "Editor-Version: vscode/1.99.0" \
  -H "Editor-Plugin-Version: copilot-chat/0.26.0" \
  -H "Copilot-Integration-Id: vscode-chat" \
  -d '{
    "model": "claude-opus-4.8",
    "max_tokens": 4096,
    "stream": true,
    "tool_choice": {"type":"tool","name":"AskUserQuestion"},
    "tools": [{
      "name": "AskUserQuestion",
      "description": "Ask the user one or more multiple-choice questions.",
      "input_schema": {
        "type":"object",
        "properties": {
          "questions": {
            "type":"array",
            "items": {
              "type":"object",
              "properties": {
                "question": {"type":"string"},
                "header": {"type":"string"},
                "multiSelect": {"type":"boolean"},
                "options": {"type":"array","items":{"type":"object","properties":{"label":{"type":"string"},"description":{"type":"string"}}}}
              },
              "required": ["question","header","options","multiSelect"]
            }
          }
        },
        "required": ["questions"]
      }
    }],
    "messages": [{"role":"user","content":"I am choosing a database and a deploy target for a new web app. Ask me two questions to narrow it down, each with 3 options."}]
  }'
```

Then, for each response, concatenate every
`data:` event whose `delta.type == "input_json_delta"` by their `partial_json`,
`JSON.parse` the result, and check that each `questions[]` item contains a
`question` key. A subset of runs will be missing it.

## Why this is server-side, not a client bug

I isolated the bridge/client out of the loop two ways:

1. **Round-trip parser test.** Feeding well-formed Anthropic tool-call SSE
   (including fragment boundaries that split mid-key right before `question`,
   CRLF line endings, and an empty first fragment) through a standard SSE parse +
   re-serialize preserves every field byte-for-byte. The client never drops it
   for well-formed input.

2. **Raw byte read.** Reading Copilot's response with a raw stream reader
   (no SSE event framing / no reassembly logic at all) and concatenating the
   `partial_json` fragments straight off the wire still shows the field missing
   on the affected runs. The omission is present in the bytes Copilot emits,
   before any client-side parsing.

## Impact

Any client that validates `tool_use.input` against the tool's declared
`input_schema` (the Anthropic SDKs and Claude Code do) will reject the entire
tool call when a required field is missing, failing the turn. Because it is
intermittent, it manifests as flaky "Invalid tool parameters" / "tool input did
not match schema" errors that are hard to attribute.

## Environment

- Copilot Enterprise, native Anthropic `/v1/messages` endpoint
- `claude-opus-4.8`, streaming, `tool_choice` forcing a specific tool
- Observed 2026-06-05

## Suggested fix

Ensure the streaming tool-call generation path emits the complete tool input —
specifically that every property the model produces (and at minimum every
`required` property per the tool's `input_schema`) is present in the
concatenated `input_json_delta` fragments, matching first-party Anthropic API
behavior. If the model occasionally produces incomplete tool JSON, consider
validating/repairing against the declared `input_schema` before the stream is
finalized, or surfacing a clear `stop_reason` rather than emitting a silently
incomplete object.
