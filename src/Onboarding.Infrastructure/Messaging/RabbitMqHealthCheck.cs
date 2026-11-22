using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Onboarding.Infrastructure.Messaging;

/// <summary>Healthy when the shared broker connection is open (connecting it if none exists yet).</summary>
public sealed class RabbitMqHealthCheck(RabbitMqConnection connection) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        if (connection.IsOpen)
        {
            return HealthCheckResult.Healthy();
        }

        try
        {
            await connection.GetAsync(ct);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("RabbitMQ connection is not open", ex);
        }
    }
}
