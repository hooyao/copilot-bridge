#!/usr/bin/env bash
# reply-resolve.sh — reply to a Copilot review comment, then resolve its thread.
#
#   Usage: reply-resolve.sh <owner>/<repo> <comment-databaseId> "<reply body>"
#
# Why both, always together: the review loop trusts the UNRESOLVED-thread count as
# its single source of truth (see pr-status.sh). That only holds if every comment
# you've addressed is resolved. Reply explains what you did (fixed / refuted-why);
# resolve takes it out of the open set so the next poll is trustworthy. Skipping
# the resolve is what forces fragile "which comment is new?" timestamp-guessing —
# and GitHub re-anchors old comments onto new commits, so timestamps lie.
#
# Fail-loud: any step failing exits non-zero so the caller knows the comment is NOT
# actually cleared (and the open-count will correctly still show it).

set -uo pipefail

if [[ $# -ne 3 ]]; then
  echo "usage: reply-resolve.sh <owner>/<repo> <comment-databaseId> \"<reply>\"" >&2
  exit 2
fi

REPO="$1"
CID="$2"
BODY="$3"
OWNER="${REPO%%/*}"
NAME="${REPO##*/}"

# CID is a GitHub comment databaseId — always numeric. Validate before we splice it
# into a GraphQL jq filter, so a malformed arg can't inject expression text.
if ! [[ "$CID" =~ ^[0-9]+$ ]]; then
  echo "comment id must be numeric (the REST databaseId), got: $CID" >&2
  exit 2
fi

# --- 1. Reply in-thread to the comment -----------------------------------------
gh api -X POST "repos/${REPO}/pulls/comments/${CID}/replies" -f body="$BODY" --jq '.id' >/dev/null 2>&1 \
  || { echo "reply to comment ${CID} failed" >&2; exit 1; }

# --- 2. Map the REST comment databaseId → GraphQL thread id --------------------
# (Different id spaces: replies take the numeric databaseId; resolve takes the
# thread's node id. This is the join.)
THREAD_ID="$(gh api graphql -f query='
  query($owner:String!,$name:String!){
    repository(owner:$owner,name:$name){
      pullRequests(last:30,states:OPEN){ nodes{
        reviewThreads(first:100){ nodes{ id comments(first:30){ nodes{ databaseId } } } }
      } }
    }
  }' -F owner="$OWNER" -F name="$NAME" \
  --jq ".data.repository.pullRequests.nodes[].reviewThreads.nodes[] | select(.comments.nodes[].databaseId==${CID}) | .id" 2>/dev/null | head -1)"

if [[ -z "${THREAD_ID:-}" ]]; then
  # Fallback: caller may pass a specific PR via env PR_NUMBER for a precise lookup
  # (the open-PRs scan above is a convenience; a merged/closed PR needs this).
  if [[ -n "${PR_NUMBER:-}" ]]; then
    THREAD_ID="$(gh api graphql -f query='
      query($owner:String!,$name:String!,$pr:Int!){
        repository(owner:$owner,name:$name){ pullRequest(number:$pr){
          reviewThreads(first:100){ nodes{ id comments(first:30){ nodes{ databaseId } } } }
        } }
      }' -F owner="$OWNER" -F name="$NAME" -F pr="$PR_NUMBER" \
      --jq ".data.repository.pullRequest.reviewThreads.nodes[] | select(.comments.nodes[].databaseId==${CID}) | .id" 2>/dev/null | head -1)"
  fi
fi

if [[ -z "${THREAD_ID:-}" ]]; then
  echo "replied, but could NOT find the thread for comment ${CID} to resolve it" >&2
  echo "  (set PR_NUMBER=<n> to look it up on a specific PR)" >&2
  exit 1
fi

# --- 3. Resolve the thread ------------------------------------------------------
RESOLVED="$(gh api graphql -f query='
  mutation($tid:ID!){ resolveReviewThread(input:{threadId:$tid}){ thread{ isResolved } } }' \
  -F tid="$THREAD_ID" --jq '.data.resolveReviewThread.thread.isResolved' 2>/dev/null)"

if [[ "$RESOLVED" != "true" ]]; then
  echo "replied, but failed to resolve thread ${THREAD_ID}" >&2
  exit 1
fi

echo "comment ${CID}: replied + resolved (thread ${THREAD_ID})"
