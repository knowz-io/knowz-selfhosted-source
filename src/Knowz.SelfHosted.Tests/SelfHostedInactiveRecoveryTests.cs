using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Security.Claims;
using Knowz.SelfHosted.API.Endpoints;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.Core.Models;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Knowz.Core.Configuration;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public sealed partial class SelfHostedGraphStoreTests
{
    private sealed class ShelfActor : IPortableActorProvider { public Guid? ActorId { get; set; } }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InactiveShelfPreservesOriginalBytesAndPolicyWhileOwnerOnlyAndReadOnly(bool dependency)
    {
        var actor = new ShelfActor { ActorId = Guid.NewGuid() }; var owner = actor.ActorId.Value;
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        using var db = new SelfHostedDbContext(new DbContextOptionsBuilder<SelfHostedDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options, tenant, actor);
        db.Tenants.Add(new Tenant { Id = _tenant, Name = "Owned inactive recovery fixture", Slug = "inactive-recovery-fixture" });
        db.Users.Add(new User { Id = owner, TenantId = _tenant, Username = "shelf-owner", IsActive = true, Role = Knowz.Core.Enums.UserRole.Admin }); await db.SaveChangesAsync();
        var graph = new SelfHostedGraphStore(db, tenant, new(db, tenant, _storage, NullLogger<PortableImportService>.Instance),
            new(db, tenant, _storage, Options.Create(new SelfHostedOptions()), NullLogger<PortableExportService>.Instance), _storage);
        var (package, _, _) = Fixture(); package.RequiredCapabilities!.Add(SuspendedReleaseProfile.Capability);
        var original = package.SealedItems![0];
        package.SuspendedReleasePolicies = [new() { KnowledgeId = original.Id, OriginalKnowledgeId = original.OriginalItemId, OriginalLockId = Guid.NewGuid(),
            OriginalOwnerUserId = Guid.NewGuid(), SourceTenantId = package.SourceTenantId, OriginalIsSealed = true,
            Recipients = [new() { OriginalRecipientId = Guid.NewGuid(), RecipientEmail = "recovery@example.test" }] }];
        if (dependency)
        {
            package.SealedItems = []; package.SealedVersions = []; package.PayloadKeyReferences!.RemoveAll(r => r.OwnerKind is "knowledge" or "version");
            package.DependencyBoundItems = [new() { Id = original.Id, OriginalItemId = original.OriginalItemId, OriginalOwnerUserId = Guid.NewGuid(), SourceTenantId = package.SourceTenantId,
                VaultIds = original.VaultIds, PrimaryVaultId = original.PrimaryVaultId, Ciphertext = Convert.ToBase64String(new byte[32]), ContentNonce = Convert.ToBase64String(new byte[12]),
                Custody = new() { SourceTenantId = package.SourceTenantId, SourceKeyRef = $"tenant:{package.SourceTenantId:D}:purpose:content-release:v1" } }];
        }
        Assert.Null(EncryptedGraphProfile.Validate(package));
        using var signer = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        using var coordinator = new ArchiveImportCoordinator(); var adapter = new SelfHostedArchiveImportAdapter(db, tenant, graph, new VaultAccessService(db));
        var archiveActor = new ArchiveActor(_tenant, owner.ToString("D"));
        async Task<ArchiveImportPlan> Plan(string key)
        {
            using var bytes = new MemoryStream(); var receipt = await KnowzArchiveWriter.WriteAsync(bytes, [package], null, new() { SigningKey = signer, SourceSnapshot = key }); bytes.Position = 0;
            return await coordinator.CreatePlanAsync(bytes, archiveActor, new() { ExpectedSignerFingerprint = receipt.SignerFingerprint, SourceReceipt = receipt, IdempotencyKey = key, Strategy = "skip" }, adapter);
        }
        var firstPlan = await Plan("initial"); await coordinator.CommitAsync(firstPlan.PlanId, archiveActor, adapter);
        var item = Assert.Single(await db.KnowledgeItems.Where(k => k.PortableRecoveryState != null).ToListAsync());
        Assert.Equal(owner, item.CreatedByUserId); Assert.False(item.IsIndexed);
        var vault = await db.KnowledgeVaults.Where(v => v.KnowledgeId == item.Id).Select(v => v.VaultId).SingleAsync();
        var state = await SelfHostedInactiveShelf.ReadAsync(db, _tenant, owner, vault, item.Id, default);
        Assert.False(state.CanEdit); Assert.False(state.CanProcessWithAi); Assert.Equal("suspended", state.ActivationState);
        Assert.Equal(package.SuspendedReleasePolicies[0].OriginalLockId, state.SuspendedReleasePolicies.Single().OriginalLockId);
        var exported = await graph.ExportAsync(new() { Type = ExportScopeType.Tenant }, false, default); Assert.Null(EncryptedGraphProfile.Validate(exported));
        if (dependency) Assert.Equal(package.DependencyBoundItems![0].Ciphertext, exported.DependencyBoundItems!.Single().Ciphertext);
        else Assert.Equal(original.Ciphertext, exported.SealedItems!.Single().Ciphertext);
        Assert.Equal(package.SuspendedReleasePolicies[0].OriginalOwnerUserId, exported.SuspendedReleasePolicies!.Single().OriginalOwnerUserId);
        package.SuspendedReleasePolicies[0].Recipients[0].RecipientEmail = "changed@example.test";
        if (dependency) package.DependencyBoundItems![0].Ciphertext = Convert.ToBase64String(new byte[33]);
        var conflictPlan = await Plan("changed-recovery"); Assert.Equal("partial", conflictPlan.PreservationState);
        await Assert.ThrowsAsync<ArchiveOperationException>(() => coordinator.CommitAsync(conflictPlan.PlanId, archiveActor, adapter));
        Assert.Equal("recovery@example.test", (await SelfHostedInactiveShelf.ReadAsync(db, _tenant, owner, vault, item.Id, default)).SuspendedReleasePolicies.Single().Recipients.Single().RecipientEmail);
        var search = Substitute.For<ISearchService>(); var ai = Substitute.For<IOpenAIService>(); var streaming = Substitute.For<IStreamingOpenAIService>();
        search.HybridSearchAsync(Arg.Any<string>(), Arg.Any<float[]?>(), Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<bool>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new SearchResultItem { KnowledgeId = item.Id, Title = "Stale external index", Content = "must not leave machine", Score = 1 }]);
        var facade = new SearchFacade(db, search, ai, streaming, NullLogger<SearchFacade>.Instance);
        Assert.Empty((await facade.SearchKnowledgeAsync("suspended", 10, null, false, [], false, null, null, null, default)).Items);
        Assert.Empty(await facade.BuildKnowledgeScopedContextAsync(item.Id, "context", default));
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(db); builder.Services.AddSingleton(tenant); builder.Services.AddSingleton(graph);
        builder.Services.AddScoped<SelfHostedSealedStore>(); builder.Services.AddSingleton<IVaultAccessService>(new VaultAccessService(db));
        await using var app = builder.Build(); app.Use(async (http, next) => { http.User = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, actor.ActorId!.Value.ToString()), new("tenantId", _tenant.ToString()), new(ClaimTypes.Role, "Admin")], "test")); await next(); });
        app.MapEncryptedGraphEndpoints(); await app.StartAsync(); var client = app.GetTestClient();
        var response = await client.GetAsync($"/api/v1/encrypted/items/{item.Id:D}/recovery-state?vaultId={vault:D}"); response.EnsureSuccessStatusCode();
        var wire = (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>());
        Assert.Equal(item.PortableRecoveryState, wire.GetProperty("recoveryState").GetString()); Assert.False(wire.GetProperty("canEdit").GetBoolean());
        if (!dependency)
        {
            var lifecycle = new SelfHostedSealedStore(db, tenant, NullLogger<SelfHostedSealedStore>.Instance);
            var live = await lifecycle.GetAsync(item.Id, default);
            var denied = await lifecycle.UpdateAsync(item.Id, new() { Item = exported.SealedItems!.Single(), VaultId = vault, PayloadKeyReferences = exported.PayloadKeyReferences!.Where(r => r.OwnerKind == "knowledge").ToList() }, default, live!.Revision);
            Assert.Equal(409, denied.StatusCode); Assert.Contains("suspended", denied.Error);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => SelfHostedInactiveShelf.RequireMutableAsync(db, item.Id, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new VersioningService(db, tenant, NullLogger<VersioningService>.Instance).CreateVersionAsync(item.Id, owner, "must stay inactive", default));
        item.Content = "ordinary write must not activate imported bytes";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear(); actor.ActorId = Guid.NewGuid();
        Assert.DoesNotContain(await db.KnowledgeItems.ToListAsync(), k => k.Id == item.Id);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/encrypted/items/{item.Id:D}/recovery-state?vaultId={vault:D}")).StatusCode);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SelfHostedInactiveShelf.ReadAsync(db, _tenant, actor.ActorId.Value, vault, item.Id, default));
        var other = await graph.ExportAsync(new() { Type = ExportScopeType.Tenant }, false, default);
        Assert.Empty(other.SuspendedReleasePolicies ?? []); Assert.Empty(other.DependencyBoundItems ?? []); Assert.Empty(other.SealedItems ?? []);
        actor.ActorId = null; Assert.DoesNotContain(await db.KnowledgeItems.ToListAsync(), k => k.Id == item.Id);
        Assert.NotEmpty(await db.KnowledgeItems.ToListAsync()); // Ordinary siblings retain existing tenant behavior.
    }
}
