using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.API.Endpoints;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public sealed partial class SelfHostedGraphStoreTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NarrowExportSelectorsWithoutExplicitScopeCannotExportTheWholeTenant(bool items, bool explicitTenant)
    {
        var actor = new Knowz.Core.Entities.User { TenantId = _tenant, Username = "scope-owner", IsActive = true, Role = UserRole.Admin };
        _db.Users.Add(actor); await _db.SaveChangesAsync();
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer(); builder.Services.AddSingleton(_db);
        builder.Services.AddSingleton<IVaultAccessService>(new VaultAccessService(_db));
        builder.Services.AddSingleton(new SelfHostedArchiveExportService(_db, tenant, _graph, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()));
        await using var app = builder.Build(); app.Use(async (http, next) =>
        {
            http.User = new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, actor.Id.ToString()), new("tenantId", _tenant.ToString()), new(ClaimTypes.Role, "Admin")], "synthetic"));
            await next();
        });
        app.MapArchivePortabilityEndpoints(); await app.StartAsync(); using var client = app.GetTestClient();
        var body = new Dictionary<string, object> { [items ? "knowledgeItemIds" : "vaultIds"] = new[] { Guid.NewGuid() } };
        if (explicitTenant) body["scopeType"] = "Tenant";
        using var response = await client.PostAsJsonAsync("/api/v1/portability/archives/export", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("scopeType", await response.Content.ReadAsStringAsync());
        Assert.False(response.Headers.Contains("X-Knowz-Source-Receipt"));
    }
}
