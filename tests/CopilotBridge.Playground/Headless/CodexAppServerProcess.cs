using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using CopilotBridge.Cli.Hosting.ClientConfig;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Drives the real headless <c>codex app-server</c> over JSONL stdio. Unlike
/// <c>codex exec</c>, app-server starts Codex's SQLite tracing layer, so a behavior
/// verdict can be scoped to the exact returned thread id in <c>logs_2.sqlite</c>.
/// </summary>
internal sealed record CodexAppServerInvocation(
    string BridgeBaseUrl,
    string Prompt,
    string Model,
    string CodexHome,
    string WorkingDirectory,
    string ExpectedCodexVersion,
    TimeSpan? Timeout = null,
    string? ModelReasoningEffort = null,
    string? ModelReasoningSummary = null,
    string? FollowUpPrompt = null,
    long? ModelContextWindow = null,
    long? ModelAutoCompactTokenLimit = null,
    long? StreamIdleTimeoutMs = null,
    int? RequestMaxRetries = null,
    int? StreamMaxRetries = null,
    string? ModelCatalogTemplateSlug = null,
    JsonArray? InjectedItems = null);

internal sealed record CodexAppServerResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    TimeSpan Duration,
    string DispatchLogPath,
    string ThreadId,
    string TurnStatus,
    string UserAgent,
    long StartedUnixSeconds,
    long EndedUnixSeconds);

