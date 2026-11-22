using RabbitMQ.Client;

namespace Onboarding.Infrastructure.Messaging;

public sealed record OutgoingMessage(
    string MessageId,
    string Type,
    ReadOnlyMemory<byte> Body,
    IReadOnlyDictionary<string, object?>? Headers = null);

/// <summary>
/// Publishes persistent messages on a confirm-enabled channel and returns only after the broker confirmed them.
/// Throws when the broker nacks, returns (unroutable), times out or is unreachable.
/// </summary>
public sealed class RabbitMqPublisher(RabbitMqConnection connection) : IAsyncDisposable
{
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChannel? _channel;

    public async Task PublishAsync(string exchange, string routingKey, OutgoingMessage message, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConfirmTimeout);

        await _gate.WaitAsync(ct);
        try
        {
            var channel = await GetChannelAsync(timeout.Token);
            var properties = new BasicProperties
            {
                MessageId = message.MessageId,
                Type = message.Type,
                ContentType = "application/json",
                DeliveryMode = DeliveryModes.Persistent,
                Headers = message.Headers?.ToDictionary(h => h.Key, h => h.Value),
            };

            try
            {
                await channel.BasicPublishAsync(exchange, routingKey, mandatory: true, properties, message.Body, timeout.Token);
            }
            catch
            {
                // The channel state is unknown after a failed or timed-out confirm; start fresh next time.
                await ResetChannelAsync();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true } open)
        {
            return open;
        }

        await ResetChannelAsync();
        var conn = await connection.GetAsync(ct);
        _channel = await conn.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            ct);
        return _channel;
    }

    private async Task ResetChannelAsync()
    {
        if (_channel is null)
        {
            return;
        }

        try
        {
            await _channel.DisposeAsync();
        }
        catch
        {
            // Disposing a broken channel can throw; it is discarded either way.
        }

        _channel = null;
    }

    public async ValueTask DisposeAsync()
    {
        await ResetChannelAsync();
        _gate.Dispose();
    }
}
