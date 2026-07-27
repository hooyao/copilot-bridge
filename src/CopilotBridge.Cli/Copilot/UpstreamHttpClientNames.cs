namespace CopilotBridge.Cli.Copilot;

/// <summary>
/// Names of the <see cref="IHttpClientFactory"/> clients the bridge registers —
/// one per upstream surface, so each gets its own connection pool.
/// </summary>
/// <remarks>
/// <para>The two <b>model</b> surfaces and the metadata surface share the Copilot
/// API origin, so one pooled handler would put them in a single connection budget
/// — and this bridge holds each connection open for <i>minutes</i> while a model
/// thinks. A burst on one surface would then stall the others.</para>
/// <para>Auth is separated for a different reason: it targets <c>github.com</c> /
/// <c>api.github.com</c>, so it never shared the model surfaces' connections
/// anyway. It gets its own client because its <b>timeout policy and lifecycle</b>
/// differ — short token calls on a background refresh timer, which must fail fast
/// rather than inherit the unbounded posture the model surfaces need. A token that
/// cannot refresh in time stalls the whole bridge.</para>
/// <para>Consumers inject <see cref="IHttpClientFactory"/> and call
/// <c>CreateClient(name)</c> where they need it. They must NOT stash the result in
/// a field: that pins one pooled handler for the object's lifetime and defeats the
/// factory's rotation, which is the reason to use it at all. <c>CreateClient</c> is
/// cheap — a thin wrapper over the already-pooled handler.</para>
/// </remarks>
internal static class UpstreamHttpClientNames
{
    /// <summary>
    /// Copilot's native Anthropic <b>turn</b> surface: <c>/v1/messages</c>. Serves
    /// the Claude Code path, whose streaming responses can stay open for many
    /// minutes, so it carries no whole-request timeout — the configured
    /// <c>Pipeline:UpstreamTimeout</c> budgets bound it instead.
    /// </summary>
    public const string Anthropic = "copilot-anthropic";

    /// <summary>
    /// Copilot's Responses surface: <c>/responses</c>. Serves the Codex path.
    /// Isolated from <see cref="Anthropic"/> so a saturated Codex path cannot
    /// starve Claude Code of connections, or the reverse. Same no-whole-request-cap
    /// rule, for the same reason.
    /// </summary>
    public const string Responses = "copilot-responses";

    /// <summary>
    /// Short Copilot <b>metadata</b> calls that are not model turns:
    /// <c>/models</c> and <c>/v1/messages/count_tokens</c>.
    /// </summary>
    /// <remarks>
    /// These do NOT go through the first-byte / stream-idle budgets (they are not
    /// turn forwards), so without a client-level timeout they would have no bound
    /// at all and could hang indefinitely. They are short by nature, so a finite
    /// cap is both safe and necessary — this is deliberately the one model-host
    /// client that keeps one.
    /// </remarks>
    public const string Metadata = "copilot-metadata";

    /// <summary>
    /// GitHub OAuth device-code flow and Copilot token exchange/refresh. Targets
    /// <c>github.com</c> / <c>api.github.com</c> rather than the Copilot API origin,
    /// and is separated for its <b>timeout policy</b>: these are short requests on a
    /// background refresh timer, so unlike the model surfaces this client keeps a
    /// finite timeout — a hung auth call should fail fast, not hang forever, and a
    /// token that cannot refresh in time stalls the whole bridge.
    /// </summary>
    public const string GitHubAuth = "github-auth";
}
