# The Copilot review loop — rationale & mechanics

This is the part of shipping a PR that is deceptively easy to get wrong. Everything
here comes from a real session where each of these was gotten wrong at least once.

## Wait with `/loop`, never a hand-rolled polling loop

The instinct is to write a background `while sleep 20; do gh ...; done`. Resist it.
Reasons it fails in practice:

- **`gh` can fail silently in a sandboxed/background shell** (no auth, no network).
  A `gh ... || echo 0` then feeds a fake `0` into your condition forever. The
  loop "succeeds" at watching nothing.
- **You have to guess a timeout.** Too short and you give up before Copilot posts;
  too long and a stuck job hangs you.
- **A background poller can't act** — it can only notify, and then you still have to
  come back and do the work.

Instead, let the harness re-invoke you between checks. `/loop 3m /ship-pr continue #<N>`
wakes you every few minutes; each wake runs ONE `pr-status.sh` and either acts or
returns to waiting. The dynamic-pacing variant (ScheduleWakeup) works too when you
want to self-pace. Either way there's no sleep loop, no swallowed error, no guessed
timeout, and each tick can actually DO something.

## Poll the right signal: unresolved review threads

Copilot's review does NOT reliably show up as an entry in the `reviews` REST/GraphQL
collection with a body — that collection stays empty and if you poll its length you
wait forever. What Copilot actually produces:

- **inline review comments** (these are what you read and address), and
- a **`reviewed` timeline event** (useful only as a round counter).

So the signal to poll is **the count of unresolved review threads**, via GraphQL
`reviewThreads { isResolved }`. `pr-status.sh` does exactly this and nothing else
as its primary output.

## Resolve after every reply — or the count lies

If you reply to a comment but don't resolve its thread, it stays in the open set.
Next poll, `OPEN_COMMENTS` is still non-zero and you can't tell addressed-from-new.
The tempting workaround — "only look at comments newer than my last push" — is a
trap: **GitHub re-anchors old comments onto the newest commit when you push**, so
an old comment's `commit_id` becomes your new SHA while its `created_at` stays old.
Timestamp filtering therefore both misses real-new comments and resurfaces old ones.
The only stable signal is resolved-vs-unresolved, and you own that by resolving
every comment right after you reply. `reply-resolve.sh` does both in one call.

## Fix or refute — but decide honestly

Not every Copilot comment is a real problem. For each: either **fix it** (change
the code/docs) or **refute it** (reply explaining why it's a false positive), then
resolve. Don't reflexively "fix" a non-issue into a worse state, and don't
reflexively dismiss — read the comment, form a judgment, and record the reasoning
in the reply. A genuinely uncertain call is worth surfacing to the user.

## Re-request review after each fix round; bound the loop

After pushing a round of fixes, re-request Copilot review so it re-reads the new
commit. Copilot reviews a PR a bounded number of times (~5). Count `reviewed`
events (`ROUND_HINT`); at round 5 with comments still open, stop and bring the
remainder to the user with your recommendation rather than spinning.

## Distinguishing "satisfied" from "still working"

`OPEN_COMMENTS=0` alone isn't "done" — it could mean Copilot hasn't reviewed your
latest push yet. Confirm a `reviewed` event landed AFTER your last fix push (round
count went up) with zero open comments. A review that lands with no new comments is
Copilot's way of saying "no more findings" — that's your green light.
