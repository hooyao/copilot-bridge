# Pitfalls — the specific ways this went wrong once

A concise catalog of failures from the origin session, each with the fix that's now
baked into the golden path. Read this if a step is behaving oddly — you may be
about to repeat one.

## 1. The silent-`0` polling loop

**What happened:** a background watcher polled `gh api ... || echo 0` for the review
state. In the sandboxed shell `gh` failed (or the queried field was always empty),
so it read `0` every tick and never fired — while the reviewer had *already*
commented. Multiple wasted waits.
**Fix:** `pr-status.sh` checks every `gh` call and prints `STATUS_ERROR=1` + exits
non-zero on any failure. A failed call can never be read as "0 / all clear". And the
loop is driven by `/loop`, not a background poller.

## 2. Polling the wrong field (`reviews[]` length)

**What happened:** waited on the length of the reviews collection to go up. It never
did — Copilot posts inline comments + a `reviewed` timeline event, not a reviews[]
body. Infinite wait.
**Fix:** poll unresolved `reviewThreads`. Count `reviewed` timeline events only as a
round hint.

## 3. Timestamp / commit_id filtering to find "new" comments

**What happened:** tried to identify the latest round's comments by "created after my
request time" or "commit_id == my HEAD". GitHub re-anchors old comments onto the new
commit on push (commit_id changes, created_at doesn't), so old already-addressed
comments looked new, and the filter was unreliable in both directions.
**Fix:** resolve every comment after replying; poll unresolved count. Never filter by
time or SHA.

## 4. Declaring "no comments" too early

**What happened:** saw an empty `reviewed` event (round N) and concluded Copilot was
done — but it then posted more comments on the just-archived spec a moment later.
**Fix:** "done" = a `reviewed` event landed after your last push AND `OPEN_COMMENTS=0`.
When unsure, one more `/loop` tick costs nothing. (In the origin session the user had
to interject "wait, Copilot has comments again" — the skill now waits for the
unresolved count, not a vibe.)

## 5. OpenSpec archived after merge → its spec never reviewed

**What happened:** the plan was to archive after merging. But `openspec archive` syncs
a spec that ships with a `TBD` Purpose and a scope line that had drifted — exactly the
kind of thing review catches. Archiving post-merge would have shipped those unreviewed.
**Fix:** archive BEFORE opening the PR, fold the archive commit into the same PR, and
read the synced `specs/<capability>/spec.md` before committing (fix the `TBD` Purpose
and any `/cc`-only-vs-both-paths scope drift yourself).

## 6. Sweeping unrelated working-tree changes into the commit

**What happened:** the working tree also held an unrelated, already-staged change
(a revert of a different feature) plus scratch files. A blanket `git add -A` would
have bundled someone else's work into this PR.
**Fix:** stage your paths explicitly; leave unrelated modified/staged files alone; if
ownership is unclear, ask the user before committing.

## 7. Building the release by hand

**What happened:** almost hand-built the AOT binaries and created the release manually.
**Fix:** this repo's `release.yml` triggers on a `release-*` tag and builds+publishes
all platforms itself. Push the tag; let CI do the rest. Confirm the run started and
hand the user the URL.

## Meta-lesson

Most of these are the same mistake wearing different hats: **trusting a signal that
can be silently wrong** (a swallowed `gh` error, an always-empty field, a re-anchored
timestamp, an early empty review) instead of a signal you actively make trustworthy
(fail-loud checks + resolve-after-reply so the unresolved count means what it says).
When designing any new check here, ask: "if this call failed, would I be able to tell,
or would it look like good news?"

## 8. The reviewer's own workflow run crashed → loop waits forever

**What happened:** Copilot's review workflow run hung ~15 min, then went to
`cancelled/failure`. The loop was watching only "new open comment" / "ROUND_HINT
up" — neither of which a crashed run reliably produces — so it would have waited
indefinitely. Compounding it: re-requesting review while the stuck run was still
`running` spawned no new run (the request was absorbed), so the "safety-net
re-request" did nothing.
**Fix:** `pr-status.sh` reports `COPILOT_RUN` (latest Copilot run's health). On
`COPILOT_RUN=failure` with no forward progress, re-trigger the review — but only
after the stuck run is terminal (a re-request during `running` is a no-op). Bound
the re-triggers (~2) then escalate to the user. Evaluate positive signals (open
comment, ROUND_HINT) BEFORE run health: a run can post comments and still end
cancelled, so a `failure` conclusion doesn't mean "no review happened".
