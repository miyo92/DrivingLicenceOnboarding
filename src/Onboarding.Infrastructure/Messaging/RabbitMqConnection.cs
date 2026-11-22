using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Onboarding.Infrastructure.Messaging;

/// <summary>
/// One shared broker connection per process. Created lazily and re-created on demand if the initial connect
/// failed or recovery gave up; the client library's automatic recovery handles transient drops.
/// Topology is declared on every newly created connection.
/// </summary>
public sealed class RabbitMqConnection(
    IOptions<RabbitMqOptions> rabbitOptions,
    IOptions<MessagingOptions> messagingOptions,
    ILogger<RabbitMqConnection> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    public bool IsOpen => _connection?.IsOpen == true;

    public async Task<IConnection> GetAsync(CancellationToken ct)
    {
        if (_connection is { IsOpen: true } open)
        {
            return open;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_connection is { IsOpen: true } current)
            {
                return current;
            }

            // An existing connection that is recovering is kept: auto-recovery will reopen it.
            if (_connection is not null && !IsGivenUp(_connection))
            {
                throw new BrokerUnavailableException("Broker connection is recovering");
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }

            var o = rabbitOptions.Value;
            var factory = new ConnectionFactory
            {
                HostName = o.HostName,
                Port = o.Port,
                UserName = o.UserName,
                Password = o.Password,
                VirtualHost = o.VirtualHost,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
                RequestedConnectionTimeout = TimeSpan.FromSeconds(5),
                ClientProvidedName = AppDomain.CurrentDomain.FriendlyName,
            };

            var connection = await factory.CreateConnectionAsync(ct);
            try
            {
                await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
                await Topology.DeclareAsync(channel, messagingOptions.Value, ct);
            }
            catch
            {
                // E.g. PRECONDITION_FAILED when configured retry delays differ from the declared queues.
                // Close it, otherwise every caller's retry would leak another connection.
                await connection.DisposeAsync();
                throw;
            }

            logger.LogInformation("Connected to RabbitMQ at {Host}:{Port}; topology declared", o.HostName, o.Port);
            _connection = connection;
            return connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Auto-recovery only covers connections that were once open; a connection closed by the application or
    // one whose close reason is not a network failure will not come back.
    private static bool IsGivenUp(IConnection connection) =>
        connection.CloseReason is { Initiator: ShutdownInitiator.Application };

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _gate.Dispose();
    }
}

public sealed class BrokerUnavailableException(string message) : Exception(message);
