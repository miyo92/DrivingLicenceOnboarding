using Onboarding.Core.Applications;

namespace Onboarding.Core.Eligibility;

/// <summary>CheckingPhoto step rules. Evaluated in order; stops at the first failure.</summary>
public sealed class PhotoChecker(EligibilityOptions options)
{
    public bool IsSupportedType(string? contentType) =>
        contentType is not null && options.SupportedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase);

    /// <returns>The failure reason, or null when the photo passes.</returns>
    /// <param name="qualityScore">Score of the photo bytes; required once presence and type pass.</param>
    public string? Check(PhotoMetadata? photo, int? qualityScore)
    {
        if (photo is null)
        {
            return "No photo provided";
        }

        if (!IsSupportedType(photo.ContentType))
        {
            return $"Unsupported photo content type: {photo.ContentType}";
        }

        if (photo.SizeBytes < options.MinPhotoSizeBytes)
        {
            return $"Photo is too small: {photo.SizeBytes} bytes (minimum {options.MinPhotoSizeBytes})";
        }

        if (qualityScore is not { } score)
        {
            throw new ArgumentNullException(nameof(qualityScore), "Quality score is required for a present, supported photo");
        }

        return score < options.MinPhotoQualityScore
            ? $"Photo quality score {score} is below minimum {options.MinPhotoQualityScore}"
            : null;
    }
}
