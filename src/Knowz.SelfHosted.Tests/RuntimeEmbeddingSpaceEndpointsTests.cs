using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.API.Endpoints;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public sealed class RuntimeEmbeddingSpaceEndpointsTests
{
    [Theory]
    [InlineData("SuperAdmin", "empty", true)]
    [InlineData("SuperAdmin", "retained-other-tenant", false)]
    [InlineData("SuperAdmin", "sealed-other-tenant", false)]
    [InlineData("SuperAdmin", "key-only", false)]
    [InlineData("SuperAdmin", "mixed-key", false)]
    [InlineData("SuperAdmin", "unavailable", false)]
    [InlineData("Admin", "empty", false)]
    [InlineData("User", "empty", false)]
    [InlineData("LegacyApiKey", "empty", false)]
    [InlineData("", "empty", false)]
    public async Task CensusOnlyAllowsAuthenticatedOperatorWithNoInstanceData(string role, string scenario, bool allowed)
    {
        var tenant = Substitute.For<ITenantProvider>();
        tenant.TenantId.Returns(Guid.NewGuid());
        await using var db = new SelfHostedDbContext(new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
        if (scenario == "retained-other-tenant")
            db.KnowledgeItems.Add(new Knowledge { TenantId = Guid.NewGuid(), Title = "Retained synthetic knowledge", IsDeleted = true });
        if (scenario == "sealed-other-tenant")
            db.ShSealedKnowledge.Add(new Knowz.SelfHosted.Infrastructure.Data.Entities.ShSealedKnowledge
                { TenantId = Guid.NewGuid(), KnowledgeId = Guid.NewGuid(), IsDeleted = true, Ciphertext = "synthetic" });
        await db.SaveChangesAsync();
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton(Substitute.For<IConfigurationManagementService>());
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(role.Length == 0 ? [] : [new Claim(ClaimTypes.Role, role)], role.Length == 0 ? null : "synthetic"));
            await next();
        });
        app.MapConfigurationEndpoints(); await app.StartAsync();
        using var client = app.GetTestClient();
        if (scenario == "unavailable") await db.DisposeAsync();
        if (role != "LegacyApiKey" && scenario != "key-only") client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer synthetic.test.jwt");
        if (scenario is "key-only" or "mixed-key") client.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", "ksh_synthetic");
        using var response = await client.GetAsync("/api/v1/admin/config/embedding-space");
        if (role != "SuperAdmin" || scenario is "key-only" or "mixed-key") { Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); return; }
        if (scenario == "unavailable") { Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); return; }
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(allowed, json.RootElement.GetProperty("canChange").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("reason").GetString()));
    }
}
