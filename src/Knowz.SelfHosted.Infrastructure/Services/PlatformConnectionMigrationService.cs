using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>
/// Idempotent one-shot data migration that backfills <see cref="PlatformConnection"/> rows
/// from the legacy per-link columns on <see cref="VaultSyncLink"/>.
///
/// Prior to this migration, every sync link owned its own <c>PlatformApiUrl</c> plus a
/// plaintext <c>ApiKeyEncrypted</c> column (misnamed — it was actually cleartext). This
/// service rolls those values forward:
///   1. Groups any legacy link rows (those with <c>PlatformConnectionId = null</c>) by tenant.
///   2. For each tenant, ensures exactly one <see cref="PlatformConnection"/> exists and
///      re-encrypts the legacy key using the per-tenant DataProtection purpose string.
///   3. Writes the new FK back onto each VaultSyncLink so the legacy columns become dead weight
///      (a follow-up migration will drop them).
///
/// The service is safe to run on every boot — the top-level check exits immediately when there
/// is no legacy data left to migrate, and the per-tenant connect step is a find-or-create.
/// </summary>
public class PlatformConnectionMigrationService : IHostedService
{
    // Master purpose must stay byte-identical to PlatformConnectionService.MasterPurpose —
    // both services share the same keyring, and a drift would make ciphertext un-decryptable.
    private const string MasterPurpose = "Knowz.SelfHosted.PlatformSync";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PlatformConnectionMigrationService> _logger;

    public PlatformConnectionMigrationService(
        IServiceScopeFactory scopeFactory,
        ILogger<PlatformConnectionMigrationService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await MigrateAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Never let a migration failure crash app startup — operators can re-run manually.
            _logger.LogError(ex,
                "PlatformConnection data migration failed at startup; app will continue without backfill");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal async Task MigrateAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
        var dpp = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();

        // Tenant lookup has to come from SyncTombstones / VaultSyncLinks — the legacy link row
        // itself doesn't carry a TenantId column. We resolve tenant via the owning local Vault.
#pragma warning disable CS0618 // Intentional: we're reading the legacy columns here.
        var legacyLinks = await db.VaultSyncLinks
            .Where(l => l.PlatformConnectionId == null
                && !string.IsNullOrEmpty(l.PlatformApiUrl)
                && !string.IsNullOrEmpty(l.ApiKeyEncrypted))
            .ToListAsync(ct);
#pragma warning restore CS0618

        if (legacyLinks.Count == 0)
        {
            _logger.LogDebug("PlatformConnection migration: no legacy sync links to backfill");
            return;
        }

        _logger.LogInformation(
            "PlatformConnection migration: found {Count} legacy link(s) to backfill", legacyLinks.Count);

        // Resolve TenantId via the owning vault. Links whose vault has been deleted are skipped.
        // IgnoreQueryFilters because the startup scope has an arbitrary tenant context and must
        // see *every* tenant's vaults during backfill.
        var vaultIds = legacyLinks.Select(l => l.LocalVaultId).Distinct().ToList();
        var vaultTenantMap = await db.Vaults
            .IgnoreQueryFilters()
            .Where(v => vaultIds.Contains(v.Id))
            .Select(v => new { v.Id, v.TenantId })
            .ToDictionaryAsync(v => v.Id, v => v.TenantId, ct);

        var migratedLinks = 0;
        var createdConnections = 0;

        // Group by tenant so we only build one PlatformConnection per tenant. When multiple
        // links in the same tenant disagree on URL/key, the first encountered wins — operators
        // can reconfigure afterwards via the /sync/connection endpoint.
        var linksByTenant = legacyLinks
            .Where(l => vaultTenantMap.ContainsKey(l.LocalVaultId))
            .GroupBy(l => vaultTenantMap[l.LocalVaultId]);

        foreach (var tenantGroup in linksByTenant)
        {
            var tenantId = tenantGroup.Key;

            var existing = await db.PlatformConnections
                .FirstOrDefaultAsync(c => c.TenantId == tenantId, ct);

            PlatformConnection connection;
            if (existing is null)
            {
                var firstLink = tenantGroup.First();
#pragma warning disable CS0618
                var legacyUrl = firstLink.PlatformApiUrl.Trim().TrimEnd('/');
                var legacyPlaintextKey = firstLink.ApiKeyEncrypted;
#pragma warning restore CS0618

                var protector = dpp
                    .CreateProtector(MasterPurpose)
                    .CreateProtector($"{MasterPurpose}.{tenantId}");

                connection = new PlatformConnection
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    PlatformApiUrl = legacyUrl,
                    ApiKeyProtected = protector.Protect(legacyPlaintextKey),
                    ApiKeyLast4 = legacyPlaintextKey.Length <= 4
                        ? legacyPlaintextKey
                        : legacyPlaintextKey.Substring(legacyPlaintextKey.Length - 4),
                    DisplayName = null,
                    LastTestStatus = PlatformConnectionTestStatus.Untested,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    CreatedByUserId = Guid.Empty, // system migration — no caller identity
                };
                db.PlatformConnections.Add(connection);
                createdConnections++;
            }
            else
            {
                connection = existing;
            }

            foreach (var link in tenantGroup)
            {
                link.PlatformConnectionId = connection.Id;
                migratedLinks++;
            }
        }

        if (createdConnections > 0 || migratedLinks > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "PlatformConnection migration complete: created {Connections} connection(s), linked {Links} vault sync link(s)",
                createdConnections, migratedLinks);
        }
    }
}
