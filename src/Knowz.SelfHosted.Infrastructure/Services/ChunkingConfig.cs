namespace Knowz.SelfHosted.Infrastructure.Services;

public class ChunkingConfig
{
    public const string SectionName = "Chunking";

    public bool MarkdownAware { get; set; } = true;
    public bool UseTokenizer { get; set; } = true;
    public int MaxTokens { get; set; } = 7500;
    public int OverlapTokens { get; set; } = 200;
    public int MinTokens { get; set; } = 100;
}
