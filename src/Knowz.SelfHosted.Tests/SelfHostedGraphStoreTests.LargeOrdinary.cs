using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.API.Endpoints;
using Knowz.SelfHosted.Application.DTOs;
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task LargeOrdinaryProtectedNameReplacementPreservesCurrentAndHistoricalBytes(bool http)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web); json.Converters.Add(new JsonStringEnumConverter());
        var source = JsonSerializer.Deserialize<PortableExportPackage>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "portable-graph-v1.json")), json)!;
        var sealedFile = source.SealedFiles!.Single();
        source.Data.FileRecords.Add(new() { Id = sealedFile.Id, OriginalFileId = sealedFile.OriginalFileId,
            FileName = "encrypted-file", ContentType = "application/octet-stream", SizeBytes = 3, BinaryContentBase64 = "AQID",
            EncryptedFieldsJson = sealedFile.EncryptedFieldsJson, Attachments = [new() { KnowledgeId = source.SealedItems!.Single().Id }] });
        source.SealedFiles.Clear(); source.PayloadKeyReferences!.RemoveAll(r => r.OwnerKind == "file" && r.Role == "binary");
        var imported = await _graph.ImportAsync(source, null, "trusted:large-ordinary", ImportConflictStrategy.Skip, null, default);
        Assert.True(imported.Success, imported.Error);
        var vault = (await _db.Vaults.SingleAsync()).Id; var file = await _db.FileRecords.SingleAsync();
        var projection = (await SelfHostedSealedFiles.GetProtectedFileAsync(_db, file.Id, default))!;
        var revision = SelfHostedSealedStore.PayloadRevision(projection); var originalFields = projection.EncryptedFieldsJson;
        var exported = await _graph.ExportAsync(new() { Type = ExportScopeType.Vault, VaultIds = [vault] }, false, default);
        var refs = exported.PayloadKeyReferences!.Where(r => r.OwnerKind == "file" && r.OwnerId == file.Id).ToList();
        var catalog = (await _lifecycle.GetCatalogAsync(vault, default))!; var key = catalog.Keys.Single();
        Assert.True((await _lifecycle.SelectPrimaryAsync(vault, key.ContextId, key.Fingerprint, catalog.Revision, default, key.Generation)).Ok);
        var actor = new Knowz.Core.Entities.User { TenantId = _tenant, Username = "large-ordinary-writer", IsActive = true, Role = Knowz.Core.Enums.UserRole.Admin };
        _db.Users.Add(actor); await _db.SaveChangesAsync();
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        var service = new SelfHostedSealedFiles(_db, tenant, _lifecycle, _storage);
        var inputPath = Path.Combine(_directory, "owned-large-input"); const long length = 64L * 1024 * 1024 + 1;
        await using (var create = File.Create(inputPath)) create.SetLength(length);
        string digest; await using (var input = File.OpenRead(inputPath)) digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(input));
        projection.SizeBytes = length; projection.BinarySha256 = digest;
        Guid next;
        if (http)
        {
            var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(_db); builder.Services.AddSingleton(_lifecycle); builder.Services.AddSingleton(_graph); builder.Services.AddSingleton(service);
            builder.Services.AddSingleton<Knowz.SelfHosted.Application.Interfaces.IVaultAccessService>(new VaultAccessService(_db));
            await using var app = builder.Build();
            app.Use(async (context, action) => { context.User = new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, actor.Id.ToString()), new("tenantId", _tenant.ToString()), new(ClaimTypes.Role, "Admin")], "owned-large-fixture")); await action(); });
            app.MapEncryptedVaultEndpoints(); await app.StartAsync(); using var client = app.GetTestClient();
            using var form = new MultipartFormDataContent(); form.Add(new StreamContent(File.OpenRead(inputPath)), "binary", "encrypted-file");
            form.Add(new StringContent(JsonSerializer.Serialize(projection, json)), "record"); form.Add(new StringContent(JsonSerializer.Serialize(refs, json)), "payloadKeyReferences"); form.Add(new StringContent(vault.ToString()), "vaultId");
            using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/encrypted/files/{file.Id}") { Content = form }; request.Headers.TryAddWithoutValidation("If-Match", revision);
            using var response = await client.SendAsync(request); Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            next = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ownerId").GetGuid();
        }
        else
        {
            await using var input = File.OpenRead(inputPath);
            var result = await service.UpdateProtectedFileAsync(actor.Id, new ProtectedFileWrite { VaultId = vault, Record = projection, PayloadKeyReferences = refs }, revision, input, default);
            Assert.True(result.Ok, result.Error); next = result.Id!.Value;
        }
        Assert.NotEqual(file.Id, next);
        await using var recovered = await _graph.OpenFileAsync(next, default);
        Assert.Equal(digest, Convert.ToHexStringLower(await SHA256.HashDataAsync(recovered)));
        Assert.Equal(originalFields, (await SelfHostedSealedFiles.GetProtectedFileAsync(_db, next, default))!.EncryptedFieldsJson);
        await using var original = await _graph.OpenFileAsync(file.Id, default);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3 })), Convert.ToHexStringLower(await SHA256.HashDataAsync(original)));
    }
}
