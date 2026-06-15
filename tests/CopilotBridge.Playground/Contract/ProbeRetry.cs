using System.Net;

namespace CopilotBridge.Playground.Contract;

/// <summary>
/// Retries a single live probe call across transient TRANSPORT failures (socket
/// reset, TLS read error, timeout) — NOT across HTTP status codes, which are the
/// contract facts the snapshot records. A backend sweep makes ~100 sequential
/// calls, so one network hiccup must not abort the whole run and lose the
/// snapshot; but a 4xx/5xx must flow through unchanged so acceptance/rejection
/// stays truthful.
/// </summary>
internal static class ProbeRetry
{
    /// <summary>
    /// Invoke <paramref name="call"/> (a single Try*Async returning
    /// status+body), retrying up to <paramref name="attempts"/> times on
    /// <see cref="HttpRequestException"/> / <see cref="IOException"/> /
    /// <see cref="TaskCanceledException"/> (transport-level), with a short linear
    /// backoff. The returned status/body is whatever the call produced — a 4xx
    /// reject is a success here (it's a contract fact), only a thrown transport
    /// error triggers a retry.
    /// </summary>
    public static async Task<(HttpStatusCode Status, string Body)> WithRetry(
        Func<Task<(HttpStatusCode, string)>> call,
        string context,
        int attempts = 3)
    {
        Exception? last = null;
        for (var i = 1; i <= attempts; i++)
        {
            try
            {
                return await call();
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                last = ex;
                if (i < attempts)
                    await Task.Delay(TimeSpan.FromSeconds(2 * i));
            }
        }
        throw new InvalidOperationException(
            $"{context}: transport failed after {attempts} attempts (last: {last?.GetType().Name}: {last?.Message})",
            last);
    }

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException
           or IOException
           or TaskCanceledException // HttpClient timeout surfaces as this
        || ex.InnerException is System.Net.Sockets.SocketException;
}
