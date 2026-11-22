using Onboarding.Core.Applications;
using S = Onboarding.Core.Applications.ApplicationStatus;
using E = Onboarding.Core.Applications.WorkflowEvent;

namespace Onboarding.UnitTests.Applications;

public class ApplicationStateMachineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    // specs/application-workflow "Allowed Transitions" + specs/audit-history "Event Names", written out independently of the aggregate.
    private static readonly (S From, S To, E Event)[] ExpectedAllowed =
    [
        (S.Submitted, S.ValidatingData, E.ValidationStarted),
        (S.ValidatingData, S.CheckingPhoto, E.ValidationPassed),
        (S.ValidatingData, S.Failed, E.ValidationFailed),
        (S.CheckingPhoto, S.RiskAssessment, E.PhotoCheckPassed),
        (S.CheckingPhoto, S.Failed, E.PhotoCheckFailed),
        (S.RiskAssessment, S.Approved, E.AutoApproved),
        (S.RiskAssessment, S.PendingManualReview, E.ManualReviewRequired),
        (S.PendingManualReview, S.Approved, E.ManuallyApproved),
        (S.PendingManualReview, S.Rejected, E.ManuallyRejected),
        (S.Submitted, S.Failed, E.ProcessingFailed),
        (S.ValidatingData, S.Failed, E.ProcessingFailed),
        (S.CheckingPhoto, S.Failed, E.ProcessingFailed),
        (S.RiskAssessment, S.Failed, E.ProcessingFailed),
    ];

    public static TheoryData<S, S, E> AllowedTransitions()
    {
        var data = new TheoryData<S, S, E>();
        foreach (var (from, to, evt) in ExpectedAllowed)
        {
            data.Add(from, to, evt);
        }

        return data;
    }

    public static TheoryData<S, S, E> DisallowedTransitions()
    {
        var data = new TheoryData<S, S, E>();
        foreach (var from in Enum.GetValues<S>())
        foreach (var to in Enum.GetValues<S>())
        foreach (var evt in Enum.GetValues<E>())
        {
            if (!ExpectedAllowed.Contains((from, to, evt)))
            {
                data.Add(from, to, evt);
            }
        }

        return data;
    }

    public static TheoryData<S> TerminalStatuses() => [S.Approved, S.Rejected, S.Failed];

    [Fact]
    public void Create_starts_Submitted_with_creation_entry_having_null_fromStatus()
    {
        var app = Application.Create(Guid.NewGuid(), new Applicant(), null, T0);

        Assert.Equal(S.Submitted, app.Status);
        Assert.Equal(T0, app.CreatedAt);
        Assert.Equal(T0, app.UpdatedAt);
        var entry = Assert.Single(app.History);
        Assert.Null(entry.FromStatus);
        Assert.Equal(S.Submitted, entry.ToStatus);
        Assert.Equal(E.ApplicationCreated, entry.Event);
        Assert.Null(entry.Details);
        Assert.Equal(T0, entry.OccurredAt);
    }

    [Theory]
    [MemberData(nameof(AllowedTransitions))]
    public void Allowed_transition_changes_status_and_appends_matching_history_entry(S from, S to, E evt)
    {
        var app = InStatus(from);
        var historyBefore = app.History.Count;
        var at = T0.AddHours(1);

        app.TransitionTo(to, evt, "some details", at);

        Assert.Equal(to, app.Status);
        Assert.Equal(at, app.UpdatedAt);
        Assert.Equal(historyBefore + 1, app.History.Count);
        var entry = app.History[^1];
        Assert.Equal(from, entry.FromStatus);
        Assert.Equal(to, entry.ToStatus);
        Assert.Equal(evt, entry.Event);
        Assert.Equal("some details", entry.Details);
        Assert.Equal(at, entry.OccurredAt);
    }

    [Theory]
    [MemberData(nameof(DisallowedTransitions))]
    public void Disallowed_transition_is_refused_and_leaves_state_and_history_unchanged(S from, S to, E evt)
    {
        var app = InStatus(from);
        var historyBefore = app.History.ToList();
        var updatedBefore = app.UpdatedAt;

        var ex = Assert.Throws<InvalidTransitionException>(() => app.TransitionTo(to, evt, null, T0.AddHours(1)));

        Assert.Equal((from, to, evt), (ex.From, ex.To, ex.Event));
        Assert.Equal(from, app.Status);
        Assert.Equal(updatedBefore, app.UpdatedAt);
        Assert.Equal(historyBefore, app.History);
    }

    [Theory]
    [MemberData(nameof(TerminalStatuses))]
    public void Terminal_status_refuses_every_transition(S terminal)
    {
        var app = InStatus(terminal);
        var historyCount = app.History.Count;

        Assert.True(app.IsTerminal);
        foreach (var to in Enum.GetValues<S>())
        foreach (var evt in Enum.GetValues<E>())
        {
            Assert.Throws<InvalidTransitionException>(() => app.TransitionTo(to, evt, null, T0.AddHours(1)));
        }

        Assert.Equal(terminal, app.Status);
        Assert.Equal(historyCount, app.History.Count);
    }

    [Fact]
    public void Approved_cannot_move_to_CheckingPhoto()
    {
        var app = InStatus(S.Approved);

        Assert.Throws<InvalidTransitionException>(() => app.TransitionTo(S.CheckingPhoto, E.ValidationPassed, null, T0));

        Assert.Equal(S.Approved, app.Status);
    }

    [Fact]
    public void Automatic_approval_path_records_full_history_in_order()
    {
        var app = InStatus(S.Approved);

        Assert.Equal(
            [E.ApplicationCreated, E.ValidationStarted, E.ValidationPassed, E.PhotoCheckPassed, E.AutoApproved],
            app.History.Select(h => h.Event));
    }

    [Fact]
    public void RecordQualityScore_sets_score_during_photo_check()
    {
        var app = InStatus(S.CheckingPhoto, WithPhoto());

        app.RecordQualityScore(55);

        Assert.Equal(55, app.Photo!.QualityScore);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void RecordQualityScore_rejects_out_of_range(int score)
    {
        var app = InStatus(S.CheckingPhoto, WithPhoto());

        Assert.Throws<ArgumentOutOfRangeException>(() => app.RecordQualityScore(score));
        Assert.Null(app.Photo!.QualityScore);
    }

    [Fact]
    public void RecordQualityScore_refused_without_photo()
    {
        var app = InStatus(S.CheckingPhoto);

        Assert.Throws<InvalidOperationException>(() => app.RecordQualityScore(50));
    }

    [Theory]
    [InlineData(S.ValidatingData)]
    [InlineData(S.RiskAssessment)]
    [InlineData(S.Approved)]
    public void RecordQualityScore_refused_outside_photo_check(S status)
    {
        var app = InStatus(status, WithPhoto());

        Assert.Throws<InvalidOperationException>(() => app.RecordQualityScore(50));
        Assert.Null(app.Photo!.QualityScore);
    }

    private static PhotoMetadata WithPhoto() => new("key", "image/jpeg", 20_000);

    /// <summary>Drives a fresh application to <paramref name="target"/> through allowed transitions only.</summary>
    internal static Application InStatus(S target, PhotoMetadata? photo = null)
    {
        var app = Application.Create(Guid.NewGuid(), new Applicant(), photo, T0);
        (S To, E Event)[] path = target switch
        {
            S.Submitted => [],
            S.ValidatingData => [(S.ValidatingData, E.ValidationStarted)],
            S.CheckingPhoto => [(S.ValidatingData, E.ValidationStarted), (S.CheckingPhoto, E.ValidationPassed)],
            S.RiskAssessment => [(S.ValidatingData, E.ValidationStarted), (S.CheckingPhoto, E.ValidationPassed), (S.RiskAssessment, E.PhotoCheckPassed)],
            S.PendingManualReview => [(S.ValidatingData, E.ValidationStarted), (S.CheckingPhoto, E.ValidationPassed), (S.RiskAssessment, E.PhotoCheckPassed), (S.PendingManualReview, E.ManualReviewRequired)],
            S.Approved => [(S.ValidatingData, E.ValidationStarted), (S.CheckingPhoto, E.ValidationPassed), (S.RiskAssessment, E.PhotoCheckPassed), (S.Approved, E.AutoApproved)],
            S.Rejected => [(S.ValidatingData, E.ValidationStarted), (S.CheckingPhoto, E.ValidationPassed), (S.RiskAssessment, E.PhotoCheckPassed), (S.PendingManualReview, E.ManualReviewRequired), (S.Rejected, E.ManuallyRejected)],
            S.Failed => [(S.Failed, E.ProcessingFailed)],
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };

        var at = T0;
        foreach (var (to, evt) in path)
        {
            at = at.AddMinutes(1);
            app.TransitionTo(to, evt, null, at);
        }

        Assert.Equal(target, app.Status);
        return app;
    }
}
