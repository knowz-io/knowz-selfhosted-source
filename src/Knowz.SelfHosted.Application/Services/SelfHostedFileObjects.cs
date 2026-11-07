using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.Application.Services;

/// <summary>Resolves committed physical identities after the caller authorizes the logical file.</summary>
public static class SelfHostedFileObjects
{
    public static async Task<Guid> ResolveStorageIdAsync(SelfHostedDbContext db, Guid tenantId, Guid fileId, CancellationToken ct = default)
    {
        // Keep the live tenant/deletion filters, including when a repository returned a tracked row.
        if (!await db.FileRecords.AsNoTracking().AnyAsync(f => f.Id == fileId && f.TenantId == tenantId, ct))
            throw new FileNotFoundException("The file is unavailable in this tenant.");
        // A malformed foreign pointer is not the absence of a pointer. Never return stale
        // logical-ID bytes merely because the tenant filter hid an inconsistent mapping.
        var pointer = await db.ShFileObjectReferences.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(p => p.FileRecordId == fileId, ct);
        if (pointer == null) return fileId;
        if (pointer.TenantId != tenantId || pointer.BlobStorageId == Guid.Empty)
            throw new InvalidDataException("The file's physical object reference is unavailable or inconsistent.");
        return pointer.BlobStorageId;
    }
}
