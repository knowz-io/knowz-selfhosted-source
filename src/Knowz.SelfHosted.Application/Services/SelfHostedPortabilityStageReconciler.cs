using System.Text.Json;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Infrastructure.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Application.Services;

/// <summary>Reclaims expired private stages after cancellation or restart. Live object references always win.</summary>
public sealed class SelfHostedPortabilityStageReconciler(SelfHostedDbContext db, IFileStorageProvider storage, ILogger<SelfHostedPortabilityStageReconciler> logger)
{
    public async Task ReconcileAsync(DateTime utcNow, CancellationToken ct)
    {
        var expired = utcNow.AddHours(-1);
        var journals = await db.ShArchiveImportJournals.IgnoreQueryFilters().Where(j => j.Status != "committed" && (j.Status != "cancelled" || j.StagedObjectIdsJson != "[]") && j.UpdatedAt <= expired).Select(j => j.PlanId).Take(100).ToListAsync(ct);
        foreach (var id in journals)
        {
            try
            {
                var row = await db.ShArchiveImportJournals.IgnoreQueryFilters().SingleAsync(j => j.PlanId == id, ct);
                if (row.Status == "committed" || row.UpdatedAt > expired) continue;
                row.Status = "reconciling"; await db.SaveChangesAsync(ct); // CAS prevents a simultaneous commit publishing these objects.
                var objects = JsonSerializer.Deserialize<List<Guid>>(row.StagedObjectIdsJson) ?? [];
                foreach (var blob in objects) await DeleteIfUnreferencedAsync(row.TenantId, blob, ct);
                row.StagedObjectIdsJson = "[]"; row.Status = "cancelled"; row.UpdatedAt = utcNow; await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { db.ChangeTracker.Clear(); logger.LogWarning(ex, "Archive stage {PlanId} cleanup will retry", id); }
        }
        var stages = await db.ShSealedFileStages.IgnoreQueryFilters().Where(s => s.Status != "committed" && (s.Status != "cancelled" || s.BlobStorageId.HasValue) && s.ExpiresAt <= utcNow).Select(s => s.Id).Take(100).ToListAsync(ct);
        foreach (var id in stages)
        {
            try
            {
                var row = await db.ShSealedFileStages.IgnoreQueryFilters().SingleAsync(s => s.Id == id, ct);
                if (row.Status == "committed" || row.ExpiresAt > utcNow) continue;
                row.Status = "reconciling"; await db.SaveChangesAsync(ct);
                if (row.BlobStorageId.HasValue) await DeleteIfUnreferencedAsync(row.TenantId, row.BlobStorageId.Value, ct);
                row.BlobStorageId = null; row.Status = "cancelled"; row.UpdatedAt = utcNow; await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { db.ChangeTracker.Clear(); logger.LogWarning(ex, "Encrypted file stage {StageId} cleanup will retry", id); }
        }
    }
    private async Task DeleteIfUnreferencedAsync(Guid tenant, Guid blob, CancellationToken ct)
    {
        if (await db.ShSealedFiles.IgnoreQueryFilters().AnyAsync(f => f.TenantId == tenant && f.BlobStorageId == blob, ct)
            || await db.ShFileObjectReferences.IgnoreQueryFilters().AnyAsync(f => f.TenantId == tenant && f.BlobStorageId == blob, ct)) return;
        await storage.DeleteAsync(tenant, blob, ct);
    }
}

internal static class SelfHostedObjectCleanup
{
    // Saved in the same transaction that detaches the old object. The reconciler rechecks live references.
    internal static void Queue(SelfHostedDbContext db, Guid tenant, Guid fileId, Guid blob)
    {
        var id = Guid.NewGuid();
        db.ShSealedFileStages.Add(new() { Id = id, TenantId = tenant, ActorId = Guid.Empty, FileId = fileId,
            IdempotencyKey = "cleanup:" + id.ToString("N"), BlobStorageId = blob, Status = "cancelled",
            ExpiresAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, MetadataJson = "{}" });
    }
}

public sealed class SelfHostedPortabilityRecoveryWorker(IServiceScopeFactory scopes, ArchiveImportCoordinator coordinator, ILogger<SelfHostedPortabilityRecoveryWorker> logger) : BackgroundService
{
    public async Task RunOnceAsync(CancellationToken ct)
    {
        await coordinator.ExpirePlansAsync(async (actor, preparation, token) =>
        {
            var previous = TenantContext.CurrentTenantId;
            try
            {
                TenantContext.CurrentTenantId = actor.TenantId;
                await using var ownerScope = scopes.CreateAsyncScope();
                if (ownerScope.ServiceProvider.GetRequiredService<ITenantProvider>().TenantId != actor.TenantId)
                    throw new InvalidOperationException("Archive cleanup could not establish its owner tenant.");
                await ownerScope.ServiceProvider.GetRequiredService<SelfHostedArchiveImportAdapter>().CleanupAsync(preparation, token);
            }
            finally { TenantContext.CurrentTenantId = previous; }
        }, ct);
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SelfHostedPortabilityStageReconciler>().ReconcileAsync(DateTime.UtcNow, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Portability stage reconciliation will retry"); }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) return; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
