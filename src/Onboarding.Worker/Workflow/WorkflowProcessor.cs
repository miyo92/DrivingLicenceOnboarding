using Microsoft.EntityFrameworkCore;
using Onboarding.Core.Applications;
using Onboarding.Core.Eligibility;
using Onboarding.Infrastructure.Persistence;
using Onboarding.Infrastructure.Photos;

namespace Onboarding.Worker.Workflow;

public sealed class ApplicationNotFoundException(Guid applicationId)
    : Exception($"Application {applicationId} does not exist");

/// <summary>
/// Runs an application's workflow from its persisted status until it is terminal or pending manual review.
/// Each step commits its transition (with history) in its own transaction before the next step starts, so a
/// redelivered message resumes where the previous attempt stopped. Business-rule outcomes are transitions;
/// any exception escapes to the caller as a transient failure.
/// </summary>
public sealed class WorkflowProcessor(
    OnboardingDbContext db,
    IPhotoStore photoStore,
    PhotoChecker photoChecker,
    RiskAssessor riskAssessor,
    TimeProvider time,
    ILogger<WorkflowProcessor> logger)
{
    /// <returns>The status the application rests in: terminal or <see cref="ApplicationStatus.PendingManualReview"/>.</returns>
    /// <exception cref="ApplicationNotFoundException">No application with this id exists.</exception>
    public async Task<ApplicationStatus> RunAsync(Guid applicationId, CancellationToken ct)
    {
        while (true)
        {
            // Always start a step from freshly loaded state (another worker or the sweeper may have moved it).
            db.ChangeTracker.Clear();
            var application = await db.Applications.SingleOrDefaultAsync(a => a.Id == applicationId, ct)
                ?? throw new ApplicationNotFoundException(applicationId);

            if (application.IsTerminal || application.Status == ApplicationStatus.PendingManualReview)
            {
                return application.Status;
            }

            var from = application.Status;
            await RunStepAsync(application, ct);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                logger.LogInformation("Concurrent update while leaving {Status}; reloading", from);
                continue;
            }

            var entry = application.History[^1];
            logger.LogInformation(
                "Workflow step {Event}: {FromStatus} -> {ToStatus}{Details}",
                entry.Event, from, application.Status, entry.Details is null ? string.Empty : $" ({entry.Details})");
        }
    }

    private async Task RunStepAsync(Application application, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var asOf = DateOnly.FromDateTime(application.CreatedAt.UtcDateTime);

        switch (application.Status)
        {
            case ApplicationStatus.Submitted:
                application.TransitionTo(ApplicationStatus.ValidatingData, WorkflowEvent.ValidationStarted, null, now);
                break;

            case ApplicationStatus.ValidatingData:
                var violations = DataValidator.Validate(application.Applicant, asOf);
                if (violations.Count == 0)
                {
                    application.TransitionTo(ApplicationStatus.CheckingPhoto, WorkflowEvent.ValidationPassed, null, now);
                }
                else
                {
                    application.TransitionTo(ApplicationStatus.Failed, WorkflowEvent.ValidationFailed, string.Join("; ", violations), now);
                }

                break;

            case ApplicationStatus.CheckingPhoto:
                var photo = application.Photo;
                int? score = null;
                if (photo is not null && photoChecker.IsSupportedType(photo.ContentType))
                {
                    var bytes = await photoStore.ReadAllBytesAsync(photo.StorageKey, ct);
                    score = PhotoQuality.Score(bytes);
                    application.RecordQualityScore(score.Value);
                }

                var reason = photoChecker.Check(photo, score);
                if (reason is null)
                {
                    application.TransitionTo(ApplicationStatus.RiskAssessment, WorkflowEvent.PhotoCheckPassed, null, now);
                }
                else
                {
                    application.TransitionTo(ApplicationStatus.Failed, WorkflowEvent.PhotoCheckFailed, reason, now);
                }

                break;

            case ApplicationStatus.RiskAssessment:
                var flags = riskAssessor.Assess(application.Applicant, application.Photo?.QualityScore, asOf);
                if (flags.Count == 0)
                {
                    application.TransitionTo(ApplicationStatus.Approved, WorkflowEvent.AutoApproved, null, now);
                }
                else
                {
                    application.TransitionTo(ApplicationStatus.PendingManualReview, WorkflowEvent.ManualReviewRequired, string.Join("; ", flags), now);
                }

                break;

            default:
                throw new InvalidOperationException($"No workflow step for status {application.Status}");
        }
    }
}
