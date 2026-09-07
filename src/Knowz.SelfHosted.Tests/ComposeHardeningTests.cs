using System.Net;
using System.Text.Json;
using Knowz.Core.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Setup.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// Kit hardening for GitHub #867 (0.16.0 live path): no published DB host
/// port, Swagger config-gated, AllowedHosts not * in compose or appsettings.
/// </summary>
public class ComposeHardeningTests
{
    [Fact]
    public void SourceCompose_DoesNotPublishDatabaseHostPort()
    {
        var compose = ReadCompose();
        var active = string.Join('\n', compose.Split('\n')
            .Where(line => !line.TrimStart().StartsWith('#')));

        Assert.DoesNotContain("${DB_PORT:-5432}:5432", active, StringComparison.Ordinal);
        Assert.DoesNotContain("\"5432:5432\"", active, StringComparison.Ordinal);
        Assert.Contains("No host port", compose, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:${DB_PORT:-5432}:5432", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceCompose_SwaggerOffUnlessOptedIn()
    {
        var compose = ReadCompose();

        Assert.Contains("SelfHosted__EnableSwagger: \"${ENABLE_SWAGGER:-false}\"", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost:5000/swagger", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("ENABLE_SWAGGER:-true", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceCompose_AllowedHostsIsNotBareStar()
    {
        var compose = ReadCompose();

        Assert.Contains("AllowedHosts: \"${ALLOWED_HOSTS:-localhost;127.0.0.1;[::1];api}\"", compose, StringComparison.Ordinal);
        Assert.Contains("AllowedHosts: \"${ALLOWED_HOSTS:-localhost;127.0.0.1;[::1];mcp}\"", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowedHosts: \"*\"", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowedHosts: '*'", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void Appsettings_EnableSwaggerDefaultsFalse()
    {
        var json = File.ReadAllText(Path.Combine(RepoRoot(), "src/Knowz.SelfHosted.API/appsettings.json"));
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("SelfHosted").GetProperty("EnableSwagger").GetBoolean());
    }

    [Fact]
    public void Appsettings_ApiAllowedHostsIsNotBareStar_McpStaysHostedSafeStar()
    {
        var api = File.ReadAllText(Path.Combine(RepoRoot(), "src/Knowz.SelfHosted.API/appsettings.json"));
        var mcp = File.ReadAllText(Path.Combine(RepoRoot(), "src/Knowz.MCP/appsettings.json"));
        using var apiDoc = JsonDocument.Parse(api);
        using var mcpDoc = JsonDocument.Parse(mcp);
        Assert.Equal("localhost;127.0.0.1;[::1]", apiDoc.RootElement.GetProperty("AllowedHosts").GetString());
        // MCP appsettings is copied into GHCR knowz-mcp (#875 baked localhost-only and
        // broke mcp.knowz.io/healthz). Compose env keeps the local harden (#867 intent).
        Assert.Equal("*", mcpDoc.RootElement.GetProperty("AllowedHosts").GetString());
    }

    [Fact]
    public void Program_SwaggerIsConfigGatedNotEnvironment()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src/Knowz.SelfHosted.API/Program.cs"));
        Assert.Contains("GetValue(\"SelfHosted:EnableSwagger\", false)", src, StringComparison.Ordinal);
        Assert.DoesNotContain("IsDevelopment() || builder.Configuration.GetValue(\"SelfHosted:EnableSwagger\"", src, StringComparison.Ordinal);
    }

    [Fact]
    public void SetupConfig_SwaggerDisabledByDefault()
    {
        var config = new SetupConfig();
        Assert.False(config.SwaggerEnabled);
    }

    [Fact]
    public async Task Swagger_IsOffUnlessEnabled_EvenInDevelopment()
    {
        var dbName = $"ComposeHardening-SwaggerOff-{Guid.NewGuid():N}";
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("ConnectionStrings:McpDb", "Server=(localdb);Database=fake;");
                builder.UseSetting("SelfHosted:ApiKey", "test-api-key");
                builder.UseSetting("SelfHosted:JwtSecret", "test-jwt-secret-must-be-at-least-32-characters-long!");
                builder.UseSetting("SelfHosted:EnableSwagger", "false");
                builder.UseSetting("Database:AutoMigrate", "false");
                builder.UseSetting("AzureKeyVault:Enabled", "false");
                builder.UseSetting("SelfHosted:RateLimiting:Enabled", "false");
                builder.UseSetting("Knowz:StrictDIValidation", "false");

                builder.ConfigureServices(services =>
                {
                    var descriptorsToRemove = services
                        .Where(d =>
                            d.ServiceType == typeof(DbContextOptions<SelfHostedDbContext>) ||
                            d.ServiceType == typeof(DbContextOptions) ||
                            d.ServiceType == typeof(SelfHostedDbContext) ||
                            d.ServiceType == typeof(IDbContextFactory<SelfHostedDbContext>) ||
                            (d.ServiceType.IsGenericType &&
                             d.ServiceType.GetGenericTypeDefinition() == typeof(DbContextOptions<>) &&
                             d.ServiceType.GenericTypeArguments[0] == typeof(SelfHostedDbContext)))
                        .ToList();
                    foreach (var d in descriptorsToRemove)
                        services.Remove(d);

                    var tenantProvider = Substitute.For<ITenantProvider>();
                    tenantProvider.TenantId.Returns(Guid.Parse("00000000-0000-0000-0000-000000000001"));

                    services.AddSingleton(_ =>
                    {
                        var optionsBuilder = new DbContextOptionsBuilder<SelfHostedDbContext>();
                        optionsBuilder.UseInMemoryDatabase(dbName);
                        return optionsBuilder.Options;
                    });

                    services.AddScoped(sp =>
                    {
                        var options = sp.GetRequiredService<DbContextOptions<SelfHostedDbContext>>();
                        return new SelfHostedDbContext(options, tenantProvider);
                    });

                    services.AddSingleton<IDbContextFactory<SelfHostedDbContext>>(sp =>
                    {
                        var options = sp.GetRequiredService<DbContextOptions<SelfHostedDbContext>>();
                        return new TestDbContextFactory(options, tenantProvider);
                    });
                });
            });

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static string ReadCompose() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "docker-compose.yml"));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Knowz.SelfHosted.sln"))
                || File.Exists(Path.Combine(dir.FullName, "docker-compose.yml")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate selfhosted repo root.");
    }

    private sealed class TestDbContextFactory : IDbContextFactory<SelfHostedDbContext>
    {
        private readonly DbContextOptions<SelfHostedDbContext> _options;
        private readonly ITenantProvider _tenantProvider;

        public TestDbContextFactory(DbContextOptions<SelfHostedDbContext> options, ITenantProvider tenantProvider)
        {
            _options = options;
            _tenantProvider = tenantProvider;
        }

        public SelfHostedDbContext CreateDbContext() => new(_options, _tenantProvider);
    }
}
