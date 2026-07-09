#!/usr/bin/env bash
# reply-resolve.sh — reply to a Copilot review comment, then resolve its thread.
#
#   Usage: reply-resolve.sh <owner>/<repo> <pr-number> <comment-databaseId> "<reply body>"
#
# Why both, always together: the review loop trusts the UNRESOLVED-thread count as
# its single source of truth (see pr-status.sh). That only holds if every comment
# you've addressed is resolved. Reply explains what you did (fixed / refuted-why);
# resolve takes it out of the open set so the next poll is trustworthy. Skipping
# the resolve is what forces fragile "which comment is new?" timestamp-guessing —
# and GitHub re-anchors old comments onto new commits, so timestamps lie.
#
# Both the reply and the resolve go through GraphQL against the thread's node id
# (looked up from the comment's databaseId), so no PR-number-bearing REST reply
# route is needed and the two operations share one lookup.
#
# Fail-loud: any step failing exits non-zero so the caller knows the comment is NOT
# actually cleared (and the unresolved-count will correctly still show it).

set -uo pipefail

if [[ $# -ne 4 ]]; then
  echo "usage: reply-resolve.sh <owner>/<repo> <pr-number> <comment-databaseId> \"<reply>\"" >&2
  exit 2
fi

REPO="$1"
PR="$2"
CID="$3"
BODY="$4"
OWNER="${REPO%%/*}"
NAME="${REPO##*/}"

# databaseId and PR are numeric — validate before splicing CID into a jq filter.
if ! [[ "$CID" =~ ^[0-9]+$ ]]; then
  echo "comment id must be numeric (the REST databaseId), got: $CID" >&2
  exit 2
fi
if ! [[ "$PR" =~ ^[0-9]+$ ]]; then
  echo "pr-number must be numeric, got: $PR" >&2
  exit 2
fi

# --- 1. Map the comment databaseId → its thread node id (on this specific PR) ----
THREAD_ID="$(gh api graphql -f query='
  query($owner:String!,$name:String!,$pr:Int!){
    repository(owner:$owner,name:$name){ pullRequest(number:$pr){
      reviewThreads(first:100){ nodes{ id comments(first:50){ nodes{ databaseId } } } }
    } }
  }' -F owner="$OWNER" -F name="$NAME" -F pr="$PR" \
  --jq ".data.repository.pullRequest.reviewThreads.nodes[] | select(.comments.nodes[].databaseId==${CID}) | .id" 2>/dev/null | head -1)" \
  || { echo "thread lookup failed for comment ${CID} on PR ${PR}" >&2; exit 1; }

if [[ -z "${THREAD_ID:-}" ]]; then
  echo "could not find a review thread containing comment ${CID} on PR ${PR}" >&2
  exit 1
fi

# --- 2. Reply in-thread via GraphQL (no PR-number-bearing REST route needed) -----
gh api graphql -f query='
  mutation($tid:ID!,$body:String!){
    addPullRequestReviewThreadReply(input:{pullRequestReviewThreadId:$tid, body:$body}){
      comment{ id }
    }
  }' -F tid="$THREAD_ID" -F body="$BODY" --jq '.data.addPullRequestReviewThreadReply.comment.id' >/dev/null 2>&1 \
  || { echo "reply to thread ${THREAD_ID} (comment ${CID}) failed" >&2; exit 1; }

# --- 3. Resolve the thread ------------------------------------------------------
RESOLVED="$(gh api graphql -f query='
  mutation($tid:ID!){ resolveReviewThread(input:{threadId:$tid}){ thread{ isResolved } } }' \
  -F tid="$THREAD_ID" --jq '.data.resolveReviewThread.thread.isResolved' 2>/dev/null)"

if [[ "$RESOLVED" != "true" ]]; then
  echo "replied, but failed to resolve thread ${THREAD_ID}" >&2
  exit 1
fi

echo "comment ${CID}: replied + resolved (thread ${THREAD_ID})"
