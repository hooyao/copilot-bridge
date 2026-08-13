using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CopilotBridge.Cli.Hosting.ClientConfig;

internal enum ClientValueSource
{
    Absent,
    Explicit,
    BuiltIn,
    Inherited,
    Disabled,
    Invalid,
    Unknown,
}

/// <summary>One client duration, retaining configured and effective facts separately.</summary>
internal sealed record ClientDurationValue(
    string Key,
    string? RawValue,
    long? ConfiguredMs,
    long? EffectiveMs,
    ClientValueSource Source,
    string? Detail = null)
{
    public bool IsKnown => EffectiveMs is not null || Source == ClientValueSource.Disabled;
    public bool IsDefault => Source == ClientValueSource.BuiltIn;
}

/// <summary>One integer client setting such as a retry count.</summary>
internal sealed record ClientCountValue(
    string Key,
    string? RawValue,
    long? Effective,
    ClientValueSource Source,
    string? Detail = null)
{
    public bool IsDefault => Source == ClientValueSource.BuiltIn;
}

/// <summary>A raw client setting whose runtime effect is not inferred here.</summary>
internal sealed record ClientSettingValue(
    string Key,
    string? RawValue,
    ClientValueSource Source,
    string? Detail = null);

internal sealed record ClientTimeoutSnapshot(
    string SettingsPath,
    bool Readable,
    string? Reason,
    ClientDurationValue EventIdle,
    ClientDurationValue ByteIdle,
    ClientDurationValue NormalRequest,
    ClientDurationValue AfterStreamErrorRequest,
    ClientSettingValue StreamWatchdog,
    ClientSettingValue ByteWatchdog,
    ClientSettingValue Retry);

/// <summary>
/// Best-effort reader for Claude Code's global settings. It never writes and never
/// claims to include repo settings or the future client process environment.
/// </summary>
internal static class ClaudeCodeTimeoutReader
{
    private const string AssumeFirstPartyKey = "_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL";

