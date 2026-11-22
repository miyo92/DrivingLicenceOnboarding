using Onboarding.Core.Applications;
using Onboarding.Core.Eligibility;

namespace Onboarding.UnitTests.Eligibility;

public class RiskAssessorTests
{
    private static readonly DateOnly AsOf = new(2026, 11, 22);
    private readonly RiskAssessor _assessor = new(new EligibilityOptions());

    private static Applicant Clean() => DataValidatorTests.Valid();

    [Fact]
    public void Clean_applicant_aged_30_raises_no_flags()
    {
        Assert.Empty(_assessor.Assess(Clean(), 85, AsOf));
    }

    [Fact]
    public void Under_21_is_flagged()
    {
        var result = _assessor.Assess(Clean() with { DateOfBirth = new DateOnly(2007, 6, 1) }, 85, AsOf);

        Assert.Equal(["Applicant is below 21"], result);
    }

    [Fact]
    public void Exactly_21_on_submission_day_is_not_flagged()
    {
        Assert.Empty(_assessor.Assess(Clean() with { DateOfBirth = new DateOnly(2005, 11, 22) }, 85, AsOf));
        Assert.Equal(["Applicant is below 21"], _assessor.Assess(Clean() with { DateOfBirth = new DateOnly(2005, 11, 23) }, 85, AsOf));
    }

    [Fact]
    public void Leap_day_birthday_reaches_21_on_1_March_in_non_leap_year()
    {
        var applicant = Clean() with { DateOfBirth = new DateOnly(2004, 2, 29) };

        Assert.Equal(["Applicant is below 21"], _assessor.Assess(applicant, 85, new DateOnly(2025, 2, 28)));
        Assert.Empty(_assessor.Assess(applicant, 85, new DateOnly(2025, 3, 1)));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("C")]
    [InlineData("b")]
    public void Non_B_category_is_flagged(string category)
    {
        var result = _assessor.Assess(Clean() with { LicenceCategory = category }, 85, AsOf);

        Assert.Equal([$"Licence category {category} requires manual review"], result);
    }

    [Theory]
    [InlineData("WL000001")]
    [InlineData("wl000001")]
    [InlineData("Wl000001")]
    public void Watchlist_hit_is_flagged_regardless_of_case(string nationalId)
    {
        var result = _assessor.Assess(Clean() with { NationalId = nationalId }, 85, AsOf);

        Assert.Equal(["National ID on watchlist"], result);
    }

    [Fact]
    public void Quality_below_review_threshold_is_flagged()
    {
        Assert.Equal(["Photo quality score 69 is below review threshold 70"], _assessor.Assess(Clean(), 69, AsOf));
        Assert.Empty(_assessor.Assess(Clean(), 70, AsOf));
    }

    [Fact]
    public void All_flags_reported_together_in_rule_order()
    {
        var applicant = Clean() with { DateOfBirth = new DateOnly(2007, 1, 1), LicenceCategory = "C", NationalId = "WL000001" };

        var result = _assessor.Assess(applicant, 50, AsOf);

        Assert.Equal(
            [
                "Applicant is below 21",
                "Licence category C requires manual review",
                "National ID on watchlist",
                "Photo quality score 50 is below review threshold 70",
            ],
            result);
    }

    [Theory]
    [InlineData("Applicant is below 21", "Applicant is below 21")]
    [InlineData("National ID on watchlist", "Flagged for manual review")]
    [InlineData("Applicant is below 21; National ID on watchlist", "Applicant is below 21")]
    [InlineData(
        "Applicant is below 21; Licence category C requires manual review; National ID on watchlist; Photo quality score 50 is below review threshold 70",
        "Applicant is below 21; Licence category C requires manual review; Photo quality score 50 is below review threshold 70")]
    public void PublicDetails_withholds_only_the_watchlist_flag(string recorded, string shown)
    {
        Assert.Equal(shown, RiskAssessor.PublicDetails(recorded));
    }

    [Fact]
    public void PublicDetails_round_trips_what_Assess_records()
    {
        var flags = _assessor.Assess(Clean() with { DateOfBirth = new DateOnly(2007, 1, 1), NationalId = "WL000001" }, 85, AsOf);

        Assert.Equal("Applicant is below 21", RiskAssessor.PublicDetails(string.Join(RiskAssessor.FlagSeparator, flags)));
    }

    [Fact]
    public void Watchlist_and_threshold_come_from_options()
    {
        var assessor = new RiskAssessor(new EligibilityOptions { Watchlist = ["X1"], ReviewPhotoQualityThreshold = 90 });

        Assert.Empty(assessor.Assess(Clean() with { NationalId = "WL000001" }, 90, AsOf));
        Assert.Equal(["National ID on watchlist"], assessor.Assess(Clean() with { NationalId = "X1" }, 90, AsOf));
        Assert.Equal(["Photo quality score 89 is below review threshold 90"], assessor.Assess(Clean(), 89, AsOf));
    }
}
