namespace CopilotBridge.Cli.Pipeline.Response.Detection;

/// <summary>
/// Non-generic view of a <see cref="DetectorOrder{TDetector}"/> so registration
/// code can read the precedence value off an unknown closed generic (e.g. to
/// detect a duplicate order across detector types) without reflection.
/// </summary>
internal interface IDetectorOrder
{
    int Value { get; }
}

/// <summary>
/// The registration-time precedence value for a specific detector type, injected
/// into that detector so it can expose <see cref="IResponseDetector.Order"/>.
/// A distinct <b>closed</b> generic per detector type
/// (<c>DetectorOrder&lt;ToolLeakDetector&gt;</c> etc.) so each detector resolves its
/// own value — a shared <c>DetectorOrder&lt;IResponseDetector&gt;</c> would collide
/// and every detector would read the last-registered value.
/// </summary>
/// <remarks>
/// Registered as a singleton (an immutable constant value) by
/// <c>BridgeServiceCollectionExtensions.RegisterResponseDetector</c>, which assigns
/// the value from an explicit per-call order argument. Injecting a singleton into a
/// scoped detector is safe (the unsafe direction is scoped→singleton). AOT-clean:
/// the closed generic is known at compile time at both the registration and
/// injection sites.
/// </remarks>
/// <typeparam name="TDetector">The detector type this order belongs to.</typeparam>
internal sealed record DetectorOrder<TDetector>(int Value) : IDetectorOrder
    where TDetector : IResponseDetector;
