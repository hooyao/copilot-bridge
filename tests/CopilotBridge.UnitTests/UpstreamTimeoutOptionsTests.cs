using System.Text;
using CopilotBridge.Cli.Hosting;
using CopilotBridge.Cli.Hosting.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>Contract tests for exact, startup-validated bridge timer values.</summary>
public sealed class UpstreamTimeoutOptionsTests
{
    [Theory]
    [InlineData("FirstByteTimeoutSeconds")]
    [InlineData("StreamIdleTimeoutSeconds")]
    [InlineData("KeepAliveIntervalSeconds")]
    public void Every_positive_timer_value_accepts_the_runtime_max_and_rejects_the_next_second(
        string option)
    {
        var validator = new UpstreamTimeoutOptionsValidator();
        var atLimit = Options(option, UpstreamTimeoutOptionsValidator.MaxTimerSeconds);
        var overLimit = Options(option, UpstreamTimeoutOptionsValidator.MaxTimerSeconds + 1);

        Assert.True(validator.Validate(null, atLimit).Succeeded);

        var failure = validator.Validate(null, overLimit);
        Assert.False(failure.Succeeded);
        Assert.Contains(
            $"Pipeline:UpstreamTimeout:{option}={UpstreamTimeoutOptionsValidator.MaxTimerSeconds + 1}s",
            failure.FailureMessage);
        Assert.Contains($"use <= {UpstreamTimeoutOptionsValidator.MaxTimerSeconds}s", failure.FailureMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Non_positive_values_remain_disabled_without_a_finite_substitute(int value)
    {
        var options = new UpstreamTimeoutOptions
        {
            FirstByteTimeoutSeconds = value,
            StreamIdleTimeoutSeconds = value,
            KeepAliveIntervalSeconds = value,
        };

        Assert.True(new UpstreamTimeoutOptionsValidator().Validate(null, options).Succeeded);
        Assert.Equal(value, options.FirstByteTimeoutSeconds);
        Assert.Equal(value, options.StreamIdleTimeoutSeconds);
        Assert.Equal(value, options.KeepAliveIntervalSeconds);
    }

    [Fact]
    public void Positive_values_remain_exact_without_margin_clamp_or_fallback()
    {
        var options = new UpstreamTimeoutOptions
        {
            FirstByteTimeoutSeconds = 241,
            StreamIdleTimeoutSeconds = 601,
            KeepAliveIntervalSeconds = 17,
        };

        Assert.True(new UpstreamTimeoutOptionsValidator().Validate(null, options).Succeeded);
        Assert.Equal(241, options.FirstByteTimeoutSeconds);
        Assert.Equal(601, options.StreamIdleTimeoutSeconds);
        Assert.Equal(17, options.KeepAliveIntervalSeconds);
    }

    [Fact]
    public void Validation_boundary_is_the_boundary_of_the_runtime_timer_apis()
    {
        var maximum = TimeSpan.FromSeconds(UpstreamTimeoutOptionsValidator.MaxTimerSeconds);
        var tooLarge = TimeSpan.FromSeconds(UpstreamTimeoutOptionsValidator.MaxTimerSeconds + 1L);

        using (var cancelAfter = new CancellationTokenSource())
        {
            cancelAfter.CancelAfter(maximum);
            Assert.Throws<ArgumentOutOfRangeException>(() => cancelAfter.CancelAfter(tooLarge));
        }

        using var delayCancellation = new CancellationTokenSource();
        _ = Task.Delay(maximum, delayCancellation.Token);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = Task.Delay(tooLarge, delayCancellation.Token);
        });
        delayCancellation.Cancel();
    }

    [Fact]
    public void Bridge_composition_registers_the_validation_in_the_pre_start_gate()
    {
        var json = $$"""
            {
              "Pipeline": {
                "UpstreamTimeout": {
                  "FirstByteTimeoutSeconds": {{UpstreamTimeoutOptionsValidator.MaxTimerSeconds + 1}}
                }
              }
            }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var config = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBridgeServer(config);
        using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("FirstByteTimeoutSeconds", error.Message);
        Assert.Contains(
            (UpstreamTimeoutOptionsValidator.MaxTimerSeconds + 1).ToString(),
            error.Message);
    }

    private static UpstreamTimeoutOptions Options(string option, int value)
    {
        var result = new UpstreamTimeoutOptions();
        switch (option)
        {
            case "FirstByteTimeoutSeconds": result.FirstByteTimeoutSeconds = value; break;
            case "StreamIdleTimeoutSeconds": result.StreamIdleTimeoutSeconds = value; break;
            case "KeepAliveIntervalSeconds": result.KeepAliveIntervalSeconds = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(option), option, null);
        }
        return result;
    }
}
