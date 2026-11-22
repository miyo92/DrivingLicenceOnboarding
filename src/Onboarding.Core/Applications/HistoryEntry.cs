namespace Onboarding.Core.Applications;

/// <summary>Immutable record of one status transition.</summary>
public sealed class HistoryEntry
{
    private HistoryEntry() { }

    internal HistoryEntry(DateTimeOffset occurredAt, ApplicationStatus? fromStatus, ApplicationStatus toStatus, WorkflowEvent @event, string? details)
    {
        OccurredAt = occurredAt;
        FromStatus = fromStatus;
        ToStatus = toStatus;
        Event = @event;
        Details = details;
    }

    /// <summary>Database identity; tie-breaker for equal timestamps.</summary>
    public long Id { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public ApplicationStatus? FromStatus { get; private set; }
    public ApplicationStatus ToStatus { get; private set; }
    public WorkflowEvent Event { get; private set; }
    public string? Details { get; private set; }
}
