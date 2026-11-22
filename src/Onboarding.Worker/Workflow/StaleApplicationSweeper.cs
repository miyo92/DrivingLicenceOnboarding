using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Onboarding.Core.Applications;
using Onboarding.Infrastructure.Logging;
using Onboarding.Infrastructure.Messaging;
using Onboarding.Infrastructure.Persistence;

namespace Onboarding.Worker.Workflow;

public sealed class SweeperOptions
{
    public const string SectionName = "Sweeper";

    public int IntervalSeconds { get; set; } = 60;
    public int StaleAfterMinutes { get; set; } = 15;
}

/// <summary>
/// Safety net for applications stuck mid-workflow, e.g. when marking them Failed after exhausted retries itself failed.
/// Fails applications in ValidatingData / CheckingPhoto / RiskAssessment without progress for
/// <see cref="SweeperOptions.StaleAfterMinutes"/> that have no unsent outbox row. A later delivery acks as a no-op.
/// </summary>
/// <remarks>
/// <see cref="ApplicationStatus.Submitted"/> is never swept: such an application may simply be waiting in a queue backlog
/// (e.g. after a worker outage), and failing it would reject an applicant who was never checked. The first sweep waits one
/// interval after startup so the consumer can resume first.
/// </remarks>
public sealed class StaleApplicationSweeper(
    IServiceScopeFactory scopeFactory,
    TimeProvider time,
    IOptions<SweeperOptions> options,
    IOptions<MessagingOptions> messagingOptions,
    ILogger<StaleApplicationSweeper> logger) : BackgroundService
{
    private const int BatchSize = 100;

    private static readonly ApplicationStatus[] Sweepable =
        [ApplicationStatus.ValidatingData, ApplicationStatus.CheckingPhoto, ApplicationStatus.RiskAssessment];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        var retryWindow = TimeSpan.FromSeconds(messagingOptions.Value.RetryDelaysSeconds.Sum());
        if (TimeSpan.FromMinutes(o.StaleAfterMinutes) <= retryWindow)
        {
            logger.LogWarning(
                "Sweeper:StaleAfterMinutes ({StaleAfter} min) does not exceed the retry window ({RetryWindow}); applications still being retried may be failed",
                o.StaleAfterMinutes, retryWindow);
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(o.IntervalSeconds), time);
        while (await WaitForNextTickAsync(timer, stoppingToken))
        {
            try
            {
                await SweepAsync(o.StaleAfterMinutes, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Stale application sweep failed; retrying next interval");
            }
        }
    }

    private async Task SweepAsync(int staleAfterMinutes, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OnboardingDbContext>();
        var cutoff = time.GetUtcNow().AddMinutes(-staleAfterMinutes);

        var staleIds = await db.Applications
            .Where(a => Sweepable.Contains(a.Status) && a.UpdatedAt < cutoff)
            .Where(a => !db.OutboxMessages.Any(m => m.ApplicationId == a.Id && m.SentAt == null))
            .OrderBy(a => a.UpdatedAt)
            .Select(a => a.Id)
            .Take(BatchSize)
            .ToListAsync(ct);

        foreach (var id in staleIds)
        {
            using var _ = logger.BeginPropertyScope(("applicationId", id));
            db.ChangeTracker.Clear();
            var application = await db.Applications.SingleAsync(a => a.Id == id, ct);
            if (!Sweepable.Contains(application.Status))
            {
                continue; // progressed since the query
            }

            var from = application.Status;
            application.TransitionTo(
                ApplicationStatus.Failed,
                WorkflowEvent.ProcessingFailed,
                $"Processing timed out after {staleAfterMinutes} minute{(staleAfterMinutes == 1 ? "" : "s")} without progress",
                time.GetUtcNow());
            try
            {
                await db.SaveChangesAsync(ct);
                logger.LogWarning("Stale application failed: no progress in {Status} for over {StaleAfterMinutes} min", from, staleAfterMinutes);
            }
            catch (DbUpdateConcurrencyException)
            {
                logger.LogInformation("Stale application progressed concurrently; skipped");
            }
        }
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
