## Why

The custom-tool fix in 0.4.11-beta (PR #39) made a gpt-5.6 `exec` (custom/grammar)
tool call's arguments reach Codex on the RESPONSE side. But it left the mirror
gap on the REQUEST side: on the NEXT turn Codex echoes that call back to the
bridge as a `function_call` whose `arguments` is **raw JavaScript**, not JSON.

T1's `ParseArgumentsToElement` did `JsonDocument.Parse(arguments)` expecting a JSON
object → `ExpectedStartOfValueNotFound` → `CodexBadRequestException` → **HTTP 400**.
So the real gpt-5.6 exec loop **died on its second turn** (verified on a live
0.4.11-beta session: `error=function_call 'call_upVNOaJ5MGfhtORZu44MMRef' has
malformed JSON arguments: ExpectedStartOfValueNotFound`).

Root cause: the IR contract "`tool_use.input` MUST be a JSON object, else 400" is
wrong for custom tools — their input is legitimately non-JSON text, and Copilot
round-trips it fine (live-probed 200 for all echo shapes, `CustomToolEchoProbe`).

## What Changes

- **T1 accepts non-JSON tool arguments.** `ParseArgumentsToElement` no longer
  throws: a JSON object → carried as-is (`grammarText=false`, unchanged); anything
  else (raw text, or a JSON scalar/array) → wrapped as a JSON **string** element
  and the tool_use block is marked `grammar_text_arguments=true` in its part-level
  `ProviderExtensions["openai"]` bag. Empty/whitespace → `{}` (unchanged).
- **T2 re-emits raw arguments verbatim.** When a tool_use block carries the
  `grammar_text_arguments` marker (and its input is a string), the `function_call`
  `arguments` is written from `GetString()` (the raw text), not `GetRawText()`
  (which would double-encode the already-quoted string). JSON function tools are
  unchanged (`GetRawText()`, byte-identical).
- **Behavior change (a bug fix, not a break):** non-JSON / non-object tool
  arguments are carried through instead of 400'd. The old `A9`/`A9b` tests that
  asserted a 400 are updated to assert the new lossless-carriage contract.

## Capabilities

### Modified Capabilities
- `codex-responses-endpoint`: the request translator (T1) SHALL carry a tool
  call's arguments regardless of shape — a JSON object OR raw grammar text (a
  custom `exec` call echoed back) — and T2 SHALL re-emit them verbatim, rather than
  rejecting non-JSON arguments with a 400.

## Impact

- **Modified production code**:
  - `Pipeline/Adapters/Codex/ResponsesToIrInboundAdapter.cs` — `ParseArgumentsToElement`
    returns `(JsonElement, bool grammarText)` and never throws on non-JSON; new
    `WrapAsStringElement` + `BuildGrammarArgsPartBag`.
  - `Pipeline/Strategies/Codex/ResponsesRequestBuilder.cs` — the `function_call`
    emit uses `GetString()` for grammar-text-marked blocks; new `IsGrammarTextArgs`.
- **Tests**: `CodexRawToolArgsRoundTripTests` (raw JS round-trips verbatim; JSON
  unchanged; empty→`{}`), the `A9`/`A9b` contract update, `CustomToolEchoProbe`
  (live: all echo shapes 200), and `CodexCustomToolEchoHeadlessTests` (the exact
  failing wire shape through the real bridge → 200 + verbatim args). All
  mutation-checked.
- **Real-wire verification (what WAS run):** the actual captured 400-ing request
  (256-char raw-JS exec call `call_upVNOaJ5MGfhtORZu44MMRef`) replayed through the
  fixed bridge → HTTP 200 + `response.completed`, 0 malformed signatures; real
  `codex.exe` multi-turn task → 4 rounds, all 200. **Still UNVERIFIED (needs the
  user):** a live DESKTOP-Codex `exec` second-turn session — the `codex exec` CLI
  does not emit the custom grammar tool (only the desktop app does), so the only
  headless proof of the exact custom-tool path is the captured-bytes replay above,
  not a live desktop loop. Per the new directive, this fix is verified against the
  exact failing bytes but NOT against a live desktop custom-`exec` turn.
- **Docs**: `CLAUDE.md`/`AGENTS.md` gain a mandatory "verify with a real client on
  a complex multi-step task" directive (this class of bug — multi-turn echo — is
  invisible to unit tests + first-turn smokes, which is how it shipped twice).
- **No breaking changes for JSON tools** — the function-tool path is byte-identical.
- **Fixes the gpt-5.6 `exec` loop dying on the second turn.**
