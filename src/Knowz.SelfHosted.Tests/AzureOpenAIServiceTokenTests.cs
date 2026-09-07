using Knowz.SelfHosted.Infrastructure.Services;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// Verifies that AzureOpenAIService token budget constants are correct.
/// The default (non-research) chat max output tokens should be 750 for concise responses.
/// Research mode should be 4000.
/// </summary>
public class AzureOpenAIServiceTokenTests
{
    [Fact]
    public void Should_HaveDefaultMaxOutputTokens750_ForNonResearchChat()
    {
        // The default max output tokens for non-research chat should be 750
        // to produce concise responses that respect the detail-level prefix.
        Assert.Equal(750, AzureOpenAIService.DefaultMaxOutputTokens);
    }

    [Fact]
    public void Should_HaveResearchMaxOutputTokens4000_ForResearchChat()
    {
        // Research mode should have a generous token budget.
        Assert.Equal(4000, AzureOpenAIService.ResearchMaxOutputTokens);
    }

    [Fact]
    public void Should_HaveContextTokenBudgets_AtExpectedValues()
    {
        // Context token budgets for building search result context
        Assert.Equal(4000, AzureOpenAIService.DefaultTokenBudget);
        Assert.Equal(8000, AzureOpenAIService.ResearchTokenBudget);
    }
}
