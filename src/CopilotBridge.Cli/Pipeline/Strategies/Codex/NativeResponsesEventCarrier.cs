using System.Net.ServerSentEvents;

namespace CopilotBridge.Cli.Pipeline.Strategies.Codex;

/// <summary>
/// Private, in-process stream records that pair one original Copilot Responses
/// event with the Anthropic-shaped semantic events T3 derived from it. Response
/// detectors inspect the semantic events; the native Codex edge may restore the
/// original only when those events arrive unchanged. Neither record is a client
/// protocol event and both client adapters must consume or drop them.
/// </summary>
internal static class NativeResponsesEventCarrier
{
    internal const string BeginType = "bridge_responses_native_begin";
    internal const string EventType = "bridge_responses_native_event";

    internal static SseItem<string> Begin() =>
        new("", BeginType);

    internal static SseItem<string> Create(
        int ordinal,
        in SseItem<string> original,
        in NativeSemanticSequence semantic,
        NativeResponsesEventLedger? ledger = null)
    {
        ledger?.Add(ordinal, original, semantic);
        return new SseItem<string>("", EventType);
    }

    internal static bool IsPrivate(in SseItem<string> item) =>
        item.EventType is BeginType or EventType;

    internal static bool TryRead(
        in SseItem<string> item,
        NativeResponsesEventLedger? ledger,
        out int ordinal,
        out SseItem<string> original,
        out NativeSemanticSequence semantic)
    {
        ordinal = -1;
        original = default;
        semantic = default;
        if (item.EventType != EventType)
            return false;

        return ledger is not null && ledger.TryTake(out ordinal, out original, out semantic);
    }
}

/// <summary>
/// One-request ledger backing compact native-event carrier tokens. It is driven
/// synchronously by one async stream enumerator, so no locking is needed. Entries
/// are removed at T4 consumption and the adapter clears any tail on fault/cancel.
/// </summary>
internal sealed class NativeResponsesEventLedger
{
    private readonly Queue<Entry> _entries = [];
    private int _nextAdd;
    private int _nextTake;

    internal int Count => _entries.Count;

    internal void Add(
        int ordinal,
        in SseItem<string> original,
        in NativeSemanticSequence semantic)
    {
        if (ordinal != _nextAdd)
            throw new InvalidOperationException(
                $"Native Responses source ordinal {ordinal} did not follow {_nextAdd - 1}.");
        _entries.Enqueue(new Entry(ordinal, original, semantic));
        _nextAdd++;
    }

    internal bool TryTake(
        out int ordinal,
        out SseItem<string> original,
        out NativeSemanticSequence semantic)
    {
        if (_entries.TryDequeue(out var entry))
        {
            ordinal = entry.Ordinal;
            if (ordinal != _nextTake)
            {
                original = default;
                semantic = default;
                return false;
            }
            _nextTake++;
            original = entry.Original;
            semantic = entry.Semantic;
            return true;
        }
        ordinal = -1;
        original = default;
        semantic = default;
        return false;
    }

    internal void Clear()
    {
        _entries.Clear();
        _nextAdd = 0;
        _nextTake = 0;
    }

    private readonly record struct Entry(
        int Ordinal,
        SseItem<string> Original,
        NativeSemanticSequence Semantic);
}

/// <summary>
/// Allocation-free semantic expansion for the common 0–4 event cases. T3's
/// current maximum is three (a defensive block stop plus terminal pair); the
/// overflow exists so a future translator extension remains correct.
/// </summary>
internal struct NativeSemanticSequence
{
    private SseItem<string> _first;
    private SseItem<string> _second;
    private SseItem<string> _third;
    private SseItem<string> _fourth;
    private List<SseItem<string>>? _overflow;

    internal int Count { get; private set; }

    internal void Add(in SseItem<string> item)
    {
        switch (Count)
        {
            case 0: _first = item; break;
            case 1: _second = item; break;
            case 2: _third = item; break;
            case 3: _fourth = item; break;
            default:
                _overflow ??= [_first, _second, _third, _fourth];
                _overflow.Add(item);
                break;
        }
        Count++;
    }

    internal readonly SseItem<string> At(int index) => index switch
    {
        0 => _first,
        1 => _second,
        2 => _third,
        3 => _fourth,
        _ when index >= 4 && index < Count => _overflow![index],
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };
}
