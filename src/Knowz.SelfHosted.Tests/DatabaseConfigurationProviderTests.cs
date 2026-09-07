using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.Security.Cryptography;

namespace Knowz.SelfHosted.Tests;

public class DatabaseConfigurationProviderTests
{
    private readonly IDataProtectionProvider _dataProtectionProvider;

    public DatabaseConfigurationProviderTests()
    {
        _dataProtectionProvider = new EphemeralDataProtectionProvider();
    }

    [Fact]
    public void Load_ReturnsEmptyData_WhenConnectionStringEmpty()
    {
        var source = new DatabaseConfigurationSource
        {
            ConnectionString = "",
            DataProtectionProvider = _dataProtectionProvider
        };
        var provider = new DatabaseConfigurationProvider(source);

        provider.Load();

        Assert.False(provider.TryGet("AnyKey", out _));
    }

    [Fact]
    public void Load_ReturnsEmptyData_WhenConnectionStringNull()
    {
        var source = new DatabaseConfigurationSource
        {
            ConnectionString = null!,
            DataProtectionProvider = _dataProtectionProvider
        };
        var provider = new DatabaseConfigurationProvider(source);

        provider.Load();

        Assert.False(provider.TryGet("AnyKey", out _));
    }

    [Fact]
    public void Load_ReturnsEmptyData_WhenConnectionFails()
    {
        var source = new DatabaseConfigurationSource
        {
            ConnectionString = "Server=nonexistent-server-12345;Database=test;Trusted_Connection=True;Connect Timeout=1;",
            DataProtectionProvider = _dataProtectionProvider
        };
        var provider = new DatabaseConfigurationProvider(source);

        // Should not throw
        provider.Load();

        Assert.False(provider.TryGet("AnyKey", out _));
    }

    [Fact]
    public void Source_Build_ReturnsProvider()
    {
        var source = new DatabaseConfigurationSource
        {
            ConnectionString = "test",
            DataProtectionProvider = _dataProtectionProvider
        };

        var configBuilder = new Microsoft.Extensions.Configuration.ConfigurationBuilder();
        var provider = source.Build(configBuilder);

        Assert.IsType<DatabaseConfigurationProvider>(provider);
    }

    [Fact]
    public void Reload_DoesNotThrow_WhenConnectionFails()
    {
        var source = new DatabaseConfigurationSource
        {
            ConnectionString = "Server=nonexistent-server-12345;Database=test;Trusted_Connection=True;Connect Timeout=1;",
            DataProtectionProvider = _dataProtectionProvider
        };
        var provider = new DatabaseConfigurationProvider(source);

        // Should not throw
        var ex = Record.Exception(() => provider.Reload());
        Assert.Null(ex);
    }

    [Fact]
    public void Load_ReturnsEmptyData_WhenNoDataProtectionProvider()
    {
        var source = new DatabaseConfigurationSource
        {
            ConnectionString = "",
            DataProtectionProvider = null
        };
        var provider = new DatabaseConfigurationProvider(source);

        provider.Load();

        Assert.False(provider.TryGet("AnyKey", out _));
    }

    // --- SEC_P0Triage §Rule 4: secret-tier keys cannot come from the DB ---

    [Theory]
    [InlineData("SelfHosted:JwtSecret")]
    [InlineData("SelfHosted:ApiKey")]
    [InlineData("ConnectionStrings:McpDb")]
    [InlineData("AzureOpenAI:ApiKey")]
    [InlineData("AzureAISearch:ApiKey")]
    [InlineData("AzureAIVision:ApiKey")]
    [InlineData("AzureDocumentIntelligence:ApiKey")]
    [InlineData("Storage:Azure:ConnectionString")]
    [InlineData("KnowzPlatform:ApiKey")]
    public void SecretConfigurationKeys_Contains_AllExpectedSecretTierKeys(string configKey)
    {
        Assert.True(SecretConfigurationKeys.IsSecret(configKey),
            $"Expected '{configKey}' to be registered as a secret-tier key — " +
            "DatabaseConfigurationProvider must never emit this from the DB.");
    }

    [Theory]
    [InlineData("selfhosted:jwtsecret")]       // case variants
    [InlineData("SELFHOSTED:JWTSECRET")]
    [InlineData("SelfHosted:JWTsecret")]
    public void SecretConfigurationKeys_IsSecret_IsCaseInsensitive(string configKey)
    {
        Assert.True(SecretConfigurationKeys.IsSecret(configKey));
    }

