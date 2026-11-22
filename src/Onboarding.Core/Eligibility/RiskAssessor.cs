using Onboarding.Core.Applications;

namespace Onboarding.Core.Eligibility;

/// <summary>RiskAssessment step rules. Reports every raised flag, in rule-table order; empty means auto-approve.</summary>
public sealed class RiskAssessor(EligibilityOptions options)
{
    public const int YoungApplicantAge = 21;
    public const string StandardLicenceCategory = "B";

    /// <summary>Separator between flags in the history <c>details</c> of a <c>ManualReviewRequired</c> entry.</summary>
    public const string FlagSeparator = "; ";

    public const string WatchlistFlag = "National ID on watchlist";

    /// <summary>Shown instead of the reasons when the only reason is the (withheld) watchlist flag.</summary>
    public const string WithheldReason = "Flagged for manual review";

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
            flags.Add(WatchlistFlag);
        }

        if (qualityScore is { } score && score < options.ReviewPhotoQualityThreshold)
        {
            flags.Add($"Photo quality score {score} is below review threshold {options.ReviewPhotoQualityThreshold}");
        }

        return flags;
    }

    /// <summary>
    /// The recorded flags as they may be shown to anyone who can read an application's history: the watchlist flag is
    /// withheld (it would turn the API into a watchlist lookup); all other flags are kept.
    /// </summary>
    public static string PublicDetails(string recordedFlags)
    {
        var shown = recordedFlags.Split(FlagSeparator).Where(flag => flag != WatchlistFlag).ToArray();
        return shown.Length == 0 ? WithheldReason : string.Join(FlagSeparator, shown);
    }
}
