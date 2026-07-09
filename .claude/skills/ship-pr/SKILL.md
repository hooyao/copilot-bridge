---
name: ship-pr
description: >-
  Ship a finished change end-to-end: finalize its OpenSpec artifacts, open a PR,
  drive the GitHub Copilot review loop to zero open comments, then squash-merge
  and cut a beta release. Use this whenever the user says "ship it", "open a PR
  and get it reviewed", "run the PR loop", "let Copilot review then merge", "cut
  a beta", "bump a release", or otherwise wants the whole finish-a-branch dance —
  even if they only name one part (e.g. "just merge and release"). This encodes a
  GOLDEN PATH with hard-won guardrails: OpenSpec is archived BEFORE the PR so
  Copilot reviews the archived spec too, review-comment polling is done through an
  idempotent status script (never ad-hoc `gh` in a bare loop), and every review
  comment is resolved after reply so the open-count is the single source of truth.
compatibility: >-
  Requires `gh` authenticated to the repo (push + PR + release scopes), a clean
  feature branch ready to ship, and OpenSpec (`openspec` CLI) if the change is
  tracked there. Release cutting assumes a tag-triggered release workflow (this
  repo: push a `release-*` tag).
metadata:
  author: cc-copilot-bridge
  version: "1.0"
---

# Ship a PR (Copilot-reviewed → squash-merge → beta release)

Take a finished branch from "code is done" to "released", driving GitHub
Copilot's review to zero open comments in between. The whole point of this skill
is a **golden path with the potholes already filled in** — every guardrail below
exists because doing it the naive way failed in a real session.

## The order that matters

```
1. Finalize OpenSpec (sync + archive)  ← BEFORE the PR, not after
2. Commit + push + open PR
3. Request Copilot review
4. LOOP:  wait → read OPEN comments → fix or refute → reply → RESOLVE → re-request
   until open-comment count is 0 (bounded: ~5 Copilot rounds)
5. Confirm CI green + mergeable
6. Squash-merge, delete branch
7. Switch to main, pull, bump the beta release tag
```

**Why OpenSpec is archived before the PR (step 1):** archiving syncs the change's
delta spec into `openspec/specs/` and moves the change into `openspec/changes/archive/`.
If you do that *after* merging, Copilot never reviews the archived spec — and in
practice the archive step generates artifacts (a `## Purpose` placeholder, a
scope line that drifted) that genuinely need review. Fold the archive commit into
the same PR so Copilot reviews the whole thing once. See `references/openspec-finalize.md`.

## Step 1 — Finalize OpenSpec (if the change is OpenSpec-tracked)

If there's no OpenSpec change for this work, skip to step 2.

1. Make sure the change's `tasks.md` reflects reality — check off what's done,
   and record any PR-review follow-ups honestly (don't let checkmarks run ahead
   of the actual work; that misleads the reviewer and future-you).
2. Archive it — this also syncs the spec:
   ```bash
   openspec archive <change-name> -y
   ```
3. Stage the resulting moves (`git add openspec/changes/<name>/ openspec/changes/archive/<date>-<name>/ openspec/specs/<capability>/`).
   Git shows them as renames + the new synced spec — that's correct.
4. **Read the newly-synced `openspec/specs/<capability>/spec.md` before committing.**
   `openspec archive` fills `## Purpose` with a `TBD` placeholder and can carry a
   stale scope line from the delta spec. Fix both now, or Copilot will (rounds 4–5
   of a real session were entirely this). Keep the archived delta copy consistent
   with the synced spec.

## Step 2 — Commit, push, open the PR

Scope the commit to THIS change only. If the working tree has unrelated staged or
modified files (a half-finished refactor, someone else's WIP), do NOT sweep them
in — stage your paths explicitly and leave the rest. If it's ambiguous whose
changes those are, ask the user before proceeding.

Create a branch if you're on the default branch (never commit the ship work
straight onto `main`), push, and open the PR with a real body (why / what /
how-tested). End the PR body with the repo's attribution footer if it has one.

## Step 3 — Request Copilot review

```bash
gh api -X POST repos/<owner>/<repo>/pulls/<N>/requested_reviewers \
  -f "reviewers[]=Copilot"
```

Copilot posts its review as inline review comments (not a `reviews` API entry
with a body), usually within a few minutes.

## Step 4 — The review loop (this is where the potholes are)

This is the part that went wrong repeatedly in the origin session. Read
`references/review-loop.md` for the full rationale; the rules in brief:

### Run the loop with `/loop`, not a hand-rolled polling script

The robust way to *wait* for Copilot (and CI) is to let the harness re-invoke you
between checks instead of sleeping inside a fragile background script. Ask the
user to drive the wait with **`/loop`** — e.g. `/loop 3m /ship-pr continue #<N>`
— or, when self-pacing, use the ScheduleWakeup mechanism. Each wake-up runs ONE
idempotent status check (below) and either acts or waits again. This sidesteps
every polling bug: no `sleep`-based loops, no timeouts to guess, no re-invoking
yourself by hand.

**Resuming is state-driven, not memory-driven.** Every `/loop` tick may start with
a fresh context, so never assume you remember where you were — reconstruct it from
the PR. Run `pr-status.sh` first thing and let its output tell you what to do:
`PR_STATE=MERGED` → the PR already merged, jump to the release step; `OPEN_COMMENTS>0`
→ address comments; `CI=pending` → wait; `OPEN_COMMENTS=0` + a new review round →
proceed to merge. The whole skill is designed so any step can be re-derived from
GitHub state, which is what makes it safe to re-enter on every wake-up.

