using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Onboarding.Infrastructure.Logging;
using Onboarding.Infrastructure.Messaging;
using Onboarding.Infrastructure.Persistence;

namespace Onboarding.Api.Outbox;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    public int PollIntervalMs { get; set; } = 1000;
    public int BatchSize { get; set; } = 50;
}

/// <summary>
/// Publishes unsent outbox rows to RabbitMQ and marks them sent only after the broker confirmed them.
/// At-least-once: a crash between confirm and commit republishes; the consumer is idempotent.
/// </summary>
public sealed class OutboxRelay(
    IServiceScopeFactory scopeFactory,
    RabbitMqPublisher publisher,
    TimeProvider time,
    IOptions<OutboxOptions> options,
    ILogger<OutboxRelay> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        while (!stoppingToken.IsCancellationRequested)
        {
            var fullBatch = false;
            try
            {
                fullBatch = await PublishBatchAsync(o.BatchSize, stoppingToken) == o.BatchSize;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Outbox relay cycle failed; retrying in {PollIntervalMs} ms", o.PollIntervalMs);
            }

            // A full batch suggests a backlog: continue immediately.
            if (!fullBatch)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(o.PollIntervalMs), time, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <returns>Number of messages confirmed by the broker.</returns>
    private async Task<int> PublishBatchAsync(int batchSize, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OnboardingDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Row locks keep concurrent relays (several API replicas) from publishing the same rows.
        var batch = await db.OutboxMessages
            .FromSql($"""
                SELECT * FROM outbox_messages
                WHERE sent_at IS NULL
                ORDER BY created_at
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct);

        if (batch.Count == 0)
        {
            return 0;
        }

        var published = 0;
        foreach (var message in batch)
        {
            using var _ = logger.BeginPropertyScope(("applicationId", message.ApplicationId), ("messageId", message.Id));
            try
            {
                await publisher.PublishAsync(
                    Topology.Exchange,
                    Topology.SubmittedRoutingKey,
                    new OutgoingMessage(message.Id.ToString(), message.Type, Encoding.UTF8.GetBytes(message.Payload)),
                    ct);
                message.MarkSent(time.GetUtcNow());
                published++;
                logger.LogInformation("Outbox message published");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Stop the batch: keeps order and avoids hammering an unavailable broker.
                message.RecordFailedAttempt($"{ex.GetType().Name}: {ex.Message}");
                logger.LogWarning("Outbox publish failed (attempt {Attempt}): {Error}", message.PublishAttempts, ex.Message);
                break;
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return published;
    }
}
