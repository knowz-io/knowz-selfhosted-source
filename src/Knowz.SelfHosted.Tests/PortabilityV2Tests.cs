using System.Text.Json;
using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
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

public class PortabilityV2Tests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private readonly PortableImportService _importSvc;
    private readonly PortableExportService _exportSvc;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public PortabilityV2Tests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(options, tenantProvider);

        var storageProvider = Substitute.For<IFileStorageProvider>();
        _importSvc = new PortableImportService(_db, tenantProvider, storageProvider, Substitute.For<ILogger<PortableImportService>>());
        _exportSvc = new PortableExportService(_db, tenantProvider, storageProvider, Options.Create(new SelfHostedOptions()), Substitute.For<ILogger<PortableExportService>>());
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

    // ===== Schema v2 backward compatibility =====

    [Fact]
    public async Task Import_V1Package_SucceedsWithNewNullableFields()
    {
        var package = CreateEmptyPackage();
        package.SchemaVersion = 1; // v1 package
        package.Data.Vaults.Add(new PortableVault
        {
            Id = Guid.NewGuid(), Name = "V1 Vault",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            // PersonIds not set (v1 doesn't have it) — defaults to empty list
        });
        package.Metadata.TotalVaults = 1;

        var result = await _importSvc.ImportAsync(package);

        Assert.True(result.Success);
        Assert.Equal(1, result.Vaults.Created);
    }

    [Fact]
    public async Task Import_V1Package_WithNoComments_Succeeds()
    {
        var package = CreateEmptyPackage();
        package.SchemaVersion = 1;
        // v1 package has no Comments, FileRecords, or Archives — all default to empty
        package.Data.KnowledgeItems.Add(new PortableKnowledge
        {
            Id = Guid.NewGuid(), Title = "K1", Content = "Content",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

        var result = await _importSvc.ImportAsync(package);

        Assert.True(result.Success);
        Assert.Equal(0, result.Comments.Total);
        Assert.Equal(0, result.FileRecords.Total);
        Assert.Equal(0, result.ArchiveRecordsStored);
    }

    // ===== VaultPerson =====

    [Fact]
    public async Task Import_VaultWithPersonIds_CreatesVaultPersonJunctions()
    {
        var vaultId = Guid.NewGuid();
        var personId = Guid.NewGuid();

        var package = CreateEmptyPackage();
        package.Data.Vaults.Add(new PortableVault
        {
            Id = vaultId, Name = "Finn's Vault",
            PersonIds = new List<Guid> { personId },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        package.Data.Persons.Add(new PortablePerson
        {
            Id = personId, Name = "Finn",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

        var result = await _importSvc.ImportAsync(package);

        Assert.True(result.Success);
        var vaultPersons = await _db.VaultPersons.ToListAsync();
        Assert.Single(vaultPersons);
        Assert.Equal(vaultId, vaultPersons[0].VaultId);
        Assert.Equal(personId, vaultPersons[0].PersonId);
    }

    [Fact]
    public async Task Import_CrossEdition_VaultPersonIds_RemappedCorrectly()
    {
        var originalVaultId = Guid.NewGuid();
        var originalPersonId = Guid.NewGuid();

        var package = CreateEmptyPackage("platform");
        package.Data.Vaults.Add(new PortableVault
        {
            Id = originalVaultId, Name = "Person Vault",
            PersonIds = new List<Guid> { originalPersonId },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        package.Data.Persons.Add(new PortablePerson
        {
            Id = originalPersonId, Name = "Test Person",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

        var result = await _importSvc.ImportAsync(package);

        Assert.True(result.Success);
        var vaultPersons = await _db.VaultPersons.ToListAsync();
        Assert.Single(vaultPersons);
        // IDs should be remapped (cross-edition)
        Assert.NotEqual(originalVaultId, vaultPersons[0].VaultId);
        Assert.NotEqual(originalPersonId, vaultPersons[0].PersonId);
    }

    [Fact]
    public async Task Export_VaultWithPersons_IncludesPersonIds()
    {
        var person = new Person { Id = Guid.NewGuid(), TenantId = TenantId, Name = "Finn" };
        var vault = new Vault { Id = Guid.NewGuid(), TenantId = TenantId, Name = "Finn's Vault" };
        _db.Persons.Add(person);
        _db.Vaults.Add(vault);
        _db.VaultPersons.Add(new VaultPerson { VaultId = vault.Id, PersonId = person.Id });
        await _db.SaveChangesAsync();

        var package = await _exportSvc.ExportAsync();

        var exportedVault = Assert.Single(package.Data.Vaults);
        Assert.Single(exportedVault.PersonIds);
        Assert.Equal(person.Id, exportedVault.PersonIds[0]);
    }

    [Fact]
    public async Task Import_VaultPerson_NoDuplicatesOnReimport()
    {
        var vaultId = Guid.NewGuid();
        var personId = Guid.NewGuid();

        // First import
        var package = CreateEmptyPackage();
        package.Data.Vaults.Add(new PortableVault
        {
            Id = vaultId, Name = "V1",
            PersonIds = new List<Guid> { personId },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        package.Data.Persons.Add(new PortablePerson
        {
            Id = personId, Name = "P1",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await _importSvc.ImportAsync(package);

        // Second import (skip strategy)
        await _importSvc.ImportAsync(package, ImportConflictStrategy.Skip);

        var vaultPersons = await _db.VaultPersons.ToListAsync();
        Assert.Single(vaultPersons);
    }

    // ===== KnowledgeComment =====

    [Fact]
    public async Task Import_CommentsWithHierarchy_RestoresParentChild()
    {
        var knowledgeId = Guid.NewGuid();
        var comment1Id = Guid.NewGuid();
        var reply1Id = Guid.NewGuid();

        var package = CreateEmptyPackage();
        package.Data.KnowledgeItems.Add(new PortableKnowledge
        {
            Id = knowledgeId, Title = "Q&A Item", Content = "Question?",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        package.Data.Comments.Add(new PortableKnowledgeComment
        {
            Id = comment1Id, KnowledgeId = knowledgeId,
            AuthorName = "Alice", Body = "Root comment", IsAnswer = false,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        package.Data.Comments.Add(new PortableKnowledgeComment
        {
            Id = reply1Id, KnowledgeId = knowledgeId,
            ParentCommentId = comment1Id,
            AuthorName = "Bob", Body = "Reply to root", IsAnswer = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

        var result = await _importSvc.ImportAsync(package);

        Assert.True(result.Success);
        Assert.Equal(2, result.Comments.Created);

        var comments = await _db.Comments.ToListAsync();
        Assert.Equal(2, comments.Count);

        var root = comments.First(c => c.Id == comment1Id);
        Assert.Null(root.ParentCommentId);
        Assert.Equal("Alice", root.AuthorName);

        var reply = comments.First(c => c.Id == reply1Id);
        Assert.Equal(comment1Id, reply.ParentCommentId);
        Assert.True(reply.IsAnswer);
    }

    [Fact]
    public async Task Import_Comments_CrossEdition_RemapsIds()
    {
        var originalKnowledgeId = Guid.NewGuid();
        var originalCommentId = Guid.NewGuid();
        var originalReplyId = Guid.NewGuid();

        var package = CreateEmptyPackage("platform");
        package.Data.KnowledgeItems.Add(new PortableKnowledge
        {
            Id = originalKnowledgeId, Title = "K1", Content = "C1",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        package.Data.Comments.Add(new PortableKnowledgeComment
        {
            Id = originalCommentId, KnowledgeId = originalKnowledgeId,
            AuthorName = "Author", Body = "Comment",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        package.Data.Comments.Add(new PortableKnowledgeComment
        {
            Id = originalReplyId, KnowledgeId = originalKnowledgeId,
            ParentCommentId = originalCommentId,
            AuthorName = "Replier", Body = "Reply",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

        var result = await _importSvc.ImportAsync(package);

        Assert.True(result.Success);
        Assert.Equal(2, result.Comments.Created);

        var comments = await _db.Comments.ToListAsync();
        // IDs should be different (cross-edition)
        Assert.DoesNotContain(comments, c => c.Id == originalCommentId);
        Assert.DoesNotContain(comments, c => c.Id == originalReplyId);

        // Parent-child relationship should still be intact
        var reply = comments.First(c => c.AuthorName == "Replier");
        var root = comments.First(c => c.AuthorName == "Author");
        Assert.Equal(root.Id, reply.ParentCommentId);
    }

    [Fact]
    public async Task Export_Comments_IncludesAllFields()
    {
        var knowledge = new Knowledge
        {
            Id = Guid.NewGuid(), TenantId = TenantId,
            Title = "K1", Content = "C1"
        };
        _db.KnowledgeItems.Add(knowledge);

        var comment = new KnowledgeComment
        {
            Id = Guid.NewGuid(), TenantId = TenantId,
            KnowledgeId = knowledge.Id,
            AuthorName = "Test Author", Body = "Test body",
            IsAnswer = true, Sentiment = "positive"
        };
        _db.Comments.Add(comment);
        await _db.SaveChangesAsync();

        var package = await _exportSvc.ExportAsync();

        Assert.Single(package.Data.Comments);
        var exported = package.Data.Comments[0];
        Assert.Equal("Test Author", exported.AuthorName);
        Assert.Equal("Test body", exported.Body);
        Assert.True(exported.IsAnswer);
        Assert.Equal("positive", exported.Sentiment);
    }

    [Fact]
    public async Task Import_Comment_PreservesPlatformData()
    {
        var knowledgeId = Guid.NewGuid();

        var package = CreateEmptyPackage("platform");
        package.Data.KnowledgeItems.Add(new PortableKnowledge
        {
            Id = knowledgeId, Title = "K1", Content = "C1",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        package.Data.Comments.Add(new PortableKnowledgeComment
        {
            Id = Guid.NewGuid(), KnowledgeId = knowledgeId,
            AuthorName = "Author", Body = "Comment",
            ExtensionData = new Dictionary<string, JsonElement>
            {
                { "ModerationStatus", JsonSerializer.Deserialize<JsonElement>("\"Approved\"") },
                { "AttachmentCount", JsonSerializer.Deserialize<JsonElement>("3") }
            },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

        var result = await _importSvc.ImportAsync(package);

        Assert.True(result.Success);
        var comment = await _db.Comments.FirstAsync();
        Assert.NotNull(comment.PlatformData);
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(comment.PlatformData!);
        Assert.True(dict!.ContainsKey("ModerationStatus"));
        Assert.Equal("Approved", dict["ModerationStatus"].GetString());
    }

    // ===== FileRecord =====

    [Fact]
    public async Task Import_FileRecord_CreatesWithBlobMigrationPending()
    {
        var knowledgeId = Guid.NewGuid();
        var fileId = Guid.NewGuid();

        var package = CreateEmptyPackage();
        package.Data.KnowledgeItems.Add(new PortableKnowledge
        {
            Id = knowledgeId, Title = "K1", Content = "C1",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        package.Data.FileRecords.Add(new PortableFileRecord
        {
            Id = fileId,
            FileName = "document.pdf",
            ContentType = "application/pdf",
            SizeBytes = 12345,
            BlobUri = "https://storage.blob.core.windows.net/container/blob",
            TranscriptionText = "Transcribed text",
            ExtractedText = "Extracted content",
            VisionDescription = "A PDF document",
            Attachments = new List<PortableFileAttachmentLink>
            {
                new() { KnowledgeId = knowledgeId }
            },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

        var result = await _importSvc.ImportAsync(package);

        Assert.True(result.Success);
        Assert.Equal(1, result.FileRecords.Created);

        var file = await _db.FileRecords.FirstAsync();
        Assert.Equal("document.pdf", file.FileName);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.Equal(12345, file.SizeBytes);
        Assert.True(file.BlobMigrationPending);
        Assert.Equal("Transcribed text", file.TranscriptionText);

        var attachments = await _db.FileAttachments.ToListAsync();
        Assert.Single(attachments);
        Assert.Equal(knowledgeId, attachments[0].KnowledgeId);
    }

    [Fact]
    public async Task Export_FileRecord_IncludesAttachments()
    {
        var knowledge = new Knowledge
        {
            Id = Guid.NewGuid(), TenantId = TenantId,
            Title = "K1", Content = "C1"
        };
        _db.KnowledgeItems.Add(knowledge);

        var file = new FileRecord
        {
            Id = Guid.NewGuid(), TenantId = TenantId,
            FileName = "test.pdf", ContentType = "application/pdf",
            SizeBytes = 100, ExtractedText = "text from pdf"
        };
        _db.FileRecords.Add(file);
        _db.FileAttachments.Add(new FileAttachment
        {
            FileRecordId = file.Id, KnowledgeId = knowledge.Id, TenantId = TenantId
        });
        await _db.SaveChangesAsync();

        var package = await _exportSvc.ExportAsync();

        Assert.Single(package.Data.FileRecords);
        var exported = package.Data.FileRecords[0];
        Assert.Equal("test.pdf", exported.FileName);
        Assert.Equal("text from pdf", exported.ExtractedText);
        Assert.Single(exported.Attachments);
        Assert.Equal(knowledge.Id, exported.Attachments[0].KnowledgeId);
    }

    [Fact]
    public async Task Import_FileRecord_CrossEdition_RemapsAttachmentIds()
    {
        var originalKnowledgeId = Guid.NewGuid();
        var originalFileId = Guid.NewGuid();

        var package = CreateEmptyPackage("platform");
        package.Data.KnowledgeItems.Add(new PortableKnowledge
        {
            Id = originalKnowledgeId, Title = "K1", Content = "C1",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        package.Data.FileRecords.Add(new PortableFileRecord
        {
            Id = originalFileId, FileName = "test.pdf",
            Attachments = new List<PortableFileAttachmentLink>
            {
                new() { KnowledgeId = originalKnowledgeId }
            },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

        var result = await _importSvc.ImportAsync(package);

        Assert.True(result.Success);
        var attachment = await _db.FileAttachments.FirstAsync();
        Assert.NotEqual(originalKnowledgeId, attachment.KnowledgeId);
        // But should still point to the correct (remapped) knowledge item
        var knowledge = await _db.KnowledgeItems.FirstAsync();
        Assert.Equal(knowledge.Id, attachment.KnowledgeId);
    }

    // ===== PortableArchive =====

    [Fact]
    public async Task Import_Archives_StoresJsonBlobs()
    {
        var package = CreateEmptyPackage("platform");
        var engagement1 = JsonSerializer.Deserialize<JsonElement>(
            "{\"Id\":\"aaaaaaaa-0000-0000-0000-000000000001\",\"Question\":\"What happened?\",\"Status\":\"Generated\"}");
        var engagement2 = JsonSerializer.Deserialize<JsonElement>(
            "{\"Id\":\"aaaaaaaa-0000-0000-0000-000000000002\",\"Question\":\"Tell me more\",\"Status\":\"Sent\"}");
        var site = JsonSerializer.Deserialize<JsonElement>(
            "{\"Id\":\"bbbbbbbb-0000-0000-0000-000000000001\",\"Slug\":\"finn-site\",\"Subdomain\":\"finn\"}");

        package.Data.Archives["EngagementQuestionSuggestion"] = new List<JsonElement> { engagement1, engagement2 };
        package.Data.Archives["CustomerSite"] = new List<JsonElement> { site };

        var result = await _importSvc.ImportAsync(package);

        Assert.True(result.Success);
        Assert.Equal(3, result.ArchiveRecordsStored);

        var archives = await _db.PortableArchives.Where(a => a.TenantId == TenantId).ToListAsync();
        Assert.Equal(3, archives.Count);
        Assert.Equal(2, archives.Count(a => a.EntityType == "EngagementQuestionSuggestion"));
        Assert.Equal(1, archives.Count(a => a.EntityType == "CustomerSite"));
    }

    [Fact]
    public async Task Export_Archives_DeserializesBack()
    {
        // Set up some archive records
        _db.PortableArchives.Add(new PortableArchive
        {
            TenantId = TenantId,
            EntityType = "EngagementQuestionSuggestion",
            OriginalId = Guid.NewGuid(),
            JsonData = "{\"Question\":\"Test?\",\"Status\":\"Generated\"}"
        });
        _db.PortableArchives.Add(new PortableArchive
        {
            TenantId = TenantId,
            EntityType = "CustomerSite",
            OriginalId = Guid.NewGuid(),
            JsonData = "{\"Slug\":\"test-site\"}"
        });
        await _db.SaveChangesAsync();

        var package = await _exportSvc.ExportAsync();

        Assert.Equal(2, package.Data.Archives.Count);
        Assert.True(package.Data.Archives.ContainsKey("EngagementQuestionSuggestion"));
        Assert.True(package.Data.Archives.ContainsKey("CustomerSite"));
        Assert.Single(package.Data.Archives["EngagementQuestionSuggestion"]);
        Assert.Single(package.Data.Archives["CustomerSite"]);
    }

    [Fact]
    public async Task Import_Archives_ReimportReplacesExisting()
    {
        // First import
        var package = CreateEmptyPackage("platform");
        package.Data.Archives["TestEntity"] = new List<JsonElement>
        {
            JsonSerializer.Deserialize<JsonElement>("{\"Id\":\"00000000-0000-0000-0000-000000000001\",\"V\":1}")
        };
        await _importSvc.ImportAsync(package);

        // Second import with different data
        package.Data.Archives["TestEntity"] = new List<JsonElement>
        {
            JsonSerializer.Deserialize<JsonElement>("{\"Id\":\"00000000-0000-0000-0000-000000000002\",\"V\":2}")
        };
        await _importSvc.ImportAsync(package);

        var archives = await _db.PortableArchives.Where(a => a.TenantId == TenantId).ToListAsync();
        Assert.Single(archives); // Replaced, not duplicated
        Assert.Contains("\"V\":2", archives[0].JsonData);
    }

    // ===== Rich junction metadata (PersonLinks) =====

    [Fact]
    public async Task Import_PersonLinks_RestoresRichMetadata()
    {
        var knowledgeId = Guid.NewGuid();
        var personId = Guid.NewGuid();

        var package = CreateEmptyPackage();
        package.Data.Persons.Add(new PortablePerson
        {
            Id = personId, Name = "Finn",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        package.Data.KnowledgeItems.Add(new PortableKnowledge
        {
            Id = knowledgeId, Title = "About Finn", Content = "Content about Finn",
            PersonIds = new List<Guid> { personId }, // Legacy fallback
            PersonLinks = new List<PortableEntityLink>
            {
                new()
                {
                    EntityId = personId,
                    RelationshipContext = "Subject of the story",
                    Role = "Protagonist",
                    Mentions = 15,
                    ConfidenceScore = 0.95
                }
            },
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

        var result = await _importSvc.ImportAsync(package);

        Assert.True(result.Success);
        var kp = await _db.KnowledgePersons.FirstAsync();
        Assert.Equal("Subject of the story", kp.RelationshipContext);
        Assert.Equal("Protagonist", kp.Role);
        Assert.Equal(15, kp.Mentions);
        Assert.Equal(0.95, kp.ConfidenceScore);
    }

    [Fact]
    public async Task Import_PersonLinks_WhenNull_FallsBackToPersonIds()
    {
        var knowledgeId = Guid.NewGuid();
        var personId = Guid.NewGuid();

        var package = CreateEmptyPackage();
        package.Data.Persons.Add(new PortablePerson
        {
            Id = personId, Name = "Basic",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        package.Data.KnowledgeItems.Add(new PortableKnowledge
        {
            Id = knowledgeId, Title = "K1", Content = "C1",
            PersonIds = new List<Guid> { personId },
            PersonLinks = null, // v1 package, no PersonLinks
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

        var result = await _importSvc.ImportAsync(package);

        Assert.True(result.Success);
        var kp = await _db.KnowledgePersons.FirstAsync();
        Assert.Equal(personId, kp.PersonId);
        Assert.Null(kp.RelationshipContext);
        Assert.Null(kp.Role);
    }

    [Fact]
    public async Task Export_PersonLinks_IncludesRichMetadata()
    {
        var person = new Person { Id = Guid.NewGuid(), TenantId = TenantId, Name = "P1" };
        var knowledge = new Knowledge
        {
            Id = Guid.NewGuid(), TenantId = TenantId,
            Title = "K1", Content = "C1"
        };
        _db.Persons.Add(person);
        _db.KnowledgeItems.Add(knowledge);
        _db.KnowledgePersons.Add(new KnowledgePerson
        {
            KnowledgeId = knowledge.Id, PersonId = person.Id,
            RelationshipContext = "Mentioned in paragraph 2",
            Role = "Witness",
            Mentions = 3,
            ConfidenceScore = 0.87
        });
        await _db.SaveChangesAsync();

        var package = await _exportSvc.ExportAsync();

        var exported = Assert.Single(package.Data.KnowledgeItems);
        Assert.NotNull(exported.PersonLinks);
        var link = Assert.Single(exported.PersonLinks);
        Assert.Equal(person.Id, link.EntityId);
        Assert.Equal("Mentioned in paragraph 2", link.RelationshipContext);
        Assert.Equal("Witness", link.Role);
        Assert.Equal(3, link.Mentions);
        Assert.Equal(0.87, link.ConfidenceScore);
    }

    // ===== Metadata counts =====

    [Fact]
    public async Task Export_MetadataCountsIncludeV2Entities()
    {
        var knowledge = new Knowledge
        {
            Id = Guid.NewGuid(), TenantId = TenantId,
            Title = "K1", Content = "C1"
        };
        _db.KnowledgeItems.Add(knowledge);
        _db.Comments.Add(new KnowledgeComment
        {
            Id = Guid.NewGuid(), TenantId = TenantId,
            KnowledgeId = knowledge.Id,
            AuthorName = "Author", Body = "Comment"
        });
        _db.FileRecords.Add(new FileRecord
        {
            Id = Guid.NewGuid(), TenantId = TenantId,
            FileName = "file.txt"
        });
        _db.PortableArchives.Add(new PortableArchive
        {
            TenantId = TenantId,
            EntityType = "TestEntity",
            OriginalId = Guid.NewGuid(),
            JsonData = "{}"
        });
        await _db.SaveChangesAsync();

        var package = await _exportSvc.ExportAsync();

        Assert.Equal(1, package.Metadata.TotalComments);
        Assert.Equal(1, package.Metadata.TotalFileRecords);
        Assert.Equal(1, package.Metadata.TotalArchiveTypes);
    }

    [Fact]
    public async Task Validate_IncludesV2Counts()
    {
        var package = CreateEmptyPackage();
        package.Metadata.TotalComments = 5;
        package.Metadata.TotalFileRecords = 3;
        package.Metadata.TotalArchiveTypes = 2;

        var result = await _importSvc.ValidateAsync(package);

        Assert.True(result.IsValid);
        Assert.Equal(5, result.TotalComments);
        Assert.Equal(3, result.TotalFileRecords);
        Assert.Equal(2, result.TotalArchiveTypes);
    }

    // ===== Round-trip tests =====

    [Fact]
    public async Task RoundTrip_VaultPerson_Preserved()
    {
        var person = new Person { Id = Guid.NewGuid(), TenantId = TenantId, Name = "RT Person" };
        var vault = new Vault { Id = Guid.NewGuid(), TenantId = TenantId, Name = "RT Vault" };
        _db.Persons.Add(person);
        _db.Vaults.Add(vault);
        _db.VaultPersons.Add(new VaultPerson { VaultId = vault.Id, PersonId = person.Id });
        await _db.SaveChangesAsync();

        // Export
        var package = await _exportSvc.ExportAsync();

        // Clear
        _db.VaultPersons.RemoveRange(await _db.VaultPersons.ToListAsync());
        _db.Vaults.RemoveRange(await _db.Vaults.ToListAsync());
        _db.Persons.RemoveRange(await _db.Persons.ToListAsync());
        await _db.SaveChangesAsync();

        // Import
        var result = await _importSvc.ImportAsync(package);

        Assert.True(result.Success);
        var vp = await _db.VaultPersons.FirstAsync();
        Assert.Equal(vault.Id, vp.VaultId);
        Assert.Equal(person.Id, vp.PersonId);
    }

    [Fact]
    public async Task RoundTrip_CommentsWithHierarchy_Preserved()
    {
        var knowledge = new Knowledge
        {
            Id = Guid.NewGuid(), TenantId = TenantId,
            Title = "K1", Content = "C1"
        };
        _db.KnowledgeItems.Add(knowledge);

        var root = new KnowledgeComment
        {
            Id = Guid.NewGuid(), TenantId = TenantId,
            KnowledgeId = knowledge.Id,
            AuthorName = "Alice", Body = "Root"
        };
        _db.Comments.Add(root);

        var reply = new KnowledgeComment
        {
            Id = Guid.NewGuid(), TenantId = TenantId,
            KnowledgeId = knowledge.Id,
            ParentCommentId = root.Id,
            AuthorName = "Bob", Body = "Reply", IsAnswer = true
        };
        _db.Comments.Add(reply);
        await _db.SaveChangesAsync();

        // Export
        var package = await _exportSvc.ExportAsync();

        // Clear
        _db.Comments.RemoveRange(await _db.Comments.ToListAsync());
        _db.KnowledgeItems.RemoveRange(await _db.KnowledgeItems.ToListAsync());
        await _db.SaveChangesAsync();

        // Import
        var result = await _importSvc.ImportAsync(package);

        Assert.True(result.Success);
        var comments = await _db.Comments.ToListAsync();
        Assert.Equal(2, comments.Count);
        var importedReply = comments.First(c => c.AuthorName == "Bob");
        Assert.Equal(root.Id, importedReply.ParentCommentId);
        Assert.True(importedReply.IsAnswer);
    }

    [Fact]
    public async Task RoundTrip_FileRecordWithAttachment_Preserved()
    {
        var knowledge = new Knowledge
        {
            Id = Guid.NewGuid(), TenantId = TenantId,
            Title = "K1", Content = "C1"
        };
        _db.KnowledgeItems.Add(knowledge);

        var file = new FileRecord
        {
            Id = Guid.NewGuid(), TenantId = TenantId,
            FileName = "test.pdf", ContentType = "application/pdf",
            SizeBytes = 999, TranscriptionText = "Transcript"
        };
        _db.FileRecords.Add(file);
        _db.FileAttachments.Add(new FileAttachment
        {
            FileRecordId = file.Id, KnowledgeId = knowledge.Id, TenantId = TenantId
        });
        await _db.SaveChangesAsync();

        // Export
        var package = await _exportSvc.ExportAsync();

        // Clear
        _db.FileAttachments.RemoveRange(await _db.FileAttachments.ToListAsync());
        _db.FileRecords.RemoveRange(await _db.FileRecords.ToListAsync());
        _db.KnowledgeItems.RemoveRange(await _db.KnowledgeItems.ToListAsync());
        await _db.SaveChangesAsync();

        // Import
        var result = await _importSvc.ImportAsync(package);

        Assert.True(result.Success);
        var importedFile = await _db.FileRecords.FirstAsync();
        Assert.Equal("test.pdf", importedFile.FileName);
        Assert.True(importedFile.BlobMigrationPending);
        Assert.Equal("Transcript", importedFile.TranscriptionText);

        var attachment = await _db.FileAttachments.FirstAsync();
        Assert.Equal(importedFile.Id, attachment.FileRecordId);
    }

    [Fact]
    public async Task RoundTrip_Archives_Preserved()
    {
        _db.PortableArchives.Add(new PortableArchive
        {
            TenantId = TenantId,
            EntityType = "EngagementQuestionSuggestion",
            OriginalId = Guid.Parse("11111111-0000-0000-0000-000000000001"),
            JsonData = "{\"Question\":\"What do you think?\",\"Status\":\"Generated\"}"
        });
        await _db.SaveChangesAsync();

        // Export
        var package = await _exportSvc.ExportAsync();

        // Clear
        _db.PortableArchives.RemoveRange(await _db.PortableArchives.ToListAsync());
        await _db.SaveChangesAsync();

        // Import
        var result = await _importSvc.ImportAsync(package);

        Assert.True(result.Success);
        var archive = await _db.PortableArchives.FirstAsync();
        Assert.Equal("EngagementQuestionSuggestion", archive.EntityType);
        Assert.Contains("What do you think?", archive.JsonData);
    }

    [Fact]
    public async Task RoundTrip_RichPersonLinks_Preserved()
    {
        var person = new Person { Id = Guid.NewGuid(), TenantId = TenantId, Name = "P1" };
        var knowledge = new Knowledge
        {
            Id = Guid.NewGuid(), TenantId = TenantId,
            Title = "K1", Content = "C1"
        };
        _db.Persons.Add(person);
        _db.KnowledgeItems.Add(knowledge);
        _db.KnowledgePersons.Add(new KnowledgePerson
        {
            KnowledgeId = knowledge.Id, PersonId = person.Id,
            RelationshipContext = "Main subject",
            Role = "CEO",
            Mentions = 42,
            ConfidenceScore = 0.99
        });
        await _db.SaveChangesAsync();

        // Export
        var package = await _exportSvc.ExportAsync();

        // Clear
        _db.KnowledgePersons.RemoveRange(await _db.KnowledgePersons.ToListAsync());
        _db.KnowledgeItems.RemoveRange(await _db.KnowledgeItems.ToListAsync());
        _db.Persons.RemoveRange(await _db.Persons.ToListAsync());
        await _db.SaveChangesAsync();

        // Import
        var result = await _importSvc.ImportAsync(package);

        Assert.True(result.Success);
        var kp = await _db.KnowledgePersons.FirstAsync();
        Assert.Equal("Main subject", kp.RelationshipContext);
        Assert.Equal("CEO", kp.Role);
        Assert.Equal(42, kp.Mentions);
        Assert.Equal(0.99, kp.ConfidenceScore);
    }

    // ===== PortableExportPackage JSON serialization =====

    [Fact]
    public void PortableExportPackage_DefaultSchemaVersion_Is2()
    {
        var package = new PortableExportPackage();
        Assert.Equal(2, package.SchemaVersion);
    }

    [Fact]
    public void PortableExportData_NewCollections_DefaultEmpty()
    {
        var data = new PortableExportData();
        Assert.Empty(data.Comments);
        Assert.Empty(data.FileRecords);
        Assert.Empty(data.Archives);
    }

    [Fact]
    public void PortableVault_PersonIds_DefaultEmpty()
    {
        var vault = new PortableVault();
        Assert.Empty(vault.PersonIds);
    }

    [Fact]
    public void PortableKnowledge_PersonLinks_DefaultNull()
    {
        var knowledge = new PortableKnowledge();
        Assert.Null(knowledge.PersonLinks);
        Assert.Null(knowledge.LocationLinks);
        Assert.Null(knowledge.EventLinks);
    }

    [Fact]
    public void PortableEntityLink_AllFieldsSerializable()
    {
        var link = new PortableEntityLink
        {
            EntityId = Guid.NewGuid(),
            RelationshipContext = "Context",
            Role = "Role",
            Mentions = 5,
            ConfidenceScore = 0.75
        };
        var json = JsonSerializer.Serialize(link);
        var deserialized = JsonSerializer.Deserialize<PortableEntityLink>(json);

        Assert.Equal(link.EntityId, deserialized!.EntityId);
        Assert.Equal("Context", deserialized.RelationshipContext);
        Assert.Equal(5, deserialized.Mentions);
        Assert.Equal(0.75, deserialized.ConfidenceScore);
    }

    [Fact]
    public void PortableKnowledgeComment_SerializesCorrectly()
    {
        var comment = new PortableKnowledgeComment
        {
            Id = Guid.NewGuid(),
            KnowledgeId = Guid.NewGuid(),
            ParentCommentId = Guid.NewGuid(),
            AuthorName = "Author",
            Body = "Body",
            IsAnswer = true,
            Sentiment = "neutral"
        };
        var json = JsonSerializer.Serialize(comment);
        var deserialized = JsonSerializer.Deserialize<PortableKnowledgeComment>(json);

        Assert.Equal(comment.Id, deserialized!.Id);
        Assert.Equal(comment.ParentCommentId, deserialized.ParentCommentId);
        Assert.True(deserialized.IsAnswer);
    }

    [Fact]
    public void PortableFileRecord_SerializesWithAttachments()
    {
        var file = new PortableFileRecord
        {
            Id = Guid.NewGuid(),
            FileName = "test.pdf",
            Attachments = new List<PortableFileAttachmentLink>
            {
                new() { KnowledgeId = Guid.NewGuid() },
                new() { CommentId = Guid.NewGuid() }
            }
        };
        var json = JsonSerializer.Serialize(file);
        var deserialized = JsonSerializer.Deserialize<PortableFileRecord>(json);

        Assert.Equal(2, deserialized!.Attachments.Count);
    }

    // ===== Import result fields =====

    [Fact]
    public async Task ImportResult_IncludesCommentAndFileCounts()
    {
        var knowledgeId = Guid.NewGuid();
        var package = CreateEmptyPackage();
        package.Data.KnowledgeItems.Add(new PortableKnowledge
        {
            Id = knowledgeId, Title = "K1", Content = "C1",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        package.Data.Comments.Add(new PortableKnowledgeComment
        {
            Id = Guid.NewGuid(), KnowledgeId = knowledgeId,
            AuthorName = "A", Body = "B",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        package.Data.FileRecords.Add(new PortableFileRecord
        {
            Id = Guid.NewGuid(), FileName = "f.txt",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

        var result = await _importSvc.ImportAsync(package);

        Assert.True(result.Success);
        Assert.Equal(1, result.Comments.Created);
        Assert.Equal(1, result.FileRecords.Created);
    }
}
