using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.API.Endpoints;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public sealed class ArchiveCapabilityMatrixTests
{
    [Fact]
    public async Task PublicCapabilitiesAdvertiseInertDependenciesWithoutManagedProviderOrReleaseAuthority()
    {
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid(); var provider = Substitute.For<ITenantProvider>(); provider.TenantId.Returns(tenant);
        await using var db = new SelfHostedDbContext(new DbContextOptionsBuilder<SelfHostedDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, provider);
        db.Users.Add(new() { Id = actor, TenantId = tenant, Username = "capability-reader", IsActive = true }); await db.SaveChangesAsync();
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer(); builder.Services.AddSingleton(db);
        await using var app = builder.Build();
        app.Use(async (http, next) => { http.User = new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, actor.ToString()), new("tenantId", tenant.ToString()), new(ClaimTypes.Role, "Admin")], "fixture")); await next(); });
        app.MapArchivePortabilityEndpoints(); await app.StartAsync(); using var client = app.GetTestClient();
        var value = await client.GetFromJsonAsync<JsonElement>("/api/v1/portability/capabilities");
        Assert.Contains("knowz.suspended-release/v1", value.GetProperty("capabilities").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(5, value.GetProperty("supportedScopes").GetArrayLength());
        Assert.Equal(new[] { 1, 2 }, value.GetProperty("readEnvelopeVersions").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(4 * 1024 * 1024, value.GetProperty("limits").GetProperty("manifestBytes").GetInt32());
        Assert.Equal(128, value.GetProperty("limits").GetProperty("packages").GetInt32());
        Assert.Equal(32L * 1024 * 1024 * 1024, value.GetProperty("limits").GetProperty("ordinaryFileBytes").GetInt64());
        Assert.Equal(64L * 1024 * 1024, value.GetProperty("limits").GetProperty("encryptedFileBytes").GetInt64());
        Assert.Equal(200, value.GetProperty("limits").GetProperty("expansionRatio").GetInt32());
        var custody = value.GetProperty("custodySupport");
        Assert.True(custody.GetProperty("customerHeldLocalConsent").GetBoolean());
        Assert.False(custody.GetProperty("managedRecovery").GetBoolean());
        Assert.False(custody.GetProperty("dependencyBoundIndependentRecovery").GetBoolean());
        Assert.False(custody.GetProperty("suspendedReleaseActivation").GetBoolean());
        Assert.False(custody.GetProperty("destinationManagedRewrap").GetBoolean());
    }
}