    [Fact]
    public void SecretConfigurationKeys_CoversEverySchemaSecret()
    {
        // One-directional parity: every IsSecret=true key in
        // ConfigurationManagementService.CategorySchemas MUST appear in
        // SecretConfigurationKeys.All. This is the dangerous direction — a
        // schema secret missing from the denylist means DBConfigProvider
        // would emit it, letting a SuperAdmin override a KV-backed value.
        //
        // Extras in the denylist (keys NOT in the schema) are INTENTIONALLY
        // allowed — SEC_P0Triage pre-denylists keys like SuperAdminPassword
        // that aren't in the schema today so a future dev adding them won't
        // slip a secret through. If this test ever needs to fail on the
        // "in denylist but missing from schema" direction, split into two
        // tests and opt in explicitly.
        var schemaSecrets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (category, schema) in ConfigurationManagementService.CategorySchemas)
        {
            foreach (var (key, keySchema) in schema.Keys)
            {
                if (keySchema.IsSecret)
                    schemaSecrets.Add($"{category}:{key}");
            }
        }

        var missingFromInfra = schemaSecrets
            .Except(SecretConfigurationKeys.All, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(
            missingFromInfra.Count == 0,
            $"Schema IsSecret=true keys missing from SecretConfigurationKeys: " +
            $"[{string.Join(", ", missingFromInfra)}]. " +
            $"Add each to SecretConfigurationKeys.All so DBConfigProvider cannot " +
            $"emit a DB override of a KV-backed secret.");
    }

    [Fact]
    public void SecretConfigurationKeys_ExtrasAreProductivePreDenylisting()
    {
        // Sibling test: documents the intentional extras in the denylist
        // (keys we pre-denylist against schema evolution). Not a guard; a
        // canary — if the list of "extras" grows without review, a dev is
        // adding keys to the denylist without the corresponding schema
        // audit. Keep this list in sync with the rationale comments in
        // SecretConfigurationKeys.cs.
        var schemaSecrets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (category, schema) in ConfigurationManagementService.CategorySchemas)
        {
            foreach (var (key, keySchema) in schema.Keys)
            {
                if (keySchema.IsSecret)
                    schemaSecrets.Add($"{category}:{key}");
            }
        }

        var extras = SecretConfigurationKeys.All
            .Except(schemaSecrets, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Current approved extras. If this list changes, update both the
        // comment in SecretConfigurationKeys.cs AND this test.
        var approvedExtras = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SelfHosted:SuperAdminPassword", // pre-denylisted against future schema-add
        };

