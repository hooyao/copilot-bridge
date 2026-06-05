using System.Security.Authentication;

namespace CopilotBridge.Cli.Copilot;

/// <summary>
/// Classifies exceptions thrown while talking to Copilot as transient
/// connection-layer failures (DNS / refused / reset / SSL handshake / premature
/// EOF) versus genuine errors. These are the
/// <c>net_http_client_execution_error</c> / <c>net_http_ssl_connection_failed</c>
/// family — upstream or network hiccups, NOT bugs in the bridge's own code.
///
/// Used in two places:
/// <list type="bullet">
///   <item><see cref="CopilotClient"/> retries a transient failure that occurs
///         <i>before</i> response headers are read (the request body was never
///         processed upstream, so a retry is idempotent).</item>
///   <item>The messages endpoint maps a transient failure that escapes the
///         retry budget to a 502 + a single Warning line (no stack trace), so
///         an operator isn't misled into hunting a regression.</item>
/// </list>
/// </summary>
internal static class TransientUpstreamError
{
    /// <summary>
    /// True if <paramref name="ex"/> (or any exception in its inner chain) is a
    /// connection-layer / network failure rather than an application error.
    /// </summary>
    public static bool Is(Exception ex)
    {
        // These failures arrive wrapped through several layers
        // (HttpRequestException → IOException → SocketException etc.), so walk
        // the whole chain.
        for (var current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                // Premature EOF reading the response body — Copilot closed the
                // socket while we were mid-stream.
                case HttpIOException:
                // SSL handshake failures (net_http_ssl_connection_failed) and
                // generic request-level connectivity issues (DNS / refused /
                // reset). HttpRequestException itself can carry either; accept
                // it as a class regardless of inner type since every member of
                // this family is "upstream / network", not a bug in our path.
                case HttpRequestException:
                case System.Net.Sockets.SocketException:
                case AuthenticationException:
                    return true;
                // The underlying "net_io_eof" / "ResponseEnded" forms that
                // surface as a plain IOException.
                case IOException io when io.Message.Contains("eof", StringComparison.OrdinalIgnoreCase)
                                         || io.Message.Contains("premature", StringComparison.OrdinalIgnoreCase)
                                         || io.Message.Contains("ResponseEnded", StringComparison.OrdinalIgnoreCase):
                    return true;
            }
        }
        return false;
    }
}
