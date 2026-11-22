namespace Onboarding.Core.Applications;

/// <summary>
/// One licence application. Status changes only through <see cref="TransitionTo"/>, which enforces the
/// transition table and appends exactly one history entry per change.
/// </summary>
public sealed class Application
{
    private static readonly HashSet<(ApplicationStatus From, ApplicationStatus To, WorkflowEvent Event)> AllowedTransitions =
    [
        (ApplicationStatus.Submitted, ApplicationStatus.ValidatingData, WorkflowEvent.ValidationStarted),
        (ApplicationStatus.ValidatingData, ApplicationStatus.CheckingPhoto, WorkflowEvent.ValidationPassed),
        (ApplicationStatus.ValidatingData, ApplicationStatus.Failed, WorkflowEvent.ValidationFailed),
        (ApplicationStatus.CheckingPhoto, ApplicationStatus.RiskAssessment, WorkflowEvent.PhotoCheckPassed),
        (ApplicationStatus.CheckingPhoto, ApplicationStatus.Failed, WorkflowEvent.PhotoCheckFailed),
        (ApplicationStatus.RiskAssessment, ApplicationStatus.Approved, WorkflowEvent.AutoApproved),
        (ApplicationStatus.RiskAssessment, ApplicationStatus.PendingManualReview, WorkflowEvent.ManualReviewRequired),
        (ApplicationStatus.PendingManualReview, ApplicationStatus.Approved, WorkflowEvent.ManuallyApproved),
        (ApplicationStatus.PendingManualReview, ApplicationStatus.Rejected, WorkflowEvent.ManuallyRejected),
        (ApplicationStatus.Submitted, ApplicationStatus.Failed, WorkflowEvent.ProcessingFailed),
        (ApplicationStatus.ValidatingData, ApplicationStatus.Failed, WorkflowEvent.ProcessingFailed),
        (ApplicationStatus.CheckingPhoto, ApplicationStatus.Failed, WorkflowEvent.ProcessingFailed),
        (ApplicationStatus.RiskAssessment, ApplicationStatus.Failed, WorkflowEvent.ProcessingFailed),
    ];

    private readonly List<HistoryEntry> _history = [];

    private Application() { }

    public Guid Id { get; private set; }
    public ApplicationStatus Status { get; private set; }
    public Applicant Applicant { get; private set; } = new();
    public PhotoMetadata? Photo { get; private set; }

    /// <summary>Submission time (UTC); reference date for age rules.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Time of the latest transition.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<HistoryEntry> History => _history;

    public bool IsTerminal => IsTerminalStatus(Status);

    public static bool IsTerminalStatus(ApplicationStatus status) =>
        status is ApplicationStatus.Approved or ApplicationStatus.Rejected or ApplicationStatus.Failed;

    public static Application Create(Guid id, Applicant applicant, PhotoMetadata? photo, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(applicant);
        var application = new Application
        {
            Id = id,
            Status = ApplicationStatus.Submitted,
            Applicant = applicant,
            Photo = photo,
            CreatedAt = now,
            UpdatedAt = now,
        };
        application._history.Add(new HistoryEntry(now, null, ApplicationStatus.Submitted, WorkflowEvent.ApplicationCreated, null));
        return application;
    }

    public static bool CanTransition(ApplicationStatus from, ApplicationStatus to, WorkflowEvent @event) =>
        AllowedTransitions.Contains((from, to, @event));

    /// <exception cref="InvalidTransitionException">The (current status, target, event) triple is not in the transition table.</exception>
    public void TransitionTo(ApplicationStatus to, WorkflowEvent @event, string? details, DateTimeOffset at)
    {
        if (!CanTransition(Status, to, @event))
        {
            throw new InvalidTransitionException(Status, to, @event);
        }

        _history.Add(new HistoryEntry(at, Status, to, @event, details));
        Status = to;
        UpdatedAt = at;
    }

    public void Approve(DateTimeOffset at) =>
        TransitionTo(ApplicationStatus.Approved, WorkflowEvent.ManuallyApproved, null, at);

    public void Reject(string? reason, DateTimeOffset at) =>
        TransitionTo(ApplicationStatus.Rejected, WorkflowEvent.ManuallyRejected, string.IsNullOrWhiteSpace(reason) ? null : reason, at);

    /// <summary>Stores the simulated photo quality score. Only valid during the photo check and when a photo exists.</summary>
    public void RecordQualityScore(int score)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(score);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(score, 100);
        if (Status != ApplicationStatus.CheckingPhoto)
        {
            throw new InvalidOperationException($"Quality score can only be recorded in {ApplicationStatus.CheckingPhoto}, not {Status}");
        }

        if (Photo is null)
        {
            throw new InvalidOperationException("Cannot record a quality score without a photo");
        }

        Photo.QualityScore = score;
    }
}
