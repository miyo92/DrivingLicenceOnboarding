using Onboarding.Infrastructure;
using Onboarding.Worker.Workflow;

// Slim web host: the only endpoint is /health, used by the compose healthcheck.
var builder = WebApplication.CreateSlimBuilder(args);

builder.Logging.AddOnboardingJsonLogging();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOnboardingPersistence(builder.Configuration);
builder.Services.AddPhotoStore(builder.Configuration);
builder.Services.AddEligibilityRules(builder.Configuration);
builder.Services.AddRabbitMq(builder.Configuration);
builder.Services.AddScoped<WorkflowProcessor>();
builder.Services.AddSingleton<ProcessingFailureHandler>();
builder.Services.AddSingleton<ConsumerState>();
builder.Services.AddHealthChecks().AddCheck<ConsumerHealthCheck>("consumer");
builder.Services.AddHostedService<ApplicationSubmittedConsumer>();
builder.Services.Configure<SweeperOptions>(builder.Configuration.GetSection(SweeperOptions.SectionName));
builder.Services.AddHostedService<StaleApplicationSweeper>();

var app = builder.Build();

app.MapHealthChecks("/health");

await app.RunAsync();
