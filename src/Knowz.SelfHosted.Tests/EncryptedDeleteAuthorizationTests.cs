using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.API.Endpoints;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public sealed partial class SelfHostedGraphStoreTests
{
    [Theory]
    [InlineData("knowledge", "write-only")]
    [InlineData("comment", "write-only")]
    [InlineData("file", "write-only")]
    [InlineData("attachment", "write-only")]
    [InlineData("knowledge", "delete-only")]
    [InlineData("comment", "delete-only")]
    [InlineData("file", "delete-only")]
    [InlineData("attachment", "delete-only")]
    [InlineData("knowledge", "shared-denied")]
    [InlineData("comment", "shared-denied")]
    [InlineData("file", "shared-denied")]
    [InlineData("attachment", "shared-denied")]
    [InlineData("knowledge", "inactive-tenant")]
    [InlineData("comment", "inactive-tenant")]
    [InlineData("file", "inactive-tenant")]
    [InlineData("attachment", "inactive-tenant")]
    [InlineData("knowledge", "stale")]
    [InlineData("comment", "stale")]
    [InlineData("file", "stale")]
    [InlineData("attachment", "stale")]
    [InlineData("knowledge", "ambiguous-attachment")]
    [InlineData("comment", "ambiguous-attachment")]
    public async Task EncryptedHttpDeleteRequiresLiveDeleteGrantForEveryOwningVault(string kind, string scenario)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web); json.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var source = JsonSerializer.Deserialize<PortableExportPackage>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "portable-graph-v1.json")), json)!;
        Assert.True((await _graph.ImportAsync(source, null, "trusted:delete-auth", ImportConflictStrategy.Skip, null, default)).Success);
        var item = await _db.ShSealedKnowledge.AsNoTracking().SingleAsync();
        var comment = await _db.ShSealedComments.AsNoTracking().FirstAsync();
        var file = await _db.ShSealedFiles.AsNoTracking().SingleAsync();
        var attachment = await _db.ShSealedAttachments.AsNoTracking().FirstAsync();
        var vault = (await _db.Vaults.SingleAsync()).Id;
        var user = new User { TenantId = _tenant, Username = "delete-fixture", IsActive = true };
        _db.Users.Add(user);
        _db.UserPermissions.Add(new UserPermissions { UserId = user.Id, TenantId = _tenant, HasAllVaultsAccess = false });
        _db.UserVaultAccess.Add(new UserVaultAccess { UserId = user.Id, TenantId = _tenant, VaultId = vault, CanRead = true, CanWrite = scenario != "delete-only", CanDelete = scenario != "write-only" });
        if (scenario == "shared-denied")
        {
            var other = new Vault { TenantId = _tenant, Name = "Other live owner vault" }; _db.Vaults.Add(other);
            _db.KnowledgeVaults.Add(new KnowledgeVault { TenantId = _tenant, KnowledgeId = item.KnowledgeId, VaultId = other.Id });
            _db.UserVaultAccess.Add(new UserVaultAccess { UserId = user.Id, TenantId = _tenant, VaultId = other.Id, CanRead = true, CanWrite = true, CanDelete = false });
        }
        if (scenario == "ambiguous-attachment")
        {
            var other = new Vault { TenantId = _tenant, Name = "Unrelated owner vault" };
            var unrelated = new Knowledge { TenantId = _tenant, Title = "Unrelated owner" };
            _db.Vaults.Add(other); _db.KnowledgeItems.Add(unrelated);
            _db.KnowledgeVaults.Add(new KnowledgeVault { TenantId = _tenant, KnowledgeId = unrelated.Id, VaultId = other.Id });
            _db.UserVaultAccess.Add(new UserVaultAccess { UserId = user.Id, TenantId = _tenant, VaultId = other.Id, CanRead = true, CanWrite = true, CanDelete = false });
            var ambiguous = await _db.ShSealedAttachments.SingleAsync(a => a.Id == attachment.Id);
            ambiguous.KnowledgeId = unrelated.Id; ambiguous.CommentId = comment.Id;
        }
        if (scenario == "inactive-tenant") (await _db.Tenants.SingleAsync()).IsActive = false;
        await _db.SaveChangesAsync();
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(_db); builder.Services.AddSingleton(_lifecycle); builder.Services.AddSingleton(_graph);
        builder.Services.AddSingleton(new SelfHostedSealedFiles(_db, tenant, _lifecycle, _storage));
        builder.Services.AddSingleton<IVaultAccessService>(new VaultAccessService(_db));
        await using var app = builder.Build();
        app.Use(async (http, next) => { http.User = new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, user.Id.ToString()), new("tenantId", _tenant.ToString()), new(ClaimTypes.Role, "User")], "synthetic")); await next(); });
        app.MapEncryptedVaultEndpoints(); await app.StartAsync(); using var client = app.GetTestClient();
        var id = kind switch { "knowledge" => item.KnowledgeId, "comment" => comment.Id, "file" => file.Id, _ => attachment.Id };
        var revision = kind switch {
            "knowledge" => (await _lifecycle.GetAsync(id, default))!.Revision,
            "comment" => SelfHostedSealedStore.PayloadRevision((await _lifecycle.GetCommentAsync(id, default))!.Envelope),
            "file" => SelfHostedSealedStore.PayloadRevision(JsonSerializer.Deserialize<SealedFileRecord>(JsonSerializer.Serialize(file))!),
            _ => SelfHostedSealedStore.PayloadRevision(JsonSerializer.Deserialize<SealedAttachmentRecord>(JsonSerializer.Serialize(attachment))!) };
        var path = kind switch { "knowledge" => "items", "comment" => "comments", "file" => "files", _ => "attachments" };
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/encrypted/{path}/{id}?vaultId={vault}");
        request.Headers.TryAddWithoutValidation("If-Match", scenario == "stale" ? "stale-revision" : revision);
        using var response = await client.SendAsync(request);
        Assert.Equal(scenario == "delete-only" ? HttpStatusCode.OK : scenario == "stale" ? HttpStatusCode.Conflict : HttpStatusCode.Forbidden, response.StatusCode);
        _db.ChangeTracker.Clear();
        var deleted = kind switch {
            "knowledge" => (await _db.ShSealedKnowledge.IgnoreQueryFilters().SingleAsync(s => s.KnowledgeId == id)).IsDeleted,
            "comment" => (await _db.ShSealedComments.IgnoreQueryFilters().SingleAsync(s => s.Id == id)).IsDeleted,
            "file" => (await _db.ShSealedFiles.IgnoreQueryFilters().SingleAsync(s => s.Id == id)).IsDeleted,
            _ => (await _db.ShSealedAttachments.IgnoreQueryFilters().SingleAsync(s => s.Id == id)).IsDeleted };
        Assert.Equal(scenario == "delete-only", deleted);
        Assert.Equal(item.Ciphertext, (await _db.ShSealedKnowledge.IgnoreQueryFilters().SingleAsync()).Ciphertext);
        Assert.Equal(source.PayloadKeyReferences!.Count, await _db.ShPayloadKeyReferences.IgnoreQueryFilters().CountAsync());
        Assert.True(await _storage.ExistsAsync(_tenant, file.BlobStorageId!.Value));
    }
}
