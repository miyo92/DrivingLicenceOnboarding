using System.Net.Mail;
using Onboarding.Core.Applications;

namespace Onboarding.Core.Eligibility;

/// <summary>ValidatingData step rules. Reports every violation, in rule-table order.</summary>
public static class DataValidator
{
    public const int MinimumAge = 18;

    public static IReadOnlyList<string> Validate(Applicant applicant, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(applicant);
        var violations = new List<string>();

        RequireText(applicant.FirstName, "firstName");
        RequireText(applicant.LastName, "lastName");
        if (applicant.DateOfBirth is null)
        {
            violations.Add("dateOfBirth is required");
        }

        // Also covers the National ID rule; reported once.
        RequireText(applicant.NationalId, "nationalId");
        RequireText(applicant.Email, "email");
        RequireText(applicant.PhoneNumber, "phoneNumber");
        RequireText(applicant.Address, "address");
        RequireText(applicant.LicenceCategory, "licenceCategory");

        if (!string.IsNullOrWhiteSpace(applicant.Email) && !IsValidEmail(applicant.Email))
        {
            violations.Add("Email address is invalid");
        }

        if (applicant.DateOfBirth is { } dob && Age.At(dob, asOf) < MinimumAge)
        {
            violations.Add("Applicant is under 18");
        }

        return violations;

        void RequireText(string? value, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                violations.Add($"{fieldName} is required");
            }
        }
    }

    private static bool IsValidEmail(string email) =>
        MailAddress.TryCreate(email, out var address) && address.Address == email;
}
