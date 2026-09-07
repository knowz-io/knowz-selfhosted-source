using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql.EntityFrameworkCore.PostgreSQL;

namespace Knowz.SelfHosted.Infrastructure.Extensions;

public static class DatabaseExtensions
{
    /// <summary>
    /// Registers SelfHostedDbContext with tenant-aware scoped resolution.
    /// Uses AddDbContextFactory to register DbContextOptions, then overrides the scoped
    /// registration to create contexts using ITenantProvider for per-request tenant isolation.
    /// </summary>
    public static IServiceCollection AddSelfHostedDatabase(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("McpDb")
            ?? throw new InvalidOperationException("ConnectionStrings:McpDb is required for self-hosted mode");

        services.AddDbContextFactory<SelfHostedDbContext>(options =>
        {
            ConfigureNpgsql(options, connectionString);
        });

        // Scoped registration: construct DbContext directly with ITenantProvider
        // so each request gets the correct tenant from JWT/header/fallback.
        services.AddScoped<SelfHostedDbContext>(sp =>
        {
            var dbOptions = sp.GetRequiredService<DbContextOptions<SelfHostedDbContext>>();
            var tenantProvider = sp.GetRequiredService<ITenantProvider>();
            return new SelfHostedDbContext(dbOptions, tenantProvider);
        });

        // SelfHostedAbstractionSeams: expose IRelationalStore over the scoped DbContext.
        // Pure adapter wrap — scoped lifetime matches DbContext. Future Postgres provider swap
        // replaces this adapter without touching consumers. See spec SelfHostedAbstractionSeams.
        services.AddScoped<IRelationalStore, SelfHostedRelationalStore>();

        return services;
    }

    public static void ConfigureNpgsql(DbContextOptionsBuilder options, string connectionString)
    {
        options.UseNpgsql(connectionString, npg =>
        {
            npg.CommandTimeout(30);
            npg.EnableRetryOnFailure(3);
        });
        options.ConfigureWarnings(w =>
            w.Ignore(RelationalEventId.PendingModelChangesWarning));
    }
}
