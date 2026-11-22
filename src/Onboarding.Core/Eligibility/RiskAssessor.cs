using Onboarding.Core.Applications;

namespace Onboarding.Core.Eligibility;

/// <summary>RiskAssessment step rules. Reports every raised flag, in rule-table order; empty means auto-approve.</summary>
public sealed class RiskAssessor(EligibilityOptions options)
{
    public const int YoungApplicantAge = 21;
    public const string StandardLicenceCategory = "B";

    public IReadOnlyList<string> Assess(Applicant applicant, int? qualityScore, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(applicant);
        var flags = new List<string>();

        if (applicant.DateOfBirth is { } dob && Age.At(dob, asOf) < YoungApplicantAge)
        {
            flags.Add("Applicant is below 21");
        }

        if (applicant.LicenceCategory != StandardLicenceCategory)
        {
            flags.Add($"Licence category {applicant.LicenceCategory} requires manual review");
        }

        if (applicant.NationalId is not null && options.Watchlist.Contains(applicant.NationalId, StringComparer.OrdinalIgnoreCase))
        {
            flags.Add("National ID on watchlist");
        }

        if (qualityScore is { } score && score < options.ReviewPhotoQualityThreshold)
        {
            flags.Add($"Photo quality score {score} is below review threshold {options.ReviewPhotoQualityThreshold}");
        }

        return flags;
    }
}
