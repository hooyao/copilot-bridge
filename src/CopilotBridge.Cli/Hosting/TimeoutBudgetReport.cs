using System.Globalization;
using System.Text;
using CopilotBridge.Cli.Hosting.ClientConfig;
using CopilotBridge.Cli.Hosting.Options;
using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Hosting;

/// <summary>Read-only startup inventory of bridge and observable global client timeout facts.</summary>
internal static class TimeoutBudgetReport
{
    public static void Emit(
        UpstreamTimeoutOptions budgets,
        ILogger log,
        string? settingsPathOverride = null,
        bool wholeResponseBuffering = false,
        string? codexConfigPathOverride = null,
        UpstreamRetryOptions? retryOptions = null,
        string? claudeVersionOverride = null)
    {
        var claudeVersion = claudeVersionOverride ?? ClaudeCodeVersionProbe.TryRead();
        var claude = ClaudeCodeTimeoutReader.Read(
            settingsPathOverride,
            installedVersion: claudeVersion);
        var codex = CodexTimeoutReader.Read(codexConfigPathOverride);
        var retries = retryOptions ?? new UpstreamRetryOptions();

        var text = new StringBuilder(1_600)
            .AppendLine("Timeouts (observed configuration; startup does not rewrite values):")
            .AppendLine("  Bridge — appsettings.json")
            .Append("    upstream response headers  ").Append(FormatBridgeSeconds(budgets.FirstByteTimeoutSeconds)).AppendLine(" / send attempt")
            .Append("    upstream SSE event gap     ").Append(FormatBridgeSeconds(budgets.StreamIdleTimeoutSeconds)).AppendLine(" / parsed event gap")
            .Append("    downstream keepalive       ").AppendLine(DescribeKeepAlive(budgets.KeepAliveIntervalSeconds))
            .Append("    network retries            ").AppendLine(retries.MaxRetries.ToString(CultureInfo.InvariantCulture))
            .AppendLine("    buffered body              no limit after headers")
            .Append("  Claude Code — ").Append(claude.SettingsPath).AppendLine(" (global only)")
            .Append("    SSE event idle             ").AppendLine(DescribeDuration(claude.EventIdle, claude.Reason))
            .Append("    SSE byte idle              ").AppendLine(DescribeDuration(claude.ByteIdle))
            .Append("    request timeout            ").AppendLine(DescribeClaudeRequest(claude))
            .AppendLine("    retries                    not visible at bridge startup")
            .Append("  Codex — ").Append(codex.ConfigPath).AppendLine(" (global only)")
            .Append("    SSE event idle             ").Append(DescribeDuration(codex.EventIdle, codex.Reason)).AppendLine(" / parsed event")
            .Append("    request retries            ").AppendLine(DescribeCount(codex.RequestRetries))
            .Append("    stream retries             ").AppendLine(DescribeCount(codex.StreamRetries))
            .AppendLine("    whole request              no limit")
            .AppendLine("  note: timeouts apply per attempt; a retry starts a new attempt, so there is no fixed whole-turn limit")
            .AppendLine("  scope: global client configs only; project/profile/CLI/env overrides are not included")
            .Append("  * = client built-in default")
            .ToString();

        log.LogInformation("{TimeoutInventory}", text);

        var keepAliveCanReach = KeepAliveCanReach(budgets, wholeResponseBuffering);
        WarnIfObservedClientRacesOrPrecedes(
            log,
            "Claude Code",
            claude.SettingsPath,
            ShortestClaudeIdle(claude),
            budgets,
            keepAliveCanReach,
            wholeResponseBuffering);
        WarnIfObservedClientRacesOrPrecedes(
            log,
            "Codex",
            codex.ConfigPath,
            codex.EventIdle,
            budgets,
            keepAliveCanReach,
            wholeResponseBuffering);
    }

    private static string DescribeKeepAlive(int seconds) =>
        seconds <= 0
            ? $"disabled ({seconds}s)"
            : $"{FormatDuration((long)seconds * 1000L)}, after first upstream event";

    private static string FormatBridgeSeconds(int seconds) =>
        seconds <= 0
            ? $"disabled ({seconds}s)"
            : FormatDuration((long)seconds * 1000L);

    internal static string DescribeDuration(ClientDurationValue value)
    {
        return value.Source switch
        {
            ClientValueSource.BuiltIn when value.EffectiveMs is { } ms => $"unset -> {FormatDuration(ms)}*",
            ClientValueSource.Explicit when value.ConfiguredMs is { } configured
                                                  && value.EffectiveMs is { } effective
                                                  && configured != effective =>
                $"configured {FormatDuration(configured)} -> effective {FormatDuration(effective)} ({value.Detail ?? "client rule"})",
            ClientValueSource.Explicit when value.EffectiveMs is { } ms => $"{FormatDuration(ms)}, explicit",
            ClientValueSource.Explicit when value.ConfiguredMs is { } configured =>
                $"configured {FormatDuration(configured)}; {value.Detail ?? "effective behavior unknown"}",
            ClientValueSource.Inherited when value.EffectiveMs is { } ms =>
                $"unset -> {FormatDuration(ms)} ({value.Detail ?? "inherited"})",
            ClientValueSource.Disabled => "disabled, explicit",
            ClientValueSource.Invalid => $"invalid ({value.RawValue ?? "unknown value"})",
            ClientValueSource.Unknown when value.Detail is not null => $"unknown ({value.Detail})",
            _ => "unknown",
        };
    }

