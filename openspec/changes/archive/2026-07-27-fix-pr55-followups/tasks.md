# Follow-ups from PR #55 review (rounds 8+)

> Status: **all done** (2026-07-27). Landed together with a rewrite of the
> startup timeout report, which the operator found unreadable as four prose lines.

These were raised by Copilot on PR #55 after the merge decision and deferred by
the operator ("可以之后单独修"). They are recorded here so the merge does not lose
them. Each is a real finding, verified against the code — none is a false
positive.

Source: https://github.com/hooyao/copilot-bridge/pull/55 (comments listed below).

## 1. `IHttpClientFactory` contradicts the architectural contract — AND has no size measurement

**Comment 3656233733.** The highest-priority item.

`docs/design.md` §2.2 lists `IHttpClientFactory` under **Forbidden**:

> `IHttpClientFactory` (default impl is AOT-friendly but adds size; a singleton
> `HttpClient` is enough)

and `docs/pipeline-design.md` still documents a singleton `HttpClient` in the
Singleton services list. PR #55 introduced the factory anyway. Separately,
`docs/design.md` §2.3 requires a measured `.exe` size entry in
`docs/size-history.md` after **every** dependency change; this change has none.

My PR comment asserted "no AOT/size cost" because `Microsoft.Extensions.Http`
ships in the ASP.NET shared framework. That argument is about **packages**, not
about **reachable code** — it does not establish zero size delta, and the project
requires a number, not an argument.

Required:

1. AOT-publish before/after and record the real delta in `docs/size-history.md`
   (baseline: the 2026-07-13 `add-startup-auto-update` row, 13.12 MB win-x64).
2. Then either:
   - **reconcile the contract** — amend `docs/design.md` §2.2 and the
     `pipeline-design.md` singleton list to permit the factory, justified by the
     measured delta and the pool-isolation requirement; or
   - **revert to a lightweight implementation** — keep per-surface isolation with
     hand-held `HttpClient` instances (one per upstream surface) and drop the
     factory, if the delta is not worth it.

Do not leave the code and the contract disagreeing.

## 2. Pool-isolation test omits the metadata surface

**Comment 3656233772.** `EachUpstreamSurface_GetsItsOwnConnectionPool` asserts
distinct handlers for `Anthropic` / `Responses` / `GitHubAuth`, but not
`Metadata` — which is the *other same-origin Copilot surface* the split exists to
isolate. Auth targets different GitHub origins and was never at risk. So
accidentally mapping `Metadata` back onto a model client would leave the test
green.

Add `UpstreamHttpClientNames.Metadata` to that test's name list.

## 3. The finite metadata timeout is not covered by a test

**Comment 3656233791.** `AMetadataStyleCall_StaysBoundedByItsClientTimeout` builds
its own `HttpClient` and never touches the named registration, and
`ModelSurfaces_HaveNoCoarseRequestTimeout_ButAuthDoes` does not assert
`Metadata`. Changing the `copilot-metadata` registration to
`Timeout.InfiniteTimeSpan` would leave both green and silently reintroduce the
unbounded `/models` + `count_tokens` failure that finding round 1 raised.

Assert the finite timeout on the *registered* `Metadata` client, and
mutation-check it.

## 4. A test comment still repeats the disproven body-timeout claim

**Comment 3656233811.** `DetectorCompositionTests` (~line 223) still says the
coarse cap bounds the buffered body. Both model sends use `ResponseHeadersRead`,
so it ended at headers in both modes — as this PR's own real-socket test
(`UnderResponseHeadersRead_TheClientTimeoutDoesNotCoverTheBodyRead`) verifies.
Last surviving instance of an error corrected everywhere else.

## 5. `config status` reports timeout drift without showing the expected value

**Comment 3656270201.** The `client-autoconfiguration` spec requires drift output
to "show both values", but `ClaudeCodeConfigurator.Read` puts only the *current*
timeout values in `Details`, and `ConfigCommand.Status` prints an expected value
only for the base URL. So a timeout-driven `DRIFTED` tells the operator something
is wrong without telling them the derived target to set — the one number they
need.

Print the expected value beside each current one, and add an output-level test
(the existing drift tests assert the `Drifted` flag, not the rendered output,
which is why this gap survived).

## Tasks

- [x] 1.1 AOT-publish before/after and record the `IHttpClientFactory` size delta in `docs/size-history.md` (baseline: 2026-07-13 `add-startup-auto-update`, 13.12 MB win-x64).
- [x] 1.2 Based on that number, either amend `docs/design.md` §2.2 + the `pipeline-design.md` singleton list to permit the factory, or revert to hand-held per-surface `HttpClient` instances. Do not leave code and contract disagreeing.
- [x] 2.1 Add `UpstreamHttpClientNames.Metadata` to `EachUpstreamSurface_GetsItsOwnConnectionPool`.
- [x] 3.1 Assert the finite timeout on the REGISTERED `copilot-metadata` client (not a hand-built one) and mutation-check it by flipping the registration to `InfiniteTimeSpan`.
- [x] 4.1 Fix the `DetectorCompositionTests` comment that still claims the coarse cap bounded the buffered body.
- [x] 5.1 Print the expected timeout beside each current value in `config status`, and add an output-level test (existing tests assert the `Drifted` flag, not the rendered output).

