using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Knowz.SelfHosted.Infrastructure.Data;

/// <summary>
/// Design-time factory for creating SelfHostedDbContext during EF Core migrations.
/// Usage: dotnet ef migrations add Initial --project src/Knowz.SelfHosted.Infrastructure --context SelfHostedDbContext
/// </summary>
public class DesignTimeFactory : IDesignTimeDbContextFactory<SelfHostedDbContext>
{
    public SelfHostedDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<SelfHostedDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Port=5432;Database=knowz_selfhosted;Username=knowz;Password=design-time");
        return new SelfHostedDbContext(optionsBuilder.Options);
    }
}
