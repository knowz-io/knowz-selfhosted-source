using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>
/// Custom ConfigurationProvider that reads SystemConfiguration rows from the database
/// and maps them into the IConfiguration pipeline as "{Category}:{Key}" entries.
/// Decrypts EncryptedValue using Data Protection API on load.
///
/// SEC_P0Triage §Rule 4: refuses to emit any key listed in
/// <see cref="SecretConfigurationKeys"/>. Secrets must come from Key Vault or
/// environment variables — never from the database. This prevents a SuperAdmin
/// with config-write access from overriding a KV-provided secret via the
/// <c>/api/config</c> UI, which would trivialize KV as a trust boundary.
/// </summary>
public class DatabaseConfigurationProvider : ConfigurationProvider
{
    private readonly DatabaseConfigurationSource _source;
    private static readonly string ProtectorPurpose = "Knowz.SelfHosted.SystemConfiguration";

    public DatabaseConfigurationProvider(DatabaseConfigurationSource source)
    {
        _source = source;
    }

    /// <summary>
    /// Loads all SystemConfiguration rows from the database into the Data dictionary.
    /// Called once at startup. Decrypts EncryptedValue for each row.
    /// Silently catches exceptions if DB is unavailable (logs warning via console).
    /// </summary>
    public override void Load()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        ILogger logger = _source.Logger ?? (ILogger)NullLogger.Instance;

        if (string.IsNullOrWhiteSpace(_source.ConnectionString))
        {
            Data = data;
            return;
        }

        // De-dup per-key warnings so a denied secret-tier row logs once, not per row.
        var warnedSecretKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var connection = new NpgsqlConnection(_source.ConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """SELECT "Category", "Key", "EncryptedValue", "LastModifiedAt" FROM "SystemConfigurations" WHERE COALESCE("LastModifiedBy", '') <> 'system-seed'""";

            using var reader = command.ExecuteReader();
            var protector = _source.DataProtectionProvider?.CreateProtector(ProtectorPurpose);

            while (reader.Read())
            {
                var category = reader.GetString(0);
                if (AiRuntimePolicy.IsSuperseded(category, reader.IsDBNull(3) ? DateTime.MinValue : reader.GetDateTime(3), _source.HostAiConfigurationUpdatedAt)) continue;
                var key = reader.GetString(1);
                var encryptedValue = reader.IsDBNull(2) ? null : reader.GetString(2);

                ProcessRow(logger, protector, category, key, encryptedValue, warnedSecretKeys, data);
            }
        }
        catch (Exception ex)
        {
            // DB not available (first run before migration, connection failure, etc.)
            // Fall back to file-based config. Logged at Information — this is the
            // expected path on fresh containers before Database:AutoMigrate runs.
            logger.LogInformation(
                ex,
                "Database configuration source unavailable; falling back to file/env config. " +
                "This is expected on first boot before migrations have run.");
        }

        Data = data;
    }

    /// <summary>
    /// Reloads configuration from the database. Called by the Service layer
    /// after an admin saves config changes. Triggers IOptionsMonitor change tokens.
    /// </summary>
    public void Reload()
    {
        Load();
        OnReload();
    }

    /// <summary>
    /// Processes a single SystemConfigurations row. Factored out of <see cref="Load"/>
    /// so tests can exercise the denylist / decrypt-failure / null-protector branches
    /// without a live SQL connection. Mutates <paramref name="data"/> and
    /// <paramref name="warnedSecretKeys"/> in place.
    /// </summary>
    internal static void ProcessRow(
        ILogger logger,
        IDataProtector? protector,
        string category,
        string key,
        string? encryptedValue,
        HashSet<string> warnedSecretKeys,
        IDictionary<string, string?> data)
    {
        if (encryptedValue is null)
            return;

        var configKey = $"{category}:{key}";

        // SEC_P0Triage §Rule 4: skip secret-tier keys entirely. The DB is
        // NOT an authoritative source for these — they come from Key Vault
        // or environment variables. Without this guard, a SuperAdmin using
        // /api/config could clobber a KV-provided value.
        if (ConfigurationAuthority.IsExternallyManaged(configKey))
        {
            if (warnedSecretKeys.Add(configKey))
            {
                logger.LogWarning(
                    "Database configuration row for secret-tier key '{ConfigKey}' " +
                    "was skipped. Secrets must come from Key Vault or environment " +
                    "variables, not the SystemConfigurations table.",
                    configKey);
            }
            return;
        }

        if (protector is null)
        {
            // Data Protection not yet initialized — skip encrypted DB values,
            // fall back to file-based config (appsettings.json / environment vars)
            return;
        }

        string? decryptedValue;
        try
        {
            decryptedValue = protector.Unprotect(encryptedValue);
        }
        catch (Exception ex)
        {
            // SEC_P0Triage §Rule 4: elevated from silent-catch to LogError so
            // operators see decrypt failures in central telemetry. Cause is
            // usually a Data Protection key-ring change without a rotation
            // plan (covered by Item 10, persistent DP key ring).
            logger.LogError(
                ex,
                "Failed to decrypt configuration value for '{ConfigKey}'. " +
                "Falling back to file/env config for this key. Root cause is " +
                "typically a Data Protection key-ring mismatch — verify the " +
                "ring is persisted (PersistKeysToAzureBlobStorage) and wrapped " +
                "with a stable Key Vault key.",
                configKey);
            return;
        }

        data[configKey] = decryptedValue;
    }
}
