using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Infrastructure.Services;

public class NoOpTextEnrichmentService : ITextEnrichmentService
{
    private readonly ILogger<NoOpTextEnrichmentService> _logger;

    public NoOpTextEnrichmentService(ILogger<NoOpTextEnrichmentService> logger)
        => _logger = logger;

    public Task<string?> GenerateTitleAsync(string content, CancellationToken ct = default, Guid? tenantId = null)
    {
        _logger.LogDebug("OpenAI not configured. Title generation unavailable.");
        return Task.FromResult<string?>(null);
    }

    public Task<string?> SummarizeAsync(string content, int maxWords = 100, CancellationToken ct = default, Guid? tenantId = null)
    {
        _logger.LogDebug("OpenAI not configured. Summarization unavailable.");
        return Task.FromResult<string?>(null);
    }

    public Task<List<string>> ExtractTagsAsync(string title, string content, int maxTags = 5, CancellationToken ct = default, Guid? tenantId = null)
    {
        _logger.LogDebug("OpenAI not configured. Tag extraction unavailable.");
        return Task.FromResult(new List<string>());
    }

    public Task<string?> GenerateBriefSummaryAsync(string content, CancellationToken ct = default, Guid? tenantId = null)
    {
        _logger.LogDebug("OpenAI not configured. Brief summary generation unavailable.");
        return Task.FromResult<string?>(null);
    }

    public Task<IList<string?>> GenerateChunkContextsAsync(
        string documentTitle, string? documentSummary,
        IList<(string Content, int Position)> chunks,
        CancellationToken ct = default)
    {
        _logger.LogDebug("OpenAI not configured. Chunk context generation unavailable.");
        return Task.FromResult<IList<string?>>(Array.Empty<string?>());
    }
}
