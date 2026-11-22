using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Onboarding.Core.Eligibility;
using Onboarding.Infrastructure.Messaging;
using Onboarding.Infrastructure.Persistence;
using Onboarding.Infrastructure.Photos;

namespace Onboarding.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static ILoggingBuilder AddOnboardingJsonLogging(this ILoggingBuilder logging)
    {
        logging.ClearProviders();
        logging.AddJsonConsole(o =>
        {
            o.IncludeScopes = true;
            o.UseUtcTimestamp = true;
            o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        });
        return logging;
    }

    public static IServiceCollection AddOnboardingPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Connection string 'Postgres' is not configured");
        services.AddDbContext<OnboardingDbContext>(o => o.UseNpgsql(connectionString));
        services.AddHealthChecks().AddDbContextCheck<OnboardingDbContext>("db");
        return services;
    }

    public static IServiceCollection AddPhotoStore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PhotoStorageOptions>(configuration.GetSection(PhotoStorageOptions.SectionName));
        services.AddSingleton<IPhotoStore, FileSystemPhotoStore>();
        return services;
    }

    public static IServiceCollection AddEligibilityRules(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<EligibilityOptions>()
            .Configure(o => configuration.GetSection(EligibilityOptions.SectionName).BindReplacingArrays(o));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<EligibilityOptions>>().Value);
        services.AddSingleton<PhotoChecker>();
        services.AddSingleton<RiskAssessor>();
        return services;
    }

    public static IServiceCollection AddRabbitMq(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RabbitMqOptions>(configuration.GetSection(RabbitMqOptions.SectionName));
        services.AddOptions<MessagingOptions>()
            .Configure(o => configuration.GetSection(MessagingOptions.SectionName).BindReplacingArrays(o))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MessagingOptions>, MessagingOptionsValidator>();
        services.AddSingleton<RabbitMqConnection>();
        services.AddSingleton<RabbitMqPublisher>();
        services.AddHealthChecks().AddCheck<RabbitMqHealthCheck>("rabbitmq");
        return services;
    }

    /// <summary>
    /// Binds like <see cref="ConfigurationBinder.Bind(IConfiguration, object)"/>, except that a configured array
    /// replaces the default value instead of being appended to it.
    /// </summary>
    public static void BindReplacingArrays<T>(this IConfiguration section, T target)
        where T : class
    {
        section.Bind(target);
        foreach (var property in typeof(T).GetProperties().Where(p => p.PropertyType.IsArray && p.CanWrite))
        {
            var configured = section.GetSection(property.Name);
            if (configured.GetChildren().Any())
            {
                property.SetValue(target, configured.Get(property.PropertyType));
            }
        }
    }
}
