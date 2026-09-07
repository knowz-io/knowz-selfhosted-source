using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class ConfigurationReadinessTests : IDisposable
{
    private readonly SelfHostedDbContext db;
    private readonly IDataProtectionProvider protection = new EphemeralDataProtectionProvider();
    public ConfigurationReadinessTests()
    {
        var tenant = Substitute.For<ITenantProvider>();
        tenant.TenantId.Returns(Guid.NewGuid());
        db = new SelfHostedDbContext(new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
    }
    private ConfigurationManagementService Service(params (string Key, string Value)[] values) => new(
        db, protection, null, NullLogger<ConfigurationManagementService>.Instance,
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build());

    [Fact]
    public async Task Should_RejectManagedSecretAtomically_WhenMixedWithEditableSetting()
    {
        var result = await Service().UpdateCategoryAsync("AzureOpenAI", [
            new() { Key = "Endpoint", Value = "https://new.example" },
            new() { Key = "ApiKey", Value = "must-not-be-stored" }], "admin");
        Assert.False(result.Success);
        Assert.Empty(await db.SystemConfigurations.ToListAsync());
        Assert.DoesNotContain("must-not-be-stored", string.Join(" ", result.Errors));
    }
    [Fact]
    public async Task Should_ReadAuthoritativeSecret_WhenHistoricalDatabaseSecretExists()
    {
        db.SystemConfigurations.Add(new SystemConfiguration { Category = "AzureOpenAI", Key = "ApiKey",
            IsSecret = true, EncryptedValue = protection.CreateProtector("Knowz.SelfHosted.SystemConfiguration").Protect("stale-db-secret") });
        await db.SaveChangesAsync();
        var key = (await Service(("AzureOpenAI:ApiKey", "managed-secret-1234")).GetCategoryAsync("AzureOpenAI"))!.Entries.Single(e => e.Key == "ApiKey");
        Assert.Equal("****1234", key.Value);
        Assert.True(key.IsSet);
        Assert.NotEqual("database", key.Source);
    }
    [Fact]
    public async Task Should_NotPinDefaultsOrCopySecrets_WhenSeedingFreshInstall()
    {
        await Service().SeedFromConfigurationAsync(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["AzureOpenAI:Endpoint"] = "https://env.example", ["AzureOpenAI:ApiKey"] = "external-secret" }).Build());
        Assert.Empty(await db.SystemConfigurations.ToListAsync());
    }
    [Fact]
    public async Task Should_ExposeNativeProvider_WhenListingConfiguration()
    {
        var categories = await Service().GetAllCategoriesAsync();
        Assert.Contains(categories, c => c.Category == "OpenAiCompatible" && c.Entries.Any(e => e.Key == "EmbeddingModel"));
        Assert.True(SecretConfigurationKeys.IsSecret("OpenAiCompatible:ApiKey"));
    }
    [Fact]
    public async Task Should_NotClaimConnectivity_WhenNoProbeExists()
    {
        Assert.False((await Service().TestConnectionAsync("SelfHosted")).IsHealthy);
    }
    [Fact]
    public async Task Should_NotPersistUnchangedDefault_WhenCallerSendsSameValue()
    {
        var result = await Service(("AzureOpenAI:Endpoint", "https://env.example")).UpdateCategoryAsync("AzureOpenAI",
            [new() { Key = "Endpoint", Value = "https://env.example" }], "admin");
        Assert.True(result.Success);
        Assert.Equal(0, result.EntriesUpdated);
        Assert.Empty(await db.SystemConfigurations.ToListAsync());
    }
    [Fact]
    public async Task Should_ReportPendingWithoutClaimingEffective_WhenSettingIsSavedBeforeRestart()
    {
        var service = Service();
        await service.UpdateCategoryAsync("AzureOpenAI", [new() { Key = "Endpoint", Value = "https://pending.example" }], "admin");
        var entry = (await service.GetCategoryAsync("AzureOpenAI"))!.Entries.Single(e => e.Key == "Endpoint");
        Assert.True(entry.PendingRestart);
        Assert.False(entry.EffectiveIsSet);
        Assert.True(Service().GetDeploymentStatus().RestartRequired);
    }
    [Fact]
    public async Task Should_NotApplyLegacySeedAsOverride_WhenExternalValueChanges()
    {
        db.SystemConfigurations.Add(new SystemConfiguration { Category = "AzureOpenAI", Key = "Endpoint",
            LastModifiedBy = "system-seed", EncryptedValue = protection.CreateProtector("Knowz.SelfHosted.SystemConfiguration").Protect("https://old.example") });
        await db.SaveChangesAsync();
        var entry = (await Service(("AzureOpenAI:Endpoint", "https://new.example")).GetCategoryAsync("AzureOpenAI"))!.Entries.Single(e => e.Key == "Endpoint");
        Assert.Equal("https://new.example", entry.Value);
        Assert.False(entry.PendingRestart);
    }
    [Fact]
    public async Task Should_ReportSelectedProviderAndIncompleteFallback()
    {
        var incomplete = Service(("OpenAiCompatible:Endpoint", "http://local.test/v1"));
        Assert.Equal("offline", incomplete.GetDeploymentStatus().ActiveProvider);
        Assert.Equal("incomplete", (await incomplete.GetCategoryAsync("OpenAiCompatible"))!.ConfigurationStatus);
        var native = Service(("OpenAiCompatible:Endpoint", "http://local.test/v1"), ("OpenAiCompatible:ChatModel", "chat"), ("OpenAiCompatible:EmbeddingModel", "embedding"));
        Assert.Equal("OpenAiCompatible", native.GetDeploymentStatus().ActiveProvider);
    }
    [Theory]
    [InlineData("AzureKeyVault", "VaultUri", "https://new-store.example")]
    [InlineData("AzureOpenAI", "Endpoint", "file:///etc/passwd")]
    [InlineData("SelfHosted", "JwtExpirationMinutes", "not-a-number")]
    public async Task Should_RejectSettingsThatCannotSafelyApply(string category, string key, string value)
    {
        var result = await Service().UpdateCategoryAsync(category, [new() { Key = key, Value = value }], "admin");
        Assert.False(result.Success);
        Assert.Empty(await db.SystemConfigurations.ToListAsync());
    }
    [Fact]
    public async Task Should_ExposeUnappliedOverrideWithoutCrashing_AndAllowExplicitRecovery()
    {
        db.SystemConfigurations.Add(new SystemConfiguration { Category = "AzureOpenAI", Key = "Endpoint",
            LastModifiedBy = "admin", EncryptedValue = "lost-protection-key-or-invalid-ciphertext" });
        await db.SaveChangesAsync();
        var service = Service(("AzureOpenAI:Endpoint", "https://fallback.example"));
        var status = service.GetDeploymentStatus();
        Assert.True(status.RestartRequired);
        Assert.Contains("AzureOpenAI:Endpoint", status.RestartReasons);
        var entry = (await service.GetCategoryAsync("AzureOpenAI"))!.Entries.Single(e => e.Key == "Endpoint");
        Assert.True(entry.PendingRestart);
        Assert.Equal("https://fallback.example", entry.Value);
        // Re-entering the fallback is still an explicit repair of a broken override.
        var repaired = await service.UpdateCategoryAsync("AzureOpenAI", [new() { Key = "Endpoint", Value = "https://fallback.example" }], "admin");
        Assert.True(repaired.Success);
        Assert.Equal(1, repaired.EntriesUpdated);
        Assert.False(service.GetDeploymentStatus().RestartRequired);
    }
    [Fact]
    public async Task Should_ExposeHostDisabledProviderAndRejectIneffectiveEditsWithoutChangingStoredValues()
    {
        var service = Service(("KNOWZ_AI_DISABLED", "true"), ("OpenAiCompatible:Endpoint", "https://saved.example"), ("OpenAiCompatible:ChatModel", "chat"), ("OpenAiCompatible:EmbeddingModel", "embed"));
        var category = (await service.GetCategoryAsync("OpenAiCompatible"))!;
        Assert.Equal("disabled", category.ConfigurationStatus);
        Assert.Contains("knowz setup", category.Description);
        Assert.All(category.Entries, entry => Assert.False(entry.Editable));
        var update = await service.UpdateCategoryAsync("OpenAiCompatible", [new() { Key = "Endpoint", Value = "https://ignored.example" }], "admin");
        Assert.False(update.Success);
        Assert.Empty(await db.SystemConfigurations.ToListAsync());
    }
    [Fact]
    public async Task HostProviderRevision_RetiresOlderOverrides_AndAllowsLaterExplicitUiChanges()
    {
        var old = new SystemConfiguration { Category = "OpenAiCompatible", Key = "Endpoint", LastModifiedBy = "admin",
            LastModifiedAt = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EncryptedValue = protection.CreateProtector("Knowz.SelfHosted.SystemConfiguration").Protect("https://old-native.example") };
        db.SystemConfigurations.Add(old);
        await db.SaveChangesAsync();
        var service = Service(("KNOWZ_AI_CONFIG_UPDATED_AT", "2021-01-01T00:00:00.000Z"), ("AzureOpenAI:Endpoint", "https://current-azure.example"));
        var native = (await service.GetCategoryAsync("OpenAiCompatible"))!.Entries.Single(e => e.Key == "Endpoint");
        Assert.Null(native.Value);
        Assert.False(native.PendingRestart);
        Assert.False(service.GetDeploymentStatus().RestartRequired);
        Assert.Equal("AzureOpenAI", service.GetDeploymentStatus().ActiveProvider);
        Assert.NotNull(await db.SystemConfigurations.FindAsync(old.Id));
        var update = await service.UpdateCategoryAsync("OpenAiCompatible", [new() { Key = "Endpoint", Value = "https://old-native.example" }], "admin");
        Assert.True(update.Success);
        Assert.Equal(1, update.EntriesUpdated);
        Assert.True((await service.GetCategoryAsync("OpenAiCompatible"))!.Entries.Single(e => e.Key == "Endpoint").PendingRestart);
    }
    public void Dispose() => db.Dispose();
}
