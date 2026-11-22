using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Onboarding.Worker.Workflow;

/// <summary>Whether <see cref="ApplicationSubmittedConsumer"/> currently holds a subscription.</summary>
public sealed class ConsumerState
{
    private volatile bool _isConsuming;

    public bool IsConsuming
    {
        get => _isConsuming;
        set => _isConsuming = value;
    }
}

/// <summary>Unhealthy while the worker is not subscribed to the queue, even if the broker connection is open.</summary>
public sealed class ConsumerHealthCheck(ConsumerState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        Task.FromResult(state.IsConsuming
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Not subscribed to the applications queue"));
}
