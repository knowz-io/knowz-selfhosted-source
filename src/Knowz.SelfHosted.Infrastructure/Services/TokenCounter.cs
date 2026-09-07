using Microsoft.ML.Tokenizers;

namespace Knowz.SelfHosted.Infrastructure.Services;

internal static class TokenCounter
{
    private static readonly Lazy<Tokenizer> TokenizerInstance = new(
        () => TiktokenTokenizer.CreateForEncoding("cl100k_base"),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static int CountTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        return TokenizerInstance.Value.CountTokens(text);
    }

    public static int EstimateTokensFromChars(int chars)
    {
        return chars / 4;
    }
}
