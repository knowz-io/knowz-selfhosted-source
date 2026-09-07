using System.Security.Claims;
using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.SelfHosted.API.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Knowz.SelfHosted.Tests;

public class AuthenticatedTenantFilterTests
{
    [Fact]
    public async Task Should_ResolveAuthenticatedTenantAfterApiKeyLookup_WithoutLeakingOtherTenants()
    {
        var fallback = Guid.NewGuid(); var tenantA = Guid.NewGuid(); var tenantB = Guid.NewGuid();
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var tenant = new HttpTenantProvider(accessor, Options.Create(new SelfHostedOptions { TenantId = fallback }));
        await using var db = new SelfHostedDbContext(new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
        db.KnowledgeItems.AddRange(
            new Knowledge { TenantId = tenantA, Title = "tenant A" },
            new Knowledge { TenantId = tenantB, Title = "tenant B" },
            new Knowledge { TenantId = fallback, Title = "fallback" },
            new Knowledge { TenantId = tenantA, Title = "deleted", IsDeleted = true });
        db.Vaults.AddRange(new Vault { TenantId = tenantA, Name = "vault A" }, new Vault { TenantId = tenantB, Name = "vault B" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        // API-key authentication queries Users before setting the authenticated principal.
        await db.Users.ToListAsync();
        accessor.HttpContext.User = Principal(tenantA);
        Assert.Equal(new[] { "tenant A" }, await db.KnowledgeItems.Select(k => k.Title).ToArrayAsync());
        Assert.Equal(new[] { "vault A" }, await db.Vaults.Select(v => v.Name).ToArrayAsync());
        // Ordinary users cannot escape their tenant through a forged override header.
        accessor.HttpContext.Request.Headers["X-Tenant-Id"] = tenantB.ToString();
        Assert.Equal(new[] { "tenant A" }, await db.KnowledgeItems.Select(k => k.Title).ToArrayAsync());
        // A background scope remains governed by the existing explicit TenantContext.
        try
        {
            TenantContext.CurrentTenantId = tenantB;
            Assert.Equal(new[] { "tenant B" }, await db.KnowledgeItems.Select(k => k.Title).ToArrayAsync());
        }
        finally { TenantContext.CurrentTenantId = null; }
        Assert.Equal(new[] { "tenant A" }, await db.KnowledgeItems.Select(k => k.Title).ToArrayAsync());
    }
    private static ClaimsPrincipal Principal(Guid tenant) => new(new ClaimsIdentity(
        [new Claim("tenantId", tenant.ToString()), new Claim(ClaimTypes.Role, "User")], "ApiKey"));
}
