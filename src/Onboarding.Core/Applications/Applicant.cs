namespace Onboarding.Core.Applications;

/// <summary>Submitted personal fields. All nullable: intake accepts missing values; the workflow validates them.</summary>
public sealed record Applicant
{
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public DateOnly? DateOfBirth { get; init; }
    public string? NationalId { get; init; }
    public string? Email { get; init; }
    public string? PhoneNumber { get; init; }
    public string? Address { get; init; }
    public string? LicenceCategory { get; init; }
}
