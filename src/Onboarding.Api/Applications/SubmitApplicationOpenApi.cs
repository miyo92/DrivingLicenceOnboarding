using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Onboarding.Api.Applications;

/// <summary>
/// Describes the multipart body of SubmitApplication in the OpenAPI document.
/// </summary>
/// <remarks>
/// <c>.Accepts&lt;T&gt;("multipart/form-data")</c> would also add a content-type routing constraint that answers 415
/// before the handler runs; the spec requires 400 for non-multipart requests, so the body is described here instead.
/// </remarks>
internal static class SubmitApplicationOpenApi
{
    private static readonly (string Name, string Example)[] Examples =
    [
        ("firstName", "Anna"),
        ("lastName", "Muster"),
        ("dateOfBirth", "1996-05-10"),
        ("nationalId", "AT1234567"),
        ("email", "anna.muster@example.com"),
        ("phoneNumber", "+43 660 1234567"),
        ("address", "Hauptstrasse 1, 1010 Wien"),
        ("licenceCategory", "B"),
    ];

    public static OpenApiOptions AddSubmitApplicationRequestBody(this OpenApiOptions options)
    {
        options.AddOperationTransformer(async (operation, context, ct) =>
        {
            if (!context.Description.ActionDescriptor.EndpointMetadata.OfType<MultipartBodyMetadata>().Any())
            {
                return;
            }

            var schema = await context.GetOrCreateSchemaAsync(typeof(SubmitApplicationForm), null, ct);

            // Swagger UI pre-fills form fields from examples; without them every field reads "string" and dateOfBirth fails to parse.
            foreach (var (name, example) in Examples)
            {
                if (schema.Properties is not null && schema.Properties.TryGetValue(name, out var p) && p is OpenApiSchema property)
                {
                    property.Examples = [JsonValue.Create(example)];
                }
            }

            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["multipart/form-data"] = new() { Schema = schema },
                },
            };
        });
        return options;
    }

    /// <summary>Marks an endpoint whose request body is <see cref="SubmitApplicationForm"/> as multipart/form-data.</summary>
    public static RouteHandlerBuilder WithMultipartFormBody(this RouteHandlerBuilder builder) =>
        builder.WithMetadata(new MultipartBodyMetadata());

    private sealed class MultipartBodyMetadata;
}
