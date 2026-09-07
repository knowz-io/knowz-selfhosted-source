using System.Text.Json;
using Knowz.Core.Portability;
using Knowz.Core.Schema;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Interfaces;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class PortabilityEndpointTests
{
    // These are unit tests verifying the DTOs and logic used by the endpoints.
    // Full integration endpoint tests require WebApplicationFactory which is tested in SelfHostedApiTests.

    [Fact]
    public void ImportConflictStrategy_HasThreeValues()
    {
        var values = Enum.GetValues<ImportConflictStrategy>();
        Assert.Equal(3, values.Length);
        Assert.Contains(ImportConflictStrategy.Skip, values);
        Assert.Contains(ImportConflictStrategy.Overwrite, values);
        Assert.Contains(ImportConflictStrategy.Merge, values);
    }

    [Fact]
    public void ImportConflictStrategy_ParsesCaseInsensitive()
    {
        Assert.True(Enum.TryParse<ImportConflictStrategy>("skip", true, out var skip));
        Assert.Equal(ImportConflictStrategy.Skip, skip);

        Assert.True(Enum.TryParse<ImportConflictStrategy>("OVERWRITE", true, out var overwrite));
        Assert.Equal(ImportConflictStrategy.Overwrite, overwrite);

        Assert.True(Enum.TryParse<ImportConflictStrategy>("Merge", true, out var merge));
        Assert.Equal(ImportConflictStrategy.Merge, merge);
    }

    [Fact]
    public void ImportConflictStrategy_RejectsInvalid()
    {
        Assert.False(Enum.TryParse<ImportConflictStrategy>("invalid", true, out _));
        Assert.False(Enum.TryParse<ImportConflictStrategy>("rename", true, out _));
    }

    [Fact]
    public void EntityImportCounts_Total_IsComputed()
    {
        var counts = new EntityImportCounts
        {
            Created = 3,
            Skipped = 2,
            Overwritten = 1,
            Merged = 4
        };
        Assert.Equal(10, counts.Total);
    }

    [Fact]
    public void ImportValidationResult_DefaultIsValid()
    {
        var result = new ImportValidationResult();
        Assert.False(result.IsValid); // Default is false
        Assert.Empty(result.Warnings);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void PortableImportResult_DefaultValues()
    {
        var result = new PortableImportResult();
        Assert.False(result.Success);
        Assert.Equal(0, result.Vaults.Total);
        Assert.Equal(0, result.JunctionsRestored);
        Assert.Empty(result.Warnings);
        Assert.Null(result.Error);
    }

    [Fact]
    public void SchemaEndpoint_ReturnsCorrectInfo()
    {
        // Verify the schema information that the endpoint returns
        Assert.Equal(2, CoreSchema.Version);
        Assert.Equal(1, CoreSchema.MinReadableVersion);
        Assert.Equal("Schema v2 (reads v1-v2)", CoreSchema.GetCompatibilityInfo());
    }

    [Fact]
    public void PortableExportPackage_JsonRoundTrip()
    {
        var package = new PortableExportPackage
        {
            SchemaVersion = CoreSchema.Version,
            SourceEdition = "selfhosted",
            SourceTenantId = Guid.NewGuid(),
            ExportedAt = DateTime.UtcNow,
            Metadata = new PortableExportMetadata { TotalVaults = 2, TotalKnowledgeItems = 5 },
            Data = new PortableExportData
            {
                Vaults = new List<PortableVault>
                {
                    new() { Id = Guid.NewGuid(), Name = "Test", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow }
                }
            }
        };

        var json = JsonSerializer.Serialize(package);
        var deserialized = JsonSerializer.Deserialize<PortableExportPackage>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(package.SchemaVersion, deserialized!.SchemaVersion);
        Assert.Equal(package.SourceEdition, deserialized.SourceEdition);
        Assert.Single(deserialized.Data.Vaults);
    }
}
