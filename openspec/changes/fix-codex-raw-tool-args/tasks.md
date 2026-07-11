# Tasks — carry raw (non-JSON) tool arguments through T1 (request side)

## 1. Ground the contract
- [x] 1.1 Confirm the failure from the real trace: a follow-up turn echoes a prior
  `exec` call as `function_call` with `arguments`=raw JS → T1 `JsonDocument.Parse`
  → `ExpectedStartOfValueNotFound` → 400 (`request-traces/20260711-091637-0002`,
  `call_upVNOaJ5MGfhtORZu44MMRef`, 256-char raw JS).
- [x] 1.2 Live-probe: Copilot accepts a `function_call` with raw-text arguments
  (and the native `custom_tool_call` shape) — all 200 (`CustomToolEchoProbe`).

## 2. Fix
- [x] 2.1 T1 `ParseArgumentsToElement` → `(JsonElement, bool grammarText)`: JSON
  object → as-is; else wrap raw text as a JSON string element + `grammarText=true`;
  empty → `{}`. Mark the tool_use block via `BuildGrammarArgsPartBag`
  (`grammar_text_arguments`). No more `CodexBadRequestException` on non-JSON args.
- [x] 2.2 T2 `function_call` emit: grammar-text-marked block (string input) →
  `GetString()` (raw), else `GetRawText()` (JSON, byte-identical). New
  `IsGrammarTextArgs`.

## 3. Tests (from contract, mutation-checked)
- [x] 3.1 `CodexRawToolArgsRoundTripTests`: raw JS round-trips verbatim; JSON
  function args unchanged; empty→`{}`. Mutation-verified (T2 always GetRawText →
  raw-JS test red).
- [x] 3.2 Update `A9`/`A9b` (were asserting the OLD 400 contract) to assert the new
  lossless carriage — they froze the bug.
- [x] 3.3 `CodexCustomToolEchoHeadlessTests`: the exact failing echo shape through
  the real in-process bridge → 200 + `response.completed` + audit shows raw args
  verbatim on the upstream wire.

## 4. Real-client verification (the mandate)
- [x] 4.1 Replayed the ACTUAL captured 400-ing request (256-char raw-JS exec call)
  through the fixed bridge on a live port → HTTP 200 + `response.completed`, 0
  malformed-JSON signatures.
- [x] 4.2 Real `codex.exe` multi-step tool task through the fixed bridge → 4 rounds
  all 200, multiple function_call/output echo round-trips, exit 0.
- [ ] 4.3 (Needs the user) A real DESKTOP-Codex `exec` session — `codex exec` CLI
  doesn't emit the custom grammar tool, only the desktop app does. The headless
  replay of the exact failing bytes (4.1) is the strongest offline proof.

## 5. Docs
- [x] 5.1 `CLAUDE.md` + `AGENTS.md`: mandatory "a fix is not done until a real
  client ran a complex multi-step task through it" directive (per-client + the
  second-turn-echo reproduction note).
