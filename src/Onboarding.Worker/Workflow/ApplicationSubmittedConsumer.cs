using System.Text.Json;
using Microsoft.Extensions.Options;
using Onboarding.Infrastructure.Logging;
using Onboarding.Infrastructure.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Onboarding.Worker.Workflow;

/// <summary>
/// Consumes <c>ApplicationSubmitted</c> with manual acks. Acks only after the workflow reached a resting state;
/// poison messages are rejected without requeue (main-queue DLX routes them to the DLQ); processing exceptions go
/// to <see cref="ProcessingFailureHandler"/>.
/// </summary>
public sealed class ApplicationSubmittedConsumer(
    RabbitMqConnection connection,
    IServiceScopeFactory scopeFactory,
    ProcessingFailureHandler failureHandler,
    IOptions<MessagingOptions> options,
    ConsumerState state,
    ILogger<ApplicationSubmittedConsumer> logger) : BackgroundService
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Subscribe, then wait until the subscription is lost and subscribe again. Connection drops are handled by the
        // client's automatic recovery (which restores channel and consumer); this loop covers what recovery does not:
        // a channel the broker closes while the connection stays open, or a consumer the broker cancels (queue deleted).
        while (!stoppingToken.IsCancellationRequested)
        {
            IChannel? channel = null;
            try
            {
                var conn = await connection.GetAsync(stoppingToken);
                channel = await conn.CreateChannelAsync(cancellationToken: stoppingToken);

                // Re-declare on every subscribe: covers a queue deleted while the connection stayed open.
                await Topology.DeclareAsync(channel, options.Value, stoppingToken);
                await channel.BasicQosAsync(0, options.Value.PrefetchCount, global: false, stoppingToken);

                var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var subscribed = channel;
                channel.ChannelShutdownAsync += (_, _) =>
                {
                    if (conn.IsOpen)
                    {
                        lost.TrySetResult(); // channel-level close; recovery will not reopen it
                    }

                    return Task.CompletedTask;
                };

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += (_, delivery) => HandleAsync(subscribed, delivery, stoppingToken);
                consumer.UnregisteredAsync += (_, _) =>
                {
                    lost.TrySetResult(); // broker cancelled the subscription
                    return Task.CompletedTask;
                };
                await channel.BasicConsumeAsync(Topology.SubmittedQueue, autoAck: false, consumer, stoppingToken);

                state.IsConsuming = true;
                logger.LogInformation("Consuming {Queue} (prefetch {Prefetch})", Topology.SubmittedQueue, options.Value.PrefetchCount);
                await lost.Task.WaitAsync(stoppingToken);

                state.IsConsuming = false;
                logger.LogWarning("Subscription to {Queue} was lost; resubscribing in {Delay}", Topology.SubmittedQueue, ReconnectDelay);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                state.IsConsuming = false;
                logger.LogWarning(ex, "Could not start consuming; retrying in {Delay}", ReconnectDelay);
            }
            finally
            {
                if (channel is not null && !stoppingToken.IsCancellationRequested)
                {
                    await DisposeQuietlyAsync(channel);
                }
            }

            await Task.Delay(ReconnectDelay, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static async Task DisposeQuietlyAsync(IChannel channel)
    {
        try
        {
            await channel.DisposeAsync();
        }
        catch
        {
            // Already closed by the broker; nothing left to release.
        }
    }

    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        using var messageScope = logger.BeginPropertyScope(("messageId", delivery.BasicProperties.MessageId));

        if (!TryParse(delivery.Body, out var applicationId))
        {
            logger.LogError("Rejecting unparseable message to the dead-letter queue");
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, ct);
            return;
        }

        using var applicationScope = logger.BeginPropertyScope(("applicationId", applicationId));
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<WorkflowProcessor>();
            var status = await processor.RunAsync(applicationId, ct);

            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
            logger.LogInformation("Message acknowledged; application rests in {Status}", status);
        }
        catch (ApplicationNotFoundException ex)
        {
            logger.LogError("Rejecting message to the dead-letter queue: {Error}", ex.Message);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: leave unacked; the broker redelivers and processing resumes from persisted state.
        }
        catch (Exception ex)
        {
            // Any other exception is treated as transient: delay-queue retry, or Failed + DLQ once exhausted.
            try
            {
                await failureHandler.HandleAsync(channel, delivery, applicationId, ex, ct);
            }
            catch (Exception handlerError)
            {
                // E.g. the channel closed: the delivery stays unacked and the broker redelivers it.
                logger.LogError(handlerError, "Failure handling did not complete; message will be redelivered");
            }
        }
    }

    private static bool TryParse(ReadOnlyMemory<byte> body, out Guid applicationId)
    {
        applicationId = Guid.Empty;
        try
        {
            var message = JsonSerializer.Deserialize<ApplicationSubmittedMessage>(body.Span, ApplicationSubmittedMessage.SerializerOptions);
            applicationId = message?.ApplicationId ?? Guid.Empty;
        }
        catch (JsonException)
        {
            return false;
        }

        return applicationId != Guid.Empty;
    }
}
