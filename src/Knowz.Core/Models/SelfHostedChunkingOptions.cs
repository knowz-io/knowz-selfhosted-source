namespace Knowz.Core.Models;

public class SelfHostedChunkingOptions
{
    public int MaxChars { get; set; } = 4000;
    public int OverlapChars { get; set; } = 400;
    public int MinChars { get; set; } = 400;
    public int MaxTokens { get; set; } = 1024;
    public int OverlapTokens { get; set; } = 128;
    public int MinTokens { get; set; } = 25;
}
