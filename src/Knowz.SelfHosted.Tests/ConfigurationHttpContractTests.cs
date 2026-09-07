using System.Net;
using System.Net.Http.Json;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

/// <summary>Actual middleware + HTTP route contracts, using isolated in-memory persistence.</summary>
public class ConfigurationHttpContractTests : IDisposable
{
    private readonly WebApplicationFactory<Program> factory;
    private readonly IConfigurationManagementService configuration = Substitute.For<IConfigurationManagementService>();
    private readonly Guid ownKnowledge = Guid.NewGuid(), otherKnowledge = Guid.NewGuid();
    public ConfigurationHttpContractTests()
    {
        var database = "configuration-http-" + Guid.NewGuid();
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:McpDb", "Host=127.0.0.1;Port=1;Database=isolated;Timeout=1");
            builder.UseSetting("SelfHosted:JwtSecret", "configuration-http-test-signing-secret-at-least-32-characters");
            builder.UseSetting("SelfHosted:RateLimiting:Enabled", "false");
            builder.UseSetting("Database:AutoMigrate", "false");
            builder.UseSetting("AzureKeyVault:Enabled", "false");
            builder.UseSetting("AzureOpenAI:Endpoint", "");
            builder.UseSetting("OpenAiCompatible:Endpoint", "");
            builder.UseSetting("KnowzPlatform:Enabled", "false");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<SelfHostedDbContext>>();
                services.RemoveAll<DbContextOptions>();
                services.RemoveAll<SelfHostedDbContext>();
                services.RemoveAll<IDbContextFactory<SelfHostedDbContext>>();
                services.AddSingleton(new DbContextOptionsBuilder<SelfHostedDbContext>().UseInMemoryDatabase(database).Options);
                services.AddScoped(sp => new SelfHostedDbContext(sp.GetRequiredService<DbContextOptions<SelfHostedDbContext>>(), sp.GetRequiredService<ITenantProvider>()));
                services.AddScoped<IDbContextFactory<SelfHostedDbContext>, ContextFactory>();
                services.RemoveAll<IConfigurationManagementService>();
                services.AddSingleton(configuration);
            });
        });
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
        var tenant = new Tenant { Name = "config HTTP tenant", Slug = "config-http" };
        db.Tenants.Add(tenant);
        foreach (var role in new[] { UserRole.User, UserRole.Admin, UserRole.SuperAdmin })
            db.Users.Add(new User { TenantId = tenant.Id, Username = "http-" + role, ApiKey = "ksh_isolated-http-key-" + role, Role = role, PasswordHash = "not-used-for-key-test" });
        db.KnowledgeItems.AddRange(new Knowledge { Id = ownKnowledge, TenantId = tenant.Id, Title = "authenticated tenant content" },
            new Knowledge { Id = otherKnowledge, TenantId = Guid.NewGuid(), Title = "different tenant content" });
        db.SaveChanges();
        configuration.ClearReceivedCalls();
    }
    private sealed class ContextFactory(DbContextOptions<SelfHostedDbContext> options, ITenantProvider tenant) : IDbContextFactory<SelfHostedDbContext>
    { public SelfHostedDbContext CreateDbContext() => new(options, tenant); }
    private HttpClient Client(UserRole role)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "ksh_isolated-http-key-" + role);
        return client;
    }
    [Fact]
    public async Task ApiKeyReadsUseAuthenticatedTenant_AndExcludeOtherTenants()
    {
        using var client = Client(UserRole.User);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/knowledge/{ownKnowledge}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/knowledge/{otherKnowledge}")).StatusCode);
    }
    [Theory]
    [InlineData(UserRole.User)]
    [InlineData(UserRole.Admin)]
    public async Task NonSuperAdminCannotReadOrProbeOrChangeConfiguration(UserRole role)
    {
        using var client = Client(role);
        foreach (var route in new[] { "categories", "AzureOpenAI", "status" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/admin/config/" + route)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/v1/admin/config/health/AzureOpenAI", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/v1/admin/config/AzureOpenAI", new { entries = new[] { new { key = "ApiKey", value = "attempted-secret" } } })).StatusCode);
        Assert.Empty(configuration.ReceivedCalls());
    }
    [Fact]
    public async Task SuperAdminRetainsAdditiveContractAndConflictResponse()
    {
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/admin/config/categories")).StatusCode);
        using var client = Client(UserRole.SuperAdmin);
        configuration.GetCategoryAsync("AzureOpenAI").Returns(new ConfigCategoryDto { Category = "AzureOpenAI", ProbeKind = "connectivity", Entries = [new() { Key = "ApiKey", Editable = false, Authority = "environment", EffectiveIsSet = true, Value = "****" }] });
        var body = await client.GetStringAsync("/api/v1/admin/config/AzureOpenAI");
        Assert.Contains("\"editable\":false", body); Assert.Contains("\"authority\":\"environment\"", body); Assert.Contains("\"effectiveIsSet\":true", body);
        configuration.UpdateCategoryAsync("AzureOpenAI", Arg.Any<List<ConfigEntryUpdateDto>>(), Arg.Any<string>())
            .Returns(Task.FromException<ConfigUpdateResult>(new DbUpdateConcurrencyException("test conflict")));
        var conflict = await client.PutAsJsonAsync("/api/v1/admin/config/AzureOpenAI", new { entries = new[] { new { key = "Endpoint", value = "https://test.example" } } });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("Please refresh", await conflict.Content.ReadAsStringAsync());
    }
    public void Dispose() => factory.Dispose();
}
