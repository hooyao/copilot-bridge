# Copilot's ~300s token-less stream cap

**Copilot closes an SSE stream that has produced no token after roughly 300
seconds.** The stream does open normally — `message_start` and a `content_block_start`
arrive within seconds — and then no *further* bytes follow until the connection ends
in a clean EOF: no `error` event, no `message_stop`. This is a mid-stream cap on a
stream that never produced a token, **not** a pre-header or first-byte failure. It is
a property of the Copilot backend, not of the bridge, and **no bridge or client
setting extends it.**

It matters because it is easy to misread. The symptom (a stream that emits
`message_start`, opens a `thinking` block, and then dies) looks exactly like a
bridge stream-idle timeout, and the bridge's own error message invites you to
raise `Pipeline:UpstreamTimeout:StreamIdleTimeoutSeconds` — which cannot help.

## What was measured

Four runs, two unrelated prompts, two independent HTTP stacks:

| Replay | Stack | Result |
|---|---|---|
| Captured request, `effort=xhigh` | **Direct to Copilot, no bridge** | 305.2s, server closed, 0 tokens |
| Unrelated synthetic prompt, `xhigh` | **Direct to Copilot, no bridge** | 303.0s, server closed, 0 tokens |
| Captured request, `effort=high` | Bridge, 900s idle budget | 305.8s, server closed, 0 tokens |
| Captured request, `effort=xhigh` | Bridge, 900s idle budget | 304.4s `premature_eof`, 0 tokens |

The two direct runs are the load-bearing ones: raw Python `HTTPSConnection`
straight to `api.enterprise.githubcopilot.com/v1/messages`, no bridge process, no
.NET `HttpClient`, no ASP.NET, and a 900-second socket timeout — three times the
disputed value, so a client-side abort could not be mistaken for a server-side
one. The probe distinguished the two outcomes explicitly: a local timer expiring
raises `socket.timeout`; what actually happened was `readline()` returning empty,
i.e. the peer closed the connection.

The second direct run used a prompt with no relation to the first (an exhaustive
graph-enumeration task, chosen only for being expensive to reason about). Same
result, so the cap is not a property of one request body.

### Supporting observations

- **Copilot sends no SSE `ping` keepalive.** Zero occurrences of `event: ping`
  across 290 captured upstream bodies. During extended thinking the wire is
  genuinely byte-free, which is why a long think is indistinguishable from a dead
  stream and why the client-side idle watchdog had to be disabled (see
  `ClaudeCodeConfigurator`, `API_FORCE_IDLE_TIMEOUT`).
- **The cap was previously invisible.** Across 9,608 captured responses in the
  trace corpus, **none** ran past 250s. Nothing had ever touched the ceiling.
- **`effort=medium` completes.** The same request that fails at `high`/`xhigh`
  finished in 276.4s at `medium` — with a **207-second** silent gap in the middle
  of the thinking block. The workload sits right at the edge of the cap: medium
  lands just inside it, high and xhigh do not.

## Operational consequence

A model whose extended thinking exceeds ~300s before emitting its first token
fails deterministically, every time, with no partial output.

The remedy is to shorten the think, not to raise a timeout:

- **Lower the effort.** `/effort medium` is what unblocked the motivating case.
- **Shorten or split the prompt.** Halving the input completed; quartering it
  completed faster.

Raising `StreamIdleTimeoutSeconds` past ~300s does **not** rescue the turn. It
only changes which side reports the failure — instead of the bridge's
`stream_idle` timeout you get Copilot's `premature_eof`. That is still worth
doing if you want the logs to name the real cause, but it buys no additional
capability.

## Reproducing

The measurement should be re-run whenever Copilot's behavior is in question — the
value may change, and it may differ by account type (all four runs above were on
an **enterprise** endpoint with `claude-opus-5`).

1. **Get a Copilot token.** Decrypt the stored GitHub token (Windows/DPAPI; the
   entropy string is in `TokenStore`), then exchange it at
   `GET https://api.github.com/copilot_internal/v2/token`. The response carries
   both the bearer token and the correct `endpoints.api` host for the account.

2. **POST a long-thinking request** to `<endpoints.api>/v1/messages` with the
   headers from `CopilotHeaderFactory` (`X-GitHub-Api-Version`,
   `Copilot-Integration-Id`, `Editor-Version`, `Editor-Plugin-Version`), a body
   using `"thinking": {"type": "adaptive"}` and `"output_config": {"effort":
   "xhigh"}`, and `"stream": true`.

3. **Read the stream with a socket timeout far above 300s** (900s works) and log
   the wall-clock arrival of every `event:` line. Critically, distinguish the two
   endings: an empty read means the server closed the connection; an exception
   means your own client gave up. Only the former demonstrates the cap.

A captured request body from any `Tracing.Enabled` run works as the payload —
`request-traces/<id>-upstream-req.json` holds the exact bytes the bridge sent.

## See also

- `docs/pipeline-design.md` — the bridge's own stream-idle budget, which is a
  different mechanism with a different (tunable) bound.
- `Hosting/ClientConfig/ClaudeCodeConfigurator.cs` — `API_FORCE_IDLE_TIMEOUT`,
  the client-side watchdog the bridge disables so it is the sole idle actor.
