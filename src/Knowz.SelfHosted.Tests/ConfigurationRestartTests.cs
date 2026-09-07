using Knowz.SelfHosted.API.Services;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Knowz.SelfHosted.Tests;

public sealed class PostgresConfigurationFactAttribute : FactAttribute
{
    public PostgresConfigurationFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KNOWZ_CONFIG_TEST_POSTGRES")))
            Skip = "Requires isolated PostgreSQL: KNOWZ_CONFIG_TEST_POSTGRES";
    }
}

public class ConfigurationRestartTests
{
    [PostgresConfigurationFact]
    public async Task Should_LoadProtectedOverridesBeforeProviderSelection_AcrossRestart()
    {
        var connectionString = Environment.GetEnvironmentVariable("KNOWZ_CONFIG_TEST_POSTGRES")!;
        var keys = Path.Combine(Path.GetTempPath(), "knowz-config-keys-" + Guid.NewGuid());
        Directory.CreateDirectory(keys);
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
                ["DataProtection:KeysPath"] = keys }).Build();
            var firstProcess = ConfigurationBootstrap.CreateDataProtection(config);
            var encrypted = firstProcess.CreateProtector("Knowz.SelfHosted.SystemConfiguration").Protect("http://127.0.0.1:11434/v1");
            await using var db = new NpgsqlConnection(connectionString);
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS "SystemConfigurations" ("Category" text, "Key" text, "EncryptedValue" text, "LastModifiedBy" text, "LastModifiedAt" timestamptz);
                ALTER TABLE "SystemConfigurations" ADD COLUMN IF NOT EXISTS "LastModifiedAt" timestamptz;
                DELETE FROM "SystemConfigurations";
                INSERT INTO "SystemConfigurations" ("Category", "Key", "EncryptedValue", "LastModifiedBy") VALUES ('OpenAiCompatible', 'Endpoint', @value, 'admin');
                INSERT INTO "SystemConfigurations" ("Category", "Key", "EncryptedValue", "LastModifiedBy") VALUES ('AzureOpenAI', 'Endpoint', @value, 'system-seed');
                INSERT INTO "SystemConfigurations" ("Category", "Key", "EncryptedValue", "LastModifiedBy") VALUES ('OpenAiCompatible', 'ApiKey', @value, 'admin');
                """;
            command.Parameters.AddWithValue("value", encrypted);
            await command.ExecuteNonQueryAsync();
            var secondProcess = ConfigurationBootstrap.CreateDataProtection(config);
            var effective = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
                ["OpenAiCompatible:ApiKey"] = "managed-key", ["AzureOpenAI:Endpoint"] = "https://managed.example" })
                .Add(new DatabaseConfigurationSource { ConnectionString = connectionString, DataProtectionProvider = secondProcess }).Build();
            Assert.Equal("http://127.0.0.1:11434/v1", effective["OpenAiCompatible:Endpoint"]);
            Assert.Equal("https://managed.example", effective["AzureOpenAI:Endpoint"]);
            Assert.Equal("managed-key", effective["OpenAiCompatible:ApiKey"]);
            await using var dates = db.CreateCommand();
            dates.CommandText = """UPDATE "SystemConfigurations" SET "LastModifiedAt" = '2020-01-01T00:00:00Z'""";
            await dates.ExecuteNonQueryAsync();
            var switched = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
                ["AzureOpenAI:Endpoint"] = "https://chosen-azure.example" })
                .Add(new DatabaseConfigurationSource { ConnectionString = connectionString, DataProtectionProvider = secondProcess,
                    HostAiConfigurationUpdatedAt = DateTimeOffset.Parse("2021-01-01T00:00:00Z") }).Build();
            Assert.Null(switched["OpenAiCompatible:Endpoint"]);
            Assert.Equal("AzureOpenAI", new RunningConfiguration(switched).ActiveProvider);
            dates.CommandText = """UPDATE "SystemConfigurations" SET "LastModifiedAt" = '2022-01-01T00:00:00Z' WHERE "Category" = 'OpenAiCompatible' AND "Key" = 'Endpoint'""";
            await dates.ExecuteNonQueryAsync();
            switched.Reload();
            Assert.Equal("http://127.0.0.1:11434/v1", switched["OpenAiCompatible:Endpoint"]);

        }
        finally { Directory.Delete(keys, true); }
    }
    [PostgresConfigurationFact]
    public async Task Should_ProbeRealPostgresAndRejectWrongCredentials()
    {
        var connection = Environment.GetEnvironmentVariable("KNOWZ_CONFIG_TEST_POSTGRES")!;
        using var client = new HttpClient();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["ConnectionStrings:McpDb"] = connection }).Build();
        Assert.True((await ConfigurationProbe.RunAsync("ConnectionStrings", config, client)).IsHealthy);
        var invalid = new NpgsqlConnectionStringBuilder(connection) { Password = "invalid-test-credential" };
        var bad = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["ConnectionStrings:McpDb"] = invalid.ConnectionString }).Build();
        var result = await ConfigurationProbe.RunAsync("ConnectionStrings", bad, client);
        Assert.False(result.IsHealthy);
        Assert.Equal("unauthorized", result.ProbeStatus);
        Assert.DoesNotContain("invalid-test-credential", result.Status);
    }

}
