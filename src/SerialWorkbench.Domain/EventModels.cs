namespace SerialWorkbench.Domain;

public sealed record EventGap(long FromSequence, long ToSequence);

public sealed record EventBatch(
    Guid StreamId,
    IReadOnlyList<SerialTrafficEvent> Events,
    long NextSequence,
    long EarliestSequence,
    long LatestSequence,
    EventGap? Gap = null,
    bool ResetRequired = false);
