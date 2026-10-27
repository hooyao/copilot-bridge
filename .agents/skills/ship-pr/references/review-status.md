# Review findings and evidence confirmation

Copilot's overview can say `Needs a closer look` and `No open issues` together.
The observed native-dispatch/stream-terminal overview then asks for confirmation
of client-owned execution evidence that the reviewer did not independently read.
That known note is a verification reminder, rather than a suppressed code issue.

`pr-status.sh` reports `COPILOT_EVIDENCE_CONFIRMATION=1` separately only when the
entire meaningful body consists of the closer-look heading, the observed evidence
confirmation sentence, and the explicit no-open-issues headline. The known
overview comment and review-effort footer are optional metadata. No additional
body text is accepted by this exception.

The scope may list the known dispatch, terminal-handling, or shipping/review-gate
areas, or the observed native-client compatibility/revised shipping-status
classification scope. The native-client scope can name locally retained
execution evidence; the client context remains explicit. Scope and request
vocabularies are bounded rather than accepting a generic prefix. Unrecognized
scope concerns still block, as do extra instructions or
additional paragraphs.

The observed request-first form (`Final human review should confirm ...`) also
requires an explicit reference to privately retained client-execution evidence.
Its evidence/classification vocabulary is bounded in the same way: adding a
failure concern or omitting the client-execution evidence makes it a finding.

The request must explicitly name human review, assessment, or verification and
client-owned execution evidence. Its vocabulary is limited to that verification reminder,
including the observed inspection wording; an unknown concern inside the same
sentence or a generic human-review request remains blocking.

One additional exact observed sentence is recognized: "Native-client
compatibility is supported by a detailed verification report, but the locally
retained execution evidence still needs human confirmation." It is an evidence
inspection reminder only when the rest of the overview has the same strict
three-line no-open-issues structure. Changed failure wording, extra instructions,
missing clean headlines, hidden issue counts, and inline threads still block.

The agent/operator must still inspect the saved real-client manifest, matching
tool/output rounds, and client-owned dispatch log using `real-client-verify`.
Missing evidence remains unverified and blocks shipment. The status script cannot
perform or replace that verification. Human confirmation text does not require
an extra routine permission request when the user already authorized shipping.

Safety rules remain conservative:

- A positive `Suppressed comments (N)`, open-issue count, or previously-missed-issue
  count always produces a body finding, even beside `No open issues`.
- An unrecognized closer-look message or any extra unexplained text remains a
  body finding; a clean headline alone never clears it.
- Unresolved inline threads are counted independently and cannot be cleared by an
  evidence note.
- Only the latest Copilot review for the current immutable head is classified.
- API/read failures are errors and never imply an all-clear.

Run the actual status script against offline `gh` responses from the repository
root; these cases never use credentials, network or GitHub mutations:

```bash
bash .agents/skills/ship-pr/scripts/test-pr-status.sh
```

On a CRLF checkout, source the test script through `tr -d '\r'` as for the status
script. `AgentRepositoryCompatibilityTests` checks mirror parity separately.
