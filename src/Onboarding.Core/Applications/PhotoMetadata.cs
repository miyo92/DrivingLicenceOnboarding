namespace Onboarding.Core.Applications;

public sealed class PhotoMetadata
{
    public PhotoMetadata(string storageKey, string contentType, long sizeBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        ArgumentOutOfRangeException.ThrowIfNegative(sizeBytes);
        StorageKey = storageKey;
        ContentType = contentType ?? string.Empty;
        SizeBytes = sizeBytes;
    }

    /// <summary>Server-generated file name on the photo volume.</summary>
    public string StorageKey { get; private set; }

    /// <summary>Client-declared content type.</summary>
    public string ContentType { get; private set; }

    public long SizeBytes { get; private set; }

    /// <summary>Simulated 0-100 score; set during the photo check via <see cref="Application.RecordQualityScore"/>.</summary>
    public int? QualityScore { get; internal set; }
}
