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
  }' -F owner="$OWNER" -F name="$NAME" -F pr="$PR" 2>/dev/null)" \
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
  --json mergeable,mergeStateStatus,state,statusCheckRollup 2>/dev/null)" \
  || fail "gh pr view failed — cannot read CI/merge state"

MERGE_STATE="$(echo "$VIEW_JSON" | jq -r '.mergeStateStatus // "UNKNOWN"')"
PR_STATE="$(echo "$VIEW_JSON" | jq -r '.state // "UNKNOWN"')"

# CI: fail if any check concluded non-success; pending if any not COMPLETED; else pass.
# none if there are no checks at all.
CI="$(echo "$VIEW_JSON" | jq -r '
  (.statusCheckRollup // []) as $c
  | if ($c|length)==0 then "none"
    elif any($c[]; (.conclusion // .state // "") | ascii_upcase | . as $x
              | ($x=="FAILURE" or $x=="ERROR" or $x=="CANCELLED" or $x=="TIMED_OUT" or $x=="ACTION_REQUIRED")) then "fail"
    elif any($c[]; (.status // "") | ascii_upcase | (.=="QUEUED" or .=="IN_PROGRESS" or .=="PENDING" or .=="WAITING")) then "pending"
    else "pass" end')" \
  || fail "could not derive CI state"
echo "CI=${CI}"
echo "MERGE_STATE=${MERGE_STATE}"
echo "PR_STATE=${PR_STATE}"

# --- 3. Copilot review round hint ----------------------------------------------
# Copilot review submissions show as `reviewed` timeline events. Counting them is
# the round number; >=5 means we've exhausted Copilot's review budget.
REVIEWS="$(gh api "repos/${REPO}/issues/${PR}/timeline" --paginate \
  --jq '[.[]|select(((.actor.login? // .user.login?)=="Copilot") and .event=="reviewed")]|length' 2>/dev/null)" \
  || fail "timeline query failed — cannot count Copilot review rounds"
# --paginate on an empty list yields empty string; normalize to 0.
REVIEWS="${REVIEWS:-0}"
echo "COPILOT_REVIEWS=${REVIEWS}"
echo "ROUND_HINT=${REVIEWS}"

exit 0
