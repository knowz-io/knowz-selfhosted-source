using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.API.Endpoints;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class PortableImportRoundTripTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "knowz-import-test-" + Guid.NewGuid());
    private readonly Guid _tenant = Guid.NewGuid();
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private SelfHostedDbContext _db = null!;
    private LocalFileStorageProvider _storage = null!;
    private PortableImportService _import = null!;
    private PortableExportService _export = null!;

    public async Task InitializeAsync()
    {
        var tenant = Substitute.For<ITenantProvider>(); tenant.TenantId.Returns(_tenant);
        _db = new SelfHostedDbContext(new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options, tenant);
        _storage = new LocalFileStorageProvider(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Storage:Local:RootPath"] = _directory }).Build(), NullLogger<LocalFileStorageProvider>.Instance);
        _import = new PortableImportService(_db, tenant, _storage, NullLogger<PortableImportService>.Instance);
        _export = new PortableExportService(_db, tenant, _storage, Options.Create(new SelfHostedOptions()), NullLogger<PortableExportService>.Instance);
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ITenantProvider>(tenant);
        builder.Services.AddSingleton<IFileStorageProvider>(_storage);
        builder.Services.AddSingleton<IPortableImportService>(_import);
        builder.Services.AddSingleton<IPortableExportService>(_export);
        _app = builder.Build();
        _app.Use(async (context, next) => {
            if (!context.Request.Headers.ContainsKey("X-Without-Admin"))
                context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, "SuperAdmin") }, "test"));
            await next(context);
        });
        _app.MapPortabilityEndpoints(); await _app.StartAsync(); _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose(); await _app.DisposeAsync(); await _db.DisposeAsync();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Theory]
    [InlineData("selfhosted")]
    [InlineData("platform")]
    public async Task HttpFullZip_RestoresRetrievableBytesAtDestinationId_AndReexports(string edition)
    {
        var bytes = RandomNumberGenerator.GetBytes(128);
        var package = Package(edition); var originalId = package.Data.FileRecords[0].Id;
        var response = await PostZip(package, bytes);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var file = await _db.FileRecords.SingleAsync();
        if (edition == "platform") Assert.NotEqual(originalId, file.Id);
        Assert.False(file.BlobMigrationPending);
        await AssertBytes(file.Id, bytes);
        var exported = await _client.GetByteArrayAsync("/api/v1/portability/export?mode=full");
        using var zip = new ZipArchive(new MemoryStream(exported));
        var roundTrip = PortableZipReader.ReadFromZip(zip);
        var record = Assert.Single(roundTrip.Data.FileRecords);
        using var entry = zip.GetEntry(record.BinaryFilePath!)!.Open(); using var copy = new MemoryStream(); await entry.CopyToAsync(copy);
        Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(copy.ToArray()));
        Assert.Equal(_tenant, roundTrip.SourceTenantId);
    }

    [Theory]
    [InlineData("skip")]
    [InlineData("merge")]
    public async Task HttpZip_ExistingFile_KeepStrategyPreservesBytes(string strategy)
    {
        var package = Package(); var old = RandomNumberGenerator.GetBytes(128); var replacement = RandomNumberGenerator.GetBytes(128);
        Assert.Equal(HttpStatusCode.OK, (await PostZip(package, old)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostZip(package, replacement, strategy)).StatusCode);
        await AssertBytes(package.Data.FileRecords[0].Id, old);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Overwrite_RestoresSuppliedBinary_JsonOrZip(bool zip)
    {
        var package = Package(); var old = RandomNumberGenerator.GetBytes(128); var replacement = RandomNumberGenerator.GetBytes(128);
        package.Data.FileRecords[0].BinaryContentBase64 = Convert.ToBase64String(old);
        Assert.True((await _import.ImportAsync(package)).Success);
        package.Data.FileRecords[0].BinaryContentBase64 = Convert.ToBase64String(replacement);
        if (zip) Assert.Equal(HttpStatusCode.OK, (await PostZip(package, replacement, "overwrite")).StatusCode);
        else { var outcome = await _import.ImportAsync(package, ImportConflictStrategy.Overwrite); Assert.True(outcome.Success, outcome.Error); }
        await AssertBytes(package.Data.FileRecords[0].Id, replacement);
        Assert.False((await _db.FileRecords.SingleAsync()).BlobMigrationPending);
    }

    [Theory]
    [InlineData(999, "skip")]
    [InlineData(2, "99")]
    public async Task HttpZip_InvalidSchemaOrStrategy_HasNoStorageSideEffects(int version, string strategy)
    {
        var package = Package(); package.SchemaVersion = version;
        var response = await PostZip(package, RandomNumberGenerator.GetBytes(128), strategy);
        Assert.False(response.IsSuccessStatusCode);
        Assert.Empty(Directory.GetFiles(_directory, "*", SearchOption.AllDirectories));
        Assert.Empty(await _db.FileRecords.ToListAsync());
    }

    [Fact]
    public async Task HttpZip_MissingBinary_ReportsRecoverableFailure()
    {
        var package = Package(); package.Data.FileRecords[0].BinaryFilePath = "files/missing.bin";
        var response = await PostZip(package, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("filesBlobFailed").GetInt32());
        Assert.NotEmpty(body.GetProperty("warnings").EnumerateArray());
        Assert.True((await _db.FileRecords.SingleAsync()).BlobMigrationPending);
    }

    [Fact]
    public async Task HttpZip_NonAdmin_RejectsBeforeStorage()
    {
        _client.DefaultRequestHeaders.Add("X-Without-Admin", "true");
        Assert.Equal(HttpStatusCode.Forbidden, (await PostZip(Package(), RandomNumberGenerator.GetBytes(128))).StatusCode);
        Assert.Empty(Directory.GetFiles(_directory, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(ImportConflictStrategy.Skip)]
    [InlineData(ImportConflictStrategy.Merge)]
    public async Task Import_KeepStrategies_PreserveExistingParents(ImportConflictStrategy strategy)
    {
        var package = Package(); package.Data.FileRecords.Clear();
        var parent1 = new PortableVault { Id = Guid.NewGuid(), Name = "Parent1" }; var parent2 = new PortableVault { Id = Guid.NewGuid(), Name = "Parent2" };
        var child = new PortableVault { Id = Guid.NewGuid(), Name = "Child", ParentVaultId = parent1.Id };
        package.Data.Vaults.AddRange(new[] { parent1, parent2, child });
        var knowledgeId = package.Data.KnowledgeItems[0].Id;
        var root1 = new PortableKnowledgeComment { Id = Guid.NewGuid(), KnowledgeId = knowledgeId, Body = "root1" };
        var root2 = new PortableKnowledgeComment { Id = Guid.NewGuid(), KnowledgeId = knowledgeId, Body = "root2" };
        var reply = new PortableKnowledgeComment { Id = Guid.NewGuid(), KnowledgeId = knowledgeId, ParentCommentId = root1.Id, Body = "reply" };
        package.Data.Comments.AddRange(new[] { root1, root2, reply });
        Assert.True((await _import.ImportAsync(package)).Success);
        child.ParentVaultId = parent2.Id; reply.ParentCommentId = root2.Id;
        Assert.True((await _import.ImportAsync(package, strategy)).Success);
        Assert.Equal(parent1.Id, (await _db.Vaults.FindAsync(child.Id))!.ParentVaultId);
        Assert.Equal(root1.Id, (await _db.Comments.FindAsync(reply.Id))!.ParentCommentId);
    }

    [Fact]
    public async Task Import_Overwrite_CanClearCommentParent()
    {
        var package = Package(); package.Data.FileRecords.Clear(); var kid = package.Data.KnowledgeItems[0].Id;
        var parent = new PortableKnowledgeComment { Id = Guid.NewGuid(), KnowledgeId = kid, Body = "parent" };
        var reply = new PortableKnowledgeComment { Id = Guid.NewGuid(), KnowledgeId = kid, ParentCommentId = parent.Id, Body = "reply" };
        package.Data.Comments.AddRange(new[] { parent, reply }); await _import.ImportAsync(package);
        reply.ParentCommentId = null; Assert.True((await _import.ImportAsync(package, ImportConflictStrategy.Overwrite)).Success);
        Assert.Null((await _db.Comments.FindAsync(reply.Id))!.ParentCommentId);
    }

    [Fact]
    public async Task Import_Skip_DoesNotAddVaultPersonLinks()
    {
        var package = Package(); package.Data.FileRecords.Clear(); var vault = new PortableVault { Id = Guid.NewGuid(), Name = "vault" };
        var person = new PortablePerson { Id = Guid.NewGuid(), Name = "person" };
        package.Data.Vaults.Add(vault); package.Data.Persons.Add(person); await _import.ImportAsync(package);
        vault.PersonIds.Add(person.Id); Assert.True((await _import.ImportAsync(package)).Success);
        Assert.Empty(await _db.VaultPersons.ToListAsync());
    }

    [Theory]
    [InlineData(ImportConflictStrategy.Skip)]
    [InlineData(ImportConflictStrategy.Merge)]
    [InlineData(ImportConflictStrategy.Overwrite)]
    public async Task Import_Archives_RespectsStrategyAndPreservesUnrelatedEntries(ImportConflictStrategy strategy)
    {
        var id = Guid.NewGuid();
        var package = new PortableExportPackage { SourceEdition = "platform" };
        package.Data.Archives["First"] = new() { JsonSerializer.SerializeToElement(new { id, name = "before", description = (string?)null }) };
        package.Data.Archives["Unrelated"] = new() { JsonSerializer.SerializeToElement(new { id = Guid.NewGuid(), name = "keep" }) };
        await _import.ImportAsync(package);
        package.Data.Archives.Remove("Unrelated");
        package.Data.Archives["First"][0] = JsonSerializer.SerializeToElement(new { id, name = "after", description = "fill" });
        Assert.True((await _import.ImportAsync(package, strategy)).Success);
        Assert.Equal(2, await _db.PortableArchives.CountAsync());
        var stored = JsonSerializer.Deserialize<JsonElement>((await _db.PortableArchives.SingleAsync(a => a.EntityType == "First")).JsonData);
        Assert.Equal(strategy == ImportConflictStrategy.Overwrite ? "after" : "before", stored.GetProperty("name").GetString());
        Assert.Equal(strategy == ImportConflictStrategy.Skip ? null : "fill", stored.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Import_CrossEdition_AttachmentMetadataSurvivesOwnerRemap()
    {
        var package = Package("platform");
        var attachment = package.Data.FileRecords[0].Attachments[0]; attachment.Title = "Document title"; attachment.Description = "Caption";
        Assert.True((await _import.ImportAsync(package)).Success);
        var exported = await _export.ExportAsync(); var restored = exported.Data.FileRecords[0].Attachments[0];
        Assert.Equal(attachment.Title, restored.Title); Assert.Equal(attachment.Description, restored.Description);
        Assert.NotEqual(attachment.KnowledgeId, restored.KnowledgeId);
        Assert.Equal(exported.Data.KnowledgeItems[0].Id, restored.KnowledgeId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_MergeKnowledge_UnionsRelationshipsWithoutChangingContentOrDuplicatingLinks(bool existingTopic)
    {
        var package = Package();
        package.Data.FileRecords.Clear();
        var oldId = Guid.NewGuid(); var addedId = Guid.NewGuid();
        foreach (var id in new[] { oldId, addedId })
        {
            package.Data.Vaults.Add(new() { Id = id, Name = id.ToString() });
            package.Data.Persons.Add(new() { Id = id, Name = id.ToString() });
            package.Data.Locations.Add(new() { Id = id, Name = id.ToString() });
            package.Data.Events.Add(new() { Id = id, Name = id.ToString() });
            package.Data.Tags.Add(new() { Id = id, Name = id.ToString() });
            package.Data.Topics.Add(new() { Id = id, Name = id.ToString() });
        }
        var item = package.Data.KnowledgeItems.Single();
        item.VaultIds = new() { oldId }; item.PrimaryVaultId = oldId;
        item.PersonIds = new() { oldId }; item.LocationIds = new() { oldId };
        item.EventIds = new() { oldId }; item.TagIds = new() { oldId };
        item.TopicId = existingTopic ? oldId : null;
        Assert.True((await _import.ImportAsync(package)).Success);

        item.Content = "Incoming content must not replace existing content";
        item.VaultIds = new() { oldId, addedId, addedId }; item.PrimaryVaultId = addedId;
        item.PersonIds = new() { oldId, addedId, addedId }; item.LocationIds = new() { oldId, addedId, addedId };
        item.EventIds = new() { oldId, addedId, addedId }; item.TagIds = new() { oldId, addedId, addedId };
        item.PersonLinks = new() { new() { EntityId = addedId, Role = "Contributor", RelationshipContext = "Imported context" } };
        item.TopicId = addedId;
        Assert.True((await _import.ImportAsync(package, ImportConflictStrategy.Merge)).Success);
        Assert.True((await _import.ImportAsync(package, ImportConflictStrategy.Merge)).Success);
        _db.ChangeTracker.Clear();
        var merged = await _db.KnowledgeItems.Include(k => k.KnowledgeVaults).Include(k => k.KnowledgePersons)
            .Include(k => k.KnowledgeLocations).Include(k => k.KnowledgeEvents).Include(k => k.Tags).SingleAsync();
        Assert.Equal("Content", merged.Content);
        Assert.Equal(existingTopic ? oldId : addedId, merged.TopicId);
        var expected = new[] { oldId, addedId }.OrderBy(id => id);
        Assert.Equal(expected, merged.KnowledgeVaults.Select(v => v.VaultId).OrderBy(id => id));
        Assert.Equal(expected, merged.KnowledgePersons.Select(p => p.PersonId).OrderBy(id => id));
        Assert.Equal(expected, merged.KnowledgeLocations.Select(l => l.LocationId).OrderBy(id => id));
        Assert.Equal(expected, merged.KnowledgeEvents.Select(e => e.EventId).OrderBy(id => id));
        Assert.Equal(expected, merged.Tags.Select(t => t.Id).OrderBy(id => id));
        Assert.Equal(oldId, Assert.Single(merged.KnowledgeVaults, v => v.IsPrimary).VaultId);
        var addedPerson = Assert.Single(merged.KnowledgePersons, p => p.PersonId == addedId);
        Assert.Equal("Contributor", addedPerson.Role);
        Assert.Equal("Imported context", addedPerson.RelationshipContext);
    }

    [Fact]
    public async Task Validate_UsesActualCountsAndIncludesEveryConflictCategory()
    {
        var package = Package();
        package.Data.FileRecords.Clear();
        package.Data.Topics.Add(new() { Id = Guid.NewGuid(), Name = "Topic" });
        package.Data.Tags.Add(new() { Id = Guid.NewGuid(), Name = "Tag" });
        package.Data.Locations.Add(new() { Id = Guid.NewGuid(), Name = "Place" });
        package.Data.Events.Add(new() { Id = Guid.NewGuid(), Name = "Event" });
        package.Data.Comments.Add(new() { Id = Guid.NewGuid(), KnowledgeId = package.Data.KnowledgeItems[0].Id, Body = "Reply" });
        Assert.True((await _import.ImportAsync(package)).Success);
        package.Metadata.TotalKnowledgeItems = 999;
        var response = await _client.PostAsJsonAsync("/api/v1/portability/import/validate", package);
        var validation = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, validation.GetProperty("totalKnowledgeItems").GetInt32());
        foreach (var name in new[] { "conflictingTopics", "conflictingTags", "conflictingLocations", "conflictingEvents", "conflictingComments" })
            Assert.Equal(1, validation.GetProperty(name).GetInt32());
    }

    [Fact]
    public async Task Import_CrossEditionRetry_ReusesIdsAndReportsConflicts()
    {
        var package = Package("platform");
        package.Data.Events.Add(new() { Id = Guid.NewGuid(), Name = "event" });
        package.Data.Comments.Add(new() { Id = Guid.NewGuid(), KnowledgeId = package.Data.KnowledgeItems[0].Id, Body = "reply" });
        var bytes = RandomNumberGenerator.GetBytes(128);
        Assert.Equal(HttpStatusCode.OK, (await PostZip(package, bytes)).StatusCode);
        var originalId = (await _db.FileRecords.SingleAsync()).Id;
        Assert.Equal(HttpStatusCode.OK, (await PostZip(package, bytes)).StatusCode);
        Assert.Single(await _db.KnowledgeItems.ToListAsync());
        Assert.Single(await _db.Comments.ToListAsync());
        Assert.Single(await _db.Events.ToListAsync());
        Assert.Equal(originalId, (await _db.FileRecords.SingleAsync()).Id);
        var validation = await _import.ValidateAsync(package);
        Assert.Equal(1, validation.ConflictingKnowledgeItems);
        Assert.Equal(1, validation.ConflictingFileRecords);
        Assert.Equal(1, validation.ConflictingComments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_ForeignIdentityNamespace_SeparatesSourceTenantsAndEditions(bool changeEdition)
    {
        var package = Package("platform");
        Assert.True((await _import.ImportAsync(package)).Success);
        if (changeEdition) package.SourceEdition = "another-edition";
        else package.SourceTenantId = Guid.NewGuid();
        Assert.True((await _import.ImportAsync(package)).Success);
        Assert.Equal(2, await _db.KnowledgeItems.CountAsync());
        Assert.Equal(2, await _db.FileRecords.CountAsync());
    }

    [Fact]
    public async Task Validate_RenamedCrossEditionReferences_ReportsStableIdentityConflicts()
    {
        var package = Package("platform");
        package.Data.Vaults.Add(new() { Id = Guid.NewGuid(), Name = "Vault" });
        package.Data.Persons.Add(new() { Id = Guid.NewGuid(), Name = "Person" });
        package.Data.Locations.Add(new() { Id = Guid.NewGuid(), Name = "Location" });
        package.Data.Events.Add(new() { Id = Guid.NewGuid(), Name = "Event" });
        package.Data.Topics.Add(new() { Id = Guid.NewGuid(), Name = "Topic" });
        package.Data.Tags.Add(new() { Id = Guid.NewGuid(), Name = "Tag" });
        Assert.True((await _import.ImportAsync(package)).Success);
        package.Data.Vaults[0].Name = "Renamed vault";
        package.Data.Persons[0].Name = "Renamed person";
        package.Data.Locations[0].Name = "Renamed location";
        package.Data.Events[0].Name = "Renamed event";
        package.Data.Topics[0].Name = "Renamed topic";
        package.Data.Tags[0].Name = "Renamed tag";
        var preview = await _import.ValidateAsync(package);
        Assert.Equal(new[] { 1, 1, 1, 1, 1, 1 }, new[] { preview.ConflictingVaults, preview.ConflictingPersons,
            preview.ConflictingLocations, preview.ConflictingEvents, preview.ConflictingTopics, preview.ConflictingTags });
    }

    [Theory]
    [InlineData("skip")]
    [InlineData("overwrite")]
    public async Task Import_MultipartSharedFile_PreservesEarlierPartLinks(string strategy)
    {
        var package = Package("platform");
        package.Data.FileRecords[0].Attachments[0].Title = "First part";
        var original = RandomNumberGenerator.GetBytes(128);
        Assert.Equal(HttpStatusCode.OK, (await PostZip(package, original)).StatusCode);
        package.Data.KnowledgeItems[0].Id = Guid.NewGuid();
        package.Data.FileRecords[0].Attachments[0].KnowledgeId = package.Data.KnowledgeItems[0].Id;
        package.Data.FileRecords[0].Attachments[0].Title = "Second part";
        var replacement = RandomNumberGenerator.GetBytes(128);
        Assert.Equal(HttpStatusCode.OK, (await PostZip(package, replacement, strategy)).StatusCode);
        Assert.Equal(2, await _db.KnowledgeItems.CountAsync());
        Assert.Equal(2, await _db.FileAttachments.CountAsync());
        await AssertBytes((await _db.FileRecords.SingleAsync()).Id, strategy == "skip" ? original : replacement);
        var exported = await _export.ExportAsync();
        Assert.Equal(new[] { "First part", "Second part" }, exported.Data.FileRecords.Single().Attachments.Select(a => a.Title).OrderBy(t => t));
    }

    private static PortableExportPackage Package(string edition = "selfhosted")
    {
        var item = Guid.NewGuid(); var package = new PortableExportPackage { SourceEdition = edition, SourceTenantId = Guid.NewGuid() };
        package.Data.KnowledgeItems.Add(new() { Id = item, Title = "Imported item", Content = "Content" });
        package.Data.FileRecords.Add(new() { Id = Guid.NewGuid(), FileName = "data.bin", ContentType = "application/octet-stream", SizeBytes = 128,
            Attachments = new() { new() { KnowledgeId = item } } });
        return package;
    }

    private async Task<HttpResponseMessage> PostZip(PortableExportPackage package, byte[]? bytes, string strategy = "skip")
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            if (bytes != null)
            {
                var file = package.Data.FileRecords[0]; file.BinaryContentBase64 = null; file.BinaryFilePath = $"files/{file.Id}.bin";
                using var entry = archive.CreateEntry(file.BinaryFilePath).Open(); entry.Write(bytes);
            }
            PortableZipWriter.WriteToZip(archive, package);
        }
        using var form = new MultipartFormDataContent(); form.Add(new ByteArrayContent(stream.ToArray()), "file", "backup.zip");
        return await _client.PostAsync($"/api/v1/portability/import/zip?strategy={strategy}", form);
    }

    private async Task AssertBytes(Guid id, byte[] expected)
    {
        var (stream, _, _) = await _storage.DownloadAsync(_tenant, id); using (stream)
        {
            using var copy = new MemoryStream(); await stream.CopyToAsync(copy);
            Assert.Equal(SHA256.HashData(expected), SHA256.HashData(copy.ToArray()));
        }
    }
}
