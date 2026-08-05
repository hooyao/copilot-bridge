using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Pipeline.Routing;

/// <summary>
/// Converts Copilot's Anthropic-named count of a canonical Responses body into
/// a conservative estimate of target-equivalent input tokens. Records are
/// integer-rational so arithmetic is deterministic and checked under Native AOT.
/// </summary>
internal sealed class ResponsesAdmissionEstimator
{
    // GPT-5.6 paired live corpus (2026-08-05), where count and Responses usage
    // consumed identical SHA-256-identified T2 bytes. Worst observed ratio was
    // 811,607 / 774,204 = 1.048311556 on the sanitized production shape.
    // 1.05 plus 16 covers it while keeping the paired minimal case at 58.
    private static readonly Calibration Gpt56Sol = new(
        "gpt-5.6-sol-v2-20260805",
        Numerator: 1_050, Denominator: 1_000,
        FixedAllowance: 0, SafetyReserve: 16);

    // Unknown Responses models add margin above the worst paired GPT-5.6 ratio
    // rather than treating the Anthropic-named count as exact. It remains bounded
    // for the paired minimal helper count: ceil(40*1.075)+32 = 75.
    private static readonly Calibration Fallback = new(
        "responses-fallback-v2-20260805",
        Numerator: 1_075, Denominator: 1_000,
        FixedAllowance: 0, SafetyReserve: 32);

    private readonly ILogger<ResponsesAdmissionEstimator> _log;

    public ResponsesAdmissionEstimator(ILogger<ResponsesAdmissionEstimator> log)
    {
        _log = log;
    }

    public ResponsesAdmissionEstimate Estimate(string model, int rawInputTokens)
    {
        if (rawInputTokens < 0)
            throw new ArgumentOutOfRangeException(
                nameof(rawInputTokens), "Raw input tokens cannot be negative.");

        var calibration = Exact(model);
        if (calibration is null)
        {
            calibration = Fallback;
            _log.LogWarning(
                "no exact Responses count calibration for model {Model}; using {CalibrationId}",
                model, calibration.Value.Id);
        }

        var c = calibration.Value;
        var scaled = ((long)rawInputTokens * c.Numerator + c.Denominator - 1)
            / c.Denominator;
        var total = scaled + c.FixedAllowance + c.SafetyReserve;
        var result = total >= int.MaxValue ? int.MaxValue : (int)total;
        return new ResponsesAdmissionEstimate(
            rawInputTokens, result, c.Id,
            c.FixedAllowance + c.SafetyReserve);
    }

    private static Calibration? Exact(string model) =>
        string.Equals(model, "gpt-5.6-sol", StringComparison.OrdinalIgnoreCase)
            ? Gpt56Sol
            : null;

    private readonly record struct Calibration(
        string Id,
        int Numerator,
        int Denominator,
        int FixedAllowance,
        int SafetyReserve);
}

internal readonly record struct ResponsesAdmissionEstimate(
    int RawInputTokens,
    int InputTokens,
    string CalibrationId,
    int Reserve);