    public static string DefaultSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "settings.json");

    public static ClientTimeoutSnapshot Read(
        string? settingsPath = null,
        string expectedBaseUrlSuffix = "/cc",
        string? installedVersion = null) =>
        ReadFile(settingsPath ?? DefaultSettingsPath, expectedBaseUrlSuffix, installedVersion);

    private static ClientTimeoutSnapshot ReadFile(
        string path,
        string expectedBaseUrlSuffix,
        string? installedVersion)
    {
        string text;
        try
        {
            if (!File.Exists(path)) return Unknown(path, "settings file does not exist");
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Unknown(path, $"settings file could not be read ({ex.GetType().Name})");
        }

        JsonObject? root;
        try { root = JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { root = null; }
        if (root is null) return Unknown(path, "settings file is not a JSON object");

        var env = root["env"] as JsonObject;
        var baseUrl = AsStringOrNull(env?["ANTHROPIC_BASE_URL"]);
        if (baseUrl is null
            || !baseUrl.TrimEnd('/').EndsWith(expectedBaseUrlSuffix, StringComparison.Ordinal))
            return Unknown(path, "settings file is not pointed at this bridge");

        var streamPresent = TryGet(env, ClaudeCodeTimeoutPolicy.StreamIdleKey, out var streamNode);
        var streamRaw = AsIntegerText(streamNode);
        var streamConfigured = ParsePositive(streamRaw);
        var parsedEventIdle = DurationFromExplicitOrDefault(
            ClaudeCodeTimeoutPolicy.StreamIdleKey,
            streamPresent,
            streamRaw,
            RawDisplay(streamNode),
            streamConfigured,
            ClaudeCodeTimeoutPolicy.EventIdleFloorMs,
            floorMs: ClaudeCodeTimeoutPolicy.EventIdleFloorMs);
        var eventIdle = parsedEventIdle;
        var streamWatchdog = ReadSetting(env, ClaudeCodeTimeoutPolicy.StreamWatchdogKey, allowBoolean: true);
        if (streamWatchdog.Source == ClientValueSource.Invalid)
            eventIdle = UnknownEffective(eventIdle, $"invalid {ClaudeCodeTimeoutPolicy.StreamWatchdogKey}");
        else if (IsDisabled(streamWatchdog.RawValue))
            eventIdle = Disabled(ClaudeCodeTimeoutPolicy.StreamIdleKey, streamRaw, "stream watchdog disabled");

        var bytePresent = TryGet(env, ClaudeCodeTimeoutPolicy.ByteIdleKey, out var byteNode);
        var byteRaw = AsIntegerText(byteNode);
        var byteConfigured = ParsePositive(byteRaw);
        ClientDurationValue byteIdle;
        if (bytePresent)
        {
            byteIdle = DurationFromExplicit(
                ClaudeCodeTimeoutPolicy.ByteIdleKey,
                byteRaw,
                RawDisplay(byteNode),
                byteConfigured,
                ClaudeCodeTimeoutPolicy.ByteIdleMinMs,
                ClaudeCodeTimeoutPolicy.ByteIdleMaxMs);
        }
        else if (streamPresent && streamConfigured is not null
                 && parsedEventIdle.EffectiveMs is { } inherited)
        {
            var effective = Math.Clamp(
                inherited,
                ClaudeCodeTimeoutPolicy.ByteIdleMinMs,
                ClaudeCodeTimeoutPolicy.ByteIdleMaxMs);
            byteIdle = new(
                ClaudeCodeTimeoutPolicy.ByteIdleKey,
                RawValue: null,
                ConfiguredMs: null,
                EffectiveMs: effective,
                ClientValueSource.Inherited,
                "inherits SSE event idle");
        }
        else if (streamPresent)
        {
            byteIdle = new(
                ClaudeCodeTimeoutPolicy.ByteIdleKey,
                RawValue: null,
                ConfiguredMs: null,
                EffectiveMs: null,
                ClientValueSource.Unknown,
                $"cannot inherit invalid {ClaudeCodeTimeoutPolicy.StreamIdleKey}");
        }
        else
        {
            var firstPartySetting = ReadSetting(env, AssumeFirstPartyKey, allowBoolean: true);
            if (firstPartySetting.Source == ClientValueSource.Invalid)
            {
                byteIdle = new(
                    ClaudeCodeTimeoutPolicy.ByteIdleKey,
                    RawValue: null,
                    ConfiguredMs: null,
                    EffectiveMs: null,
                    ClientValueSource.Unknown,
                    $"invalid {AssumeFirstPartyKey}");
            }
            else
            {
                var firstParty = IsEnabled(firstPartySetting.RawValue);
                var fallback = firstParty
                    ? ClaudeCodeTimeoutPolicy.AbsentByteIdleFirstPartyDefaultMs
                    : ClaudeCodeTimeoutPolicy.AbsentByteIdleDefaultMs;
                byteIdle = BuiltIn(ClaudeCodeTimeoutPolicy.ByteIdleKey, fallback);
            }
        }
        var byteWatchdog = ReadSetting(env, ClaudeCodeTimeoutPolicy.ByteWatchdogKey, allowBoolean: true);
        if (byteWatchdog.Source == ClientValueSource.Invalid)
            byteIdle = UnknownEffective(byteIdle, $"invalid {ClaudeCodeTimeoutPolicy.ByteWatchdogKey}");
        else if (IsDisabled(byteWatchdog.RawValue))
            byteIdle = Disabled(ClaudeCodeTimeoutPolicy.ByteIdleKey, byteRaw, "byte watchdog disabled");

        var requestPresent = TryGet(env, ClaudeCodeTimeoutPolicy.RequestTimeoutKey, out var requestNode);
        var requestRaw = AsIntegerText(requestNode);
        var requestConfigured = ParsePositive(requestRaw);
        ClientDurationValue normalRequest;
        ClientDurationValue afterErrorRequest;
        if (!requestPresent)
        {
            normalRequest = BuiltIn(
                ClaudeCodeTimeoutPolicy.RequestTimeoutKey,
                ClaudeCodeTimeoutPolicy.AbsentNormalRequestTimeoutMs);
            afterErrorRequest = BuiltIn(
                ClaudeCodeTimeoutPolicy.RequestTimeoutKey,
                ClaudeCodeTimeoutPolicy.AbsentAfterStreamErrorTimeoutMs);
        }
        else if (requestConfigured is { } explicitRequest)
        {
            normalRequest = Explicit(ClaudeCodeTimeoutPolicy.RequestTimeoutKey, requestRaw!, explicitRequest);
            afterErrorRequest = Explicit(ClaudeCodeTimeoutPolicy.RequestTimeoutKey, requestRaw!, explicitRequest);
        }
        else
        {
            var display = requestRaw ?? RawDisplay(requestNode);
            normalRequest = Invalid(ClaudeCodeTimeoutPolicy.RequestTimeoutKey, display);
            afterErrorRequest = Invalid(ClaudeCodeTimeoutPolicy.RequestTimeoutKey, display);
        }

        if (!ClaudeCodeTimeoutPolicy.HasVerifiedFacts(installedVersion))
        {
            var detail = installedVersion is null
                ? "Claude Code version unavailable; effective behavior is version-dependent"
                : $"Claude Code {installedVersion} has not been verified; effective behavior is version-dependent";
            eventIdle = VersionDependent(eventIdle, detail);
            byteIdle = VersionDependent(byteIdle, detail);
            normalRequest = VersionDependent(normalRequest, detail);
            afterErrorRequest = VersionDependent(afterErrorRequest, detail);
        }

        return new ClientTimeoutSnapshot(
            path,
            Readable: true,
            Reason: null,
            eventIdle,
            byteIdle,
            normalRequest,
            afterErrorRequest,
            streamWatchdog,
            byteWatchdog,
            Retry: ReadSetting(env, ClaudeCodeTimeoutPolicy.RetryKey, allowBoolean: false));
    }

    private static ClientDurationValue DurationFromExplicitOrDefault(
        string key,
        bool present,
        string? raw,
        string rawDisplay,
        long? configured,
        long defaultMs,
        long floorMs)
    {
        if (!present) return BuiltIn(key, defaultMs);
        if (configured is not { } value) return Invalid(key, raw ?? rawDisplay);
        var effective = Math.Max(value, floorMs);
        return new(
            key,
            raw,
            value,
            effective,
            ClientValueSource.Explicit,
            effective != value ? "client floor" : null);
    }

    private static ClientDurationValue DurationFromExplicit(
        string key, string? raw, string rawDisplay, long? configured, long minMs, long maxMs)
    {
        if (configured is not { } value) return Invalid(key, raw ?? rawDisplay);
        var effective = Math.Clamp(value, minMs, maxMs);
        var detail = effective == value ? null
            : effective == minMs ? "client floor"
            : "client cap";
        return new(key, raw, value, effective, ClientValueSource.Explicit, detail);
    }

    private static long? ParsePositive(string? raw) =>
        raw is not null
        && long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        && value > 0
            ? value
            : null;

    private static ClientDurationValue Explicit(string key, string raw, long ms) =>
        new(key, raw, ms, ms, ClientValueSource.Explicit);

    private static ClientDurationValue BuiltIn(string key, long ms) =>
        new(key, null, null, ms, ClientValueSource.BuiltIn);

    private static ClientDurationValue Invalid(string key, string raw) =>
        new(key, raw, null, null, ClientValueSource.Invalid, "invalid value");

    private static ClientDurationValue UnknownEffective(ClientDurationValue value, string detail) =>
        value with { EffectiveMs = null, Source = ClientValueSource.Unknown, Detail = detail };

    private static ClientDurationValue VersionDependent(ClientDurationValue value, string detail) =>
        value.Source is ClientValueSource.Invalid or ClientValueSource.Unknown
            ? value
            : value with
            {
                EffectiveMs = null,
                Source = value.ConfiguredMs is not null
                    ? ClientValueSource.Explicit
                    : ClientValueSource.Unknown,
                Detail = detail,
            };

    private static ClientDurationValue Disabled(string key, string? raw, string detail) =>
        new(key, raw, null, null, ClientValueSource.Disabled, detail);

    private static ClientTimeoutSnapshot Unknown(string path, string reason) =>
        new(
            path,
            Readable: false,
            reason,
            UnknownDuration(ClaudeCodeTimeoutPolicy.StreamIdleKey),
            UnknownDuration(ClaudeCodeTimeoutPolicy.ByteIdleKey),
            UnknownDuration(ClaudeCodeTimeoutPolicy.RequestTimeoutKey),
            UnknownDuration(ClaudeCodeTimeoutPolicy.RequestTimeoutKey),
            UnknownSetting(ClaudeCodeTimeoutPolicy.StreamWatchdogKey),
            UnknownSetting(ClaudeCodeTimeoutPolicy.ByteWatchdogKey),
            UnknownSetting(ClaudeCodeTimeoutPolicy.RetryKey));

    private static ClientDurationValue UnknownDuration(string key) =>
        new(key, null, null, null, ClientValueSource.Unknown);

    private static ClientSettingValue UnknownSetting(string key) =>
        new(key, null, ClientValueSource.Unknown);

    private static bool IsEnabled(string? raw) =>
        !string.IsNullOrWhiteSpace(raw)
        && !string.Equals(raw.Trim(), "0", StringComparison.Ordinal)
        && !string.Equals(raw.Trim(), "false", StringComparison.OrdinalIgnoreCase);

    private static bool IsDisabled(string? raw) =>
        raw is not null
        && (string.Equals(raw.Trim(), "0", StringComparison.Ordinal)
            || string.Equals(raw.Trim(), "false", StringComparison.OrdinalIgnoreCase));

    private static ClientSettingValue ReadSetting(
        JsonObject? env,
        string key,
        bool allowBoolean)
    {
        if (!TryGet(env, key, out var node))
            return new(key, null, ClientValueSource.Absent);
        if (node is not JsonValue value)
            return new(key, RawDisplay(node), ClientValueSource.Invalid, "not a scalar value");
        if (value.TryGetValue<string>(out var text))
            return new(key, text, ClientValueSource.Explicit);
        if (value.TryGetValue<long>(out var number))
            return new(key, number.ToString(CultureInfo.InvariantCulture), ClientValueSource.Explicit);
        if (allowBoolean && value.TryGetValue<bool>(out var boolean))
            return new(key, boolean ? "true" : "false", ClientValueSource.Explicit);
        return new(key, RawDisplay(node), ClientValueSource.Invalid, "unsupported value type");
    }

    private static bool TryGet(JsonObject? env, string key, out JsonNode? node)
    {
        node = null;
        return env is not null && env.TryGetPropertyValue(key, out node);
    }

    private static string? AsIntegerText(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<string>(out var text)) return text;
        return value.TryGetValue<long>(out var number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    private static string RawDisplay(JsonNode? node) => node?.ToJsonString() ?? "null";

    private static string? AsStringOrNull(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
