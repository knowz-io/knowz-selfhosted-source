using System.Text.Json;
using Knowz.Core.Enums;
using Knowz.Core.Portability;
using Knowz.Core.Schema;

namespace Knowz.SelfHosted.Tests;

public class PortableExportDtoTests
{
    #region PortableExportPackage

    [Fact]
    public void PortableExportPackage_SchemaVersion_DefaultsToCoreSchemaVersion()
    {
        var package = new PortableExportPackage();
        Assert.Equal(CoreSchema.Version, package.SchemaVersion);
    }

    [Fact]
    public void PortableExportPackage_DataCollections_InitializedEmpty()
    {
        var package = new PortableExportPackage();

        Assert.NotNull(package.Data);
        Assert.NotNull(package.Metadata);
        Assert.Empty(package.Data.Vaults);
        Assert.Empty(package.Data.KnowledgeItems);
        Assert.Empty(package.Data.Topics);
        Assert.Empty(package.Data.Tags);
        Assert.Empty(package.Data.Persons);
        Assert.Empty(package.Data.Locations);
        Assert.Empty(package.Data.Events);
        Assert.Empty(package.Data.InboxItems);
    }

    [Fact]
    public void PortableExportPackage_JsonRoundTrip_PreservesAllFields()
    {
        var tenantId = Guid.NewGuid();
        var exportTime = new DateTime(2026, 2, 14, 12, 0, 0, DateTimeKind.Utc);

        var package = new PortableExportPackage
        {
            SchemaVersion = 1,
            SourceEdition = "platform",
            SourceTenantId = tenantId,
            ExportedAt = exportTime,
            Metadata = new PortableExportMetadata
            {
                TotalVaults = 5,
                TotalKnowledgeItems = 100,
                TotalTopics = 10,
                TotalTags = 20,
                TotalPersons = 15,
                TotalLocations = 8,
                TotalEvents = 3,
                TotalInboxItems = 12
            }
        };

        var json = JsonSerializer.Serialize(package);
        var deserialized = JsonSerializer.Deserialize<PortableExportPackage>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(1, deserialized.SchemaVersion);
        Assert.Equal("platform", deserialized.SourceEdition);
        Assert.Equal(tenantId, deserialized.SourceTenantId);
        Assert.Equal(exportTime, deserialized.ExportedAt);
        Assert.Equal(5, deserialized.Metadata.TotalVaults);
        Assert.Equal(100, deserialized.Metadata.TotalKnowledgeItems);
    }

    #endregion

    #region PortableVault

