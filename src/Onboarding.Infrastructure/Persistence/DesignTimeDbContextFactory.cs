using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Onboarding.Infrastructure.Persistence;

/// <summary>Used by <c>dotnet ef migrations</c> only; never connects.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<OnboardingDbContext>
{
    public OnboardingDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<OnboardingDbContext>()
            .UseNpgsql("Host=localhost;Database=onboarding")
            .Options);
}
