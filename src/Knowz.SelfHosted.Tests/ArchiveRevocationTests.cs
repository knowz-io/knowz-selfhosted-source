using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.API.Endpoints;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;
public sealed partial class SelfHostedGraphStoreTests
{
    [Fact]
    public async Task OrdinaryArchiveExportCannotPublishAfterActorRevokedDuringBinaryCapture()
    {
        var (source, _, _) = Fixture(); Assert.True((await _graph.ImportAsync(source, null, "trusted:revoked-capture", ImportConflictStrategy.Skip, null, default)).Success);
        var vault = await _db.Vaults.SingleAsync(); var actor = new User { TenantId = _tenant, Username = "capture-owner", IsActive = true, Role = Knowz.Core.Enums.UserRole.Admin }; _db.Users.Add(actor); await _db.SaveChangesAsync();
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var storage = Substitute.For<IFileStorageProvider>(); var captured = false;
        storage.DownloadAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(async call => {
            var binary = await _storage.DownloadAsync(call.ArgAt<Guid>(0), call.ArgAt<Guid>(1), call.ArgAt<CancellationToken>(2));
            captured = true; (await _db.Users.SingleAsync(u => u.Id == actor.Id)).IsActive = false; await _db.SaveChangesAsync(); return binary;
        });
        var graph = new SelfHostedGraphStore(_db, tenant, new(_db, tenant, storage, NullLogger<PortableImportService>.Instance),
            new(_db, tenant, storage, Microsoft.Extensions.Options.Options.Create(new Knowz.Core.Configuration.SelfHostedOptions()), NullLogger<PortableExportService>.Instance), storage);
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer(); builder.Services.AddSingleton(_db);
        builder.Services.AddSingleton<IVaultAccessService>(new VaultAccessService(_db));
        builder.Services.AddSingleton(new SelfHostedArchiveExportService(_db, tenant, graph, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()));
        await using var app = builder.Build(); app.Use(async (http, next) => { http.User = new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, actor.Id.ToString()), new("tenantId", _tenant.ToString()), new(ClaimTypes.Role, "Admin")], "synthetic")); await next(); });
        app.MapArchivePortabilityEndpoints(); await app.StartAsync(); using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync("/api/v1/portability/archives/export", new { scopeType = "Vault", vaultIds = new[] { vault.Id } });
        Assert.True(captured); Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); Assert.False(response.Headers.Contains("X-Knowz-Source-Receipt"));
        Assert.NotEqual("application/vnd.knowz.archive+zip", response.Content.Headers.ContentType?.MediaType);
    }
}
