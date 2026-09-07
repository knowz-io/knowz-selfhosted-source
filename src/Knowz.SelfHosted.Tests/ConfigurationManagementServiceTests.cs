using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class ConfigurationManagementServiceTests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private readonly IDataProtectionProvider _dataProtectionProvider;
    private readonly IDataProtector _dataProtector;
    private readonly DatabaseConfigurationProvider? _configProvider;
    private readonly ConfigurationManagementService _service;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public ConfigurationManagementServiceTests()
    {
        var dbOptions = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(dbOptions, tenantProvider);

        _dataProtectionProvider = new EphemeralDataProtectionProvider();
        _dataProtector = _dataProtectionProvider.CreateProtector("Knowz.SelfHosted.SystemConfiguration");

        _configProvider = Substitute.For<DatabaseConfigurationProvider>(
            new DatabaseConfigurationSource { ConnectionString = "", DataProtectionProvider = _dataProtectionProvider });

        var logger = Substitute.For<ILogger<ConfigurationManagementService>>();

        // Empty IConfiguration for default service (no Key Vault, no config values)
        var emptyConfig = new ConfigurationBuilder().Build();

        _service = new ConfigurationManagementService(
            _db, _dataProtectionProvider, _configProvider, logger, emptyConfig);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    // --- GetAllCategoriesAsync ---

    [Fact]
    public async Task GetAllCategories_ReturnsAllCategoriesIncludingAttachmentAICategories()
    {
        var result = await _service.GetAllCategoriesAsync();

        Assert.Equal(12, result.Count);
        var categoryNames = result.Select(c => c.Category).ToList();
        Assert.Contains("ConnectionStrings", categoryNames);
        Assert.Contains("AzureOpenAI", categoryNames);
        Assert.Contains("AzureAIVision", categoryNames);
        Assert.Contains("AzureDocumentIntelligence", categoryNames);
        Assert.Contains("AzureAISearch", categoryNames);
        Assert.Contains("Storage", categoryNames);
        Assert.Contains("SelfHosted", categoryNames);
        Assert.Contains("AzureKeyVault", categoryNames);
        Assert.Contains("KnowzPlatform", categoryNames);
        Assert.Contains("Inbox", categoryNames);
        Assert.Contains("SSO", categoryNames);
    }

    [Fact]
    public async Task GetAllCategories_DoesNotPresentIgnoredDatabaseSecretAsConfigured()
    {
        _db.SystemConfigurations.Add(new SystemConfiguration { Category = "AzureOpenAI", Key = "ApiKey",
            IsSecret = true, EncryptedValue = _dataProtector.Protect("stale-db-secret") });
        await _db.SaveChangesAsync();
        var entry = (await _service.GetCategoryAsync("AzureOpenAI"))!.Entries.Single(e => e.Key == "ApiKey");
        Assert.Null(entry.Value);
        Assert.False(entry.IsSet);
        Assert.False(entry.Editable);
    }

    [Fact]
    public async Task GetAllCategories_ShowsPlaintextForNonSecrets()
    {
        _db.SystemConfigurations.Add(new SystemConfiguration
        {
            Category = "AzureOpenAI",
            Key = "Endpoint",
            EncryptedValue = _dataProtector.Protect("https://myopenai.openai.azure.com"),
            IsSecret = false
        });
        await _db.SaveChangesAsync();

        var result = await _service.GetAllCategoriesAsync();

        var openAi = result.First(c => c.Category == "AzureOpenAI");
        var endpointEntry = openAi.Entries.First(e => e.Key == "Endpoint");
        Assert.Equal("https://myopenai.openai.azure.com", endpointEntry.Value);
    }

    // --- GetCategoryAsync ---

    [Fact]
    public async Task GetCategory_ReturnsCategory_WhenExists()
    {
        var result = await _service.GetCategoryAsync("AzureOpenAI");

        Assert.NotNull(result);
        Assert.Equal("AzureOpenAI", result!.Category);
        Assert.Equal("Azure OpenAI", result.DisplayName);
        Assert.Equal(4, result.Entries.Count);
    }

    [Fact]
    public async Task GetCategory_ReturnsNull_WhenNotExists()
    {
        var result = await _service.GetCategoryAsync("NonExistent");

        Assert.Null(result);
    }

    // --- UpdateCategoryAsync ---

    [Fact]
    public async Task UpdateCategory_EncryptsAndSavesToDb()
    {
        var entries = new List<ConfigEntryUpdateDto>
        {
            new() { Key = "Endpoint", Value = "https://test.openai.azure.com" }
        };

        var result = await _service.UpdateCategoryAsync("AzureOpenAI", entries, "admin");

        Assert.True(result.Success);
        Assert.Equal(1, result.EntriesUpdated);

        var dbEntry = await _db.SystemConfigurations
            .FirstOrDefaultAsync(sc => sc.Category == "AzureOpenAI" && sc.Key == "Endpoint");
        Assert.NotNull(dbEntry);
        // Value should be encrypted - not plaintext
        Assert.NotEqual("https://test.openai.azure.com", dbEntry!.EncryptedValue);
    }

    [Fact]
    public async Task UpdateCategory_DecryptsOnRead()
    {
        // First save an encrypted value
        var entries = new List<ConfigEntryUpdateDto>
        {
            new() { Key = "Endpoint", Value = "https://test.openai.azure.com" }
        };
        await _service.UpdateCategoryAsync("AzureOpenAI", entries, "admin");

        // Now read it back
        var category = await _service.GetCategoryAsync("AzureOpenAI");
        var entry = category!.Entries.First(e => e.Key == "Endpoint");

        Assert.Equal("https://test.openai.azure.com", entry.Value);
        Assert.True(entry.IsSet);
    }

    [Fact]
    public async Task UpdateCategory_EncryptionRoundTrip()
    {
        var originalValue = "https://saved-setting.example";

        var entries = new List<ConfigEntryUpdateDto>
        {
            new() { Key = "Endpoint", Value = originalValue }
        };
        await _service.UpdateCategoryAsync("AzureOpenAI", entries, "admin");

        // Read back from DB and decrypt
        var dbEntry = await _db.SystemConfigurations
            .FirstAsync(sc => sc.Category == "AzureOpenAI" && sc.Key == "Endpoint");
        var decrypted = _dataProtector.Unprotect(dbEntry.EncryptedValue!);

        Assert.Equal(originalValue, decrypted);
    }

    [Fact]
    public async Task UpdateCategory_PreservesExistingValue_WhenSentinel()
    {
        // First set a value
        _db.SystemConfigurations.Add(new SystemConfiguration
        {
            Category = "AzureOpenAI",
            Key = "ApiKey",
            EncryptedValue = _dataProtector.Protect("original-key"),
            IsSecret = true
        });
        await _db.SaveChangesAsync();

        var originalEncrypted = (await _db.SystemConfigurations
            .FirstAsync(sc => sc.Key == "ApiKey")).EncryptedValue;

        // Update with sentinel
        var entries = new List<ConfigEntryUpdateDto>
        {
            new() { Key = "ApiKey", Value = "****" }
        };
        await _service.UpdateCategoryAsync("AzureOpenAI", entries, "admin");

        // Value should remain unchanged
        var dbEntry = await _db.SystemConfigurations
            .FirstAsync(sc => sc.Category == "AzureOpenAI" && sc.Key == "ApiKey");
        Assert.Equal(originalEncrypted, dbEntry.EncryptedValue);
    }

    [Fact]
    public async Task UpdateCategory_RejectsUnknownKeys()
    {
        var entries = new List<ConfigEntryUpdateDto>
        {
            new() { Key = "UnknownKey", Value = "value" }
        };

        var result = await _service.UpdateCategoryAsync("AzureOpenAI", entries, "admin");

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("UnknownKey"));
    }

    [Fact]
    public async Task UpdateCategory_RejectsUnknownCategory()
    {
        var entries = new List<ConfigEntryUpdateDto>
        {
            new() { Key = "Key", Value = "value" }
        };

        var result = await _service.UpdateCategoryAsync("NonExistent", entries, "admin");

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("NonExistent"));
    }

    [Fact]
    public async Task UpdateCategory_SetsRestartRequired_WhenConnectionKeyChanged()
    {
        var entries = new List<ConfigEntryUpdateDto>
        {
            new() { Key = "Endpoint", Value = "https://new.openai.azure.com" }
        };

        var result = await _service.UpdateCategoryAsync("AzureOpenAI", entries, "admin");

        Assert.True(result.Success);
        Assert.True(result.RestartRequired);
    }

    [Fact]
    public async Task UpdateCategory_RequiresRestart_ForFixedOptions()
    {
        var entries = new List<ConfigEntryUpdateDto>
        {
            new() { Key = "JwtExpirationMinutes", Value = "120" }
        };

        var result = await _service.UpdateCategoryAsync("SelfHosted", entries, "admin");

        Assert.True(result.Success);
        Assert.True(result.RestartRequired);
    }

    [Fact]
    public async Task UpdateCategory_DoesNotPartiallyReloadFixedProviderSettings()
    {
        var entries = new List<ConfigEntryUpdateDto>
        {
            new() { Key = "ServerName", Value = "test-server" }
        };

        await _service.UpdateCategoryAsync("SelfHosted", entries, "admin");

        _configProvider!.DidNotReceive().Reload();
        Assert.True(_service.GetDeploymentStatus().RestartRequired);
    }

    [Fact]
    public async Task UpdateCategory_RecordsLastModifiedAtAndBy()
    {
        var entries = new List<ConfigEntryUpdateDto>
        {
            new() { Key = "Endpoint", Value = "https://test.openai.azure.com" }
        };

        var before = DateTime.UtcNow;
        await _service.UpdateCategoryAsync("AzureOpenAI", entries, "admin-user");

        var dbEntry = await _db.SystemConfigurations
            .FirstAsync(sc => sc.Category == "AzureOpenAI" && sc.Key == "Endpoint");

        Assert.Equal("admin-user", dbEntry.LastModifiedBy);
        Assert.True(dbEntry.LastModifiedAt >= before);
        Assert.True(dbEntry.LastModifiedAt <= DateTime.UtcNow);
    }

    [Fact]
    public async Task UpdateCategory_HandlesNullProvider_Gracefully()
    {
        var logger = Substitute.For<ILogger<ConfigurationManagementService>>();
        var emptyConfig = new ConfigurationBuilder().Build();
        var serviceWithNullProvider = new ConfigurationManagementService(
            _db, _dataProtectionProvider, null, logger, emptyConfig);

        var entries = new List<ConfigEntryUpdateDto>
        {
            new() { Key = "ServerName", Value = "test" }
        };

        var result = await serviceWithNullProvider.UpdateCategoryAsync("SelfHosted", entries, "admin");

        Assert.True(result.Success);
    }

    // --- MaskValue ---

    [Fact]
    public void MaskValue_ShowsLastFourChars_ForLongValues()
    {
        var result = ConfigurationManagementService.MaskValue("sk-abc123xyz", true);

        Assert.Equal("****3xyz", result);
    }

    [Fact]
    public void MaskValue_ShowsAllStars_ForShortValues()
    {
        var result = ConfigurationManagementService.MaskValue("abc", true);

        Assert.Equal("****", result);
    }

    [Fact]
    public void MaskValue_ShowsAllStars_ForExactlyFourChars()
    {
        var result = ConfigurationManagementService.MaskValue("abcd", true);

        Assert.Equal("****", result);
    }

    [Fact]
    public void MaskValue_ReturnsNull_ForNullValues()
    {
        var result = ConfigurationManagementService.MaskValue(null, true);

        Assert.Null(result);
    }

    [Fact]
    public void MaskValue_ReturnsNull_ForEmptyValues()
    {
        var result = ConfigurationManagementService.MaskValue("", true);

        Assert.Null(result);
    }

    [Fact]
    public void MaskValue_ReturnsPlaintext_WhenNotSecret()
    {
        var result = ConfigurationManagementService.MaskValue("plaintext-value", false);

        Assert.Equal("plaintext-value", result);
    }

    // --- IsKeepExistingSentinel ---

    [Fact]
    public void IsKeepExistingSentinel_ReturnsTrue_ForAllStars()
    {
        Assert.True(ConfigurationManagementService.IsKeepExistingSentinel("****"));
        Assert.True(ConfigurationManagementService.IsKeepExistingSentinel("********"));
        Assert.True(ConfigurationManagementService.IsKeepExistingSentinel("*"));
    }

    [Fact]
    public void IsKeepExistingSentinel_ReturnsFalse_ForMixedContent()
    {
        Assert.False(ConfigurationManagementService.IsKeepExistingSentinel("****abc"));
        Assert.False(ConfigurationManagementService.IsKeepExistingSentinel("abc"));
        Assert.False(ConfigurationManagementService.IsKeepExistingSentinel(""));
        Assert.False(ConfigurationManagementService.IsKeepExistingSentinel(null));
    }

    // --- SeedFromConfigurationAsync ---

    [Fact]
    public async Task SeedFromConfiguration_DoesNotPinStartupDefaults()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["AzureOpenAI:Endpoint"] = "https://startup.example" }).Build();
        await _service.SeedFromConfigurationAsync(config);
        Assert.Empty(await _db.SystemConfigurations.ToListAsync());
    }

    [Fact]
    public async Task SeedFromConfiguration_SkipsWhenTableHasEntries()
    {
        // Add an existing entry
        _db.SystemConfigurations.Add(new SystemConfiguration
        {
            Category = "AzureOpenAI",
            Key = "Endpoint",
            EncryptedValue = _dataProtector.Protect("existing")
        });
        await _db.SaveChangesAsync();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureOpenAI:Endpoint"] = "https://new.openai.azure.com"
            })
            .Build();

        await _service.SeedFromConfigurationAsync(config);

        // Should still be 1 entry (not seeded)
        var count = await _db.SystemConfigurations.CountAsync();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task SeedFromConfiguration_DoesNotCopyManagedSecrets()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["AzureOpenAI:ApiKey"] = "managed-secret" }).Build();
        await _service.SeedFromConfigurationAsync(config);
        Assert.Empty(await _db.SystemConfigurations.ToListAsync());
    }

    // --- TestConnectionAsync ---

    [Fact]
    public async Task TestConnection_ReturnsNotConfigured_WhenKeysEmpty()
    {
        var result = await _service.TestConnectionAsync("ConnectionStrings");

        Assert.False(result.IsHealthy);
        Assert.Equal("Not Configured", result.Status);
    }

    [Fact]
    public async Task TestConnection_ReturnsUnknownCategory_WhenInvalid()
    {
        var result = await _service.TestConnectionAsync("FakeCategory");

        Assert.False(result.IsHealthy);
        Assert.Equal("Unknown category", result.Status);
    }

    // --- GetDeploymentStatus ---

    [Fact]
    public void GetDeploymentStatus_ReturnsCorrectMode()
    {
        var status = _service.GetDeploymentStatus();

        Assert.Equal("Direct", status.Mode);
        Assert.NotEqual(default, status.StartupTime);
    }

    // --- AzureKeyVault Category ---

    [Fact]
    public async Task GetAllCategories_IncludesAzureKeyVaultCategory()
    {
        var result = await _service.GetAllCategoriesAsync();

        var categoryNames = result.Select(c => c.Category).ToList();
        Assert.Contains("AzureKeyVault", categoryNames);
    }

    [Fact]
    public async Task GetAllCategories_ReturnsCategoryCountIncludingAttachmentAICategories()
    {
        var result = await _service.GetAllCategoriesAsync();

        Assert.Equal(12, result.Count);
    }

    [Fact]
    public async Task TestConnection_AzureAIVision_DoesNotProbeUnappliedDatabaseSettings()
    {
        _db.SystemConfigurations.AddRange(
            new SystemConfiguration
            {
                Category = "AzureAIVision",
                Key = "Endpoint",
                EncryptedValue = _dataProtector.Protect("https://vision.cognitiveservices.azure.com"),
                IsSecret = false,
                RequiresRestart = true
            },
            new SystemConfiguration
            {
                Category = "AzureAIVision",
                Key = "ApiKey",
                EncryptedValue = _dataProtector.Protect("vision-key"),
                IsSecret = true,
                RequiresRestart = true
            });
        await _db.SaveChangesAsync();

        var result = await _service.TestConnectionAsync("AzureAIVision");

        Assert.False(result.IsHealthy);
        Assert.Equal("Not Configured", result.Status);
    }

    [Fact]
    public async Task TestConnection_AzureDocumentIntelligence_DoesNotProbeUnappliedDatabaseSettings()
    {
        _db.SystemConfigurations.AddRange(
            new SystemConfiguration
            {
                Category = "AzureDocumentIntelligence",
                Key = "Endpoint",
                EncryptedValue = _dataProtector.Protect("https://docint.cognitiveservices.azure.com"),
                IsSecret = false,
                RequiresRestart = true
            },
            new SystemConfiguration
            {
                Category = "AzureDocumentIntelligence",
                Key = "ApiKey",
                EncryptedValue = _dataProtector.Protect("docint-key"),
                IsSecret = true,
                RequiresRestart = true
            });
        await _db.SaveChangesAsync();

        var result = await _service.TestConnectionAsync("AzureDocumentIntelligence");

        Assert.False(result.IsHealthy);
        Assert.Equal("Not Configured", result.Status);
    }

    [Fact]
    public async Task GetCategory_AzureKeyVault_HasVaultUriAndEnabledKeys()
    {
        var result = await _service.GetCategoryAsync("AzureKeyVault");

        Assert.NotNull(result);
        Assert.Equal("Azure Key Vault", result!.DisplayName);
        var keys = result.Entries.Select(e => e.Key).ToList();
        Assert.Contains("VaultUri", keys);
        Assert.Contains("Enabled", keys);
        Assert.Equal(2, result.Entries.Count);
    }

    [Fact]
    public async Task GetCategory_AzureKeyVault_KeysRequireRestart()
    {
        var result = await _service.GetCategoryAsync("AzureKeyVault");

        Assert.NotNull(result);
        Assert.True(result!.RequiresRestart);
        foreach (var entry in result.Entries)
        {
            Assert.True(entry.RequiresRestart);
        }
    }

    [Fact]
    public async Task GetCategory_AzureKeyVault_KeysAreNotSecret()
    {
        var result = await _service.GetCategoryAsync("AzureKeyVault");

        Assert.NotNull(result);
        foreach (var entry in result.Entries)
        {
            Assert.False(entry.IsSecret);
        }
    }

    // --- Source Detection ---

    [Fact]
    public async Task GetAllCategories_EntriesHaveSourceProperty()
    {
        // Seed a DB value
        _db.SystemConfigurations.Add(new SystemConfiguration
        {
            Category = "AzureOpenAI",
            Key = "Endpoint",
            EncryptedValue = _dataProtector.Protect("https://test.openai.azure.com"),
            IsSecret = false
        });
        await _db.SaveChangesAsync();

        var result = await _service.GetAllCategoriesAsync();

        var openAi = result.First(c => c.Category == "AzureOpenAI");
        var endpointEntry = openAi.Entries.First(e => e.Key == "Endpoint");
        Assert.Equal("database", endpointEntry.Source);
    }

    [Fact]
    public async Task GetCategory_Source_IsDatabase_WhenDbHasValue()
    {
        _db.SystemConfigurations.Add(new SystemConfiguration
        {
            Category = "AzureOpenAI",
            Key = "Endpoint",
            EncryptedValue = _dataProtector.Protect("https://test.openai.azure.com"),
            IsSecret = false
        });
        await _db.SaveChangesAsync();

        var result = await _service.GetCategoryAsync("AzureOpenAI");

        var entry = result!.Entries.First(e => e.Key == "Endpoint");
        Assert.Equal("database", entry.Source);
    }

    [Fact]
    public async Task GetCategory_Source_IsNull_WhenNoValueAnywhere()
    {
        // No DB entries, no IConfiguration values
        var result = await _service.GetCategoryAsync("AzureOpenAI");

        var entry = result!.Entries.First(e => e.Key == "Endpoint");
        Assert.Null(entry.Source);
    }

    [Fact]
    public async Task GetCategory_Source_IdentifiesGenericProviderWithoutInventingEnvironment()
    {
        // Create service with IConfiguration that has a value
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureOpenAI:Endpoint"] = "https://from-config.openai.azure.com"
            })
            .Build();

        var logger = Substitute.For<ILogger<ConfigurationManagementService>>();
        var serviceWithConfig = new ConfigurationManagementService(
            _db, _dataProtectionProvider, _configProvider, logger, config);

        var result = await serviceWithConfig.GetCategoryAsync("AzureOpenAI");

        var entry = result!.Entries.First(e => e.Key == "Endpoint");
        // In-memory fixture is a generic source, not an environment provider.
        Assert.Equal("configuration", entry.Source);
    }

    [Fact]
    public async Task GetCategory_Source_DoesNotInferKeyVaultFromUnrelatedFlag()
    {
        // Create service with IConfiguration that has KV enabled and a value
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureKeyVault:Enabled"] = "true",
                ["AzureKeyVault:VaultUri"] = "https://my-vault.vault.azure.net/",
                ["AzureOpenAI:Endpoint"] = "https://from-kv.openai.azure.com"
            })
            .Build();

        var logger = Substitute.For<ILogger<ConfigurationManagementService>>();
        var serviceWithConfig = new ConfigurationManagementService(
            _db, _dataProtectionProvider, _configProvider, logger, config);

        var result = await serviceWithConfig.GetCategoryAsync("AzureOpenAI");

        var entry = result!.Entries.First(e => e.Key == "Endpoint");
        Assert.Equal("configuration", entry.Source);
    }

    [Fact]
    public async Task GetCategory_Source_Database_TakesPrecedenceOverKeyVault()
    {
        // DB has a value AND Key Vault is enabled with config value
        _db.SystemConfigurations.Add(new SystemConfiguration
        {
            Category = "AzureOpenAI",
            Key = "Endpoint",
            EncryptedValue = _dataProtector.Protect("https://from-db.openai.azure.com"),
            IsSecret = false
        });
        await _db.SaveChangesAsync();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureKeyVault:Enabled"] = "true",
                ["AzureKeyVault:VaultUri"] = "https://my-vault.vault.azure.net/",
                ["AzureOpenAI:Endpoint"] = "https://from-kv.openai.azure.com"
            })
            .Build();

        var logger = Substitute.For<ILogger<ConfigurationManagementService>>();
        var serviceWithConfig = new ConfigurationManagementService(
            _db, _dataProtectionProvider, _configProvider, logger, config);

        var result = await serviceWithConfig.GetCategoryAsync("AzureOpenAI");

        var entry = result!.Entries.First(e => e.Key == "Endpoint");
        Assert.Equal("database", entry.Source);
    }

    // --- ConfigEntryDto Source Property Existence ---

    [Fact]
    public void ConfigEntryDto_HasSourceProperty()
    {
        var dto = new ConfigEntryDto();
        dto.Source = "database";
        Assert.Equal("database", dto.Source);

        dto.Source = null;
        Assert.Null(dto.Source);
    }
}
