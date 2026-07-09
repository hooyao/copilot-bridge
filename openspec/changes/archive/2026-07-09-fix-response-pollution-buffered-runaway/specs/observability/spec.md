# observability (delta)

## ADDED Requirements

### Requirement: Runaway detection is logged at the detection point

When the runaway guard aborts a response, the bridge SHALL emit exactly one
`Warning` log record at the point of detection, identifying the tripped signal (byte
budget, delta-count budget, repetition-density, or consecutive-run) via its reason
string, and the delivery mode by which the abort was applied (`stream` for a
mid-stream injected error, `buffer` for a real HTTP status). The record SHALL NOT
contain any of the runaway content — no repeated token, delta text, or body values —
only the counts/reason and signal metadata needed to diagnose the trip.

The record SHALL carry the request's trace id, like other pipeline log records, so an
operator can move between the runaway `Warning` and the request's trace JSON files.

#### Scenario: Runaway Warning names the reason and delivery mode

- **WHEN** the runaway guard aborts a response — on either the streaming or the
  buffered path — for any signal
- **THEN** a single `Warning` is emitted naming the tripped signal/reason and the
  delivery mode (`stream` vs `buffer`), and it carries the request trace id

#### Scenario: Runaway content is not logged

- **WHEN** a runaway is detected
- **THEN** the emitted log record does not contain the repeated token, any delta
  text, or any body values from the runaway response

#### Scenario: Exactly one Warning per runaway

- **WHEN** a runaway is detected and the response is aborted
- **THEN** exactly one `Warning`-level record describes the runaway (no second,
  redundant `Warning` for the same event)
