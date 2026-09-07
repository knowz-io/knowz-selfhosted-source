using System.Text.Json;
using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.Core.Schema;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class PortableExportServiceTests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private readonly PortableExportService _svc;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public PortableExportServiceTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(options, tenantProvider);

        var storageProvider = Substitute.For<IFileStorageProvider>();
        var selfHostedOptions = Options.Create(new SelfHostedOptions());
        var logger = Substitute.For<ILogger<PortableExportService>>();
        _svc = new PortableExportService(_db, tenantProvider, storageProvider, selfHostedOptions, logger);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    [Fact]
    public async Task ExportAsync_EmptyDb_ReturnsEmptyPackage()
    {
        var package = await _svc.ExportAsync();

        Assert.Equal(CoreSchema.Version, package.SchemaVersion);
        Assert.Equal("selfhosted", package.SourceEdition);
        Assert.Equal(TenantId, package.SourceTenantId);
        Assert.Equal(0, package.Metadata.TotalVaults);
        Assert.Equal(0, package.Metadata.TotalKnowledgeItems);
        Assert.Empty(package.Data.Vaults);
        Assert.Empty(package.Data.KnowledgeItems);
    }

    [Fact]
    public async Task ExportAsync_SchemaVersion_MatchesCoreSchema()
    {
        var package = await _svc.ExportAsync();
        Assert.Equal(CoreSchema.Version, package.SchemaVersion);
    }

    [Fact]
    public async Task ExportAsync_SourceEdition_IsSelfHosted()
    {
        var package = await _svc.ExportAsync();
        Assert.Equal("selfhosted", package.SourceEdition);
    }

    [Fact]
    public async Task ExportAsync_ExportsVaults()
    {
        var vault = new Vault
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            Name = "Test Vault",
            Description = "A test vault",
            VaultType = VaultType.GeneralKnowledge,
            IsDefault = true
        };
        _db.Vaults.Add(vault);
        await _db.SaveChangesAsync();

        var package = await _svc.ExportAsync();

        Assert.Single(package.Data.Vaults);
        Assert.Equal(1, package.Metadata.TotalVaults);
        var pv = package.Data.Vaults[0];
        Assert.Equal(vault.Id, pv.Id);
        Assert.Equal("Test Vault", pv.Name);
        Assert.Equal("A test vault", pv.Description);
        Assert.Equal(VaultType.GeneralKnowledge, pv.VaultType);
        Assert.True(pv.IsDefault);
    }

    [Fact]
    public async Task ExportAsync_ExportsVaultHierarchy()
    {
        var parentVault = new Vault { Id = Guid.NewGuid(), TenantId = TenantId, Name = "Parent" };
        var childVault = new Vault { Id = Guid.NewGuid(), TenantId = TenantId, Name = "Child", ParentVaultId = parentVault.Id };
        _db.Vaults.AddRange(parentVault, childVault);
        await _db.SaveChangesAsync();

        var package = await _svc.ExportAsync();

        Assert.Equal(2, package.Data.Vaults.Count);
        var child = package.Data.Vaults.First(v => v.Name == "Child");
        Assert.Equal(parentVault.Id, child.ParentVaultId);
    }

    [Fact]
    public async Task ExportAsync_ExportsKnowledgeWithJunctions()
    {
        var vault = new Vault { Id = Guid.NewGuid(), TenantId = TenantId, Name = "V1" };
        var person = new Person { Id = Guid.NewGuid(), TenantId = TenantId, Name = "P1" };
        var location = new Location { Id = Guid.NewGuid(), TenantId = TenantId, Name = "L1" };
        var evt = new Event { Id = Guid.NewGuid(), TenantId = TenantId, Name = "E1" };
        var tag = new Tag { Id = Guid.NewGuid(), TenantId = TenantId, Name = "T1" };
        var topic = new Topic { Id = Guid.NewGuid(), TenantId = TenantId, Name = "Topic1" };

        var knowledge = new Knowledge
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            Title = "Test Knowledge",
            Content = "Some content",
            Type = KnowledgeType.Note,
            TopicId = topic.Id
        };

        _db.Vaults.Add(vault);
        _db.Persons.Add(person);
        _db.Locations.Add(location);
        _db.Events.Add(evt);
        _db.Tags.Add(tag);
        _db.Topics.Add(topic);
        _db.KnowledgeItems.Add(knowledge);
        _db.KnowledgeVaults.Add(new KnowledgeVault { KnowledgeId = knowledge.Id, VaultId = vault.Id, TenantId = TenantId, IsPrimary = true });
        _db.KnowledgePersons.Add(new KnowledgePerson { KnowledgeId = knowledge.Id, PersonId = person.Id });
        _db.KnowledgeLocations.Add(new KnowledgeLocation { KnowledgeId = knowledge.Id, LocationId = location.Id });
        _db.KnowledgeEvents.Add(new KnowledgeEvent { KnowledgeId = knowledge.Id, EventId = evt.Id });
        knowledge.Tags.Add(tag);
        await _db.SaveChangesAsync();

        var package = await _svc.ExportAsync();

        var pk = package.Data.KnowledgeItems.Single();
        Assert.Equal("Test Knowledge", pk.Title);
        Assert.Equal("Some content", pk.Content);
        Assert.Equal(topic.Id, pk.TopicId);
        Assert.Contains(vault.Id, pk.VaultIds);
        Assert.Equal(vault.Id, pk.PrimaryVaultId);
        Assert.Contains(tag.Id, pk.TagIds);
        Assert.Contains(person.Id, pk.PersonIds);
        Assert.Contains(location.Id, pk.LocationIds);
        Assert.Contains(evt.Id, pk.EventIds);
        Assert.Null(pk.BriefSummary);
    }

    [Fact]
    public async Task ExportAsync_MapsBriefSummary()
    {
        var knowledge = new Knowledge
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            Title = "Journal",
            Content = "Body",
            BriefSummary = "A short retrieval summary.",
        };
        _db.KnowledgeItems.Add(knowledge);
        await _db.SaveChangesAsync();

        var package = await _svc.ExportAsync();

        var pk = package.Data.KnowledgeItems.Single();
        Assert.Equal("A short retrieval summary.", pk.BriefSummary);
    }

    [Fact]
    public async Task ExportAsync_ExcludesSoftDeletedEntities()
    {
        _db.Vaults.Add(new Vault { Id = Guid.NewGuid(), TenantId = TenantId, Name = "Active" });
        _db.Vaults.Add(new Vault { Id = Guid.NewGuid(), TenantId = TenantId, Name = "Deleted", IsDeleted = true });
        await _db.SaveChangesAsync();

        var package = await _svc.ExportAsync();

        Assert.Single(package.Data.Vaults);
        Assert.Equal("Active", package.Data.Vaults[0].Name);
    }

    [Fact]
    public async Task ExportAsync_MetadataCountsMatchData()
    {
        _db.Vaults.Add(new Vault { Id = Guid.NewGuid(), TenantId = TenantId, Name = "V1" });
        _db.Vaults.Add(new Vault { Id = Guid.NewGuid(), TenantId = TenantId, Name = "V2" });
        _db.Tags.Add(new Tag { Id = Guid.NewGuid(), TenantId = TenantId, Name = "Tag1" });
        await _db.SaveChangesAsync();

        var package = await _svc.ExportAsync();

        Assert.Equal(package.Data.Vaults.Count, package.Metadata.TotalVaults);
        Assert.Equal(package.Data.Tags.Count, package.Metadata.TotalTags);
        Assert.Equal(package.Data.KnowledgeItems.Count, package.Metadata.TotalKnowledgeItems);
    }

    [Fact]
    public async Task ExportAsync_PlatformData_NullForNativeEntities()
    {
        _db.Vaults.Add(new Vault { Id = Guid.NewGuid(), TenantId = TenantId, Name = "V1" });
        await _db.SaveChangesAsync();

        var package = await _svc.ExportAsync();

        Assert.Null(package.Data.Vaults[0].ExtensionData);
    }

    [Fact]
    public async Task ExportAsync_PlatformData_MergedIntoExtensionData()
    {
        var platformData = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            { "Email", "test@example.com" },
            { "Phone", "555-1234" }
        });

        _db.Persons.Add(new Person
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            Name = "Test Person",
            PlatformData = platformData
        });
        await _db.SaveChangesAsync();

        var package = await _svc.ExportAsync();

        var pp = package.Data.Persons.Single();
        Assert.NotNull(pp.ExtensionData);
        Assert.True(pp.ExtensionData!.ContainsKey("Email"));
        Assert.True(pp.ExtensionData!.ContainsKey("Phone"));
    }

    [Fact]
    public async Task ExportAsync_JsonRoundTrip_ProducesValidJson()
    {
        _db.Vaults.Add(new Vault { Id = Guid.NewGuid(), TenantId = TenantId, Name = "V1" });
        _db.KnowledgeItems.Add(new Knowledge { Id = Guid.NewGuid(), TenantId = TenantId, Title = "K1", Content = "C1" });
        await _db.SaveChangesAsync();

        var package = await _svc.ExportAsync();
        var json = JsonSerializer.Serialize(package);
        var deserialized = JsonSerializer.Deserialize<Knowz.Core.Portability.PortableExportPackage>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(package.SchemaVersion, deserialized!.SchemaVersion);
        Assert.Equal(package.Data.Vaults.Count, deserialized.Data.Vaults.Count);
        Assert.Equal(package.Data.KnowledgeItems.Count, deserialized.Data.KnowledgeItems.Count);
    }

    [Fact]
    public async Task ExportAsync_ExportsAllEntityTypes()
    {
        _db.Vaults.Add(new Vault { Id = Guid.NewGuid(), TenantId = TenantId, Name = "V1" });
        _db.Topics.Add(new Topic { Id = Guid.NewGuid(), TenantId = TenantId, Name = "T1" });
        _db.Tags.Add(new Tag { Id = Guid.NewGuid(), TenantId = TenantId, Name = "Tag1" });
        _db.Persons.Add(new Person { Id = Guid.NewGuid(), TenantId = TenantId, Name = "P1" });
        _db.Locations.Add(new Location { Id = Guid.NewGuid(), TenantId = TenantId, Name = "L1" });
        _db.Events.Add(new Event { Id = Guid.NewGuid(), TenantId = TenantId, Name = "E1" });
        _db.KnowledgeItems.Add(new Knowledge { Id = Guid.NewGuid(), TenantId = TenantId, Title = "K1", Content = "C1" });
        _db.InboxItems.Add(new InboxItem { Id = Guid.NewGuid(), TenantId = TenantId, Body = "Inbox body" });
        await _db.SaveChangesAsync();

        var package = await _svc.ExportAsync();

        Assert.Single(package.Data.Vaults);
        Assert.Single(package.Data.Topics);
        Assert.Single(package.Data.Tags);
        Assert.Single(package.Data.Persons);
        Assert.Single(package.Data.Locations);
        Assert.Single(package.Data.Events);
        Assert.Single(package.Data.KnowledgeItems);
        Assert.Single(package.Data.InboxItems);
    }

    [Fact]
    public async Task ExportAsync_PrimaryVaultId_NullWhenNoPrimary()
    {
        var vault = new Vault { Id = Guid.NewGuid(), TenantId = TenantId, Name = "V1" };
        var knowledge = new Knowledge { Id = Guid.NewGuid(), TenantId = TenantId, Title = "K1", Content = "C1" };
        _db.Vaults.Add(vault);
        _db.KnowledgeItems.Add(knowledge);
        _db.KnowledgeVaults.Add(new KnowledgeVault
        {
            KnowledgeId = knowledge.Id, VaultId = vault.Id, TenantId = TenantId, IsPrimary = false
        });
        await _db.SaveChangesAsync();

        var package = await _svc.ExportAsync();

        var pk = package.Data.KnowledgeItems.Single();
        Assert.Null(pk.PrimaryVaultId);
        Assert.Contains(vault.Id, pk.VaultIds);
    }
}
