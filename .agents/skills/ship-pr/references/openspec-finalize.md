# OpenSpec finalize — do it before the PR

If the change you're shipping is tracked under `openspec/changes/<name>/`, finalize
it as part of the PR, not after the merge. This keeps the archived spec inside the
diff Copilot reviews.

## Steps

1. **Reconcile `tasks.md` with reality.** Check off completed tasks; add a short
   section recording PR-review follow-ups as they happen. Don't let checkmarks run
   ahead of the actual work — an over-checked tasks.md misleads both the reviewer and
   the next person to touch this.

2. **Archive (this also syncs the spec):**
   ```bash
   openspec archive <change-name> -y
   ```
   This moves `openspec/changes/<name>/` → `openspec/changes/archive/<date>-<name>/`
   and creates/updates `openspec/specs/<capability>/spec.md` from the change's delta
   spec. Git renders the move as renames.

3. **Read the synced spec before committing.** `openspec archive` is mechanical and
   leaves two things a human/reviewer will flag:
   - `## Purpose` is filled with a literal `TBD - created by archiving change ...`
     placeholder. Replace it with a real one-paragraph purpose.
   - The requirements can carry **scope drift** from when the delta spec was first
     written — e.g. a requirement scoped to one path/component when the feature
     actually covers several (a later section of the same spec often already says so).
     Reword so the spec doesn't contradict itself.
   Fix the same wording in the archived delta copy so the two stay consistent.

4. **Stage explicitly:**
   ```bash
   git add openspec/changes/<name>/ \
           openspec/changes/archive/<date>-<name>/ \
           openspec/specs/<capability>/
   ```

5. Commit as part of the ship branch. The repo's convention is to fold this archive
   into the same squash commit as the code, so the merged history has one commit that
   contains both the implementation and its archived spec.

## Why before the PR, restated

If you archive after merging, the `TBD` Purpose and any scope drift ship unreviewed —
and those are exactly what a spec reviewer catches. Archiving in-PR gets them a review
pass for free, and it's the reason the whole ship-pr order puts OpenSpec at step 1.
