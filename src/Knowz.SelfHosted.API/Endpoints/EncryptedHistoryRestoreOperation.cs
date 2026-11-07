using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.API.Endpoints;

internal static class EncryptedHistoryRestoreOperation
{
    internal static Task<SealedStoreResult> RunAsync(HttpContext http, SelfHostedDbContext db,
        IVaultAccessService access, Guid id, Func<CancellationToken, Task<SealedStoreResult>> restore,
        CancellationToken ct) => db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
    {
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var vaults = await db.KnowledgeVaults.AsNoTracking().Where(v => v.KnowledgeId == id).Select(v => v.VaultId).Distinct().OrderBy(v => v).ToListAsync(ct);
        async Task<bool> AuthorizedAsync()
        {
            if (vaults.Count == 0 || !await db.KnowledgeItems.AsNoTracking().AnyAsync(k => k.Id == id && k.PortableRecoveryState == null, ct)) return false;
            var fresh = await db.KnowledgeVaults.AsNoTracking().Where(v => v.KnowledgeId == id).Select(v => v.VaultId).Distinct().OrderBy(v => v).ToListAsync(ct);
            if (!fresh.SequenceEqual(vaults)) return false;
            foreach (var vault in vaults)
                if (!await EncryptedGraphEndpoints.AuthorizedAsync(http, vault, db, access, true, ct)) return false;
            return true;
        }
        if (!await AuthorizedAsync()) return SealedStoreResult.Fail(403, "Write access to every owning vault is required.");
        try
        {
            var result = await restore(ct);
            if (!result.Ok || !await AuthorizedAsync())
            {
                if (transaction != null) await transaction.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                return result.Ok ? SealedStoreResult.Fail(403, "Access changed; the write was rolled back.") : result;
            }
            if (transaction != null) await transaction.CommitAsync(ct);
            return result;
        }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction != null) await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear(); return SealedStoreResult.Fail(409, "Item changed; reload before writing.");
        }
        catch
        {
            if (transaction != null) await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear(); throw;
        }
    });
}
