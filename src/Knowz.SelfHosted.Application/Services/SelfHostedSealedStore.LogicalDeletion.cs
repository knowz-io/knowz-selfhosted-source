using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Knowz.SelfHosted.Application.Services;

public partial class SelfHostedSealedStore
{
    private async Task HideAttachmentsAndRetainFilesAsync(List<ShSealedAttachment> links, Guid? deletedKnowledge, CancellationToken ct)
    {
        foreach (var link in links) { link.IsDeleted = true; link.IsPrimary = false; link.UpdatedAt = DateTime.UtcNow; }
        foreach (var fileId in links.Select(a => a.FileRecordId).Distinct())
        {
            var file = await _db.ShSealedFiles.SingleOrDefaultAsync(f => f.Id == fileId, ct);
            if (file == null) continue;
            if (file.KnowledgeId == deletedKnowledge) file.KnowledgeId = null;
            file.UpdatedAt = DateTime.UtcNow;
        }
    }
}
