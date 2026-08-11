# startup-update-discovery (delta)

## MODIFIED Requirements

### Requirement: Anonymous GitHub release discovery

The bridge SHALL use its own AOT-compatible HTTP client to query the public GitHub Releases REST API anonymously. It SHALL send the GitHub-required user-agent/API headers and paginate until the published release set is proven exhausted. One monotonic wall-clock deadline SHALL bound the complete discovery traversal, in addition to finite per-request timeouts. The client SHALL detect repeated pagination targets/pages and enforce a defensive maximum page count; reaching any overall bound before proving exhaustion SHALL discard all partial release results and fail open. Neither the bridge nor updater SHALL invoke or depend on the `gh` executable or another external downloader. DNS, TLS, HTTP, API rate-limit, per-request timeout, overall deadline, pagination-cycle/page-limit, cancellation unrelated to application shutdown, or JSON/schema failures SHALL be fail-open update-check failures: the bridge SHALL log a Warning and continue starting the current proxy without modifying installation files.

Because anonymous GitHub quota is keyed on the source IP and is therefore shared with every other client behind the same NAT, a rate-limit response describes the shared bucket at that instant rather than a verdict on this check. Discovery SHALL retry a rate-limit response for the same page a bounded number of times, spaced by a delay, and SHALL NOT start a retry whose delay would carry the traversal past the overall deadline. A refusal SHALL be treated as rate limiting only when the response reports an exhausted rate-limit counter or a retry-after signal; a forbidden response carrying neither SHALL fail open immediately without consuming retries. Retries SHALL be counted per page and SHALL NOT be treated as a pagination cycle, while a next link returning to an already-fetched page SHALL still be detected as one. Discovery SHALL issue its requests over connections that are not reused, so that a retry can be assigned a different egress address; a retry on a pinned connection re-selects the same exhausted quota bucket and cannot recover. Exhausting the retry allowance SHALL remain fail-open with the existing Warning.

#### Scenario: Public API works without GitHub CLI
- **WHEN** the machine has no `gh` executable and GitHub's public Releases API returns valid anonymous responses
- **THEN** the bridge discovers releases using ordinary HTTPS and continues the update decision normally

#### Scenario: More than one page is considered
- **WHEN** eligible releases extend beyond the first API page and traversal proves exhaustion within its bounds
- **THEN** the bridge follows pagination and compares all published releases rather than silently capping discovery

#### Scenario: Repeated pagination target fails open
- **WHEN** GitHub pagination repeats a previously visited next target or page
- **THEN** the bridge discards the partial release set, logs a Warning, and starts the current proxy

#### Scenario: Overall discovery deadline expires
- **WHEN** individual pages respond within their request timeouts but the complete traversal does not prove exhaustion before the overall deadline or defensive page limit
- **THEN** the bridge cancels traversal, discards all partial results, logs a Warning, and starts the current proxy

#### Scenario: Anonymous rate limit is exhausted
- **WHEN** GitHub returns a rate-limit response to every attempt for a page, including the full bounded retry allowance
- **THEN** the bridge logs a Warning that update checking failed and starts the current proxy

#### Scenario: Shared-bucket rate limit recovers on retry
- **WHEN** GitHub returns a rate-limit response to the anonymous request and a subsequent attempt within the retry allowance succeeds
- **THEN** the bridge uses the successful response and continues the update decision normally, without reporting a failed update check

#### Scenario: Forbidden response unrelated to rate limiting is not retried
- **WHEN** GitHub returns a forbidden response that reports neither an exhausted rate-limit counter nor a retry-after signal
- **THEN** the bridge fails open immediately without spending retry attempts on a refusal a retry cannot clear

#### Scenario: Secondary rate limit is retried
- **WHEN** GitHub signals a secondary rate limit with a retry-after signal instead of the primary counters
- **THEN** the bridge retries it like the primary rate limit

#### Scenario: Rate-limit retry is not mistaken for a pagination cycle
- **WHEN** discovery re-requests the same page URL because the previous attempt was rate-limited
- **THEN** the re-request proceeds instead of being reported as a repeated pagination target, and a next link pointing back at an already-fetched page is still detected as a cycle

#### Scenario: Rate-limit retry respects the overall deadline
- **WHEN** the retry allowance is not yet exhausted but waiting the retry delay would carry the traversal past the overall discovery deadline
- **THEN** the bridge stops retrying, logs a Warning, and starts the current proxy rather than delaying startup past the bound

#### Scenario: Discovery does not reuse connections
- **WHEN** discovery issues a request, including a rate-limit retry
- **THEN** it does not reuse a pooled connection from a previous request, so egress-address selection can differ between attempts

#### Scenario: Discovery times out
- **WHEN** the release request does not finish within its finite timeout
- **THEN** the bridge cancels the request, logs a Warning, and starts the current proxy

#### Scenario: Application shutdown cancels discovery
- **WHEN** the user cancels startup while discovery is in progress
- **THEN** cancellation propagates as shutdown rather than being converted into a warning followed by proxy startup

#### Scenario: Application shutdown cancels a pending rate-limit retry
- **WHEN** the user cancels startup while discovery is waiting to retry a rate-limited request
- **THEN** cancellation propagates as shutdown rather than being swallowed by the retry loop
