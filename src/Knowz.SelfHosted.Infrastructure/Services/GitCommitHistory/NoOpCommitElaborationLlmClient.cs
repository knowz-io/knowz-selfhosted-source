using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Infrastructure.Services.GitCommitHistory;

/// <summary>
/// No-op LLM client used when <c>KnowzPlatform:Enabled</c> is false (or the platform
/// base URL / API key are missing). <see cref="IsAvailable"/> returns false, so
/// <c>GitCommitHistoryService</c> short-circuits to metadata-only commit stubs tagged
/// <c>elaborationSkipped = "platform-ai-unavailable"</c>. Matches HIGH-3 fallback.
///
/// WorkGroupID: kc-feat-git-commit-knowledge-sync-20260410
/// NodeID: SelfHostedCommitHistoryParity (NODE-4)
/// </summary>
public sealed class NoOpCommitElaborationLlmClient : ICommitElaborationLlmClient
{
    private readonly ILogger<NoOpCommitElaborationLlmClient> _logger;

    public NoOpCommitElaborationLlmClient(ILogger<NoOpCommitElaborationLlmClient> logger)
    {
        _logger = logger;
    }

    public bool IsAvailable => false;

    public Task<string?> ElaborateAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "NoOpCommitElaborationLlmClient.ElaborateAsync called — returning null (platform AI unavailable)");
        return Task.FromResult<string?>(null);
    }
}
