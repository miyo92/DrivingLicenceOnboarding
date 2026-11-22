using System.ComponentModel;
using Onboarding.Core.Applications;

namespace Onboarding.Api.Applications;

/// <summary>OpenAPI description of the multipart submission. The handler parses the form itself (design decision #17).</summary>
/// <remarks>
/// Properties are optional (no constructor parameters) so the schema marks nothing as required, and
/// <see cref="Photo"/> is declared non-nullable so it is a plain binary schema that Swagger UI renders as a file picker.
/// </remarks>
public sealed class SubmitApplicationForm
{
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    [Description("Date of birth, yyyy-MM-dd")] public string? DateOfBirth { get; init; }
    public string? NationalId { get; init; }
    public string? Email { get; init; }
    public string? PhoneNumber { get; init; }
    public string? Address { get; init; }
    [Description("Licence category, e.g. B")] public string? LicenceCategory { get; init; }
    [Description("Optional face photo (image/jpeg or image/png)")] public IFormFile Photo { get; init; } = null!;
}

public sealed record ApplicationStatusResponse(Guid ApplicationId, ApplicationStatus Status);

public sealed record ApplicationResponse(Guid ApplicationId, ApplicationStatus Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record HistoryEntryResponse(DateTimeOffset Timestamp, ApplicationStatus? FromStatus, ApplicationStatus ToStatus, WorkflowEvent Event, string? Details);
