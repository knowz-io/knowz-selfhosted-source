using System.Threading.Channels;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Infrastructure.Services;

public class EnrichmentOutboxWriter : IEnrichmentOutboxWriter
{
    private readonly SelfHostedDbContext _db;
    private readonly ChannelWriter<EnrichmentWorkItem> _channelWriter;
    private readonly ILogger<EnrichmentOutboxWriter> _logger;

    public EnrichmentOutboxWriter(
        SelfHostedDbContext db,
        Channel<EnrichmentWorkItem> channel,
        ILogger<EnrichmentOutboxWriter> logger)
    {
        _db = db;
        _channelWriter = channel.Writer;
        _logger = logger;
    }

    public Task EnqueueAsync(Guid knowledgeId, Guid tenantId, CancellationToken ct = default)
        => EnqueueAsync(knowledgeId, tenantId, forceReset: false, ct);

    public async Task EnqueueAsync(Guid knowledgeId, Guid tenantId, bool forceReset, CancellationToken ct = default)
    {
        var existingRows = await _db.EnrichmentOutbox
            .Where(e => e.KnowledgeId == knowledgeId &&
                        (e.Status == EnrichmentStatus.Pending || e.Status == EnrichmentStatus.Processing))
            .ToListAsync(ct);

        if (existingRows.Count > 0)
        {
            if (forceReset)
            {
                foreach (var row in existingRows)
                {
                    var oldStatus = row.Status;
                    row.Status = EnrichmentStatus.Pending;
                    row.RetryCount = 0;
                    row.ErrorMessage = null;
                    row.NextRetryAt = null;
                    row.ProcessedAt = null;
                    _logger.LogInformation(
                        "Reprocess: reset outbox row for knowledge {KnowledgeId} from {OldStatus} to Pending",
                        knowledgeId, oldStatus);
                }
                await _db.SaveChangesAsync(ct);
            }
            else
            {
                _logger.LogDebug("Enrichment already queued for knowledge {KnowledgeId}, skipping DB insert", knowledgeId);
            }
        }
        else
        {
            var outboxItem = new EnrichmentOutboxItem
            {
                TenantId = tenantId,
                KnowledgeId = knowledgeId
            };
            _db.EnrichmentOutbox.Add(outboxItem);
            await _db.SaveChangesAsync(ct);
            _logger.LogDebug("Created enrichment outbox item for knowledge {KnowledgeId}", knowledgeId);
        }

        // Always write to channel (ensures pickup even on dedup)
        try
        {
            _channelWriter.TryWrite(new EnrichmentWorkItem(knowledgeId, tenantId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write enrichment work item to channel for knowledge {KnowledgeId}", knowledgeId);
        }
    }
}
