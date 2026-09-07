namespace Knowz.SelfHosted.Infrastructure.Services;

using Knowz.Core.Interfaces;
using Knowz.Core.Models;

/// <summary>
/// Default adapter that exposes <see cref="IVectorStore"/> over the existing
/// <see cref="ISearchService"/> tier (Platform / Azure / Local / Database / NoOp). Implements
/// <c>SelfHostedAbstractionSeams</c> spec — pure delegating wrap, zero behavior change.
/// </summary>
/// <remarks>
/// The adapter maps the narrow <see cref="IVectorStore"/> contract to the richer
/// <see cref="ISearchService"/> surface. <see cref="SearchAsync"/> uses
/// <see cref="ISearchService.HybridSearchAsync"/> with a stub query string and the supplied
/// vector — for callers wanting pure vector search, prefer a future
/// <c>PostgresVectorSearchService</c> direct implementation (see
/// <c>Planned_SelfHostedPgvectorAdapter</c>). Metadata in <see cref="VectorSearchResult"/>
/// contains the title, content snippet, vault ID, and (when available) the matching tags.
/// </remarks>
internal sealed class VectorStoreAdapter : IVectorStore
{
    private readonly ISearchService _inner;

    public VectorStoreAdapter(ISearchService inner)
    {
        _inner = inner;
    }

    public Task IndexEmbeddingAsync(
        Guid documentId,
        float[] vector,
        IDictionary<string, object> metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vector);
        ArgumentNullException.ThrowIfNull(metadata);

        string title = ReadString(metadata, "title") ?? string.Empty;
        string content = ReadString(metadata, "content") ?? string.Empty;
        string? summary = ReadString(metadata, "summary");
        string? vaultName = ReadString(metadata, "vaultName");
        Guid? vaultId = ReadGuid(metadata, "vaultId");
        var ancestorVaultIds = metadata.TryGetValue("ancestorVaultIds", out var avo) && avo is IEnumerable<Guid> avg
            ? avg.ToList()
            : null;
        string? topicName = ReadString(metadata, "topicName");
        var tags = metadata.TryGetValue("tags", out var to) && to is IEnumerable<string> ts ? ts : null;
        string? knowledgeType = ReadString(metadata, "knowledgeType");
        string? filePath = ReadString(metadata, "filePath");
        int? chunkIndex = metadata.TryGetValue("chunkIndex", out var co) && co is int ci ? ci : (int?)null;
        DateTime? createdAt = metadata.TryGetValue("createdAt", out var cao) && cao is DateTime cad ? cad : (DateTime?)null;
        DateTime? updatedAt = metadata.TryGetValue("updatedAt", out var uao) && uao is DateTime uad ? uad : (DateTime?)null;

        return _inner.IndexDocumentAsync(
            documentId,
            title,
            content,
            summary,
            vaultName,
            vaultId,
            ancestorVaultIds,
            topicName,
            tags,
            knowledgeType,
            filePath,
            vector,
            chunkIndex,
            createdAt,
            updatedAt,
            cancellationToken);
    }

    public Task DeleteByDocumentIdAsync(Guid documentId, CancellationToken cancellationToken = default)
        => _inner.DeleteDocumentAsync(documentId, cancellationToken);

    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        float[] queryVector,
        int topK,
        IDictionary<string, object>? filters = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queryVector);

        Guid? vaultId = filters is not null ? ReadGuid(filters, "vaultId") : null;
        bool includeDescendants = filters is not null && filters.TryGetValue("includeDescendants", out var ido) && ido is bool idb ? idb : true;
        var tags = filters is not null && filters.TryGetValue("tags", out var to) && to is IEnumerable<string> ts ? ts : null;
        bool requireAllTags = filters is not null && filters.TryGetValue("requireAllTags", out var rato) && rato is bool ratb ? ratb : false;

        var results = await _inner.HybridSearchAsync(
            query: string.Empty,
            queryEmbedding: queryVector,
            vaultId: vaultId,
            includeDescendants: includeDescendants,
            tags: tags,
            requireAllTags: requireAllTags,
            startDate: null,
            endDate: null,
            maxResults: topK,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return results.Select(MapToVectorResult).ToList();
    }

    private static VectorSearchResult MapToVectorResult(SearchResultItem r)
    {
        IDictionary<string, object> md = new Dictionary<string, object>();
        if (!string.IsNullOrEmpty(r.Title)) md["title"] = r.Title;
        if (!string.IsNullOrEmpty(r.Content)) md["content"] = r.Content;
        if (!string.IsNullOrEmpty(r.Summary)) md["summary"] = r.Summary!;
        if (!string.IsNullOrEmpty(r.VaultName)) md["vaultName"] = r.VaultName!;
        if (!string.IsNullOrEmpty(r.TopicName)) md["topicName"] = r.TopicName!;
        if (r.Tags is { Count: > 0 }) md["tags"] = r.Tags;
        if (r.CreatedAt != default) md["createdAt"] = r.CreatedAt;
        if (r.UpdatedAt.HasValue) md["updatedAt"] = r.UpdatedAt.Value;
        if (!string.IsNullOrEmpty(r.KnowledgeType)) md["knowledgeType"] = r.KnowledgeType!;

        return new VectorSearchResult(r.KnowledgeId, (float)r.Score, md);
    }

    private static string? ReadString(IDictionary<string, object> md, string key)
        => md.TryGetValue(key, out var v) && v is string s ? s : null;

    private static Guid? ReadGuid(IDictionary<string, object> md, string key)
        => md.TryGetValue(key, out var v) && v is Guid g ? g : (Guid?)null;
}
