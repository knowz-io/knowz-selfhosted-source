using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Portability;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.Tests;

public partial class PortableImportRoundTripTests
{
    [Fact]
    public async Task ScopedSchema_AdvertisesSynchronousCapabilities()
    {
        var body = await _client.GetFromJsonAsync<JsonElement>("/api/v1/portability/schema");
        Assert.True(body.GetProperty("capabilities").GetProperty("scopedExport").GetBoolean());
        Assert.True(body.GetProperty("capabilities").GetProperty("targetVaultImport").GetBoolean());
        Assert.False(body.GetProperty("capabilities").GetProperty("asyncExport").GetBoolean());
    }

    [Theory]
    [InlineData("Vault", 1)]
    [InlineData("VaultWithChildren", 2)]
    public async Task ScopedExport_VaultSelectionIncludesOnlyRequestedHierarchy(string scopeType, int expected)
    {
        var (root, child, other, first, second, third) = await SeedScopedData();
        var response = await _client.PostAsJsonAsync("/api/v1/portability/export", new { scopeType, vaultIds = new[] { root.Id } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var package = await response.Content.ReadFromJsonAsync<PortableExportPackage>();
        Assert.Equal(expected, package!.Data.KnowledgeItems.Count);
        Assert.Equal(expected, package.Data.Vaults.Count);
        Assert.Equal(expected, package.Metadata.TotalKnowledgeItems);
        Assert.Equal(scopeType, package.Scope!.Type.ToString());
        Assert.Equal(new[] { root.Id }, package.Scope.VaultIds);
        Assert.DoesNotContain(package.Data.KnowledgeItems, k => k.Id == third.Id);
        Assert.All(package.Data.KnowledgeItems, k => Assert.DoesNotContain(other.Id, k.VaultIds));
        Assert.Empty(package.Data.InboxItems);
        Assert.Empty(package.Data.Archives);
        Assert.Equal("preserved", package.Data.KnowledgeItems.Single(k => k.Id == first.Id).ExtensionData!["custom"].GetString());
        var wholeTenant = await _client.GetFromJsonAsync<PortableExportPackage>("/api/v1/portability/export");
        Assert.Equal(3, wholeTenant!.Data.KnowledgeItems.Count);
        Assert.Equal(ExportScopeType.Tenant, wholeTenant.Scope!.Type);
    }

    [Theory]
    [InlineData("SingleItem", false)]
    [InlineData("MultiSelect", true)]
    public async Task ScopedExport_ItemSelectionFiltersDependenciesAndSharedAttachments(string scopeType, bool multiple)
    {
        var (_, _, _, first, second, third) = await SeedScopedData();
        var topic = new Topic { TenantId = _tenant, Name = "selected topic" };
        var otherTopic = new Topic { TenantId = _tenant, Name = "unrelated topic" };
        var person = new Person { TenantId = _tenant, Name = "selected person" };
        var unrelated = new Person { TenantId = _tenant, Name = "unrelated person" };
        first.TopicId = topic.Id;
        first.KnowledgePersons.Add(new KnowledgePerson { PersonId = person.Id, KnowledgeId = first.Id });
        _db.Topics.AddRange(topic, otherTopic); _db.Persons.AddRange(person, unrelated);
        var selectedComment = new KnowledgeComment { TenantId = _tenant, KnowledgeId = first.Id, Body = "selected" };
        var otherComment = new KnowledgeComment { TenantId = _tenant, KnowledgeId = third.Id, Body = "outside" };
        selectedComment.ParentCommentId = otherComment.Id;
        _db.Comments.AddRange(selectedComment, otherComment);
        var sharedFile = new FileRecord { TenantId = _tenant, FileName = "shared.txt", ContentType = "text/plain" };
        sharedFile.PlatformData = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["fileExtra"] = "preserved",
            ["__knowzPortableAttachmentMetadata"] = new[]
            {
                new PortableFileAttachmentLink { KnowledgeId = first.Id, Title = "selected title", Description = "selected caption" },
                new PortableFileAttachmentLink { KnowledgeId = third.Id, Title = "outside title" }
            }
        });
        var commentFile = new FileRecord { TenantId = _tenant, FileName = "comment.txt", ContentType = "text/plain" };
        _db.FileRecords.AddRange(sharedFile, commentFile, new FileRecord { TenantId = _tenant, FileName = "unrelated.txt" });
        _db.FileAttachments.AddRange(
            new FileAttachment { TenantId = _tenant, FileRecordId = sharedFile.Id, KnowledgeId = first.Id },
            new FileAttachment { TenantId = _tenant, FileRecordId = sharedFile.Id, KnowledgeId = third.Id },
            new FileAttachment { TenantId = _tenant, FileRecordId = commentFile.Id, CommentId = selectedComment.Id });
        await _db.SaveChangesAsync();
        var ids = multiple ? new[] { first.Id, second.Id } : new[] { first.Id };
        var response = await _client.PostAsJsonAsync("/api/v1/portability/export", new { scopeType, knowledgeItemIds = ids });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var package = (await response.Content.ReadFromJsonAsync<PortableExportPackage>())!;
        Assert.Equal(ids.Order(), package.Data.KnowledgeItems.Select(k => k.Id).Order());
        Assert.Equal(topic.Id, Assert.Single(package.Data.Topics).Id);
        Assert.Equal(person.Id, Assert.Single(package.Data.Persons).Id);
        Assert.Null(Assert.Single(package.Data.Comments).ParentCommentId);
        Assert.Equal(2, package.Data.FileRecords.Count);
        var selectedFile = package.Data.FileRecords.Single(f => f.Id == sharedFile.Id);
        var attachment = Assert.Single(selectedFile.Attachments);
        Assert.Equal(first.Id, attachment.KnowledgeId);
        Assert.Equal("selected title", attachment.Title);
        Assert.Equal("selected caption", attachment.Description);
        Assert.Equal("preserved", selectedFile.ExtensionData!["fileExtra"].GetString());
        Assert.False(selectedFile.ExtensionData.ContainsKey("__knowzPortableAttachmentMetadata"));
        Assert.Equal(selectedComment.Id, Assert.Single(package.Data.FileRecords.Single(f => f.Id == commentFile.Id).Attachments).CommentId);
    }