### The single source of truth is the OPEN (unresolved) comment count

Get status with the bundled script — **never** ad-hoc `gh` in a bare loop:

```bash
bash .claude/skills/ship-pr/scripts/pr-status.sh <owner>/<repo> <N>
```

It prints `OPEN_COMMENTS=<n>`, `CI=<pending|pass|fail>`, `MERGE_STATE=<...>`, and
`ROUND_HINT=<n>`. Decision table:

| pr-status says | do |
| --- | --- |
| `OPEN_COMMENTS>0` | there are unresolved review comments → go fix/refute them |
| `OPEN_COMMENTS=0` and a Copilot review landed after your last push | Copilot is satisfied → proceed to step 5 |
| `OPEN_COMMENTS=0` but no new review since your push | still waiting → loop again |
| `STATUS_ERROR=1` | a `gh` call FAILED (not "zero comments") → do NOT treat as done; loop again |

The last row is a real trap: a failed `gh` call must never be silently read as
"0 open comments / all clear". The script exits non-zero and prints
`STATUS_ERROR=1` precisely so a green-looking `OPEN_COMMENTS=0` can't come from a
swallowed error. If you ever find yourself waiting on a field like `reviews`
length that stays `0` forever, you're polling the wrong signal — Copilot reviews
surface as inline comments + a `reviewed` timeline event, not `reviews[]` bodies.

### After you handle each comment: reply, then RESOLVE it

For every comment, decide honestly: **fix it** (it's a real issue) or **refute
it** (it's a false positive — say why). Either way, reply in-thread, then resolve
the thread:

```bash
bash .claude/skills/ship-pr/scripts/reply-resolve.sh <owner>/<repo> <comment-id> "<reply>"
```

Resolving after every reply is what makes `OPEN_COMMENTS` trustworthy on the next
poll. If you skip it, old already-addressed comments pile up and you're back to
guessing "which of these is new?" by timestamp — which is unreliable because
GitHub re-anchors old comments onto new commits (their `commit_id` changes but
`created_at` doesn't). Don't rely on timestamps or `commit_id`; rely on
resolved-vs-open. See the pitfalls doc.

### Push fixes, then re-request review

After committing fixes for a round, `git push` and re-request Copilot review
(same command as step 3) so it re-reviews the new commit. Then loop.

### Bound the loop

Copilot reviews a PR at most ~5 times. Track rounds (`ROUND_HINT` in pr-status,
or count `reviewed` timeline events). If you hit round 5 and comments remain,
STOP and bring the remaining items to the user with your recommendation — don't
spin.

## Step 5 — Confirm CI + mergeable

Before merging, `pr-status.sh` must show `CI=pass` and a mergeable state. If CI is
still `pending`, loop until it settles (same `/loop` mechanism). If `CI=fail`,
read the failing job, fix, push, and re-enter the review loop — a red CI is never
merged.

## Step 6 — Squash-merge

```bash
gh pr merge <N> --squash --delete-branch
```

Squash because the branch has many small "address review round N" commits that
shouldn't litter `main`'s history — one PR, one commit. This repo's convention is
to fold the OpenSpec archive into that same squash commit (why step 1 is in-PR).

## Step 7 — Switch to main, pull, bump the beta release

```bash
git checkout main && git pull --ff-only
```

Then cut the release. **First learn how this repo releases** — don't assume.
Check for a tag-triggered workflow and the existing tag/version convention:

```bash
ls .github/workflows/ && grep -l 'tags:' .github/workflows/*.yml
git tag --sort=-creatordate | grep -iE 'release|beta|^v' | head
gh release list --limit 5
```

In THIS repo, pushing a `release-<version>` tag triggers `release.yml`, which
builds the AOT binaries for every platform and publishes the GitHub Release with
`v<version>` — you only push the tag; you do NOT build or upload manually. The
next beta is the current beta's patch number + 1 (e.g. `0.4.5-beta` →
`release-0.4.6-beta`). Tag the merged `main` HEAD and push:

```bash
git tag -a release-<version> -m "<project> <version> — <one-line summary>"
git push origin release-<version>
```

Confirm the release workflow started (`gh run list --workflow=release.yml -L 1`)
and hand the user the run URL. Cutting a release is outward-facing — the user
asking to "bump a beta" is the authorization; a routine patch-version bump needs
no further confirmation, but a major/minor jump or a non-obvious version does.

## Guardrails distilled (the potholes, in one place)

- **Archive OpenSpec before the PR**, not after — so Copilot reviews the archived spec.
- **Read the synced spec** — `openspec archive` leaves a `TBD` Purpose + can drift scope.
- **Never sweep unrelated working-tree changes** into the ship commit.
- **Wait with `/loop` / ScheduleWakeup**, not a `sleep` loop or a guessed timeout.
- **Poll OPEN (unresolved) comment count**, via `pr-status.sh` — not `reviews[]` length, not timestamps.
- **A failed `gh` call ≠ "all clear"** — `STATUS_ERROR=1` means loop again, never merge.
- **Reply THEN resolve every comment** — so the open-count stays the source of truth.
- **Re-request review after each fix push**; **bound at ~5 rounds** then escalate.
- **Green CI is a merge precondition**, never merged red.
- **Push a `release-*` tag; let CI build/publish** — don't hand-build the release.

For the deeper "why" behind each, read `references/review-loop.md` and
`references/pitfalls.md`.
