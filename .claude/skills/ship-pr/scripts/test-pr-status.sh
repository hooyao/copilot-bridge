#!/usr/bin/env bash
# Offline contract checks of the REAL status script with a deterministic gh facade.
# No network, credentials or GitHub mutations. Run from the repository root.
set -euo pipefail

SKILL_ROOT="${1:-.claude/skills/ship-pr}"
export STATUS_SCRIPT="$SKILL_ROOT/scripts/pr-status.sh"
FIXTURES="$SKILL_ROOT/references/review-status-cases.json"
command -v jq >/dev/null

gh() {
  if [[ "$1 $2" == "api graphql" ]]; then
    if jq -e '.apiFailure // false' "$CASE_FILE" >/dev/null; then return 1; fi
    jq -c '[{data:{repository:{pullRequest:{reviewThreads:{nodes:[range(0;(.threads // 0))|{isResolved:false}]}}}}}]' "$CASE_FILE"
  elif [[ "$1 $2" == "pr view" ]]; then
    printf '%s\n' '{"headRefName":"test","headRefOid":"current","state":"OPEN","mergeStateStatus":"CLEAN","statusCheckRollup":[{"status":"COMPLETED","conclusion":"SUCCESS"}]}'
  elif [[ "$1 $2" == "run list" ]]; then
    printf '%s\n' '[{"status":"completed","conclusion":"success"}]'
  elif [[ "$2" == */reviews ]]; then
    jq -c '[.reviews]' "$CASE_FILE"
  elif [[ "$2" == */timeline ]]; then
    printf '%s\n' reviewed
  else
    printf 'unexpected mock gh arguments: %s\n' "$*" >&2
    return 1
  fi
}
export -f gh

export CASE_FILE
CASE_FILE="$(mktemp)"
trap 'rm -f "$CASE_FILE"' EXIT
failures=0
count="$(jq length "$FIXTURES")"
for ((i=0; i<count; i++)); do
  jq ".[$i]" "$FIXTURES" > "$CASE_FILE"
  name="$(jq -r .name "$CASE_FILE")"
  status=0
  output="$(bash -c 'source <(tr -d "\r" < "$STATUS_SCRIPT")' -- test/repo 95 2>&1)" || status=$?
  if jq -e '.error // false' "$CASE_FILE" >/dev/null; then
    if [[ $status -ne 0 && "$output" == *STATUS_ERROR=1* && "$output" != *OPEN_COMMENTS=0* ]]; then
      printf 'PASS %s\n' "$name"
      continue
    fi
  elif [[ $status -eq 0 ]]; then
    expected_open="$(jq .open "$CASE_FILE")"
    expected_suppressed="$(jq .suppressed "$CASE_FILE")"
    expected_evidence="$(jq .evidence "$CASE_FILE")"
    actual_evidence=0
    while IFS= read -r line; do
      [[ "$line" != COPILOT_EVIDENCE_CONFIRMATION=* ]] || actual_evidence="${line#*=}"
    done <<< "$output"
    if [[ $'\n'"$output"$'\n' == *$'\n'"OPEN_COMMENTS=$expected_open"$'\n'* \
       && $'\n'"$output"$'\n' == *$'\n'"SUPPRESSED_FINDINGS=$expected_suppressed"$'\n'* \
       && "$actual_evidence" == "$expected_evidence" ]]; then
      printf 'PASS %s\n' "$name"
      continue
    fi
  fi
  printf 'FAIL %s (exit=%s)\n%s\n' "$name" "$status" "$output" >&2
  failures=$((failures + 1))
done
[[ $failures -eq 0 ]] || exit 1
printf 'Passed %s offline status contracts.\n' "$count"