        var unexpectedExtras = extras.Where(e => !approvedExtras.Contains(e)).ToList();
        Assert.True(
            unexpectedExtras.Count == 0,
            $"Unexpected extras in SecretConfigurationKeys (not in schema, not in " +
            $"approvedExtras): [{string.Join(", ", unexpectedExtras)}]. " +
            $"Either add the key to ConfigurationManagementService.CategorySchemas " +
            $"or document it as an intentional pre-denylist in approvedExtras " +
            $"(with rationale) above.");
    }

    [Theory]
    [InlineData("SelfHosted:JwtIssuer")]              // non-secret sibling of JwtSecret
    [InlineData("SelfHosted:JwtExpirationMinutes")]
    [InlineData("SelfHosted:EnableSwagger")]
    [InlineData("AzureOpenAI:Endpoint")]              // endpoints are not secrets
    [InlineData("AzureOpenAI:DeploymentName")]
    [InlineData("AzureAISearch:IndexName")]
    [InlineData("Storage:Provider")]
    [InlineData("KnowzPlatform:BaseUrl")]
    [InlineData("AzureKeyVault:VaultUri")]
    [InlineData("SomeRandomKey")]
    public void SecretConfigurationKeys_ExcludesNonSecretKeys(string configKey)
    {
        Assert.False(SecretConfigurationKeys.IsSecret(configKey),
            $"'{configKey}' is not a secret — DB override should still be allowed.");
    }

    // --- SEC_P0Triage §Rule 4 — ProcessRow unit tests (test-advisor Gap 5b) ---
    // Gated via the internal ProcessRow helper, which isolates the per-row
    // decision logic from SQL. Exercises the three branches Load() cannot
    // exercise without a live DB: denylist warn, null-protector skip, and
    // decrypt-failure LogError.

    private sealed class ThrowingDataProtector : IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => this;
        public byte[] Protect(byte[] plaintext) => plaintext;
        public byte[] Unprotect(byte[] protectedData) =>
            throw new CryptographicException("simulated ring-mismatch — key rotated without re-encryption");
    }

    [Fact]
    public void ProcessRow_SecretKey_SkipsAndLogsWarning()
    {
        var logger = Substitute.For<ILogger>();
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        DatabaseConfigurationProvider.ProcessRow(
            logger, _dataProtectionProvider.CreateProtector("Knowz.SelfHosted.SystemConfiguration"),
            "SelfHosted", "JwtSecret",
            "any-ciphertext-value",
            warned, data);

        Assert.False(data.ContainsKey("SelfHosted:JwtSecret"));
        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("secret-tier", StringComparison.Ordinal)),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void ProcessRow_SameSecretKey_LoggedOnlyOnce()
    {
        var logger = Substitute.For<ILogger>();
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Three rows with the same denied key — should warn exactly once
        for (var i = 0; i < 3; i++)
        {
            DatabaseConfigurationProvider.ProcessRow(
                logger, _dataProtectionProvider.CreateProtector("Knowz.SelfHosted.SystemConfiguration"),
                "SelfHosted", "JwtSecret", $"ciphertext-{i}", warned, data);
        }

        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void ProcessRow_DecryptFailure_LogsError_WithException_AndSkips()
    {
        var logger = Substitute.For<ILogger>();
        var protector = new ThrowingDataProtector();
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        DatabaseConfigurationProvider.ProcessRow(
            logger, protector,
            "SomeFeature", "Flag",                // non-secret key, so it reaches decrypt
            "corrupt-ciphertext",
            warned, data);

        // Value NOT added to output dictionary
        Assert.False(data.ContainsKey("SomeFeature:Flag"));

        // LogError fired with the CryptographicException attached
        logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("decrypt", StringComparison.OrdinalIgnoreCase)),
            Arg.Is<Exception>(e => e is CryptographicException),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void ProcessRow_NullProtector_SkipsSilently_NoLog()
    {
        var logger = Substitute.For<ILogger>();
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        DatabaseConfigurationProvider.ProcessRow(
            logger, protector: null,
            "SomeFeature", "Flag",
            "any-ciphertext",
            warned, data);

        Assert.False(data.ContainsKey("SomeFeature:Flag"));
        // No log expected — this is the "DP not yet initialized" path on first boot,
        // happens on every fresh container and would be noisy.
        logger.DidNotReceive().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void ProcessRow_NullEncryptedValue_Skips_NoLog()
    {
        var logger = Substitute.For<ILogger>();
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        DatabaseConfigurationProvider.ProcessRow(
            logger, _dataProtectionProvider.CreateProtector("Knowz.SelfHosted.SystemConfiguration"),
            "SomeFeature", "Flag",
            encryptedValue: null,
            warned, data);

        Assert.False(data.ContainsKey("SomeFeature:Flag"));
    }

    [Fact]
    public void ProcessRow_NonSecretKey_WithValidProtector_EmitsDecryptedValue()
    {
        var logger = Substitute.For<ILogger>();
        var protector = _dataProtectionProvider.CreateProtector("Knowz.SelfHosted.SystemConfiguration");
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var plaintext = "feature-flag-on";
        var ciphertext = protector.Protect(plaintext);

        DatabaseConfigurationProvider.ProcessRow(
            logger, protector,
            "SomeFeature", "Flag",
            ciphertext,
            warned, data);

        Assert.Equal(plaintext, data["SomeFeature:Flag"]);
        // Happy path — no warn, no error
        logger.DidNotReceive().Log(
            LogLevel.Warning, Arg.Any<EventId>(), Arg.Any<object>(),
            Arg.Any<Exception?>(), Arg.Any<Func<object, Exception?, string>>());
        logger.DidNotReceive().Log(
            LogLevel.Error, Arg.Any<EventId>(), Arg.Any<object>(),
            Arg.Any<Exception?>(), Arg.Any<Func<object, Exception?, string>>());
    }
}
