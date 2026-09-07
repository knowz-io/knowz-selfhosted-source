using System.Net;
using System.Text.Json;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// VERIFY (SH_ENTERPRISE_CREDENTIAL_BOOTSTRAP §3.1-3.4):
/// HTTP-level contract test for `GET /api/bootstrap/status` — asserts on the
/// actual response body, not a DB predicate proxy.
///
/// G5 (Reviewer B audit): the original shape assertion in BootstrapEndpointTests
/// only verified the DB query the handler uses, not the serialized JSON shape
/// that `post-deploy-smoke.sh` consumes. Any future refactor that adds a field
/// to the response body (e.g., a progress percentage) would not be caught.
///
/// This test spins the API in-process via WebApplicationFactory and asserts:
/// - 200 OK (always, both ready=true and ready=false)
/// - Response body parses to an object with exactly one property named "ready"
/// - That property is a JSON boolean
/// - Anonymous access — no API key / JWT required
/// </summary>
public class BootstrapStatusHttpTests : IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _anonymousClient;
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public BootstrapStatusHttpTests()
    {
        var dbName = $"BootstrapStatusHttpTests-{Guid.NewGuid():N}";

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // Minimal config — enough to let the host boot without a real SQL / KV.
                builder.UseSetting("ConnectionStrings:McpDb", "Server=(localdb);Database=fake;");
                builder.UseSetting("SelfHosted:ApiKey", "test-api-key");
                builder.UseSetting("SelfHosted:JwtSecret", "test-jwt-secret-must-be-at-least-32-characters-long!");
                builder.UseSetting("Database:AutoMigrate", "false");
                builder.UseSetting("AzureKeyVault:Enabled", "false");
                builder.UseSetting("SelfHosted:RateLimiting:Enabled", "false"); // rate-limit is a separate test

                builder.ConfigureServices(services =>
                {
                    // Swap SQL DbContext for InMemory (same pattern as SelfHostedApiTests).
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
                    tenantProvider.TenantId.Returns(TestTenantId);

                    services.AddSingleton(sp =>
                    {
                        var optionsBuilder = new DbContextOptionsBuilder<SelfHostedDbContext>();
                        optionsBuilder.UseInMemoryDatabase(dbName);
                        return optionsBuilder.Options;
                    });

                    services.AddScoped<SelfHostedDbContext>(sp =>
                    {
                        var options = sp.GetRequiredService<DbContextOptions<SelfHostedDbContext>>();
                        return new SelfHostedDbContext(options, tenantProvider);
                    });

                    services.AddSingleton<IDbContextFactory<SelfHostedDbContext>>(sp =>
                    {
                        var options = sp.GetRequiredService<DbContextOptions<SelfHostedDbContext>>();
                        return new BootstrapTestDbContextFactory(options, tenantProvider);
                    });
                });
            });

        _anonymousClient = _factory.CreateClient(); // no Authorization header set
    }

    private sealed class BootstrapTestDbContextFactory : IDbContextFactory<SelfHostedDbContext>
    {
        private readonly DbContextOptions<SelfHostedDbContext> _options;
        private readonly ITenantProvider _tenantProvider;
        public BootstrapTestDbContextFactory(DbContextOptions<SelfHostedDbContext> options, ITenantProvider tenantProvider)
        { _options = options; _tenantProvider = tenantProvider; }
        public SelfHostedDbContext CreateDbContext() => new(_options, _tenantProvider);
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task Status_AnonymousAccess_Returns200()
    {
        var response = await _anonymousClient.GetAsync("/api/bootstrap/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Status_ResponseBody_IsExactly_ReadyBool()
    {
        var response = await _anonymousClient.GetAsync("/api/bootstrap/status");
        response.EnsureSuccessStatusCode();

        var bodyText = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(bodyText);
        var root = doc.RootElement;

        Assert.Equal(JsonValueKind.Object, root.ValueKind);

        // Must have "ready" and ONLY "ready".
        var propNames = root.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Single(propNames);
        Assert.Equal("ready", propNames[0]);

        // And that property must be a JSON boolean.
        var ready = root.GetProperty("ready");
        Assert.True(ready.ValueKind is JsonValueKind.True or JsonValueKind.False,
            $"Expected a JSON boolean for 'ready', got ValueKind={ready.ValueKind}");
    }

    [Fact]
    public async Task Status_BodySize_IsSmall_NoLeakage()
    {
        // SH_ENTERPRISE_CREDENTIAL_BOOTSTRAP §3.2: body must stay under 32 bytes
        // to ensure no hidden fields (config hints, counts, etc.) snuck in.
        // `{"ready":false}` = 15 bytes. `{"ready":true}` = 14 bytes. Use 32 as a
        // buffer for JSON formatting variance, not a tight fit.
        var response = await _anonymousClient.GetAsync("/api/bootstrap/status");
        response.EnsureSuccessStatusCode();

        var bodyBytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bodyBytes.Length < 32,
            $"Bootstrap status body should be tiny; was {bodyBytes.Length} bytes. " +
            "Any new fields on the response are a reconnaissance vector.");
    }
}
