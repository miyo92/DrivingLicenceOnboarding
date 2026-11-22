using System.Globalization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Onboarding.Core.Applications;
using Onboarding.Infrastructure.Logging;
using Onboarding.Infrastructure.Persistence;
using Onboarding.Infrastructure.Photos;

namespace Onboarding.Api.Applications;

public static class ApplicationEndpoints
{
    public static IEndpointRouteBuilder MapApplicationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/applications").WithTags("Applications");

        group.MapPost("/", SubmitApplication)
            .WithName(nameof(SubmitApplication))
            .WithMultipartFormBody()
            .Produces<ApplicationStatusResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

        group.MapGet("/{applicationId:guid}", GetApplication)
            .WithName(nameof(GetApplication))
            .Produces<ApplicationResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{applicationId:guid}/history", GetApplicationHistory)
            .WithName(nameof(GetApplicationHistory))
            .Produces<List<HistoryEntryResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapManualReviewEndpoints();

        return endpoints;
    }

    private static async Task<IResult> SubmitApplication(
        HttpContext context,
        OnboardingDbContext db,
        IPhotoStore photoStore,
        TimeProvider time,
        IOptions<PhotoStorageOptions> photoOptions,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var request = context.Request;
        var maxBytes = photoOptions.Value.MaxUploadBytes;

        if (request.ContentLength > maxBytes)
        {
            return TooLarge(maxBytes);
        }

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
        {
            sizeFeature.MaxRequestBodySize = maxBytes;
        }

        if (request.ContentType is null || !request.ContentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unsupported request format", detail: "Request body must be multipart/form-data.");
        }

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(ct);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return TooLarge(maxBytes);
        }
        catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException or IOException)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Malformed multipart request", detail: ex.Message);
        }

        // A field sent twice would otherwise be silently joined ("B,C").
        var repeated = SubmissionFields.Where(name => form.TryGetValue(name, out var values) && values.Count > 1).ToList();
        if (form.Files.GetFiles("photo").Count > 1)
        {
            repeated.Add("photo");
        }

        if (repeated.Count > 0)
        {
            return Results.ValidationProblem(repeated.ToDictionary(name => name, name => new[] { $"{name} must be sent at most once." }));
        }

        DateOnly? dateOfBirth = null;
        if (Field(form, "dateOfBirth") is { } dobText)
        {
            if (!DateOnly.TryParseExact(dobText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dob))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["dateOfBirth"] = ["dateOfBirth must be a date in yyyy-MM-dd format."],
                });
            }

            dateOfBirth = dob;
        }

        var applicant = new Applicant
        {
            FirstName = Field(form, "firstName"),
            LastName = Field(form, "lastName"),
            DateOfBirth = dateOfBirth,
            NationalId = Field(form, "nationalId"),
            Email = Field(form, "email"),
            PhoneNumber = Field(form, "phoneNumber"),
            Address = Field(form, "address"),
            LicenceCategory = Field(form, "licenceCategory"),
        };

        // An empty file part (e.g. an untouched file input) counts as no photo.
        PhotoMetadata? photo = null;
        if (form.Files.GetFile("photo") is { Length: > 0 } file)
        {
            await using var stream = file.OpenReadStream();
            var storageKey = await photoStore.SaveAsync(stream, ct);
            photo = new PhotoMetadata(storageKey, file.ContentType, file.Length);
        }

        var now = time.GetUtcNow();
        var application = Application.Create(Guid.CreateVersion7(now), applicant, photo, now);
        db.Applications.Add(application);
        db.OutboxMessages.Add(OutboxMessage.ApplicationSubmitted(application.Id, now));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch when (photo is not null)
        {
            // Nothing references the file now. (Only a crash between write and commit can still orphan one.)
            TryDeletePhoto(photoStore, photo.StorageKey, loggerFactory);
            throw;
        }

        var logger = loggerFactory.CreateLogger(typeof(ApplicationEndpoints));
        using (logger.BeginPropertyScope(("applicationId", application.Id)))
        {
            logger.LogInformation("Application submitted (photo: {HasPhoto})", photo is not null);
        }

        return Results.Accepted($"/api/applications/{application.Id}", new ApplicationStatusResponse(application.Id, application.Status));
    }

    private static async Task<IResult> GetApplication(Guid applicationId, OnboardingDbContext db, CancellationToken ct)
    {
        var response = await db.Applications.AsNoTracking()
            .Where(a => a.Id == applicationId)
            .Select(a => new ApplicationResponse(a.Id, a.Status, a.CreatedAt, a.UpdatedAt))
            .SingleOrDefaultAsync(ct);

        return response is null ? NotFound(applicationId) : Results.Ok(response);
    }

    private static async Task<IResult> GetApplicationHistory(Guid applicationId, OnboardingDbContext db, CancellationToken ct)
    {
        if (!await db.Applications.AnyAsync(a => a.Id == applicationId, ct))
        {
            return NotFound(applicationId);
        }

        var entries = await db.History.AsNoTracking()
            .Where(h => EF.Property<Guid>(h, "ApplicationId") == applicationId)
            .OrderBy(h => h.OccurredAt).ThenBy(h => h.Id)
            .Select(h => new HistoryEntryResponse(h.OccurredAt, h.FromStatus, h.ToStatus, h.Event, h.Details))
            .ToListAsync(ct);

        return Results.Ok(entries);
    }

    private static readonly string[] SubmissionFields =
        ["firstName", "lastName", "dateOfBirth", "nationalId", "email", "phoneNumber", "address", "licenceCategory"];

    private static void TryDeletePhoto(IPhotoStore photoStore, string storageKey, ILoggerFactory loggerFactory)
    {
        try
        {
            photoStore.Delete(storageKey);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger(typeof(ApplicationEndpoints))
                .LogWarning(ex, "Could not delete photo {StorageKey} after a failed submission", storageKey);
        }
    }

    private static string? Field(IFormCollection form, string name) =>
        form.TryGetValue(name, out var values) && !string.IsNullOrWhiteSpace(values.ToString()) ? values.ToString().Trim() : null;

    internal static IResult NotFound(Guid applicationId) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Application not found", detail: $"Application {applicationId} was not found.");

    private static IResult TooLarge(long maxBytes) =>
        Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Request too large", detail: $"Request body must not exceed {maxBytes} bytes.");
}
