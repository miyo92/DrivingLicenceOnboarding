using Onboarding.Core.Applications;
using Onboarding.Core.Eligibility;

namespace Onboarding.UnitTests.Eligibility;

public class DataValidatorTests
{
    private static readonly DateOnly AsOf = new(2026, 11, 22);

    internal static Applicant Valid() => new()
    {
        FirstName = "Anna",
        LastName = "Muster",
        DateOfBirth = new DateOnly(1996, 5, 10),
        NationalId = "AT123456",
        Email = "anna.muster@example.com",
        PhoneNumber = "+43 660 1234567",
        Address = "Hauptstrasse 1, 1010 Wien",
        LicenceCategory = "B",
    };

    [Fact]
    public void Valid_applicant_has_no_violations()
    {
        Assert.Empty(DataValidator.Validate(Valid(), AsOf));
    }

    [Theory]
    [InlineData("firstName")]
    [InlineData("lastName")]
    [InlineData("nationalId")]
    [InlineData("email")]
    [InlineData("phoneNumber")]
    [InlineData("address")]
    [InlineData("licenceCategory")]
    public void Missing_text_field_is_required(string field)
    {
        foreach (var missing in new string?[] { null, "", "   " })
        {
            var result = DataValidator.Validate(With(Valid(), field, missing), AsOf);

            Assert.Equal([$"{field} is required"], result);
        }
    }

    [Fact]
    public void Missing_dateOfBirth_is_required_and_skips_age_rule()
    {
        var result = DataValidator.Validate(Valid() with { DateOfBirth = null }, AsOf);

        Assert.Equal(["dateOfBirth is required"], result);
    }

    [Fact]
    public void Missing_nationalId_reported_once()
    {
        var result = DataValidator.Validate(Valid() with { NationalId = null }, AsOf);

        Assert.Single(result, v => v.Contains("nationalId", StringComparison.OrdinalIgnoreCase) || v.Contains("National ID", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("anna@")]
    [InlineData("Anna <anna@example.com>")]
    [InlineData(" anna@example.com")]
    public void Invalid_email_is_reported(string email)
    {
        var result = DataValidator.Validate(Valid() with { Email = email }, AsOf);

        Assert.Equal(["Email address is invalid"], result);
    }

    [Fact]
    public void Under_18_is_reported()
    {
        var result = DataValidator.Validate(Valid() with { DateOfBirth = new DateOnly(2009, 1, 1) }, AsOf);

        Assert.Equal(["Applicant is under 18"], result);
    }

    [Fact]
    public void Exactly_18_on_submission_day_passes()
    {
        Assert.Empty(DataValidator.Validate(Valid() with { DateOfBirth = new DateOnly(2008, 11, 22) }, AsOf));
        Assert.Equal(["Applicant is under 18"], DataValidator.Validate(Valid() with { DateOfBirth = new DateOnly(2008, 11, 23) }, AsOf));
    }

    [Fact]
    public void Leap_day_birthday_reaches_18_on_1_March_in_non_leap_year()
    {
        var applicant = Valid() with { DateOfBirth = new DateOnly(2008, 2, 29) };

        Assert.Equal(["Applicant is under 18"], DataValidator.Validate(applicant, new DateOnly(2026, 2, 28)));
        Assert.Empty(DataValidator.Validate(applicant, new DateOnly(2026, 3, 1)));
    }

    [Fact]
    public void Invalid_email_and_missing_nationalId_are_reported_together()
    {
        var result = DataValidator.Validate(Valid() with { Email = "nope", NationalId = null }, AsOf);

        Assert.Equal(["nationalId is required", "Email address is invalid"], result);
    }

    [Fact]
    public void All_violations_reported_in_rule_order()
    {
        var applicant = new Applicant { DateOfBirth = new DateOnly(2015, 1, 1), Email = "bad" };

        var result = DataValidator.Validate(applicant, AsOf);

        Assert.Equal(
            [
                "firstName is required",
                "lastName is required",
                "nationalId is required",
                "phoneNumber is required",
                "address is required",
                "licenceCategory is required",
                "Email address is invalid",
                "Applicant is under 18",
            ],
            result);
    }

    private static Applicant With(Applicant a, string field, string? value) => field switch
    {
        "firstName" => a with { FirstName = value },
        "lastName" => a with { LastName = value },
        "nationalId" => a with { NationalId = value },
        "email" => a with { Email = value },
        "phoneNumber" => a with { PhoneNumber = value },
        "address" => a with { Address = value },
        "licenceCategory" => a with { LicenceCategory = value },
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };
}
