using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Onboarding.Api.Applications;
using Onboarding.Api.Outbox;
using Onboarding.Infrastructure;
using Onboarding.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddOnboardingJsonLogging();

builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi(o => o.AddSubmitApplicationRequestBody());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOnboardingPersistence(builder.Configuration);
builder.Services.AddPhotoStore(builder.Configuration);
builder.Services.AddRabbitMq(builder.Configuration);

// Started after the migration below: hosted services start in RunAsync.
builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection(OutboxOptions.SectionName));
builder.Services.AddHostedService<OutboxRelay>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Swagger is served in every environment: this is a local assignment stack (see README, production improvements).
app.MapOpenApi();
app.UseSwaggerUI(o =>
{
    o.SwaggerEndpoint("/openapi/v1.json", "Onboarding API v1");
    o.RoutePrefix = "swagger";
});

app.MapHealthChecks("/health");
app.MapApplicationEndpoints();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<OnboardingDbContext>().Database.MigrateAsync();
}

await app.RunAsync();
