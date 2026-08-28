using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Cli.Models;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract-first coverage for the Responses target-input estimator. The
/// estimator must never turn a larger valid upstream count into a smaller
/// downstream value, must fail malformed count responses explicitly, and must
/// use an operator-visible conservative fallback for an uncalibrated model.
/// </summary>
public sealed class ResponsesAdmissionEstimatorTests
{
    [Theory]
    [InlineData("minimal", 40, 14)]
    [InlineData("long-history", 126_668, 129_901)]
    [InlineData("tool-heavy", 4_039, 1_657)]
    [InlineData("production-shape", 774_204, 811_607)]
    public void Gpt56Sol_PairedCorpus_NeverUndercountsTargetUsage(
        string caseId, int rawCount, int targetUsage)
    {
        var estimator = new ResponsesAdmissionEstimator(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ResponsesAdmissionEstimator>.Instance);

        var estimate = estimator.Estimate("gpt-5.6-sol", rawCount);

        Assert.True(
            estimate.InputTokens >= targetUsage,
            $"{caseId}: estimate {estimate.InputTokens} < paired target usage {targetUsage}");
    }

    [Fact]
    public void Gpt56Sol_PairedProductionShape_IsRaisedAboveObservedTargetUsage()
    {
        var estimator = new ResponsesAdmissionEstimator(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ResponsesAdmissionEstimator>.Instance);

        var estimate = estimator.Estimate("gpt-5.6-sol", 774_204);

        Assert.True(estimate.InputTokens >= 811_607);
        Assert.Equal(774_204, estimate.RawInputTokens);
        Assert.Contains("gpt-5.6", estimate.CalibrationId);
    }

    [Fact]
    public void Estimate_IsMonotonic_AndSaturatesWithoutWrapping()
    {
        var estimator = new ResponsesAdmissionEstimator(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ResponsesAdmissionEstimator>.Instance);

        var lower = estimator.Estimate("gpt-5.6-sol", 100).InputTokens;
        var higher = estimator.Estimate("gpt-5.6-sol", 101).InputTokens;
        var saturated = estimator.Estimate("gpt-5.6-sol", int.MaxValue).InputTokens;

        Assert.True(lower <= higher);
        Assert.Equal(int.MaxValue, saturated);
    }

    [Fact]
    public void MinimalInput_UsesABoundedReserve_NotAWholeTurnReservation()
    {
        var estimator = new ResponsesAdmissionEstimator(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ResponsesAdmissionEstimator>.Instance);

        var estimate = estimator.Estimate("gpt-5.6-sol", 40);

        Assert.InRange(estimate.InputTokens, 40, 128);
    }

    [Fact]
    public void UnknownResponsesModel_UsesFallback_AndWarnsWithoutPromptContent()
    {
        var recorder = new RecordingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(recorder));
        var estimator = new ResponsesAdmissionEstimator(
            factory.CreateLogger<ResponsesAdmissionEstimator>());

        var estimate = estimator.Estimate("gpt-future-secretless", 1_000);

        Assert.True(estimate.InputTokens > 1_000);
        Assert.Contains("fallback", estimate.CalibrationId);
        var warning = Assert.Single(recorder.Events, e => e.Level == LogLevel.Warning);
        Assert.Equal("gpt-future-secretless", warning.Properties["Model"]);
    }

    [Theory]
    [InlineData("gpt-5.6-luna")]
    [InlineData("gpt-5.6-sol-fast")]
    [InlineData("gpt-5.6-terra")]
    public void UnprobedSiblingModel_DoesNotBorrowSolsExactCalibration(string model)
    {
        var recorder = new RecordingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(recorder));
        var estimator = new ResponsesAdmissionEstimator(
            factory.CreateLogger<ResponsesAdmissionEstimator>());

        var estimate = estimator.Estimate(model, 1_000);

        Assert.Contains("fallback", estimate.CalibrationId);
        Assert.Single(recorder.Events, e => e.Level == LogLevel.Warning);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"input_tokens\":-1}")]
    [InlineData("{\"input_tokens\":1.5}")]
    [InlineData("{\"input_tokens\":\"9\"}")]
    [InlineData("{\"input_tokens\":2147483648}")]
    [InlineData("{\"input_tokens\":1}{\"unexpected\":true}")]
    public void InvalidUpstreamCount_IsRejected(string json)
    {
        Assert.False(CountTokensResponseParser.TryParse(
            System.Text.Encoding.UTF8.GetBytes(json), out _, out _));
    }
}
