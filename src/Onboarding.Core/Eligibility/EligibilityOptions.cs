namespace Onboarding.Core.Eligibility;

/// <summary>Thresholds and watchlist for the simulated checks. Defaults per specs/eligibility-checks "Configuration Defaults".</summary>
public sealed class EligibilityOptions
{
    public const string SectionName = "Eligibility";

    public string[] SupportedContentTypes { get; set; } = ["image/jpeg", "image/png"];
    public long MinPhotoSizeBytes { get; set; } = 10240;
    public int MinPhotoQualityScore { get; set; } = 40;
    public int ReviewPhotoQualityThreshold { get; set; } = 70;
    public string[] Watchlist { get; set; } = ["WL000001"];
}
