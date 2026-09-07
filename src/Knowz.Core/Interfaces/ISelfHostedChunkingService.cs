using Knowz.Core.Enums;
using Knowz.Core.Models;

namespace Knowz.Core.Interfaces;

public interface ISelfHostedChunkingService
{
    List<SelfHostedChunkResult> ChunkContent(
        string content,
        SelfHostedChunkingStrategy strategy,
        SelfHostedChunkingOptions? options = null);

    List<SelfHostedChunkResult> ChunkWithContext(
        string content,
        string title,
        string? summary = null,
        IEnumerable<string>? tags = null,
        SelfHostedChunkingStrategy strategy = SelfHostedChunkingStrategy.Prose,
        SelfHostedChunkingOptions? options = null);

    SelfHostedChunkingStrategy DetermineStrategy(KnowledgeType knowledgeType);
}
