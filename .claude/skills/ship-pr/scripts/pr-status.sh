#!/usr/bin/env bash
# pr-status.sh — idempotent, fail-loud PR status for the ship-pr review loop.
#
#   Usage: pr-status.sh <owner>/<repo> <pr-number>
#
# Prints machine-readable lines the caller acts on:
#   OPEN_COMMENTS=<n>     unresolved Copilot review threads — the ONE source of truth
#   CI=<pending|pass|fail|none>
#   MERGE_STATE=<CLEAN|BLOCKED|UNKNOWN|...>
#   PR_STATE=<OPEN|MERGED|CLOSED>
#   COPILOT_REVIEWS=<n>   count of Copilot `reviewed` timeline events (round hint)
#   ROUND_HINT=<n>        == COPILOT_REVIEWS (>=5 → stop looping, escalate)
#
# THE CARDINAL RULE: a failed `gh` call must NEVER masquerade as "0 open comments /
# all clear". Every query is checked; any failure prints STATUS_ERROR=1 and exits
# non-zero, so the caller loops again instead of merging on a swallowed error.
# (In the origin session, `gh ... || echo 0` turned auth/network failures into a
# permanent fake "0", and a green-looking result came from nowhere. Never again.)

set -uo pipefail

if [[ $# -ne 2 ]]; then
  echo "usage: pr-status.sh <owner>/<repo> <pr-number>" >&2
  echo "STATUS_ERROR=1"
  exit 2
fi

REPO="$1"
PR="$2"
OWNER="${REPO%%/*}"
NAME="${REPO##*/}"

fail() {
  echo "STATUS_ERROR=1"
  echo "# $1" >&2
  exit 1
}

# Hard dependency check up front. Every signal below parses gh output through jq;
# if jq (or gh) is missing, an unguarded parse would emit empty/garbage state
# WITHOUT STATUS_ERROR=1, silently defeating the fail-loud contract. Abort loudly
# instead so a missing tool can never look like "0 open comments / all clear".
for tool in gh jq; do
  command -v "$tool" >/dev/null 2>&1 || fail "required tool '$tool' not found on PATH"
done

# --- 1. Unresolved review threads (the primary signal) --------------------------
# GraphQL is the ONLY place isResolved lives; gh pr view can't see it. We resolve
# every comment after replying, so unresolved == genuinely-open == needs action.
THREADS_JSON="$(gh api graphql -f query='
  query($owner:String!,$name:String!,$pr:Int!){
    repository(owner:$owner,name:$name){
      pullRequest(number:$pr){
        reviewThreads(first:100){ nodes{ isResolved } }
      }
    }
  }' -f owner="$OWNER" -f name="$NAME" -F pr="$PR" 2>/dev/null)" \
  || fail "graphql reviewThreads query failed (auth/network?) — NOT zero comments"

# Guard against a well-formed-but-empty/error body being read as 0.
if ! echo "$THREADS_JSON" | jq -e '.data.repository.pullRequest.reviewThreads.nodes' >/dev/null 2>&1; then
  fail "reviewThreads response missing expected shape — treating as error, not zero"
fi
OPEN="$(echo "$THREADS_JSON" | jq '[.data.repository.pullRequest.reviewThreads.nodes[]|select(.isResolved==false)]|length')" \
  || fail "could not count unresolved threads"
echo "OPEN_COMMENTS=${OPEN}"

# --- 2. CI + merge + PR state (one call) ---------------------------------------
VIEW_JSON="$(gh pr view "$PR" --repo "$REPO" \
  --json mergeable,mergeStateStatus,state,statusCheckRollup,headRefName 2>/dev/null)" \
  || fail "gh pr view failed — cannot read CI/merge state"

MERGE_STATE="$(echo "$VIEW_JSON" | jq -r '.mergeStateStatus // "UNKNOWN"')" || fail "jq parse of mergeStateStatus failed"
PR_STATE="$(echo "$VIEW_JSON" | jq -r '.state // "UNKNOWN"')" || fail "jq parse of state failed"
HEAD_BRANCH="$(echo "$VIEW_JSON" | jq -r '.headRefName // ""')" || fail "jq parse of headRefName failed"

# CI: fail if any check concluded non-success; pending if any not COMPLETED; else pass.
# none if there are no checks at all. Two entry shapes coexist in statusCheckRollup:
# CheckRun (uses .status/.conclusion) and legacy StatusContext (uses .state, e.g.
# PENDING/EXPECTED/SUCCESS/FAILURE/ERROR). Both must be consulted or a still-pending
# legacy status context falls through to "pass" and we could merge too early.
CI="$(echo "$VIEW_JSON" | jq -r '
  (.statusCheckRollup // []) as $c
  | ($c | map((.conclusion // .state // "") | ascii_upcase)) as $terminal
  | ($c | map((.status // .state // "") | ascii_upcase)) as $progress
  | if ($c|length)==0 then "none"
    elif any($terminal[]; (.=="FAILURE" or .=="ERROR" or .=="CANCELLED" or .=="TIMED_OUT" or .=="ACTION_REQUIRED")) then "fail"
    elif any($progress[]; (.=="QUEUED" or .=="IN_PROGRESS" or .=="PENDING" or .=="WAITING" or .=="EXPECTED" or .=="")) then "pending"
    else "pass" end')" \
  || fail "could not derive CI state"
echo "CI=${CI}"
echo "MERGE_STATE=${MERGE_STATE}"
echo "PR_STATE=${PR_STATE}"

# --- 3. Copilot review round hint ----------------------------------------------
# Copilot review submissions show as `reviewed` timeline events. Counting them is
# the round number; >=5 means we've exhausted Copilot's review budget.
#
# `gh api --paginate` applies --jq to EACH page separately, so a page-level
# `[...]|length` yields one number per page (e.g. "1\n2" across two pages), which
# corrupts the count on a long timeline. Emit one line per matching event instead
# and total them with `wc -l`, which sums correctly across pages. `set -o pipefail`
# still surfaces a gh failure through the pipe.
REVIEWS="$(gh api "repos/${REPO}/issues/${PR}/timeline" --paginate \
  --jq '.[]|select(((.actor.login? // .user.login?)=="Copilot") and .event=="reviewed")|.event' 2>/dev/null | wc -l | tr -d '[:space:]')" \
  || fail "timeline query failed — cannot count Copilot review rounds"
REVIEWS="${REVIEWS:-0}"
echo "COPILOT_REVIEWS=${REVIEWS}"
echo "ROUND_HINT=${REVIEWS}"

# --- 4. Copilot review-workflow health (deadlock guard) -------------------------
# The review loop otherwise waits on TWO signals: a new open comment, or ROUND_HINT
# going up. Neither arrives if Copilot's *review workflow run itself* fails/cancels
# (observed: a run that hung ~15min then went to `cancelled/failure` and produced no
# `reviewed` event). Without this the loop waits forever on a review that will never
# land. Surface the latest Copilot review run's conclusion so the caller can tell
# "still reviewing" from "the reviewer crashed" and re-trigger instead of hanging.
#   COPILOT_RUN=success   last run finished (a `reviewed` event should exist/appear)
#   COPILOT_RUN=running   a run is in progress — genuinely wait
#   COPILOT_RUN=failure   last run failed/cancelled — re-trigger the review
#   COPILOT_RUN=none      no Copilot run yet for this branch (or workflow named
#                         differently) — fall back to the timeline signal
COPILOT_RUN="none"
if [[ -n "$HEAD_BRANCH" ]]; then
  # Capture output and exit code SEPARATELY — do NOT `|| true`, which would make an
  # auth/network failure indistinguishable from "no run yet" and reintroduce the very
  # "trust a silently-wrong signal" failure this guard exists to prevent. gh returns
  # exit 0 with `[]` when the workflow simply has no runs (or isn't found); a non-zero
  # exit means a real error → fail-loud.
  RUN_ERR="$(mktemp 2>/dev/null || echo /tmp/prstatus_runerr.$$)"
  if RUN_JSON="$(gh run list --workflow=Copilot --branch "$HEAD_BRANCH" -L 1 \
      --json status,conclusion 2>"$RUN_ERR")"; then
    rm -f "$RUN_ERR" 2>/dev/null || true
    if [[ -n "${RUN_JSON:-}" && "$RUN_JSON" != "[]" ]]; then
      RUN_STATUS="$(echo "$RUN_JSON" | jq -r '.[0].status // ""')" || fail "jq parse of run status failed"
      RUN_CONCL="$(echo "$RUN_JSON" | jq -r '.[0].conclusion // ""')" || fail "jq parse of run conclusion failed"
      if [[ "$RUN_STATUS" != "completed" ]]; then
        COPILOT_RUN="running"
      elif [[ "$RUN_CONCL" == "success" ]]; then
        COPILOT_RUN="success"
      else
        # failure, cancelled, timed_out, action_required, ... → the reviewer did not
        # deliver; treat as needing a re-trigger.
        COPILOT_RUN="failure"
      fi
    fi
    # else: empty/[] → genuinely no run yet → COPILOT_RUN stays "none" (correct).
  else
    # gh itself errored. Only "no such workflow" is a benign none; anything else
    # (auth, network, rate limit) must NOT masquerade as none.
    if grep -qiE 'could not find|no workflow|not found' "$RUN_ERR" 2>/dev/null; then
      COPILOT_RUN="none"
    else
      rm -f "$RUN_ERR" 2>/dev/null || true
      fail "gh run list (Copilot workflow) failed — cannot assess reviewer-run health"
    fi
    rm -f "$RUN_ERR" 2>/dev/null || true
  fi
fi
echo "COPILOT_RUN=${COPILOT_RUN}"

exit 0
