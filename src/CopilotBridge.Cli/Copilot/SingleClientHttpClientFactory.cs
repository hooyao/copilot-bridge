namespace CopilotBridge.Cli.Copilot;

/// <summary>
/// One-client <see cref="IHttpClientFactory"/> for the standalone CLI commands
/// (<c>auth</c>, <c>debug</c>) that run outside the server's DI container.
/// </summary>
/// <remarks>
/// Those commands are short-lived and single-shot, so pool isolation and handler
/// rotation buy nothing — every name resolves to the same client. It exists so
/// production types can depend on <see cref="IHttpClientFactory"/> (the shape the
/// long-running server needs) without forcing a throwaway command to build a
/// service provider.
/// <para>The caller owns and disposes the client it passes in. That is safe only
/// because this is NOT the pooling factory: nothing else shares the handler.</para>
/// </remarks>
internal sealed class SingleClientHttpClientFactory(HttpClient http) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => http;
}
