using System.Text.Json;
using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Portability;
using Knowz.Core.Schema;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class PortableImportServiceTests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private readonly PortableImportService _svc;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public PortableImportServiceTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        var tenantProvider = Substitute.For<Knowz.Core.Interfaces.ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(options, tenantProvider);

        var storageProvider = Substitute.For<IFileStorageProvider>();
        var logger = Substitute.For<ILogger<PortableImportService>>();
        _svc = new PortableImportService(_db, tenantProvider, storageProvider, logger);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    private static PortableExportPackage CreateEmptyPackage(string sourceEdition = "selfhosted")
    {
        return new PortableExportPackage
        {
            SchemaVersion = CoreSchema.Version,
            SourceEdition = sourceEdition,
            SourceTenantId = Guid.NewGuid(),
            ExportedAt = DateTime.UtcNow,
            Metadata = new PortableExportMetadata(),
            Data = new PortableExportData()
        };
    }

    private static PortableExportPackage CreatePackageWithVault(
        string sourceEdition = "selfhosted",
        Guid? vaultId = null,
        string name = "Imported Vault")
    {
        var package = CreateEmptyPackage(sourceEdition);
        var id = vaultId ?? Guid.NewGuid();
        package.Data.Vaults.Add(new PortableVault { Id = id, Name = name, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        package.Metadata.TotalVaults = 1;
        return package;
    }

    // --- Schema validation tests ---

    [Fact]
    public async Task ValidateAsync_CompatibleSchema_ReturnsValid()
    {
        var package = CreateEmptyPackage();
        var result = await _svc.ValidateAsync(package);

        Assert.True(result.IsValid);
        Assert.True(result.SchemaCompatible);
        Assert.Null(result.SchemaError);
    }

    [Fact]
    public async Task ValidateAsync_IncompatibleSchema_ReturnsInvalid()
    {
        var package = CreateEmptyPackage();
        package.SchemaVersion = 999;

        var result = await _svc.ValidateAsync(package);

        Assert.False(result.IsValid);
        Assert.False(result.SchemaCompatible);
        Assert.NotNull(result.SchemaError);
        Assert.Contains("999", result.SchemaError!);
    }

    [Fact]
    public async Task ValidateAsync_DetectsGuidConflicts_SameEdition()
    {
        var vaultId = Guid.NewGuid();
        _db.Vaults.Add(new Vault { Id = vaultId, TenantId = TenantId, Name = "Existing" });
        await _db.SaveChangesAsync();

        var package = CreatePackageWithVault(vaultId: vaultId);
        var result = await _svc.ValidateAsync(package);

        Assert.True(result.IsValid);
        Assert.Equal(1, result.ConflictingVaults);
    }

    [Fact]
    public async Task ValidateAsync_DetectsNameConflicts_CrossEdition()
    {
        _db.Vaults.Add(new Vault { Id = Guid.NewGuid(), TenantId = TenantId, Name = "Shared Name" });
        await _db.SaveChangesAsync();

        var package = CreatePackageWithVault(sourceEdition: "platform", name: "Shared Name");
        var result = await _svc.ValidateAsync(package);

        Assert.True(result.IsValid);
        Assert.Equal(1, result.ConflictingVaults);
    }

    [Fact]
    public async Task ValidateAsync_ReturnsCounts()
    {
        var package = CreateEmptyPackage();
        package.Data.Vaults.Add(new PortableVault { Id = Guid.NewGuid(), Name = "V1", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        package.Data.Vaults.Add(new PortableVault { Id = Guid.NewGuid(), Name = "V2", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        package.Metadata.TotalVaults = 2;

        var result = await _svc.ValidateAsync(package);
        Assert.Equal(2, result.TotalVaults);
    }

    // --- Import: Schema gate ---

    [Fact]
    public async Task ImportAsync_IncompatibleSchema_RejectsImport()
    {
        var package = CreateEmptyPackage();
        package.SchemaVersion = 999;

        var result = await _svc.ImportAsync(package);

        Assert.False(result.Success);
        Assert.Contains("999", result.Error!);
    }

    // --- Import: Same-edition GUID preservation ---

    [Fact]
    public async Task ImportAsync_SameEdition_PreservesGuids()
    {
        var vaultId = Guid.NewGuid();
        var package = CreatePackageWithVault(vaultId: vaultId);

        var result = await _svc.ImportAsync(package);

        Assert.True(result.Success);
        Assert.Equal(1, result.Vaults.Created);

        var vault = await _db.Vaults.FindAsync(vaultId);
        Assert.NotNull(vault);
        Assert.Equal("Imported Vault", vault!.Name);
    }

    // --- Import: Cross-edition GUID regeneration ---

    [Fact]
    public async Task ImportAsync_CrossEdition_GeneratesNewGuids()
    {
        var originalId = Guid.NewGuid();
        var package = CreatePackageWithVault(sourceEdition: "platform", vaultId: originalId);

        var result = await _svc.ImportAsync(package);

        Assert.True(result.Success);
        Assert.Equal(1, result.Vaults.Created);

        // Original ID should NOT exist (new GUID generated)
        var vaultByOriginalId = await _db.Vaults.FindAsync(originalId);
        Assert.Null(vaultByOriginalId);

        // But vault should exist with a new ID
        var allVaults = await _db.Vaults.ToListAsync();
        Assert.Single(allVaults);
        Assert.NotEqual(originalId, allVaults[0].Id);
    }

    // --- Import: Conflict strategies ---

    [Fact]
    public async Task ImportAsync_Skip_LeavesExistingUnchanged()
    {
        var vaultId = Guid.NewGuid();
        _db.Vaults.Add(new Vault { Id = vaultId, TenantId = TenantId, Name = "Original", Description = "Original desc" });
        await _db.SaveChangesAsync();

        var package = CreateEmptyPackage();
        package.Data.Vaults.Add(new PortableVault
        {
            Id = vaultId, Name = "Updated", Description = "New desc", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        package.Metadata.TotalVaults = 1;

        var result = await _svc.ImportAsync(package, ImportConflictStrategy.Skip);

        Assert.True(result.Success);
        Assert.Equal(1, result.Vaults.Skipped);
        Assert.Equal(0, result.Vaults.Created);

        var vault = await _db.Vaults.FindAsync(vaultId);
        Assert.Equal("Original", vault!.Name);
        Assert.Equal("Original desc", vault.Description);
    }

    [Fact]
    public async Task ImportAsync_Overwrite_ReplacesExisting()
    {
        var vaultId = Guid.NewGuid();
        _db.Vaults.Add(new Vault { Id = vaultId, TenantId = TenantId, Name = "Original", Description = "Old" });
        await _db.SaveChangesAsync();

        var package = CreateEmptyPackage();
        package.Data.Vaults.Add(new PortableVault
        {
            Id = vaultId, Name = "Overwritten", Description = "New", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        package.Metadata.TotalVaults = 1;

        var result = await _svc.ImportAsync(package, ImportConflictStrategy.Overwrite);

        Assert.True(result.Success);
        Assert.Equal(1, result.Vaults.Overwritten);

        var vault = await _db.Vaults.FindAsync(vaultId);
        Assert.Equal("Overwritten", vault!.Name);
        Assert.Equal("New", vault.Description);
    }

    [Fact]
    public async Task ImportAsync_Merge_FillsNullFields()
    {
        var vaultId = Guid.NewGuid();
        _db.Vaults.Add(new Vault { Id = vaultId, TenantId = TenantId, Name = "Original", Description = null });
        await _db.SaveChangesAsync();

        var package = CreateEmptyPackage();
        package.Data.Vaults.Add(new PortableVault
        {
            Id = vaultId, Name = "Updated", Description = "Merged desc", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        package.Metadata.TotalVaults = 1;

        var result = await _svc.ImportAsync(package, ImportConflictStrategy.Merge);

        Assert.True(result.Success);
        Assert.Equal(1, result.Vaults.Merged);

        var vault = await _db.Vaults.FindAsync(vaultId);
        Assert.Equal("Original", vault!.Name); // Name NOT merged (already has value in merge)
        Assert.Equal("Merged desc", vault.Description); // Description was null, gets filled
    }

    [Fact]
    public async Task ImportAsync_Merge_DoesNotOverwriteExistingValues()
    {
        var vaultId = Guid.NewGuid();
        _db.Vaults.Add(new Vault { Id = vaultId, TenantId = TenantId, Name = "Original", Description = "Existing desc" });
        await _db.SaveChangesAsync();

        var package = CreateEmptyPackage();
        package.Data.Vaults.Add(new PortableVault
        {
            Id = vaultId, Name = "Updated", Description = "Should not replace", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        package.Metadata.TotalVaults = 1;

        var result = await _svc.ImportAsync(package, ImportConflictStrategy.Merge);

        var vault = await _db.Vaults.FindAsync(vaultId);
        Assert.Equal("Existing desc", vault!.Description); // Not overwritten
    }

    // --- Import: Knowledge with junctions ---

    [Fact]
    public async Task ImportAsync_CreatesKnowledgeWithJunctions()
    {
        var vaultId = Guid.NewGuid();
        var personId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        var topicId = Guid.NewGuid();
        var knowledgeId = Guid.NewGuid();

        var package = CreateEmptyPackage();
        package.Data.Vaults.Add(new PortableVault { Id = vaultId, Name = "V1", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        package.Data.Persons.Add(new PortablePerson { Id = personId, Name = "P1", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        package.Data.Locations.Add(new PortableLocation { Id = locationId, Name = "L1", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        package.Data.Events.Add(new PortableEvent { Id = eventId, Name = "E1", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        package.Data.Tags.Add(new PortableTag { Id = tagId, Name = "Tag1", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        package.Data.Topics.Add(new PortableTopic { Id = topicId, Name = "Topic1", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        package.Data.KnowledgeItems.Add(new PortableKnowledge
        {
            Id = knowledgeId,
            Title = "Knowledge 1",
            Content = "Content",
            Type = KnowledgeType.Note,
            TopicId = topicId,
            VaultIds = new List<Guid> { vaultId },
            PrimaryVaultId = vaultId,
            TagIds = new List<Guid> { tagId },
            PersonIds = new List<Guid> { personId },
            LocationIds = new List<Guid> { locationId },
            EventIds = new List<Guid> { eventId },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        var result = await _svc.ImportAsync(package);

        Assert.True(result.Success);
        Assert.Equal(1, result.KnowledgeItems.Created);
        Assert.True(result.JunctionsRestored > 0);

        // Verify junctions
        var kv = await _db.KnowledgeVaults.Where(j => j.KnowledgeId == knowledgeId).ToListAsync();
        Assert.Single(kv);
        Assert.True(kv[0].IsPrimary);

        var kp = await _db.KnowledgePersons.Where(j => j.KnowledgeId == knowledgeId).ToListAsync();
        Assert.Single(kp);

        var kl = await _db.KnowledgeLocations.Where(j => j.KnowledgeId == knowledgeId).ToListAsync();
        Assert.Single(kl);

        var ke = await _db.KnowledgeEvents.Where(j => j.KnowledgeId == knowledgeId).ToListAsync();
        Assert.Single(ke);

        var knowledge = await _db.KnowledgeItems.Include(k => k.Tags).FirstAsync(k => k.Id == knowledgeId);
        Assert.Single(knowledge.Tags);
    }

    [Fact]
    public async Task ImportAsync_Knowledge_IsIndexedSetToFalse()
    {
        var package = CreateEmptyPackage();
        var knowledgeId = Guid.NewGuid();
        package.Data.KnowledgeItems.Add(new PortableKnowledge
        {
            Id = knowledgeId,
            Title = "K1",
            Content = "C1",
            IsIndexed = true,
            IndexedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        var result = await _svc.ImportAsync(package);

        Assert.True(result.Success);
        var knowledge = await _db.KnowledgeItems.FindAsync(knowledgeId);
        Assert.False(knowledge!.IsIndexed);
        Assert.Null(knowledge.IndexedAt);
    }

    [Fact]
    public async Task ImportAsync_Create_SetsBriefSummary()
    {
        var knowledgeId = Guid.NewGuid();
        var package = CreateEmptyPackage();
        package.Data.KnowledgeItems.Add(new PortableKnowledge
        {
            Id = knowledgeId,
            Title = "K1",
            Content = "C1",
            BriefSummary = "Imported brief.",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        var result = await _svc.ImportAsync(package);

        Assert.True(result.Success);
        var knowledge = await _db.KnowledgeItems.FindAsync(knowledgeId);
        Assert.Equal("Imported brief.", knowledge!.BriefSummary);
    }

    [Fact]
    public async Task ImportAsync_Overwrite_ReplacesBriefSummary()
    {
        var knowledgeId = Guid.NewGuid();
        _db.KnowledgeItems.Add(new Knowledge
        {
            Id = knowledgeId,
            TenantId = TenantId,
            Title = "Old",
            Content = "Old content",
            BriefSummary = "Old brief"
        });
        await _db.SaveChangesAsync();

        var package = CreateEmptyPackage();
        package.Data.KnowledgeItems.Add(new PortableKnowledge
        {
            Id = knowledgeId,
            Title = "New",
            Content = "New content",
            BriefSummary = "New brief",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        var result = await _svc.ImportAsync(package, ImportConflictStrategy.Overwrite);

        Assert.True(result.Success);
        var knowledge = await _db.KnowledgeItems.FindAsync(knowledgeId);
        Assert.Equal("New brief", knowledge!.BriefSummary);
    }

    // --- Import: Dependency order ---

    [Fact]
    public async Task ImportAsync_TopicIdRemapped_WhenCrossEdition()
    {
        var originalTopicId = Guid.NewGuid();
        var knowledgeId = Guid.NewGuid();

        var package = CreateEmptyPackage("platform");
        package.Data.Topics.Add(new PortableTopic { Id = originalTopicId, Name = "Topic1", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        package.Data.KnowledgeItems.Add(new PortableKnowledge
        {
            Id = knowledgeId,
            Title = "K1",
            Content = "C1",
            TopicId = originalTopicId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        var result = await _svc.ImportAsync(package);

        Assert.True(result.Success);

        // Topic should have a new ID
        var allTopics = await _db.Topics.ToListAsync();
        Assert.Single(allTopics);
        Assert.NotEqual(originalTopicId, allTopics[0].Id);

        // Knowledge should reference the remapped topic ID
        var allKnowledge = await _db.KnowledgeItems.ToListAsync();
        Assert.Single(allKnowledge);
        Assert.Equal(allTopics[0].Id, allKnowledge[0].TopicId);
    }

    // --- Import: VaultAncestor recomputation ---

    [Fact]
    public async Task ImportAsync_RecomputesVaultAncestors()
    {
        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var grandChildId = Guid.NewGuid();

        var package = CreateEmptyPackage();
        package.Data.Vaults.Add(new PortableVault { Id = rootId, Name = "Root", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        package.Data.Vaults.Add(new PortableVault { Id = childId, Name = "Child", ParentVaultId = rootId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        package.Data.Vaults.Add(new PortableVault { Id = grandChildId, Name = "GrandChild", ParentVaultId = childId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });

        var result = await _svc.ImportAsync(package);

        Assert.True(result.Success);

        var ancestors = await _db.VaultAncestors.ToListAsync();
        // Child -> Root (depth 1)
        Assert.Contains(ancestors, va => va.DescendantVaultId == childId && va.AncestorVaultId == rootId && va.Depth == 1);
        // GrandChild -> Child (depth 1)
        Assert.Contains(ancestors, va => va.DescendantVaultId == grandChildId && va.AncestorVaultId == childId && va.Depth == 1);
        // GrandChild -> Root (depth 2)
        Assert.Contains(ancestors, va => va.DescendantVaultId == grandChildId && va.AncestorVaultId == rootId && va.Depth == 2);
    }

    // --- Import: PlatformData storage ---

    [Fact]
    public async Task ImportAsync_PlatformSourced_StoresExtensionDataAsPlatformData()
    {
        var package = CreateEmptyPackage("platform");
        var personId = Guid.NewGuid();
        package.Data.Persons.Add(new PortablePerson
        {
            Id = personId,
            Name = "Test Person",
            ExtensionData = new Dictionary<string, JsonElement>
            {
                { "Email", JsonSerializer.Deserialize<JsonElement>("\"test@example.com\"") },
                { "Phone", JsonSerializer.Deserialize<JsonElement>("\"555-1234\"") }
            },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        var result = await _svc.ImportAsync(package);

        Assert.True(result.Success);

        var allPersons = await _db.Persons.ToListAsync();
        Assert.Single(allPersons);
        Assert.NotNull(allPersons[0].PlatformData);
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(allPersons[0].PlatformData!);
        Assert.True(dict!.ContainsKey("Email"));
        Assert.Equal("test@example.com", dict["Email"].GetString());
    }

    [Fact]
    public async Task ImportAsync_SelfHostedSourced_PlatformDataIsNull()
    {
        var package = CreatePackageWithVault();

        var result = await _svc.ImportAsync(package);

        Assert.True(result.Success);
        var vault = await _db.Vaults.FirstAsync();
        Assert.Null(vault.PlatformData);
    }

    // --- Import: Overwrite strategy PlatformData ---

    [Fact]
    public async Task ImportAsync_Overwrite_ReplacesPlatformData()
    {
        var vaultId = Guid.NewGuid();
        _db.Vaults.Add(new Vault
        {
            Id = vaultId, TenantId = TenantId, Name = "Existing",
            PlatformData = JsonSerializer.Serialize(new Dictionary<string, object> { { "OldKey", "OldValue" } })
        });
        await _db.SaveChangesAsync();

        var package = CreateEmptyPackage();
        package.Data.Vaults.Add(new PortableVault
        {
            Id = vaultId, Name = "Updated",
            ExtensionData = new Dictionary<string, JsonElement>
            {
                { "NewKey", JsonSerializer.Deserialize<JsonElement>("\"NewValue\"") }
            },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

        var result = await _svc.ImportAsync(package, ImportConflictStrategy.Overwrite);

        Assert.True(result.Success);
        var vault = await _db.Vaults.FindAsync(vaultId);
        Assert.NotNull(vault!.PlatformData);
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(vault.PlatformData!);
        Assert.True(dict!.ContainsKey("NewKey"));
        Assert.False(dict.ContainsKey("OldKey")); // Replaced, not merged
    }

    // --- Import: Skip strategy preserves PlatformData ---

    [Fact]
    public async Task ImportAsync_Skip_PreservesPlatformData()
    {
        var vaultId = Guid.NewGuid();
        _db.Vaults.Add(new Vault
        {
            Id = vaultId, TenantId = TenantId, Name = "Existing",
            PlatformData = JsonSerializer.Serialize(new Dictionary<string, object> { { "Preserved", "Yes" } })
        });
        await _db.SaveChangesAsync();

        var package = CreateEmptyPackage();
        package.Data.Vaults.Add(new PortableVault
        {
            Id = vaultId, Name = "Ignored",
            ExtensionData = new Dictionary<string, JsonElement>
            {
                { "ShouldNotAppear", JsonSerializer.Deserialize<JsonElement>("\"Nope\"") }
            },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

        var result = await _svc.ImportAsync(package, ImportConflictStrategy.Skip);

        var vault = await _db.Vaults.FindAsync(vaultId);
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(vault!.PlatformData!);
        Assert.True(dict!.ContainsKey("Preserved"));
        Assert.False(dict.ContainsKey("ShouldNotAppear"));
    }

    // --- Import: All entity types ---

    [Fact]
    public async Task ImportAsync_ImportsAllEntityTypes()
    {
        var package = CreateEmptyPackage();
        package.Data.Vaults.Add(new PortableVault { Id = Guid.NewGuid(), Name = "V1", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        package.Data.Topics.Add(new PortableTopic { Id = Guid.NewGuid(), Name = "T1", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        package.Data.Tags.Add(new PortableTag { Id = Guid.NewGuid(), Name = "Tag1", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        package.Data.Persons.Add(new PortablePerson { Id = Guid.NewGuid(), Name = "P1", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        package.Data.Locations.Add(new PortableLocation { Id = Guid.NewGuid(), Name = "L1", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        package.Data.Events.Add(new PortableEvent { Id = Guid.NewGuid(), Name = "E1", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        package.Data.KnowledgeItems.Add(new PortableKnowledge
        {
            Id = Guid.NewGuid(), Title = "K1", Content = "C1",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        package.Data.InboxItems.Add(new PortableInboxItem
        {
            Id = Guid.NewGuid(), Body = "Inbox body",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

        var result = await _svc.ImportAsync(package);

        Assert.True(result.Success);
        Assert.Equal(1, result.Vaults.Created);
        Assert.Equal(1, result.Topics.Created);
        Assert.Equal(1, result.Tags.Created);
        Assert.Equal(1, result.Persons.Created);
        Assert.Equal(1, result.Locations.Created);
        Assert.Equal(1, result.Events.Created);
        Assert.Equal(1, result.KnowledgeItems.Created);
        Assert.Equal(1, result.InboxItems.Created);
    }

    // --- Import: Duration tracking ---

    [Fact]
    public async Task ImportAsync_TracksDuration()
    {
        var package = CreateEmptyPackage();
        var result = await _svc.ImportAsync(package);

        Assert.True(result.Duration >= TimeSpan.Zero);
    }

    // --- Import: Empty package ---

    [Fact]
    public async Task ImportAsync_EmptyPackage_Succeeds()
    {
        var package = CreateEmptyPackage();
        var result = await _svc.ImportAsync(package);

        Assert.True(result.Success);
        Assert.Equal(0, result.Vaults.Total);
        Assert.Equal(0, result.KnowledgeItems.Total);
    }

    // --- Import: Merge PlatformData on entities ---

    [Fact]
    public async Task ImportAsync_Merge_MergesPlatformData_ImportWinsOnKeyConflict()
    {
        var vaultId = Guid.NewGuid();
        _db.Vaults.Add(new Vault
        {
            Id = vaultId, TenantId = TenantId, Name = "Existing", Description = null,
            PlatformData = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                { "SharedKey", "OldValue" },
                { "ExistingOnly", "Kept" }
            })
        });
        await _db.SaveChangesAsync();

        var package = CreateEmptyPackage();
        package.Data.Vaults.Add(new PortableVault
        {
            Id = vaultId, Name = "Import", Description = "Filled",
            ExtensionData = new Dictionary<string, JsonElement>
            {
                { "SharedKey", JsonSerializer.Deserialize<JsonElement>("\"NewValue\"") },
                { "ImportOnly", JsonSerializer.Deserialize<JsonElement>("\"Added\"") }
            },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

        var result = await _svc.ImportAsync(package, ImportConflictStrategy.Merge);

        Assert.True(result.Success);
        var vault = await _db.Vaults.FindAsync(vaultId);
        Assert.Equal("Filled", vault!.Description); // Was null, got merged
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(vault.PlatformData!);
        Assert.Equal("NewValue", dict!["SharedKey"].GetString()); // Import wins
        Assert.Equal("Kept", dict["ExistingOnly"].GetString()); // Existing preserved
        Assert.Equal("Added", dict["ImportOnly"].GetString()); // New key added
    }

    // --- Import: InboxItem ---

    [Fact]
    public async Task ImportAsync_InboxItem_Skip_LeavesExisting()
    {
        var itemId = Guid.NewGuid();
        _db.InboxItems.Add(new InboxItem { Id = itemId, TenantId = TenantId, Body = "Original" });
        await _db.SaveChangesAsync();

        var package = CreateEmptyPackage();
        package.Data.InboxItems.Add(new PortableInboxItem
        {
            Id = itemId, Body = "Updated", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

        var result = await _svc.ImportAsync(package, ImportConflictStrategy.Skip);

        var item = await _db.InboxItems.FindAsync(itemId);
        Assert.Equal("Original", item!.Body);
    }

    // --- Round-trip test ---

    [Fact]
    public async Task RoundTrip_ExportThenImport_PreservesData()
    {
        // Set up data
        var vault = new Vault { Id = Guid.NewGuid(), TenantId = TenantId, Name = "RT Vault", Description = "Round trip" };
        var tag = new Tag { Id = Guid.NewGuid(), TenantId = TenantId, Name = "RT Tag" };
        var knowledge = new Knowledge
        {
            Id = Guid.NewGuid(), TenantId = TenantId, Title = "RT Knowledge", Content = "Round trip content"
        };

        _db.Vaults.Add(vault);
        _db.Tags.Add(tag);
        _db.KnowledgeItems.Add(knowledge);
        _db.KnowledgeVaults.Add(new KnowledgeVault
        {
            KnowledgeId = knowledge.Id, VaultId = vault.Id, TenantId = TenantId, IsPrimary = true
        });
        knowledge.Tags.Add(tag);
        await _db.SaveChangesAsync();

        // Export
        var tenantProviderLocal = Substitute.For<Knowz.Core.Interfaces.ITenantProvider>();
        tenantProviderLocal.TenantId.Returns(TenantId);
        var exportStorageProvider = Substitute.For<IFileStorageProvider>();
        var exportOptions = Options.Create(new SelfHostedOptions());
        var exportLogger = Substitute.For<ILogger<PortableExportService>>();
        var exportSvc = new PortableExportService(_db, tenantProviderLocal, exportStorageProvider, exportOptions, exportLogger);
        var package = await exportSvc.ExportAsync();

        // Clear database
        _db.KnowledgeVaults.RemoveRange(await _db.KnowledgeVaults.ToListAsync());
        _db.KnowledgeItems.RemoveRange(await _db.KnowledgeItems.ToListAsync());
        _db.Tags.RemoveRange(await _db.Tags.ToListAsync());
        _db.Vaults.RemoveRange(await _db.Vaults.ToListAsync());
        await _db.SaveChangesAsync();

        // Import into clean database
        var result = await _svc.ImportAsync(package);

        Assert.True(result.Success);

        // Verify data preserved
        var importedVault = await _db.Vaults.FirstAsync();
        Assert.Equal("RT Vault", importedVault.Name);
        Assert.Equal("Round trip", importedVault.Description);

        var importedKnowledge = await _db.KnowledgeItems.Include(k => k.Tags).FirstAsync();
        Assert.Equal("RT Knowledge", importedKnowledge.Title);
        Assert.Single(importedKnowledge.Tags);

        var importedKv = await _db.KnowledgeVaults.FirstAsync();
        Assert.True(importedKv.IsPrimary);
    }
}
