using RabbitMQ.Client;

namespace Onboarding.Infrastructure.Messaging;

/// <summary>Broker topology, declared idempotently by every service on each new connection.</summary>
public static class Topology
{
    public const string Exchange = "onboarding";
    public const string DeadLetterExchange = "onboarding.dlx";
    public const string SubmittedRoutingKey = "application.submitted";
    public const string SubmittedQueue = "applications.submitted";
    public const string DeadLetterQueue = "applications.dlq";

    public const string AttemptHeader = "x-attempt";
    public const string ErrorHeader = "x-error";

    /// <summary>Delay queue holding messages before attempt <paramref name="attempt"/> + 1.</summary>
    public static string RetryQueue(int attempt) => $"applications.retry.{attempt}";

    public static async Task DeclareAsync(IChannel channel, MessagingOptions options, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(DeadLetterExchange, ExchangeType.Fanout, durable: true, autoDelete: false, cancellationToken: ct);

        // Main queue dead-letters rejected (poison) messages to the DLX.
        await channel.QueueDeclareAsync(SubmittedQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = DeadLetterExchange }, cancellationToken: ct);
        await channel.QueueBindAsync(SubmittedQueue, Exchange, SubmittedRoutingKey, cancellationToken: ct);

        // Delay queues: no consumers; expired messages dead-letter back to the main queue.
        for (var attempt = 1; attempt < options.MaxAttempts; attempt++)
        {
            await channel.QueueDeclareAsync(RetryQueue(attempt), durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?>
                {
                    ["x-message-ttl"] = options.RetryDelaysSeconds[attempt - 1] * 1000,
                    ["x-dead-letter-exchange"] = Exchange,
                    ["x-dead-letter-routing-key"] = SubmittedRoutingKey,
                },
                cancellationToken: ct);
        }

        await channel.QueueDeclareAsync(DeadLetterQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync(DeadLetterQueue, DeadLetterExchange, routingKey: string.Empty, cancellationToken: ct);
    }
}
