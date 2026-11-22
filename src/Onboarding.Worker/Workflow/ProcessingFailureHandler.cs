using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Onboarding.Core.Applications;
using Onboarding.Infrastructure.Messaging;
using Onboarding.Infrastructure.Persistence;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Onboarding.Worker.Workflow;

/// <summary>
/// Handles an exception that escaped the workflow (treated as transient): republish to the attempt's delay queue,
/// or — when attempts are exhausted — fail the application and dead-letter the message. The original delivery is
/// acked only after the replacement message is confirmed by the broker.
/// </summary>
public sealed class ProcessingFailureHandler(
    RabbitMqPublisher publisher,
    IServiceScopeFactory scopeFactory,
    TimeProvider time,
    IOptions<MessagingOptions> options,
    ILogger<ProcessingFailureHandler> logger)
{
    public const int MaxErrorLength = 500;
    private static readonly TimeSpan RequeueDelay = TimeSpan.FromSeconds(5);

    public async Task HandleAsync(IChannel channel, BasicDeliverEventArgs delivery, Guid applicationId, Exception error, CancellationToken ct)
    {
        var attempt = ReadAttempt(delivery.BasicProperties.Headers);
        var summary = Summarize(error);

        if (attempt < options.Value.MaxAttempts)
        {
            await RetryAsync(channel, delivery, attempt, summary, error, ct);
        }
        else
        {
            await ExhaustAsync(channel, delivery, applicationId, attempt, summary, error, ct);
        }
    }

    private async Task RetryAsync(IChannel channel, BasicDeliverEventArgs delivery, int attempt, string summary, Exception error, CancellationToken ct)
    {
        var delay = options.Value.RetryDelaysSeconds[attempt - 1];
        try
        {
            await publisher.PublishAsync(
                exchange: string.Empty, // default exchange routes by queue name
                Topology.RetryQueue(attempt),
                Copy(delivery, (Topology.AttemptHeader, attempt + 1)),
                ct);
        }
        catch (Exception publishError) when (!ct.IsCancellationRequested)
        {
            // Could not schedule the retry: requeue the original (same attempt) rather than lose it, after a pause so a
            // persistent publish failure cannot spin. If it never recovers, the stale-application sweeper ends it.
            logger.LogError(publishError, "Attempt {Attempt} failed ({Error}) and the retry could not be scheduled; requeueing in {Delay}",
                attempt, summary, RequeueDelay);
            await Task.Delay(RequeueDelay, time, ct);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, ct);
            return;
        }

        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
        logger.LogWarning(error, "Attempt {Attempt} of {MaxAttempts} failed ({Error}); retrying in {Delay}s",
            attempt, options.Value.MaxAttempts, summary, delay);
    }

    private async Task ExhaustAsync(IChannel channel, BasicDeliverEventArgs delivery, Guid applicationId, int attempt, string summary, Exception error, CancellationToken ct)
    {
        logger.LogError(error, "Attempt {Attempt} of {MaxAttempts} failed ({Error}); retries exhausted", attempt, options.Value.MaxAttempts, summary);
        await TryFailApplicationAsync(applicationId, summary, ct);

        try
        {
            await publisher.PublishAsync(
                Topology.DeadLetterExchange,
                routingKey: string.Empty,
                Copy(delivery, (Topology.AttemptHeader, attempt), (Topology.ErrorHeader, summary)),
                ct);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
            logger.LogWarning("Message dead-lettered to {Queue}", Topology.DeadLetterQueue);
        }
        catch (Exception publishError) when (!ct.IsCancellationRequested)
        {
            // Fallback: reject; the main queue's DLX still routes it to the DLQ, without the x-error header.
            logger.LogError(publishError, "Dead-letter publish failed; rejecting message to the queue's dead-letter exchange");
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, ct);
        }
    }

    private async Task TryFailApplicationAsync(Guid applicationId, string summary, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OnboardingDbContext>();
            var application = await db.Applications.SingleOrDefaultAsync(a => a.Id == applicationId, ct);
            if (application is null || application.IsTerminal || application.Status == ApplicationStatus.PendingManualReview)
            {
                return;
            }

            application.TransitionTo(ApplicationStatus.Failed, WorkflowEvent.ProcessingFailed, summary, time.GetUtcNow());
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Application failed after exhausting retries");
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another worker or the sweeper moved it at the same moment; nothing is stuck.
            logger.LogInformation("Application changed concurrently; not marking it Failed");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Typically the same outage that caused the failures. The stale-application sweeper fails it later.
            logger.LogCritical(ex, "Could not mark the application Failed after exhausting retries; leaving it to the stale-application sweeper");
        }
    }

    private static OutgoingMessage Copy(BasicDeliverEventArgs delivery, params (string Key, object Value)[] headers)
    {
        var merged = new Dictionary<string, object?>(delivery.BasicProperties.Headers ?? new Dictionary<string, object?>());
        foreach (var (key, value) in headers)
        {
            merged[key] = value;
        }

        return new OutgoingMessage(
            delivery.BasicProperties.MessageId ?? Guid.NewGuid().ToString(),
            delivery.BasicProperties.Type ?? ApplicationSubmittedMessage.TypeName,
            delivery.Body.ToArray(),
            merged);
    }

    /// <summary>Delivery attempt from the <c>x-attempt</c> header; absent or unreadable means the first attempt.</summary>
    internal static int ReadAttempt(IDictionary<string, object?>? headers)
    {
        if (headers is null || !headers.TryGetValue(Topology.AttemptHeader, out var value))
        {
            return 1;
        }

        var attempt = value switch
        {
            int i => i,
            long l => (int)l,
            byte[] bytes when int.TryParse(Encoding.UTF8.GetString(bytes), out var parsed) => parsed,
            _ => 1,
        };
        return Math.Max(attempt, 1);
    }

    internal static string Summarize(Exception error)
    {
        var root = error.GetBaseException();
        var summary = $"{root.GetType().Name}: {root.Message}";
        return summary.Length > MaxErrorLength ? summary[..MaxErrorLength] : summary;
    }
}
