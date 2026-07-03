using Microsoft.Extensions.Options;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Test helpers for supplying options to services that inject
/// <see cref="IOptionsSnapshot{T}"/>. The framework's <c>Options.Create</c> only
/// produces an <see cref="IOptions{T}"/>; the scoped detectors take a snapshot, so
/// unit tests (which have no DI container) wrap a fixed value in this shim.
/// </summary>
internal static class TestOptions
{
    public static IOptionsSnapshot<T> Snapshot<T>(T value) where T : class =>
        new StaticOptionsSnapshot<T>(value);

    private sealed class StaticOptionsSnapshot<T> : IOptionsSnapshot<T> where T : class
    {
        private readonly T _value;
        public StaticOptionsSnapshot(T value) => _value = value;
        public T Value => _value;
        public T Get(string? name) => _value;
    }
}
