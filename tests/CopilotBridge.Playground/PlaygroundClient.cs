using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Copilot;

namespace CopilotBridge.Playground;

/// <summary>
/// Minimal CAPI client for ad-hoc experiments. Reuses the bridge's
/// <see cref="AuthService"/> and <see cref="CopilotHeaderFactory"/> so what we
/// learn here translates directly to bridge code. Keeps payloads as raw JSON
/// strings — the whole point is to iterate on wire format without fighting DTOs.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PlaygroundClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly AuthService _auth;
    private readonly CopilotHeaderFactory _headers;

    public PlaygroundClient()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("copilot-playground/0.1");
        _auth = new AuthService(_http);
        _headers = new CopilotHeaderFactory();
    }

    /// <summary>POST <c>/v1/messages</c> with the given JSON body. Returns the raw response body.</summary>
    public async Task<string> PostMessagesAsync(
        string jsonBody,
        bool vision = false,
        string? anthropicBeta = null,
        CancellationToken ct = default)
    {
        var (status, body) = await TryPostMessagesAsync(jsonBody, vision, anthropicBeta, ct);
        if ((int)status >= 400)
        {
            Console.Error.WriteLine($"HTTP {(int)status} {status}");
            Console.Error.WriteLine(body);
            throw new HttpRequestException($"Copilot returned {(int)status}");
        }
        return body;
    }

    /// <summary>
    /// Like <see cref="PostMessagesAsync"/> but returns the status code instead
    /// of throwing on non-2xx — for experiments that need to OBSERVE failure
    /// modes (e.g. beta-header acceptance probes).
    /// </summary>
    public async Task<(System.Net.HttpStatusCode Status, string Body)> TryPostMessagesAsync(
        string jsonBody,
        bool vision = false,
        string? anthropicBeta = null,
        CancellationToken ct = default)
    {
        var token = await _auth.GetCopilotTokenAsync(ct);
        var baseUrl = _auth.CopilotApiBaseUrl
            ?? throw new InvalidOperationException("CopilotApiBaseUrl is unknown after token fetch.");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/messages");
        _headers.ApplyTo(req, token, vision);
        if (anthropicBeta is not null)
            req.Headers.TryAddWithoutValidation("anthropic-beta", anthropicBeta);
        req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return (resp.StatusCode, body);
    }

    /// <summary>
    /// POST <c>/v1/messages/count_tokens</c>. Returns status + body without
    /// throwing so a probe can OBSERVE whether Copilot exposes the endpoint at
    /// all (expected: 404 / 405 / similar — every reference impl works around its
    /// absence rather than proxying it).
    /// </summary>
    public async Task<(System.Net.HttpStatusCode Status, string Body)> TryPostCountTokensAsync(
        string jsonBody,
        CancellationToken ct = default)
    {
        var token = await _auth.GetCopilotTokenAsync(ct);
        var baseUrl = _auth.CopilotApiBaseUrl
            ?? throw new InvalidOperationException("CopilotApiBaseUrl is unknown after token fetch.");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/messages/count_tokens");
        _headers.ApplyTo(req, token);
        req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return (resp.StatusCode, body);
    }

    /// <summary>
    /// POST <c>/responses</c> (Copilot's native OpenAI Responses endpoint, the
    /// Codex backend) with the given JSON body. Returns status + body without
    /// throwing so Track-A probes can OBSERVE acceptance/rejection. Uses the same
    /// official-VS-Code header set as <c>/v1/messages</c> (the header factory is
    /// endpoint-agnostic — no anthropic-version). Caller controls
    /// <c>"stream"</c> in the body; for non-streaming probes set it false/omit.
    /// </summary>
    public async Task<(System.Net.HttpStatusCode Status, string Body)> TryPostResponsesAsync(
        string jsonBody,
        bool vision = false,
        CancellationToken ct = default)
    {
        var token = await _auth.GetCopilotTokenAsync(ct);
        var baseUrl = _auth.CopilotApiBaseUrl
            ?? throw new InvalidOperationException("CopilotApiBaseUrl is unknown after token fetch.");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/responses");
        _headers.ApplyTo(req, token, vision);
        req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return (resp.StatusCode, body);
    }

    /// <summary>
    /// POST <c>/responses</c> with <c>stream:true</c> and read the RAW SSE text
    /// (no parser) — to capture the exact event sequence Copilot emits and spot
    /// any non-spec terminator (e.g. <c>[DONE]</c>). Returns status + raw body;
    /// on non-2xx the body is the error payload (does not throw). Caller must set
    /// <c>"stream": true</c> in the JSON body.
    /// </summary>
    public async Task<(System.Net.HttpStatusCode Status, string RawBody)> TryPostResponsesRawStreamAsync(
        string jsonBody,
        CancellationToken ct = default)
    {
        var token = await _auth.GetCopilotTokenAsync(ct);
        var baseUrl = _auth.CopilotApiBaseUrl
            ?? throw new InvalidOperationException("CopilotApiBaseUrl is unknown after token fetch.");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/responses");
        _headers.ApplyTo(req, token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var raw = await reader.ReadToEndAsync(ct);
        return (resp.StatusCode, raw);
    }

    /// <summary>
    /// Sends an arbitrary HTTP request to a Copilot-relative path. Generic
    /// escape hatch for one-shot gap probes (e.g. <c>GET /v1/files</c>,
    /// <c>POST /v1/messages/batches</c>) without adding a dedicated method per
    /// endpoint. Returns status + body without throwing.
    /// </summary>
    public async Task<(System.Net.HttpStatusCode Status, string Body)> TryRequestAsync(
        HttpMethod method,
        string relativePath,
        string? jsonBody = null,
        CancellationToken ct = default)
    {
        var token = await _auth.GetCopilotTokenAsync(ct);
        var baseUrl = _auth.CopilotApiBaseUrl
            ?? throw new InvalidOperationException("CopilotApiBaseUrl is unknown after token fetch.");

        using var req = new HttpRequestMessage(method, $"{baseUrl}{relativePath}");
        _headers.ApplyTo(req, token);
        if (jsonBody is not null)
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return (resp.StatusCode, body);
    }

    /// <summary>
    /// POST <c>/v1/messages</c> with <c>stream:true</c> and read the response
    /// body as RAW TEXT — no <see cref="SseParser"/>, no event framing logic.
    /// Returns the entire concatenated SSE payload exactly as Copilot put it on
    /// the wire. Used to determine whether a field present/absent in the
    /// bridge's parsed output was already present/absent in the raw upstream
    /// bytes (i.e. to tell a parser bug apart from an upstream bug). The caller
    /// must set <c>"stream": true</c> in the JSON body.
    /// </summary>
    public async Task<string> PostMessagesRawStreamAsync(
        string jsonBody,
        CancellationToken ct = default)
    {
        var token = await _auth.GetCopilotTokenAsync(ct);
        var baseUrl = _auth.CopilotApiBaseUrl
            ?? throw new InvalidOperationException("CopilotApiBaseUrl is unknown after token fetch.");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/messages");
        _headers.ApplyTo(req, token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct);
            Console.Error.WriteLine($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
            Console.Error.WriteLine(errBody);
            throw new HttpRequestException($"Copilot returned {(int)resp.StatusCode}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }

    /// <summary>
    /// POST <c>/v1/messages</c> with <c>stream:true</c>; yields each SSE item as it arrives.
    /// Caller is responsible for setting <c>"stream": true</c> in the JSON body.
    /// </summary>
    public async IAsyncEnumerable<SseItem<string>> PostMessagesStreamAsync(
        string jsonBody,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var token = await _auth.GetCopilotTokenAsync(ct);
        var baseUrl = _auth.CopilotApiBaseUrl
            ?? throw new InvalidOperationException("CopilotApiBaseUrl is unknown after token fetch.");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/messages");
        _headers.ApplyTo(req, token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            Console.Error.WriteLine($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
            Console.Error.WriteLine(body);
            throw new HttpRequestException($"Copilot returned {(int)resp.StatusCode}");
        }

        var stream = await resp.Content.ReadAsStreamAsync(ct);
        var parser = SseParser.Create(stream);
        await foreach (var item in parser.EnumerateAsync(ct))
        {
            yield return item;
        }
    }

    public static string PrettyJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, PrettyOptions);
        }
        catch
        {
            return json;
        }
    }

    private static readonly JsonSerializerOptions PrettyOptions = new() { WriteIndented = true };

    public void Dispose()
    {
        _auth.Dispose();
        _http.Dispose();
    }
}
