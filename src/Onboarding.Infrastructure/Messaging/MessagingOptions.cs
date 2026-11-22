using Microsoft.Extensions.Options;

namespace Onboarding.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";
}

public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    /// <summary>Total delivery attempts per message, including the first.</summary>
    public int MaxAttempts { get; set; } = 4;

    /// <summary>Delay before attempt n+1, for n = 1..MaxAttempts-1. Fixed per retry queue (queue-level TTL).</summary>
    public int[] RetryDelaysSeconds { get; set; } = [5, 20, 80];

    public ushort PrefetchCount { get; set; } = 10;

    /// <summary>Upper bound for one retry delay (1 day); the queue TTL is declared in milliseconds as a 32-bit integer.</summary>
    public const int MaxRetryDelaySeconds = 86_400;

    public bool IsValid(out string? error)
    {
        error = MaxAttempts < 1
            ? "Messaging:MaxAttempts must be at least 1"
            : RetryDelaysSeconds.Length != MaxAttempts - 1
                ? $"Messaging:RetryDelaysSeconds must have MaxAttempts - 1 = {MaxAttempts - 1} entries, has {RetryDelaysSeconds.Length}"
                : RetryDelaysSeconds.Any(d => d is <= 0 or > MaxRetryDelaySeconds)
                    ? $"Messaging:RetryDelaysSeconds entries must be between 1 and {MaxRetryDelaySeconds}"
                    : null;
        return error is null;
    }
}

/// <summary>Fails startup with the specific <see cref="MessagingOptions.IsValid"/> error.</summary>
public sealed class MessagingOptionsValidator : IValidateOptions<MessagingOptions>
{
    public ValidateOptionsResult Validate(string? name, MessagingOptions options) =>
        options.IsValid(out var error)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(error!);
}
