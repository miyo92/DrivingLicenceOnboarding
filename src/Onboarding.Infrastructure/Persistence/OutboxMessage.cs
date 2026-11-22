using Onboarding.Infrastructure.Messaging;

namespace Onboarding.Infrastructure.Persistence;

/// <summary>Broker message written in the same transaction as the business change; published later by the relay.</summary>
public sealed class OutboxMessage
{
    private OutboxMessage() { }

    public Guid Id { get; private set; }
    public Guid ApplicationId { get; private set; }
    public string Type { get; private set; } = string.Empty;
    public string Payload { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }
    public int PublishAttempts { get; private set; }
    public string? LastError { get; private set; }

    public static OutboxMessage ApplicationSubmitted(Guid applicationId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        ApplicationId = applicationId,
        Type = ApplicationSubmittedMessage.TypeName,
        Payload = new ApplicationSubmittedMessage(applicationId).ToJson(),
        CreatedAt = now,
    };

    public void MarkSent(DateTimeOffset at) => SentAt = at;

    public void RecordFailedAttempt(string error)
    {
        PublishAttempts++;
        LastError = error.Length > 1000 ? error[..1000] : error;
    }
}