    private static string DescribeDuration(ClientDurationValue value, string? unknownReason) =>
        value.Source == ClientValueSource.Unknown && unknownReason is not null
            ? $"unknown ({unknownReason})"
            : DescribeDuration(value);

    private static string DescribeClaudeRequest(ClientTimeoutSnapshot snapshot)
    {
        if (!snapshot.Readable) return "unknown";
        if (snapshot.NormalRequest.Source == ClientValueSource.BuiltIn
            && snapshot.AfterStreamErrorRequest.Source == ClientValueSource.BuiltIn)
        {
            return $"unset -> normal {FormatDuration(snapshot.NormalRequest.EffectiveMs!.Value)}*; "
                   + $"after stream error {FormatDuration(snapshot.AfterStreamErrorRequest.EffectiveMs!.Value)}*";
        }
        if (snapshot.NormalRequest.Source == ClientValueSource.Explicit)
            return DescribeDuration(snapshot.NormalRequest);
        return DescribeDuration(snapshot.NormalRequest);
    }

    private static string DescribeCount(ClientCountValue value) => value.Source switch
    {
        ClientValueSource.BuiltIn when value.Effective is { } count => $"unset -> {count}*",
        ClientValueSource.Explicit when value.Effective is { } count => $"{count}, explicit",
        ClientValueSource.Invalid => $"invalid ({value.RawValue ?? "unknown value"})",
        _ => "unknown",
    };

    internal static string FormatDuration(long milliseconds)
    {
        if (milliseconds == 0) return "0s";
        if (milliseconds % 60_000 == 0) return $"{milliseconds / 60_000}m";
        if (milliseconds % 1_000 == 0) return $"{milliseconds / 1_000}s";
        return (milliseconds / 1000m).ToString("0.###", CultureInfo.InvariantCulture) + "s";
    }

    private static bool KeepAliveCanReach(UpstreamTimeoutOptions budgets, bool wholeResponseBuffering) =>
        budgets.KeepAliveIntervalSeconds > 0
        && !wholeResponseBuffering
        && (budgets.StreamIdleTimeoutSeconds <= 0
            || budgets.KeepAliveIntervalSeconds < budgets.StreamIdleTimeoutSeconds);

    private static ClientDurationValue ShortestClaudeIdle(ClientTimeoutSnapshot snapshot)
    {
        if (!snapshot.Readable)
            return new("Claude idle", null, null, null, ClientValueSource.Unknown);

        var active = new[] { snapshot.EventIdle, snapshot.ByteIdle }
            .Where(v => v.EffectiveMs is not null)
            .OrderBy(v => v.EffectiveMs)
            .ToArray();
        if (active.Length > 0) return active[0];
        if (snapshot.EventIdle.Source == ClientValueSource.Disabled
            && snapshot.ByteIdle.Source == ClientValueSource.Disabled)
            return new("Claude idle", null, null, null, ClientValueSource.Disabled);
        return new("Claude idle", null, null, null, ClientValueSource.Unknown);
    }

    private static void WarnIfObservedClientRacesOrPrecedes(
        ILogger log,
        string clientName,
        string sourcePath,
        ClientDurationValue client,
        UpstreamTimeoutOptions budgets,
        bool keepAliveCanReach,
        bool wholeResponseBuffering)
    {
        if (budgets.StreamIdleTimeoutSeconds <= 0 || client.EffectiveMs is not { } clientMs) return;
        var bridgeMs = (long)budgets.StreamIdleTimeoutSeconds * 1000L;
        var keepAliveEffectiveAfterFirstEvent = keepAliveCanReach
            && (long)budgets.KeepAliveIntervalSeconds * 1000L < clientMs;
        if (clientMs > bridgeMs) return;

        var relation = clientMs == bridgeMs ? "races the bridge" : "can fire before the bridge";
        var phase = keepAliveEffectiveAfterFirstEvent
            ? "during the first upstream SSE event gap (keepalive starts only after that event)"
            : wholeResponseBuffering
                ? "during a buffered upstream SSE event gap (keepalive cannot reach the client)"
                : "during an upstream SSE event gap";
        log.LogWarning(
            "Timeouts: observed global {Client} {ClientKey} ({ClientValue}, {Source}) {Relation} "
            + "{Phase}; Pipeline:UpstreamTimeout:StreamIdleTimeoutSeconds is {BridgeValue}. Review the native client and "
            + "bridge settings; project/profile/CLI/env overrides are not included.",
            clientName,
            client.Key,
            DescribeDuration(client),
            sourcePath,
            relation,
            phase,
            FormatDuration(bridgeMs));
    }
}
