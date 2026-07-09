using System.Net.ServerSentEvents;

namespace CopilotBridge.Cli.Pipeline.Response.Detection;

/// <summary>
/// Base for order-aware detectors. Carries the <see cref="IResponseDetector.Order"/>
/// value (sourced from an injected <see cref="DetectorOrder{TDetector}"/>) and the
/// default <see cref="IResponseDetector"/> members, so concrete detectors implement
/// only <see cref="Name"/>, <see cref="Enabled"/>, and <see cref="InspectEvent"/>
/// (plus <see cref="Begin"/>/<see cref="InspectBuffered"/> when they need them).
/// </summary>
/// <remarks>
/// <para>
/// CRTP (curiously-recurring generic): the base is generic over the deriving type
/// <typeparamref name="TSelf"/> so its constructor takes the strongly-typed
/// <see cref="DetectorOrder{TSelf}"/> — the "order comes from an injected
/// <c>DetectorOrder</c>" contract is visible on the base itself. A concrete detector
/// derives as <c>: AbstractOrderAwareDetector&lt;ThatDetector&gt;</c>.
/// </para>
/// <para>
/// DI note: the container injects only the most-derived constructor, so each
/// concrete detector still declares <c>DetectorOrder&lt;TSelf&gt;</c> as its own
/// constructor parameter and forwards it via <c>: base(order)</c> — the base does
/// not remove that. What the base removes is the repeated <c>Order</c> property and
/// the default-member boilerplate.
/// </para>
/// </remarks>
/// <typeparam name="TSelf">The deriving detector type.</typeparam>
internal abstract class AbstractOrderAwareDetector<TSelf>(DetectorOrder<TSelf> order) : IResponseDetector
    where TSelf : AbstractOrderAwareDetector<TSelf>
{
    public int Order { get; } = order.Value;

    public abstract string Name { get; }

    public abstract bool Enabled { get; }

    public virtual bool RequiresBuffering => false;

    public virtual bool BuffersScannableBlocks => false;

    public virtual void Begin() { }

    public abstract DetectionAction InspectEvent(in SseItem<string> evt);

    public virtual DetectionAction InspectBuffered(byte[] body) => DetectionAction.None;
}