    [Theory]
    [InlineData("{}")] // SaaS default Vault requires IDs, never defaults to tenant.
    [InlineData("{\"scopeType\":\"Tenant\",\"knowledgeItemIds\":[\"00000000-0000-0000-0000-000000000001\"]}")]
    [InlineData("{\"scopeType\":\"SingleItem\",\"knowledgeItemIds\":[]}")]
    [InlineData("{\"scopeType\":\"SingleItem\",\"knowledgeItemIds\":[\"00000000-0000-0000-0000-000000000001\",\"00000000-0000-0000-0000-000000000002\"]}")]
    [InlineData("{\"scopeType\":\"MultiSelect\",\"vaultIds\":[\"00000000-0000-0000-0000-000000000001\"]}")]
    [InlineData("{\"scopeType\":\"unknown\"}")]
    [InlineData("{\"scopeType\":\"99\"}")]
    [InlineData("{\"scopeType\":\"Tenant\",\"mode\":\"archive\"}")]
    public async Task ScopedExport_InvalidRequestNeverWidensScope(string json)
    {
        using var body = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync("/api/v1/portability/export", body)).StatusCode);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ScopedExport_UnavailableIdsFailClosed(bool foreign, bool deleted)
    {
        var vault = new Vault { TenantId = foreign ? Guid.NewGuid() : _tenant, Name = "unavailable", IsDeleted = deleted };
        var item = new Knowledge { TenantId = vault.TenantId, Title = "unavailable", IsDeleted = deleted };
        if (foreign || deleted) { _db.Vaults.Add(vault); _db.KnowledgeItems.Add(item); await _db.SaveChangesAsync(); }
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/v1/portability/export", new { scopeType = "Vault", vaultIds = new[] { vault.Id } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/v1/portability/export", new { scopeType = "SingleItem", knowledgeItemIds = new[] { item.Id } })).StatusCode);
    }

    [Fact]
    public async Task ScopedExport_FullZipContainsOnlySelectedContent()
    {
        var package = Package(); var bytes = new byte[] { 1, 2, 3 };
        Assert.Equal(HttpStatusCode.OK, (await PostZip(package, bytes)).StatusCode);
        _db.KnowledgeItems.Add(new Knowledge { TenantId = _tenant, Title = "outside" });
        await _db.SaveChangesAsync();
        var response = await _client.PostAsJsonAsync("/api/v1/portability/export", new { scopeType = "SingleItem", knowledgeItemIds = new[] { package.Data.KnowledgeItems[0].Id }, mode = "Full" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        var exported = PortableZipReader.ReadFromZip(zip);
        Assert.Single(exported.Data.KnowledgeItems);
        Assert.Equal(ExportMode.Full, exported.Mode);
        Assert.Equal(ExportScopeType.SingleItem, exported.Scope!.Type);
        using var binary = zip.GetEntry(Assert.Single(exported.Data.FileRecords).BinaryFilePath!)!.Open();
        using var copy = new MemoryStream(); await binary.CopyToAsync(copy);
        Assert.Equal(bytes, copy.ToArray());
    }

    [Theory]
    [InlineData("skip", false)]
    [InlineData("overwrite", false)]
    [InlineData("merge", false)]
    [InlineData("skip", true)]
    [InlineData("overwrite", true)]
    [InlineData("merge", true)]
    public async Task ScopedImport_TargetVaultHonorsStrategiesWithoutImportingSourceVault(string strategy, bool zip)
    {
        var target = new Vault { TenantId = _tenant, Name = "destination" };
        var existingVault = new Vault { TenantId = _tenant, Name = "existing" };
        var existing = new Knowledge { TenantId = _tenant, Title = "old", Content = "keep" };
        _db.Vaults.AddRange(target, existingVault); _db.KnowledgeItems.Add(existing);
        _db.KnowledgeVaults.Add(new KnowledgeVault { TenantId = _tenant, KnowledgeId = existing.Id, VaultId = existingVault.Id, IsPrimary = true });
        await _db.SaveChangesAsync();
        var package = Package(); package.Data.FileRecords.Clear();
        var sourceVault = new PortableVault { Id = Guid.NewGuid(), Name = "source" }; package.Data.Vaults.Add(sourceVault);
        package.Data.KnowledgeItems[0].VaultIds = new() { sourceVault.Id };
        package.Data.KnowledgeItems.Add(new PortableKnowledge { Id = existing.Id, Title = "replacement", Content = "new", VaultIds = new() { sourceVault.Id }, PrimaryVaultId = sourceVault.Id });
        HttpResponseMessage response;
        if (zip)
        {
            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true)) PortableZipWriter.WriteToZip(archive, package);
            using var form = new MultipartFormDataContent(); form.Add(new ByteArrayContent(stream.ToArray()), "file", "data.zip");
            response = await _client.PostAsync($"/api/v1/portability/import/zip?strategy={strategy}&targetVaultId={target.Id}", form);
        }
        else response = await _client.PostAsJsonAsync($"/api/v1/portability/import?strategy={strategy}&targetVaultId={target.Id}", package);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, await _db.Vaults.CountAsync());
        var added = await _db.KnowledgeVaults.SingleAsync(k => k.KnowledgeId == package.Data.KnowledgeItems[0].Id);
        Assert.Equal(target.Id, added.VaultId); Assert.True(added.IsPrimary);
        var memberships = await _db.KnowledgeVaults.Where(k => k.KnowledgeId == existing.Id).ToListAsync();
        Assert.Equal(strategy == "merge" ? 2 : 1, memberships.Count);
        Assert.Equal(strategy == "overwrite" ? "replacement" : "old", existing.Title);
        Assert.Equal(strategy != "skip", memberships.Any(k => k.VaultId == target.Id));
        Assert.Single(memberships, k => k.IsPrimary);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ScopedImport_UnavailableTargetRejectsBeforeWrites(bool foreign, bool deleted)
    {
        var target = new Vault { TenantId = foreign ? Guid.NewGuid() : _tenant, Name = "unavailable", IsDeleted = deleted };
        if (foreign || deleted) { _db.Vaults.Add(target); await _db.SaveChangesAsync(); }
        var response = await _client.PostAsJsonAsync($"/api/v1/portability/import?targetVaultId={target.Id}", Package());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(await _db.KnowledgeItems.ToListAsync());
        Assert.Empty(await _db.FileRecords.ToListAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ScopedImport_ZipUnavailableTargetRejectsBeforeWrites(bool foreign, bool deleted)
    {
        var target = new Vault { TenantId = foreign ? Guid.NewGuid() : _tenant, Name = "unavailable", IsDeleted = deleted };
        if (foreign || deleted) { _db.Vaults.Add(target); await _db.SaveChangesAsync(); }
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true)) PortableZipWriter.WriteToZip(archive, Package());
        using var form = new MultipartFormDataContent(); form.Add(new ByteArrayContent(stream.ToArray()), "file", "data.zip");
        var response = await _client.PostAsync($"/api/v1/portability/import/zip?targetVaultId={target.Id}", form);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(await _db.KnowledgeItems.ToListAsync());
        Assert.Empty(await _db.FileRecords.ToListAsync());
        Assert.Empty(Directory.GetFiles(_directory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ScopedImport_TargetIdMatchingSourceVaultCannotOverwriteDestinationMetadata()
    {
        var target = new Vault { TenantId = _tenant, Name = "keep target", Description = "keep description" };
        _db.Vaults.Add(target); await _db.SaveChangesAsync();
        var package = Package(); package.Data.FileRecords.Clear();
        package.Data.Vaults.Add(new PortableVault { Id = target.Id, Name = "replace target", Description = "replace description" });
        var response = await _client.PostAsJsonAsync($"/api/v1/portability/import?strategy=overwrite&targetVaultId={target.Id}", package);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("keep target", target.Name);
        Assert.Equal("keep description", target.Description);
        Assert.Equal(target.Id, (await _db.KnowledgeVaults.SingleAsync()).VaultId);
    }

    [Theory]
    [InlineData("export")]
    [InlineData("import")]
    public async Task ScopedEndpoints_RetainSuperAdminGate(string operation)
    {
        _client.DefaultRequestHeaders.Add("X-Without-Admin", "true");
        var response = operation == "export"
            ? await _client.PostAsJsonAsync("/api/v1/portability/export", new { scopeType = "Tenant" })
            : await _client.PostAsJsonAsync($"/api/v1/portability/import?targetVaultId={Guid.NewGuid()}", Package());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<(Vault Root, Vault Child, Vault Other, Knowledge First, Knowledge Second, Knowledge Third)> SeedScopedData()
    {
        var root = new Vault { TenantId = _tenant, Name = "root" };
        var child = new Vault { TenantId = _tenant, Name = "child", ParentVaultId = root.Id };
        var other = new Vault { TenantId = _tenant, Name = "other" };
        var first = new Knowledge { TenantId = _tenant, Title = "first", PlatformData = "{\"custom\":\"preserved\"}" };
        var second = new Knowledge { TenantId = _tenant, Title = "second" };
        var third = new Knowledge { TenantId = _tenant, Title = "third" };
        _db.Vaults.AddRange(root, child, other); _db.KnowledgeItems.AddRange(first, second, third);
        _db.KnowledgeVaults.AddRange(
            new KnowledgeVault { TenantId = _tenant, KnowledgeId = first.Id, VaultId = root.Id, IsPrimary = true },
            new KnowledgeVault { TenantId = _tenant, KnowledgeId = first.Id, VaultId = other.Id },
            new KnowledgeVault { TenantId = _tenant, KnowledgeId = second.Id, VaultId = child.Id, IsPrimary = true },
            new KnowledgeVault { TenantId = _tenant, KnowledgeId = third.Id, VaultId = other.Id, IsPrimary = true });
        _db.InboxItems.Add(new InboxItem { TenantId = _tenant, Body = "tenant inbox" });
        await _db.SaveChangesAsync();
        return (root, child, other, first, second, third);
    }
}