internal static class CodexAppServerProcess
{
    public static async Task<CodexAppServerResult> RunAsync(
        CodexAppServerInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        var codexExe = CodexProcess.ResolveCodexExe(invocation.ExpectedCodexVersion);
        Directory.CreateDirectory(invocation.CodexHome);
        var dispatchHome = Path.Combine(
            ServeProcess.EvidenceRoot(), "client-logs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dispatchHome);
        var dispatchLog = Path.Combine(dispatchHome, "logs_2.sqlite");

        var connection = new BridgeConnection(new Uri(invocation.BridgeBaseUrl).Port);
        string config;
        if (invocation.ModelCatalogTemplateSlug is { Length: > 0 } templateSlug)
        {
            // A local startup catalog must remain authoritative for this diagnostic
            // case. Command auth would fetch /codex/models after startup and replace
            // the alias with the public exact-version catalog that omits this id.
            config =
                "model_provider = \"copilot-bridge\"\n\n"
                + "[features]\n"
                + "multi_agent = false\n"
                + "tool_suggest = false\n"
                + "plugins = false\n"
                + "remote_plugin = false\n\n"
                + "[model_providers.copilot-bridge]\n"
                + "name = \"copilot-bridge\"\n"
                + $"base_url = \"{connection.CodexBaseUrl}\"\n"
                + "wire_api = \"responses\"\n"
                + "env_key = \"BRIDGE_DUMMY_KEY\"\n";
            var catalogPath = Path.Combine(invocation.CodexHome, "model-catalog.json");
            await WriteAliasedBundledModelCatalogAsync(
                codexExe,
                invocation.CodexHome,
                templateSlug,
                invocation.Model,
                catalogPath,
                cancellationToken);
            var tomlPath = Path.GetFullPath(catalogPath).Replace('\\', '/');
            config = $"model_catalog_json = \"{tomlPath}\"\n" + config;
        }
        else
        {
            (config, _) = CodexConfigurator.BuildContent(
                original: null,
                connection,
                CodexProviderAuthInvocation.ResolveCurrent());
        }
        if (invocation.StreamIdleTimeoutMs is not null
            || invocation.RequestMaxRetries is not null
            || invocation.StreamMaxRetries is not null)
        {
            const string wireLine = "wire_api = \"responses\"\n";
            var providerValues = new StringBuilder(wireLine);
            if (invocation.StreamIdleTimeoutMs is { } streamIdleTimeoutMs)
                providerValues.Append("stream_idle_timeout_ms = ").Append(streamIdleTimeoutMs).Append('\n');
            if (invocation.RequestMaxRetries is { } requestMaxRetries)
                providerValues.Append("request_max_retries = ").Append(requestMaxRetries).Append('\n');
            if (invocation.StreamMaxRetries is { } streamMaxRetries)
                providerValues.Append("stream_max_retries = ").Append(streamMaxRetries).Append('\n');
            var rewritten = config.Replace(
                wireLine,
                providerValues.ToString(),
                StringComparison.Ordinal);
            if (ReferenceEquals(rewritten, config) || rewritten == config)
                throw new InvalidOperationException(
                    "Could not inject the isolated Codex provider timeout/retry values.");
            config = rewritten;
        }
        File.WriteAllText(Path.Combine(invocation.CodexHome, "config.toml"), config);

        var start = new ProcessStartInfo
        {
            FileName = codexExe,
            WorkingDirectory = invocation.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // JSONL stdio starts at byte zero. Encoding.UTF8 emits a BOM through the
            // redirected StreamWriter, which app-server correctly rejects as invalid
            // JSON before the initialize request.
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("app-server");
        start.ArgumentList.Add("--stdio");
        start.Environment["CODEX_HOME"] = invocation.CodexHome;
        start.Environment["CODEX_SQLITE_HOME"] = dispatchHome;
        start.Environment["BRIDGE_DUMMY_KEY"] = "dummy-bridge-bypass";
        start.Environment["RUST_LOG"] = "warn";
        start.Environment.Remove("CODEX_THREAD_ID");
        start.Environment.Remove("CODEX_INTERNAL_ORIGINATOR_OVERRIDE");

        using var process = new Process { StartInfo = start };
        var stopwatch = Stopwatch.StartNew();
        var startedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 2;
        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdout = new StringBuilder();
        var timeout = invocation.Timeout ?? TimeSpan.FromMinutes(12);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var token = timeoutSource.Token;

        try
        {
            await SendAsync(process, new JsonObject
            {
                ["method"] = "initialize",
                ["id"] = 1,
                ["params"] = new JsonObject
                {
                    // This reserved non-originating name leaves the real codex_cli_rs
                    // product/version prefix intact on /models while still completing
                    // the required app-server handshake.
                    ["clientInfo"] = new JsonObject
                    {
                        ["name"] = "codex_app_server_daemon",
                        ["title"] = "Copilot Bridge Behavior Harness",
                        ["version"] = "1.0.0",
                    },
                },
            }, token);
            var initialize = await ReadUntilAsync(process, stdout,
                message => ResponseId(message) == 1, token);
            var userAgent = initialize["result"]?["userAgent"]?.GetValue<string>()
                ?? throw new InvalidDataException("Codex initialize response did not include userAgent.");
            await SendAsync(process, new JsonObject { ["method"] = "initialized" }, token);

            var threadStartParams = new JsonObject
            {
                ["model"] = invocation.Model,
                ["modelProvider"] = "copilot-bridge",
                ["cwd"] = Path.GetFullPath(invocation.WorkingDirectory),
                ["approvalPolicy"] = "never",
                ["sandbox"] = "danger-full-access",
            };
            JsonObject? configOverrides = null;
            if (invocation.ModelReasoningEffort is { Length: > 0 } reasoningEffort)
            {
                configOverrides ??= [];
                configOverrides["model_reasoning_effort"] = reasoningEffort;
            }
            if (invocation.ModelReasoningSummary is { Length: > 0 } reasoningSummary)
            {
                configOverrides ??= [];
                configOverrides["model_reasoning_summary"] = reasoningSummary;
            }
            if (invocation.ModelContextWindow is { } contextWindow)
            {
                configOverrides ??= [];
                configOverrides["model_context_window"] = contextWindow;
            }
            if (invocation.ModelAutoCompactTokenLimit is { } autoCompactTokenLimit)
            {
                configOverrides ??= [];
                configOverrides["model_auto_compact_token_limit"] = autoCompactTokenLimit;
            }
            if (configOverrides is not null)
                threadStartParams["config"] = configOverrides;
            await SendAsync(process, new JsonObject
            {
                ["method"] = "thread/start",
                ["id"] = 2,
                ["params"] = threadStartParams,
            }, token);
            var threadStart = await ReadUntilAsync(process, stdout,
                message => ResponseId(message) == 2, token);
            var threadId = threadStart["result"]?["thread"]?["id"]?.GetValue<string>()
                ?? throw new InvalidDataException("Codex thread/start response did not include thread.id.");
            if (invocation.ModelReasoningEffort is { Length: > 0 } expectedEffort)
            {
                var actualEffort = threadStart["result"]?["reasoningEffort"]?.GetValue<string>();
                if (!string.Equals(actualEffort, expectedEffort, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"Codex thread/start reasoningEffort was '{actualEffort ?? "<null>"}', expected '{expectedEffort}'.");
            }

            var nextRequestId = 3;
            if (invocation.InjectedItems is { Count: > 0 } injectedItems)
            {
                var injectRequestId = nextRequestId++;
                await SendAsync(process, new JsonObject
                {
                    ["method"] = "thread/inject_items",
                    ["id"] = injectRequestId,
                    ["params"] = new JsonObject
                    {
                        ["threadId"] = threadId,
                        // A JsonNode may have only one parent. Clone the caller-owned
                        // array before attaching it to the JSON-RPC request.
                        ["items"] = injectedItems.DeepClone(),
                    },
                }, token);
                _ = await ReadUntilAsync(process, stdout,
                    message => ResponseId(message) == injectRequestId, token);
            }

            async Task<string> RunTurnAsync(int id, string prompt)
            {
                await SendAsync(process, new JsonObject
                {
                    ["method"] = "turn/start",
                    ["id"] = id,
                    ["params"] = new JsonObject
                    {
                        ["threadId"] = threadId,
                        ["input"] = new JsonArray
                        {
                            new JsonObject { ["type"] = "text", ["text"] = prompt },
                        },
                    },
                }, token);
                _ = await ReadUntilAsync(process, stdout,
                    message => ResponseId(message) == id, token);
                var completed = await ReadUntilAsync(process, stdout,
                    message => message["method"]?.GetValue<string>() == "turn/completed" &&
                        message["params"]?["threadId"]?.GetValue<string>() == threadId,
                    token);
                return completed["params"]?["turn"]?["status"]?.GetValue<string>()
                    ?? throw new InvalidDataException(
                        "Codex turn/completed notification did not include status.");
            }

            var turnStatus = await RunTurnAsync(nextRequestId++, invocation.Prompt);
            if (invocation.FollowUpPrompt is { Length: > 0 } followUpPrompt)
                turnStatus = await RunTurnAsync(nextRequestId, followUpPrompt);

            process.StandardInput.Close();
            var remaining = await process.StandardOutput.ReadToEndAsync(token);
            stdout.Append(remaining);
            if (!await WaitForExitAsync(process, TimeSpan.FromSeconds(30), token))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("codex app-server did not exit after stdin closed.");
            }
            var stderr = await stderrTask;
            stopwatch.Stop();
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            var endedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return new CodexAppServerResult(
                process.ExitCode,
                stdout.ToString(),
                stderr,
                stopwatch.Elapsed,
                dispatchLog,
                threadId,
                turnStatus,
                userAgent,
                startedUnix,
                endedUnix);
        }
        catch
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { }
            throw;
        }
    }

    private static async Task SendAsync(Process process, JsonObject message, CancellationToken token)
    {
        await process.StandardInput.WriteLineAsync(message.ToJsonString().AsMemory(), token);
        await process.StandardInput.FlushAsync(token);
    }

    /// <summary>
    /// Builds a one-model startup catalog from the exact real client's bundled
    /// metadata, changing only the slug/display name. This lets a candidate model
    /// exercise a client tool mode that its own upstream-only id does not yet carry
    /// in OpenAI's public catalog; the model id on every request remains the target.
    /// </summary>
    private static async Task WriteAliasedBundledModelCatalogAsync(
        string codexExe,
        string codexHome,
        string templateSlug,
        string targetSlug,
        string destination,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = codexExe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("debug");
        start.ArgumentList.Add("models");
        start.ArgumentList.Add("--bundled");
        start.Environment["CODEX_HOME"] = codexHome;
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Failed to start codex debug models.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"codex debug models --bundled exited {process.ExitCode}: {error.Trim()}");

        var root = JsonNode.Parse(output)?.AsObject()
            ?? throw new InvalidDataException("Codex bundled catalog was not a JSON object.");
        var models = root["models"]?.AsArray()
            ?? throw new InvalidDataException("Codex bundled catalog had no models array.");
        var template = models.OfType<JsonObject>().SingleOrDefault(model =>
                string.Equals(model["slug"]?.GetValue<string>(), templateSlug, StringComparison.Ordinal))
            ?? throw new InvalidDataException(
                $"Codex bundled catalog had no template model {templateSlug}.");
        var target = (JsonObject)template.DeepClone();
        target["slug"] = targetSlug;
        target["display_name"] = targetSlug;
        var catalog = new JsonObject { ["models"] = new JsonArray(target) };
        File.WriteAllText(destination, catalog.ToJsonString());
    }

    private static async Task<JsonNode> ReadUntilAsync(
        Process process,
        StringBuilder captured,
        Func<JsonNode, bool> predicate,
        CancellationToken token)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(token)
                ?? throw new EndOfStreamException("codex app-server stdout closed before the expected message.");
            captured.AppendLine(line);
            var message = JsonNode.Parse(line)
                ?? throw new InvalidDataException("codex app-server emitted an empty JSONL message.");
            if (message["error"] is not null && message["id"] is not null)
                throw new InvalidDataException($"codex app-server JSON-RPC error: {message["error"]}");
            if (predicate(message)) return message;
        }
    }

    private static int? ResponseId(JsonNode message) =>
        message["id"] is JsonValue id && id.TryGetValue<int>(out var value) ? value : null;

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken token)
    {
        var wait = process.WaitForExitAsync(token);
        var completed = await Task.WhenAny(wait, Task.Delay(timeout, token));
        if (completed != wait) return false;
        await wait;
        return true;
    }
}
