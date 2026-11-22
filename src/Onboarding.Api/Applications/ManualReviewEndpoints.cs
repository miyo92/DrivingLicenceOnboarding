using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Onboarding.Core.Applications;
using Onboarding.Infrastructure.Logging;
using Onboarding.Infrastructure.Persistence;

namespace Onboarding.Api.Applications;

public sealed record RejectApplicationRequest(string? Reason);

/// <summary>
/// Reviewer actions on applications in <see cref="ApplicationStatus.PendingManualReview"/>. Unauthenticated in this
/// change (see README). Concurrent actions are serialized by the <c>xmin</c> concurrency token: one wins, the other gets 409.
/// </summary>
internal static class ManualReviewEndpoints
{
    public const int MaxReasonLength = 1000;

    public static RouteGroupBuilder MapManualReviewEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{applicationId:guid}/approve", ApproveApplication)
            .WithName(nameof(ApproveApplication))
            .Produces<ApplicationStatusResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{applicationId:guid}/reject", RejectApplication)
            .WithName(nameof(RejectApplication))
            .Produces<ApplicationStatusResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return group;
    }

    private static Task<IResult> ApproveApplication(
        Guid applicationId, OnboardingDbContext db, TimeProvider time, ILoggerFactory loggerFactory, CancellationToken ct) =>
        DecideAsync(applicationId, db, loggerFactory, (application, now) => application.Approve(now), time, ct);

    private static Task<IResult> RejectApplication(
        Guid applicationId,
        [FromBody] RejectApplicationRequest? request,
        OnboardingDbContext db,
        TimeProvider time,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var reason = string.IsNullOrWhiteSpace(request?.Reason) ? null : request.Reason.Trim();
        if (reason?.Length > MaxReasonLength)
        {
            return Task.FromResult(Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["reason"] = [$"reason must not exceed {MaxReasonLength} characters."],
            }));
        }

        return DecideAsync(applicationId, db, loggerFactory, (application, now) => application.Reject(reason, now), time, ct);
    }

    private static async Task<IResult> DecideAsync(
        Guid applicationId,
        OnboardingDbContext db,
        ILoggerFactory loggerFactory,
        Action<Application, DateTimeOffset> decide,
        TimeProvider time,
        CancellationToken ct)
    {
        var application = await db.Applications.SingleOrDefaultAsync(a => a.Id == applicationId, ct);
        if (application is null)
        {
            return ApplicationEndpoints.NotFound(applicationId);
        }

        if (application.Status != ApplicationStatus.PendingManualReview)
        {
            return Conflict(application.Status);
        }

        decide(application, time.GetUtcNow());
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another reviewer (or request) changed it first; report what it is now.
            var current = await db.Applications.AsNoTracking()
                .Where(a => a.Id == applicationId)
                .Select(a => a.Status)
                .SingleAsync(ct);
            var raceLogger = loggerFactory.CreateLogger(typeof(ManualReviewEndpoints));
            using (raceLogger.BeginPropertyScope(("applicationId", applicationId)))
            {
                raceLogger.LogInformation("Manual review decision lost a concurrent update; application is now {Status}", current);
            }

            return Conflict(current);
        }

        var logger = loggerFactory.CreateLogger(typeof(ManualReviewEndpoints));
        using (logger.BeginPropertyScope(("applicationId", applicationId)))
        {
            logger.LogInformation("Manual review decision: {Event} -> {Status}", application.History[^1].Event, application.Status);
        }

        return Results.Ok(new ApplicationStatusResponse(application.Id, application.Status));
    }

    private static IResult Conflict(ApplicationStatus current) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Application is not pending manual review",
            detail: $"Application is in status {current}",
            // Not "status": that key is ProblemDetails' HTTP status code.
            extensions: new Dictionary<string, object?> { ["currentStatus"] = current.ToString() });
}
