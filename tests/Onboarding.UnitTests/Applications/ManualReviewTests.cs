using Onboarding.Core.Applications;
using static Onboarding.UnitTests.Applications.ApplicationStateMachineTests;

namespace Onboarding.UnitTests.Applications;

public class ManualReviewTests
{
    private static readonly DateTimeOffset DecidedAt = new(2026, 11, 23, 9, 30, 0, TimeSpan.Zero);

    public static TheoryData<ApplicationStatus> NotPendingStatuses() =>
    [
        ApplicationStatus.Submitted,
        ApplicationStatus.ValidatingData,
        ApplicationStatus.CheckingPhoto,
        ApplicationStatus.RiskAssessment,
        ApplicationStatus.Approved,
        ApplicationStatus.Rejected,
        ApplicationStatus.Failed,
    ];

    [Fact]
    public void Approve_from_pending_records_ManuallyApproved()
    {
        var app = InStatus(ApplicationStatus.PendingManualReview);

        app.Approve(DecidedAt);

        Assert.Equal(ApplicationStatus.Approved, app.Status);
        Assert.Equal(DecidedAt, app.UpdatedAt);
        var entry = app.History[^1];
        Assert.Equal(ApplicationStatus.PendingManualReview, entry.FromStatus);
        Assert.Equal(ApplicationStatus.Approved, entry.ToStatus);
        Assert.Equal(WorkflowEvent.ManuallyApproved, entry.Event);
        Assert.Null(entry.Details);
        Assert.Equal(DecidedAt, entry.OccurredAt);
    }

    [Fact]
    public void Reject_from_pending_records_ManuallyRejected_with_reason_in_details()
    {
        var app = InStatus(ApplicationStatus.PendingManualReview);

        app.Reject("Photo does not meet quality requirements", DecidedAt);

        Assert.Equal(ApplicationStatus.Rejected, app.Status);
        var entry = app.History[^1];
        Assert.Equal(ApplicationStatus.PendingManualReview, entry.FromStatus);
        Assert.Equal(ApplicationStatus.Rejected, entry.ToStatus);
        Assert.Equal(WorkflowEvent.ManuallyRejected, entry.Event);
        Assert.Equal("Photo does not meet quality requirements", entry.Details);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Reject_without_reason_records_null_details(string? reason)
    {
        var app = InStatus(ApplicationStatus.PendingManualReview);

        app.Reject(reason, DecidedAt);

        Assert.Equal(ApplicationStatus.Rejected, app.Status);
        Assert.Null(app.History[^1].Details);
    }

    [Theory]
    [MemberData(nameof(NotPendingStatuses))]
    public void Approve_refused_from_every_other_status(ApplicationStatus status)
    {
        var app = InStatus(status);
        var history = app.History.ToList();

        Assert.Throws<InvalidTransitionException>(() => app.Approve(DecidedAt));

        Assert.Equal(status, app.Status);
        Assert.Equal(history, app.History);
    }

    [Theory]
    [MemberData(nameof(NotPendingStatuses))]
    public void Reject_refused_from_every_other_status(ApplicationStatus status)
    {
        var app = InStatus(status);
        var history = app.History.ToList();

        Assert.Throws<InvalidTransitionException>(() => app.Reject("reason", DecidedAt));

        Assert.Equal(status, app.Status);
        Assert.Equal(history, app.History);
    }
}
