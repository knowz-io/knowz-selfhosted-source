namespace Knowz.Core.Enums;

public enum SelfHostedChunkingStrategy
{
    Recursive = 0,  // General-purpose, separator-based
    Prose = 1,      // Paragraph-aware with sentence splitting
    Sentence = 2,   // Sentence-boundary splitting
    Markdown = 3    // Heading-aware splitting
}
