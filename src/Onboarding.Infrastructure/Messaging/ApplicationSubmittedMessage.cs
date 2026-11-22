using System.Text.Json;

namespace Onboarding.Infrastructure.Messaging;

/// <summary>Body of the <c>ApplicationSubmitted</c> message.</summary>
public sealed record ApplicationSubmittedMessage(Guid ApplicationId)
{
    public const string TypeName = "ApplicationSubmitted";

    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);
}
