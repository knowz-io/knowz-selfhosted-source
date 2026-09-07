using System.Text.Json;
using System.Threading.Channels;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Knowz.SelfHosted.Infrastructure.Extensions;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Fix 6 TODO: Add StartedProcessingAt field to EnrichmentOutboxItem and use it
// for stuck detection instead of CreatedAt. Requires a DB migration.
// The 5-min threshold on CreatedAt is acceptable for now.

namespace Knowz.SelfHosted.Infrastructure.Services;

public class EnrichmentBackgroundService : BackgroundService
{
    private readonly Channel<EnrichmentWorkItem> _channel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EnrichmentBackgroundService> _logger;

    private static readonly HashSet<string> PlaceholderTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "untitled", "untitled content", "media file", "document", "file", "n/a", "unknown"
    };

    public EnrichmentBackgroundService(
        Channel<EnrichmentWorkItem> channel,
        IServiceScopeFactory scopeFactory,
        ILogger<EnrichmentBackgroundService> logger)
    {
        _channel = channel;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("EnrichmentBackgroundService started");

        // Immediate startup scan: load pending/stuck items into channel before entering processing loop
        await ScanAndEnqueuePendingAsync(stoppingToken);

        var channelTask = ProcessChannelAsync(stoppingToken);
        var recoveryTask = RecoveryTimerAsync(stoppingToken);

        await Task.WhenAll(channelTask, recoveryTask);
    }

    internal async Task ScanAndEnqueuePendingAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
            var now = DateTime.UtcNow;
            var stuckThreshold = now.AddMinutes(-5);

            var pendingItems = await db.EnrichmentOutbox
                .Where(e =>
                    (e.Status == EnrichmentStatus.Pending &&
                     (e.NextRetryAt == null || e.NextRetryAt <= now)) ||
                    (e.Status == EnrichmentStatus.Processing &&
                     e.CreatedAt < stuckThreshold))
                .OrderBy(e => e.CreatedAt)
                .Take(100)
                .ToListAsync(ct);

            foreach (var item in pendingItems)
            {
                await _channel.Writer.WriteAsync(
                    new EnrichmentWorkItem(item.KnowledgeId, item.TenantId), ct);
            }

            if (pendingItems.Count > 0)
                _logger.LogInformation("Startup recovery: enqueued {Count} pending enrichment items", pendingItems.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup recovery scan failed — items will be picked up by recovery timer");
        }
    }

    internal async Task ProcessChannelAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var workItem in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await ProcessWorkItemAsync(workItem, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Enrichment failed for KnowledgeId {Id}", workItem.KnowledgeId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown
        }
    }

    internal async Task ProcessWorkItemAsync(EnrichmentWorkItem workItem, CancellationToken ct)
    {
        TenantContext.CurrentTenantId = workItem.TenantId;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
            var enrichmentService = scope.ServiceProvider.GetRequiredService<ITextEnrichmentService>();
            var searchService = scope.ServiceProvider.GetRequiredService<ISearchService>();
            var openAIService = scope.ServiceProvider.GetRequiredService<IOpenAIService>();
            var chunkingService = scope.ServiceProvider.GetRequiredService<ISelfHostedChunkingService>();

            // Load outbox item
            var outboxItem = await db.EnrichmentOutbox
                .FirstOrDefaultAsync(e => e.KnowledgeId == workItem.KnowledgeId &&
                    (e.Status == EnrichmentStatus.Pending || e.Status == EnrichmentStatus.Processing), ct);

            // SH_ENTERPRISE_RUNTIME_RESILIENCE §Rule 4: increment attempts counter
            // + mint activity-log row BEFORE the enrichment body runs. `finally`
            // stamps FinishedAt and Status so operators can distinguish "still
            // running" from "silently died".
            EnrichmentActivityLog? activity = null;
            if (outboxItem != null)
            {
                outboxItem.Status = EnrichmentStatus.Processing;
                outboxItem.AiProcessingAttempts += 1;
                outboxItem.StartedProcessingAt = DateTime.UtcNow;
                activity = new EnrichmentActivityLog
                {
                    TenantId = outboxItem.TenantId,
                    KnowledgeId = outboxItem.KnowledgeId,
                    AttemptNumber = outboxItem.AiProcessingAttempts,
                    StartedAt = DateTime.UtcNow,
                    Status = EnrichmentStatus.Processing,
                };
                db.EnrichmentActivityLogs.Add(activity);
                await db.SaveChangesAsync(ct);
            }

            // Load knowledge entity
            var knowledge = await db.KnowledgeItems
                .Include(k => k.Tags)
                .FirstOrDefaultAsync(k => k.Id == workItem.KnowledgeId, ct);

            if (knowledge == null)
            {
                _logger.LogWarning("Knowledge {Id} not found for enrichment", workItem.KnowledgeId);
                if (outboxItem != null)
                {
                    outboxItem.Status = EnrichmentStatus.Failed;
                    outboxItem.ErrorMessage = "Knowledge item not found";
                    outboxItem.ProcessedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
                return;
            }

            try
            {
                // 1. Title generation (if placeholder)
                if (IsPlaceholderTitle(knowledge.Title, knowledge.Content))
                {
                    var newTitle = await enrichmentService.GenerateTitleAsync(knowledge.Content, ct, workItem.TenantId);
                    if (newTitle != null)
                    {
                        knowledge.Title = newTitle;
                        _logger.LogDebug("Generated title for knowledge {Id}: {Title}", knowledge.Id, newTitle);
                    }
                }

                // 2. Gather attachment + comment text for enrichment
                var attachmentText = string.Empty;
                try
                {
                    attachmentText = await GetAllAttachmentTextAsync(db, knowledge.Id, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load attachment text for knowledge {Id}", knowledge.Id);
                }

                var contentForEnrichment = string.IsNullOrEmpty(attachmentText)
                    ? knowledge.Content
                    : $"{knowledge.Content}\n\n{attachmentText}";

                // 2b. Resolve author name for summary context
                string? authorName = null;
                if (knowledge.CreatedByUserId.HasValue)
                {
                    try
                    {
                        authorName = await db.Users
                            .Where(u => u.Id == knowledge.CreatedByUserId)
                            .Select(u => u.DisplayName ?? u.Username)
                            .FirstOrDefaultAsync(ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to resolve author name for knowledge {Id}", knowledge.Id);
                    }
                }

                // 2c. Prepend refinement guidance if present
                if (!string.IsNullOrWhiteSpace(knowledge.SummaryRefinementGuidance))
                {
                    contentForEnrichment = $"[REFINEMENT GUIDANCE: {knowledge.SummaryRefinementGuidance}]\n\n{contentForEnrichment}";
                    _logger.LogDebug("Applied summary refinement guidance for knowledge {Id}", knowledge.Id);
                }

                // 3. Summarize (with attachment context, createdAt, authorName)
                // Scale summary depth with content length — always summarize, no word-count short-circuits
                var wordCount = contentForEnrichment.Split(Array.Empty<char>(), StringSplitOptions.RemoveEmptyEntries).Length;
                var maxWords = wordCount switch
                {
                    <= 50 => 100,        // Brief note → short summary
                    <= 200 => 300,       // Short content → moderate summary
                    <= 1000 => 1000,     // Medium content → detailed summary
                    <= 5000 => 2500,     // Long content → comprehensive summary
                    _ => 5000            // Very long technical docs → thorough summary
                };

                // Use concrete TextEnrichmentService overload if available (for createdAt/authorName)
                if (enrichmentService is TextEnrichmentService concreteService)
                {
                    var summary = await concreteService.SummarizeAsync(
                        contentForEnrichment, maxWords, ct, workItem.TenantId,
                        knowledge.CreatedAt, authorName);
                    if (summary != null)
                    {
                        knowledge.Summary = summary;
                        _logger.LogDebug("Generated summary for knowledge {Id} (maxWords={MaxWords}, contentWords={WordCount})", knowledge.Id, maxWords, wordCount);
                    }
                }
                else
                {
                    var summary = await enrichmentService.SummarizeAsync(contentForEnrichment, maxWords, ct, workItem.TenantId);
                    if (summary != null)
                    {
                        knowledge.Summary = summary;
                        _logger.LogDebug("Generated summary for knowledge {Id} (maxWords={MaxWords})", knowledge.Id, maxWords);
                    }
                }

                // 3.5. Generate BriefSummary
                try
                {
                    var briefSummary = await enrichmentService.GenerateBriefSummaryAsync(contentForEnrichment, ct, workItem.TenantId);
                    knowledge.BriefSummary = briefSummary;
                    _logger.LogDebug("Generated brief summary for knowledge {Id}: {HasValue}", knowledge.Id, briefSummary != null);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to generate brief summary for knowledge {Id}", knowledge.Id);
                    // Non-blocking: BriefSummary remains null
                }

                // 4. Extract tags (with attachment context)
                var tags = await enrichmentService.ExtractTagsAsync(knowledge.Title, contentForEnrichment, 5, ct, workItem.TenantId);
                if (tags.Count > 0)
                {
                    await LinkTagsAsync(db, knowledge, tags, workItem.TenantId, ct);
                    _logger.LogDebug("Extracted {Count} tags for knowledge {Id}", tags.Count, knowledge.Id);
                }

                knowledge.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);

                // Re-index in search (non-critical)
                try
                {
                    await ReindexAsync(db, knowledge, searchService, openAIService, chunkingService, enrichmentService, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to re-index knowledge {Id} after enrichment", knowledge.Id);
                }

                // Mark completed
                if (outboxItem != null)
                {
                    outboxItem.Status = EnrichmentStatus.Completed;
                    outboxItem.ProcessedAt = DateTime.UtcNow;
                    if (activity != null)
                    {
                        activity.Status = EnrichmentStatus.Completed;
                        activity.FinishedAt = DateTime.UtcNow;
                    }
                    await db.SaveChangesAsync(ct);
                }

                _logger.LogInformation("Enrichment completed for knowledge {Id}", knowledge.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Enrichment processing error for knowledge {Id}", workItem.KnowledgeId);
                if (outboxItem != null)
                {
                    outboxItem.RetryCount++;
                    outboxItem.ErrorMessage = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                    if (outboxItem.RetryCount >= outboxItem.MaxRetries)
                    {
                        outboxItem.Status = EnrichmentStatus.Failed;
                    }
                    else
                    {
                        outboxItem.Status = EnrichmentStatus.Pending;
                        outboxItem.NextRetryAt = DateTime.UtcNow + TimeSpan.FromMinutes(Math.Pow(2, outboxItem.RetryCount));
                    }
                    if (activity != null)
                    {
                        activity.Status = EnrichmentStatus.Failed;
                        activity.FinishedAt = DateTime.UtcNow;
                        activity.ErrorMessage = outboxItem.ErrorMessage;
                    }
                    await db.SaveChangesAsync(ct);
                }
            }
        }
        finally
        {
            TenantContext.CurrentTenantId = null;
        }
    }

    internal async Task RecoveryTimerAsync(CancellationToken stoppingToken)
    {
        // Short initial delay to let DI and DB context initialize
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();

                    var now = DateTime.UtcNow;
                    var stuckThreshold = now.AddMinutes(-5);

                    var items = await db.EnrichmentOutbox
                        .Where(e =>
                            (e.Status == EnrichmentStatus.Pending && (e.NextRetryAt == null || e.NextRetryAt <= now)) ||
                            (e.Status == EnrichmentStatus.Processing && e.CreatedAt < stuckThreshold))
                        .Take(50)
                        .ToListAsync(stoppingToken);

                    foreach (var item in items)
                    {
                        try
                        {
                            await _channel.Writer.WriteAsync(
                                new EnrichmentWorkItem(item.KnowledgeId, item.TenantId), stoppingToken);
                        }
                        catch (ChannelClosedException)
                        {
                            break; // Service shutting down
                        }
                    }

                    if (items.Count > 0)
                        _logger.LogInformation("Recovery: re-enqueued {Count} outbox items", items.Count);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Recovery timer error");
                }

                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown
        }
    }

    internal static bool IsPlaceholderTitle(string? title, string? content)
    {
        if (string.IsNullOrWhiteSpace(title))
            return true;

        var trimmedTitle = title.Trim();

        if (PlaceholderTitles.Contains(trimmedTitle))
            return true;

        // Any content-derived title: strip trailing ellipsis, then check whether
        // the title is a prefix of the content. Catches legacy items created under
        // the `content[..80]+"..."` and `content[..100]` heuristics, plus any
        // title that the user left equal to (a prefix of) their content body.
        if (!string.IsNullOrWhiteSpace(content))
        {
            var titleCore = trimmedTitle.TrimEnd('.', '\u2026').Trim();
            var contentCore = content.Trim();
            if (titleCore.Length > 0 && titleCore.Length < contentCore.Length &&
                contentCore.StartsWith(titleCore, StringComparison.Ordinal))
                return true;

            // Exact match (short content where the title IS the whole body).
            if (trimmedTitle == contentCore)
                return true;
        }

        return false;
    }

    private static async Task LinkTagsAsync(SelfHostedDbContext db, Knowledge knowledge,
        List<string> tagNames, Guid tenantId, CancellationToken ct)
    {
        var existingTags = await db.Tags
            .Where(t => tagNames.Contains(t.Name))
            .ToListAsync(ct);

        foreach (var tagName in tagNames)
        {
            // Skip if already linked
            if (knowledge.Tags.Any(t => string.Equals(t.Name, tagName, StringComparison.OrdinalIgnoreCase)))
                continue;

            var tag = existingTags.FirstOrDefault(t => string.Equals(t.Name, tagName, StringComparison.OrdinalIgnoreCase));
            if (tag == null)
            {
                tag = new Tag { TenantId = tenantId, Name = tagName };
                db.Tags.Add(tag);
            }
            knowledge.Tags.Add(tag);
        }
    }

    public static async Task<string> GetAllAttachmentTextAsync(
        SelfHostedDbContext db, Guid knowledgeId, CancellationToken ct)
        => await AttachmentContextBuilder.BuildKnowledgeAttachmentContextAsync(
            db,
            knowledgeId,
            AttachmentContextRenderMode.Enrichment,
            50_000,
            ct);

    internal static string? BuildAttachmentSection(
        string fileName, string? contentType, string? extractedText,
        string? visionDescription, string? visionTagsJson, string? visionObjectsJson,
        string? visionExtractedText, int textExtractionStatus,
        bool isCommentAttachment)
    {
        var isImage = contentType != null &&
            contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        var hasVisionData = !string.IsNullOrEmpty(visionDescription) ||
            !string.IsNullOrEmpty(visionExtractedText);

        var prefix = isCommentAttachment ? "Comment " : "";

        // Image with structured vision fields
        if (isImage && hasVisionData)
        {
            var lines = new List<string>
            {
                $"--- {prefix}Image Analysis: {fileName} ---"
            };

            if (!string.IsNullOrEmpty(visionDescription))
                lines.Add($"Caption: {visionDescription}");

            if (!string.IsNullOrEmpty(visionObjectsJson))
            {
                var objects = ParseJsonArray(visionObjectsJson);
                if (objects.Count > 0)
                    lines.Add($"Objects detected: {string.Join(", ", objects)}");
            }

            if (!string.IsNullOrEmpty(visionTagsJson))
            {
                var tags = ParseJsonArray(visionTagsJson);
                if (tags.Count > 0)
                    lines.Add($"Tags: {string.Join(", ", tags)}");
            }

            if (!string.IsNullOrEmpty(visionExtractedText))
            {
                lines.Add("Text from image:");
                lines.Add(visionExtractedText);
            }

            return string.Join("\n", lines);
        }

        // Image or document with ExtractedText (GPT-4V fallback for images, or standard for docs)
        if (!string.IsNullOrEmpty(extractedText))
        {
            return $"--- {prefix}Attachment: {fileName} ---\n{extractedText}";
        }

        // Image with no data at all — placeholder
        if (isImage)
        {
            return $"--- {prefix}Attachment: {fileName} ---\n[Image: {fileName} — not yet analyzed]";
        }

        // Document with TextExtractionStatus=Failed
        if (textExtractionStatus == 3) // Failed
        {
            return $"[Document: {fileName} — extraction failed]";
        }

        // Non-image with no extracted text — skip
        return null;
    }

    internal static List<string> ParseJsonArray(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<string>();

        try
        {
            var document = JsonDocument.Parse(json);
            var results = new List<string>();

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    var value = element.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        results.Add(value);
                }
                else if (element.ValueKind == JsonValueKind.Object)
                {
                    if (element.TryGetProperty("name", out var nameProp))
                    {
                        var value = nameProp.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                            results.Add(value);
                    }
                }
            }

            return results;
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }

    internal static async Task ReindexAsync(SelfHostedDbContext db, Knowledge knowledge,
        ISearchService searchService, IOpenAIService openAIService,
        ISelfHostedChunkingService chunkingService, ITextEnrichmentService enrichmentService,
        CancellationToken ct)
    {
        var primaryKv = await db.KnowledgeVaults
            .Include(kv => kv.Vault)
            .Where(kv => kv.KnowledgeId == knowledge.Id)
            .OrderByDescending(kv => kv.IsPrimary)
            .FirstOrDefaultAsync(ct);

        string? vaultName = primaryKv?.Vault?.Name;
        Guid? vaultId = primaryKv?.VaultId;

        List<Guid>? ancestorVaultIds = null;
        if (vaultId.HasValue)
        {
            ancestorVaultIds = await db.VaultAncestors
                .Where(va => va.DescendantVaultId == vaultId.Value)
                .Select(va => va.AncestorVaultId)
                .ToListAsync(ct);
        }

        var topicName = await db.KnowledgeItems
            .Where(k => k.Id == knowledge.Id)
            .Select(k => k.Topic != null ? k.Topic.Name : null)
            .FirstOrDefaultAsync(ct);

        var currentTags = knowledge.Tags.Select(t => t.Name).ToList();
        var tagsString = currentTags.Count > 0 ? string.Join(", ", currentTags) : null;

        // Load existing chunks for embedding cache and delta comparison
        var existingChunks = await db.ContentChunks
            .Where(c => c.KnowledgeId == knowledge.Id)
            .ToListAsync(ct);
        var existingHashMap = new Dictionary<string, ContentChunk>();
        foreach (var c in existingChunks.Where(c => c.EmbeddingVectorJson != null))
        {
            existingHashMap.TryAdd(c.ContentHash, c);
        }

        // Gather attachment text for indexing
        var attachmentText = string.Empty;
        try
        {
            attachmentText = await GetAllAttachmentTextAsync(db, knowledge.Id, ct);
        }
        catch (Exception ex)
        {
            // Non-critical: proceed with knowledge content only
            _ = ex;
        }

        var contentForChunking = string.IsNullOrEmpty(attachmentText)
            ? knowledge.Content
            : $"{knowledge.Content}\n\n{attachmentText}";

        await searchService.DeleteDocumentWithChunksAsync(knowledge.Id,
            existingChunks.Select(c => c.Position), ct);

        var strategy = chunkingService.DetermineStrategy(knowledge.Type);
        var chunks = chunkingService.ChunkWithContext(
            contentForChunking, knowledge.Title, knowledge.Summary,
            currentTags, strategy);

        // Generate chunk contexts (contextual retrieval)
        IList<string?> chunkContexts;
        try
        {
            var chunkTuples = chunks.Select(c => (c.Content, c.Position)).ToList();
            chunkContexts = await enrichmentService.GenerateChunkContextsAsync(
                knowledge.Title, knowledge.Summary, chunkTuples, ct);
        }
        catch (Exception)
        {
            // Non-blocking: proceed without contextual retrieval
            chunkContexts = new string?[chunks.Count];
        }

        // Resolve BriefSummary fallback
        var briefSummary = knowledge.BriefSummary
            ?? TextEnrichmentService.GetFallbackBriefSummary(knowledge.Summary);

        // Delta chunking: compare new chunk hashes against existing
        var newHashes = chunks.Select(c => ContentHasher.Hash(c.Content)).ToList();
        var existingHashSet = new HashSet<string>(existingHashMap.Keys);
        var matchCount = newHashes.Count(h => existingHashSet.Contains(h));
        var matchRatio = chunks.Count > 0 ? (double)matchCount / chunks.Count : 0;
        var useDelta = matchRatio >= 0.75;

        var chunkData = new Dictionary<int, (string Hash, string? EmbeddingJson, string? ContextSummary, bool IsContextual)>();

        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var hash = newHashes[i];
            var contextSummary = i < chunkContexts.Count ? chunkContexts[i] : null;

            // Build embedding text with new prefix format
            var prefix = TextEnrichmentService.BuildEmbeddingPrefix(
                knowledge.Title, briefSummary, contextSummary, tagsString);
            var embeddingText = $"{prefix}\n\n{chunk.Content}";

            float[]? embedding;
            string? embeddingJson;
            bool isContextual = contextSummary != null;

            // Delta chunking: reuse existing embedding for unchanged chunks
            // Force re-embed if existing chunk lacks contextual embedding (upgrade path)
            if (useDelta && existingHashMap.TryGetValue(hash, out var existingChunk)
                && existingChunk.IsContextualEmbedding)
            {
                // Preserve existing embedding and context if chunk content unchanged
                embeddingJson = existingChunk.EmbeddingVectorJson;
                contextSummary = existingChunk.ContextSummary ?? contextSummary;
                isContextual = existingChunk.IsContextualEmbedding || isContextual;
                try
                {
                    embedding = JsonSerializer.Deserialize<float[]>(embeddingJson!);
                }
                catch (JsonException)
                {
                    embedding = await openAIService.GenerateEmbeddingAsync(embeddingText, ct);
                    embeddingJson = embedding != null ? JsonSerializer.Serialize(embedding) : null;
                }
            }
            else
            {
                embedding = await openAIService.GenerateEmbeddingAsync(embeddingText, ct);
                embeddingJson = embedding != null ? JsonSerializer.Serialize(embedding) : null;
            }

            chunkData[chunk.Position] = (hash, embeddingJson, contextSummary, isContextual);

            // FEAT_SelfHostedTemporalAwareness: thread the entity's real
            // dates so re-index operations don't overwrite the index
            // createdAt with the re-index time.
            await searchService.IndexDocumentAsync(
                knowledge.Id, knowledge.Title, chunk.Content, knowledge.Summary,
                vaultName, vaultId, ancestorVaultIds, topicName,
                currentTags, knowledge.Type.ToString(),
                knowledge.FilePath, embedding,
                chunkIndex: chunks.Count > 1 ? chunk.Position : null,
                knowledgeCreatedAt: knowledge.CreatedAt,
                knowledgeUpdatedAt: knowledge.UpdatedAt,
                cancellationToken: ct);
        }

        // Replace persisted chunks with contextual data
        try
        {
            db.ContentChunks.RemoveRange(existingChunks);
            foreach (var chunk in chunks)
            {
                var (hash, embeddingJson, contextSummary, isContextual) = chunkData[chunk.Position];
                db.ContentChunks.Add(new ContentChunk
                {
                    TenantId = knowledge.TenantId,
                    KnowledgeId = knowledge.Id,
                    Position = chunk.Position,
                    Content = chunk.Content,
                    ContentHash = hash,
                    EmbeddingVectorJson = embeddingJson,
                    ContextSummary = contextSummary,
                    IsContextualEmbedding = isContextual,
                    EmbeddedAt = embeddingJson != null ? DateTime.UtcNow : null
                });
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception)
        {
            // Chunk persistence is optional; indexing already completed
        }

        knowledge.IsIndexed = true;
        knowledge.IndexedAt = DateTime.UtcNow;
    }
}