    [Fact]
    public void PortableVault_JsonRoundTrip_PreservesTypedFields()
    {
        var vault = new PortableVault
        {
            Id = Guid.NewGuid(),
            Name = "Test Vault",
            Description = "A test vault",
            VaultType = VaultType.Business,
            IsDefault = true,
            ParentVaultId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(vault);
        var deserialized = JsonSerializer.Deserialize<PortableVault>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(vault.Id, deserialized.Id);
        Assert.Equal(vault.Name, deserialized.Name);
        Assert.Equal(vault.Description, deserialized.Description);
        Assert.Equal(vault.VaultType, deserialized.VaultType);
        Assert.True(deserialized.IsDefault);
        Assert.Equal(vault.ParentVaultId, deserialized.ParentVaultId);
    }

    [Fact]
    public void PortableVault_ExtensionData_CapturesUnknownProperties()
    {
        var json = """
        {
            "Id": "00000000-0000-0000-0000-000000000001",
            "Name": "Test",
            "CreatedAt": "2026-01-01T00:00:00Z",
            "UpdatedAt": "2026-01-01T00:00:00Z",
            "Settings": {"theme": "dark"},
            "SensitivityLevel": 2,
            "CustomField": "extra"
        }
        """;

        var vault = JsonSerializer.Deserialize<PortableVault>(json);

        Assert.NotNull(vault);
        Assert.Equal("Test", vault.Name);
        Assert.NotNull(vault.ExtensionData);
        Assert.True(vault.ExtensionData.ContainsKey("Settings"));
        Assert.True(vault.ExtensionData.ContainsKey("SensitivityLevel"));
        Assert.True(vault.ExtensionData.ContainsKey("CustomField"));
    }

    [Fact]
    public void PortableVault_ExtensionData_RoundTrip_PreservesExtraFields()
    {
        var json = """
        {
            "Id": "00000000-0000-0000-0000-000000000001",
            "Name": "Test",
            "CreatedAt": "2026-01-01T00:00:00Z",
            "UpdatedAt": "2026-01-01T00:00:00Z",
            "PlatformSpecific": "should survive round-trip"
        }
        """;

        var vault = JsonSerializer.Deserialize<PortableVault>(json);
        var reJson = JsonSerializer.Serialize(vault);
        var vault2 = JsonSerializer.Deserialize<PortableVault>(reJson);

        Assert.NotNull(vault2);
        Assert.NotNull(vault2.ExtensionData);
        Assert.True(vault2.ExtensionData.ContainsKey("PlatformSpecific"));
        Assert.Equal("should survive round-trip", vault2.ExtensionData["PlatformSpecific"].GetString());
    }

    [Fact]
    public void PortableVault_ExtensionData_IsNull_WhenNoExtraFields()
    {
        var vault = new PortableVault
        {
            Id = Guid.NewGuid(),
            Name = "Clean",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(vault);
        var deserialized = JsonSerializer.Deserialize<PortableVault>(json);

        Assert.NotNull(deserialized);
        Assert.Null(deserialized.ExtensionData);
    }

    #endregion

    #region PortableKnowledge

    [Fact]
    public void PortableKnowledge_JsonRoundTrip_PreservesAllTypedFields()
    {
        var topicId = Guid.NewGuid();
        var vaultId1 = Guid.NewGuid();
        var vaultId2 = Guid.NewGuid();
        var personId = Guid.NewGuid();

        var knowledge = new PortableKnowledge
        {
            Id = Guid.NewGuid(),
            Title = "Test Knowledge",
            Content = "Test content",
            Summary = "A summary",
            Type = KnowledgeType.Document,
            Source = "https://example.com",
            FilePath = "/docs/test.pdf",
            IsIndexed = true,
            IndexedAt = DateTime.UtcNow,
            TopicId = topicId,
            VaultIds = new List<Guid> { vaultId1, vaultId2 },
            PrimaryVaultId = vaultId1,
            TagIds = new List<Guid> { Guid.NewGuid() },
            PersonIds = new List<Guid> { personId },
            LocationIds = new List<Guid> { Guid.NewGuid() },
            EventIds = new List<Guid> { Guid.NewGuid() },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(knowledge);
        var deserialized = JsonSerializer.Deserialize<PortableKnowledge>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(knowledge.Id, deserialized.Id);
        Assert.Equal("Test Knowledge", deserialized.Title);
        Assert.Equal("Test content", deserialized.Content);
        Assert.Equal("A summary", deserialized.Summary);
        Assert.Equal(KnowledgeType.Document, deserialized.Type);
        Assert.Equal("https://example.com", deserialized.Source);
        Assert.Equal("/docs/test.pdf", deserialized.FilePath);
        Assert.True(deserialized.IsIndexed);
        Assert.NotNull(deserialized.IndexedAt);
        Assert.Equal(topicId, deserialized.TopicId);
        Assert.Equal(2, deserialized.VaultIds.Count);
        Assert.Equal(vaultId1, deserialized.PrimaryVaultId);
        Assert.Single(deserialized.PersonIds);
        Assert.Null(deserialized.BriefSummary);
    }

    [Fact]
    public void PortableKnowledge_JsonRoundTrip_PreservesBriefSummary()
    {
        var knowledge = new PortableKnowledge
        {
            Id = Guid.NewGuid(),
            Title = "Test Knowledge",
            Content = "Test content",
            BriefSummary = "Two or three sentences of retrieval context.",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(knowledge);
        var deserialized = JsonSerializer.Deserialize<PortableKnowledge>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("Two or three sentences of retrieval context.", deserialized.BriefSummary);
        if (deserialized.ExtensionData != null)
        {
            Assert.DoesNotContain("BriefSummary", deserialized.ExtensionData.Keys, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void PortableKnowledge_MissingBriefSummary_DeserializesAsNull()
    {
        var json = """
        {
            "Id": "00000000-0000-0000-0000-000000000001",
            "Title": "Legacy",
            "Content": "Content",
            "Type": 0,
            "CreatedAt": "2026-01-01T00:00:00Z",
            "UpdatedAt": "2026-01-01T00:00:00Z"
        }
        """;

        var knowledge = JsonSerializer.Deserialize<PortableKnowledge>(json);

        Assert.NotNull(knowledge);
        Assert.Null(knowledge.BriefSummary);
    }

    [Fact]
    public void PortableKnowledge_ExtensionData_CapturesPlatformFields()
    {
        var json = """
        {
            "Id": "00000000-0000-0000-0000-000000000001",
            "Title": "Test",
            "Content": "Content",
            "Type": 0,
            "CreatedAt": "2026-01-01T00:00:00Z",
            "UpdatedAt": "2026-01-01T00:00:00Z",
            "ContentHash": "abc123",
            "SensitivityLevel": 3,
            "AiProcessingStatus": "Completed",
            "EnrichmentVersion": 2
        }
        """;

        var knowledge = JsonSerializer.Deserialize<PortableKnowledge>(json);

        Assert.NotNull(knowledge);
        Assert.NotNull(knowledge.ExtensionData);
        Assert.True(knowledge.ExtensionData.ContainsKey("ContentHash"));
        Assert.True(knowledge.ExtensionData.ContainsKey("SensitivityLevel"));
        Assert.True(knowledge.ExtensionData.ContainsKey("AiProcessingStatus"));
        Assert.True(knowledge.ExtensionData.ContainsKey("EnrichmentVersion"));
    }

    [Fact]
    public void PortableKnowledge_RelationshipLists_DefaultToEmpty()
    {
        var knowledge = new PortableKnowledge();

        Assert.NotNull(knowledge.VaultIds);
        Assert.Empty(knowledge.VaultIds);
        Assert.NotNull(knowledge.TagIds);
        Assert.Empty(knowledge.TagIds);
        Assert.NotNull(knowledge.PersonIds);
        Assert.Empty(knowledge.PersonIds);
        Assert.NotNull(knowledge.LocationIds);
        Assert.Empty(knowledge.LocationIds);
        Assert.NotNull(knowledge.EventIds);
        Assert.Empty(knowledge.EventIds);
    }

    #endregion

    #region PortableTopic

    [Fact]
    public void PortableTopic_JsonRoundTrip()
    {
        var topic = new PortableTopic
        {
            Id = Guid.NewGuid(),
            Name = "AI Research",
            Description = "Research on AI topics",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(topic);
        var deserialized = JsonSerializer.Deserialize<PortableTopic>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(topic.Id, deserialized.Id);
        Assert.Equal("AI Research", deserialized.Name);
        Assert.Equal("Research on AI topics", deserialized.Description);
    }

    [Fact]
    public void PortableTopic_ExtensionData_CapturesUnknownFields()
    {
        var json = """
        {
            "Id": "00000000-0000-0000-0000-000000000001",
            "Name": "Test",
            "CreatedAt": "2026-01-01T00:00:00Z",
            "UpdatedAt": "2026-01-01T00:00:00Z",
            "PlatformOnly": true
        }
        """;

        var topic = JsonSerializer.Deserialize<PortableTopic>(json);
        Assert.NotNull(topic);
        Assert.NotNull(topic.ExtensionData);
        Assert.True(topic.ExtensionData.ContainsKey("PlatformOnly"));
    }

    #endregion

    #region PortableTag

    [Fact]
    public void PortableTag_JsonRoundTrip()
    {
        var tag = new PortableTag
        {
            Id = Guid.NewGuid(),
            Name = "important",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(tag);
        var deserialized = JsonSerializer.Deserialize<PortableTag>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(tag.Id, deserialized.Id);
        Assert.Equal("important", deserialized.Name);
    }

    #endregion

    #region PortablePerson

    [Fact]
    public void PortablePerson_JsonRoundTrip()
    {
        var person = new PortablePerson
        {
            Id = Guid.NewGuid(),
            Name = "John Doe",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(person);
        var deserialized = JsonSerializer.Deserialize<PortablePerson>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("John Doe", deserialized.Name);
    }

    [Fact]
    public void PortablePerson_ExtensionData_CapturesPlatformFields()
    {
        var json = """
        {
            "Id": "00000000-0000-0000-0000-000000000001",
            "Name": "Jane",
            "CreatedAt": "2026-01-01T00:00:00Z",
            "UpdatedAt": "2026-01-01T00:00:00Z",
            "Email": "jane@example.com",
            "Phone": "+1234567890",
            "Biography": "A bio",
            "LinkedInUrl": "https://linkedin.com/in/jane"
        }
        """;

        var person = JsonSerializer.Deserialize<PortablePerson>(json);
        Assert.NotNull(person);
        Assert.NotNull(person.ExtensionData);
        Assert.True(person.ExtensionData.ContainsKey("Email"));
        Assert.True(person.ExtensionData.ContainsKey("Phone"));
        Assert.True(person.ExtensionData.ContainsKey("Biography"));
        Assert.True(person.ExtensionData.ContainsKey("LinkedInUrl"));
    }

    #endregion

    #region PortableLocation

    [Fact]
    public void PortableLocation_JsonRoundTrip()
    {
        var location = new PortableLocation
        {
            Id = Guid.NewGuid(),
            Name = "London",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(location);
        var deserialized = JsonSerializer.Deserialize<PortableLocation>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("London", deserialized.Name);
    }

    [Fact]
    public void PortableLocation_ExtensionData_CapturesGeoFields()
    {
        var json = """
        {
            "Id": "00000000-0000-0000-0000-000000000001",
            "Name": "London",
            "CreatedAt": "2026-01-01T00:00:00Z",
            "UpdatedAt": "2026-01-01T00:00:00Z",
            "Latitude": 51.5074,
            "Longitude": -0.1278,
            "Country": "UK"
        }
        """;

        var location = JsonSerializer.Deserialize<PortableLocation>(json);
        Assert.NotNull(location);
        Assert.NotNull(location.ExtensionData);
        Assert.True(location.ExtensionData.ContainsKey("Latitude"));
        Assert.True(location.ExtensionData.ContainsKey("Longitude"));
        Assert.True(location.ExtensionData.ContainsKey("Country"));
    }

    #endregion

    #region PortableEvent

    [Fact]
    public void PortableEvent_JsonRoundTrip()
    {
        var evt = new PortableEvent
        {
            Id = Guid.NewGuid(),
            Name = "Conference 2026",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(evt);
        var deserialized = JsonSerializer.Deserialize<PortableEvent>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("Conference 2026", deserialized.Name);
    }

    [Fact]
    public void PortableEvent_ExtensionData_CapturesDateFields()
    {
        var json = """
        {
            "Id": "00000000-0000-0000-0000-000000000001",
            "Name": "Conference",
            "CreatedAt": "2026-01-01T00:00:00Z",
            "UpdatedAt": "2026-01-01T00:00:00Z",
            "StartDate": "2026-06-01T09:00:00Z",
            "EndDate": "2026-06-03T17:00:00Z",
            "LocationId": "00000000-0000-0000-0000-000000000002"
        }
        """;

        var evt = JsonSerializer.Deserialize<PortableEvent>(json);
        Assert.NotNull(evt);
        Assert.NotNull(evt.ExtensionData);
        Assert.True(evt.ExtensionData.ContainsKey("StartDate"));
        Assert.True(evt.ExtensionData.ContainsKey("EndDate"));
        Assert.True(evt.ExtensionData.ContainsKey("LocationId"));
    }

    #endregion

    #region PortableInboxItem

    [Fact]
    public void PortableInboxItem_JsonRoundTrip()
    {
        var item = new PortableInboxItem
        {
            Id = Guid.NewGuid(),
            Body = "Check this out",
            Type = InboxItemType.Link,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(item);
        var deserialized = JsonSerializer.Deserialize<PortableInboxItem>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("Check this out", deserialized.Body);
        Assert.Equal(InboxItemType.Link, deserialized.Type);
    }

    [Fact]
    public void PortableInboxItem_ExtensionData_CapturesExtraFields()
    {
        var json = """
        {
            "Id": "00000000-0000-0000-0000-000000000001",
            "Body": "Test",
            "Type": 0,
            "CreatedAt": "2026-01-01T00:00:00Z",
            "UpdatedAt": "2026-01-01T00:00:00Z",
            "Priority": "High",
            "AssignedTo": "user@example.com"
        }
        """;

        var item = JsonSerializer.Deserialize<PortableInboxItem>(json);
        Assert.NotNull(item);
        Assert.NotNull(item.ExtensionData);
        Assert.True(item.ExtensionData.ContainsKey("Priority"));
        Assert.True(item.ExtensionData.ContainsKey("AssignedTo"));
    }

    #endregion

    #region All IDs are Guid

    [Fact]
    public void AllPortableDTOs_UseGuidIds()
    {
        // Verify all portable DTO Id properties are Guid type
        Assert.IsType<Guid>(new PortableVault().Id);
        Assert.IsType<Guid>(new PortableKnowledge().Id);
        Assert.IsType<Guid>(new PortableTopic().Id);
        Assert.IsType<Guid>(new PortableTag().Id);
        Assert.IsType<Guid>(new PortablePerson().Id);
        Assert.IsType<Guid>(new PortableLocation().Id);
        Assert.IsType<Guid>(new PortableEvent().Id);
        Assert.IsType<Guid>(new PortableInboxItem().Id);
    }

    #endregion

    #region Enum Types

    [Fact]
    public void PortableKnowledge_UsesKnowzCoreEnumType()
    {
        var knowledge = new PortableKnowledge { Type = KnowledgeType.Document };
        Assert.Equal(KnowledgeType.Document, knowledge.Type);
    }

    [Fact]
    public void PortableVault_UsesKnowzCoreEnumType()
    {
        var vault = new PortableVault { VaultType = VaultType.Business };
        Assert.Equal(VaultType.Business, vault.VaultType);
    }

    [Fact]
    public void PortableInboxItem_UsesKnowzCoreEnumType()
    {
        var item = new PortableInboxItem { Type = InboxItemType.File };
        Assert.Equal(InboxItemType.File, item.Type);
    }

    #endregion

    #region No Virtual Properties

    [Fact]
    public void PortableDTOs_HaveNoVirtualProperties()
    {
        // Verify no virtual properties exist on portable DTOs (no EF coupling)
        var types = new[]
        {
            typeof(PortableVault),
            typeof(PortableKnowledge),
            typeof(PortableTopic),
            typeof(PortableTag),
            typeof(PortablePerson),
            typeof(PortableLocation),
            typeof(PortableEvent),
            typeof(PortableInboxItem)
        };

        foreach (var type in types)
        {
            var virtualProps = type.GetProperties()
                .Where(p => p.GetGetMethod()?.IsVirtual == true && !p.GetGetMethod()!.IsFinal)
                .ToList();

            Assert.Empty(virtualProps);
        }
    }

    #endregion
}
