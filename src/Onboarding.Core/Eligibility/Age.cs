namespace Onboarding.Core.Eligibility;

public static class Age
{
    /// <summary>Completed years at <paramref name="asOf"/>. A 29 Feb birthday falls on 1 Mar in non-leap years.</summary>
    public static int At(DateOnly dateOfBirth, DateOnly asOf)
    {
        var age = asOf.Year - dateOfBirth.Year;
        if (asOf < BirthdayIn(dateOfBirth, asOf.Year))
        {
            age--;
        }

        return age;
    }

    private static DateOnly BirthdayIn(DateOnly dateOfBirth, int year) =>
        dateOfBirth is { Month: 2, Day: 29 } && !DateTime.IsLeapYear(year)
            ? new DateOnly(year, 3, 1)
            : new DateOnly(year, dateOfBirth.Month, dateOfBirth.Day);
}
