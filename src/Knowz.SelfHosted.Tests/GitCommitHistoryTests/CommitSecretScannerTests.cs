using Knowz.SelfHosted.Application.Services.GitCommitHistory;

namespace Knowz.SelfHosted.Tests.GitCommitHistoryTests;

/// <summary>
/// Unit tests for <see cref="CommitSecretScanner"/> — the selfhosted CRIT-2 mitigation
/// for secret leakage on the commit-history path.
///
/// Mirror of platform <c>CommitSecretScannerTests</c>. Selfhosted has no Service Bus / Function
/// layer, so the scanner runs inline from <c>GitCommitHistoryService</c> and
/// <c>CommitElaborationPromptBuilder</c> — behaviour is identical.
///
/// WorkGroupID: kc-feat-git-commit-knowledge-sync-20260410
/// NodeID: SelfHostedCommitHistoryParity (NODE-4)
/// </summary>
public class CommitSecretScannerTests
{
    private readonly CommitSecretScanner _scanner = new();

    [Fact]
    public void Scan_EmptyInput_ReturnsNoMatch()
    {
        var result = _scanner.Scan(string.Empty);
        Assert.False(result.HasMatches);
        Assert.Equal(string.Empty, result.RedactedText);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public void Scan_NullInput_ReturnsNoMatch()
    {
        var result = _scanner.Scan(null);
        Assert.False(result.HasMatches);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public void Scan_PlainText_ReturnsNoMatch()
    {
        var result = _scanner.Scan("This is a regular commit message that does nothing sensitive.");
        Assert.False(result.HasMatches);
    }

    [Fact]
    public void Scan_AwsAccessKey_IsRedacted()
    {
        var input = "Leaked AKIAIOSFODNN7EXAMPLE in commit";
        var result = _scanner.Scan(input);

        Assert.True(result.HasMatches);
        Assert.Contains("[REDACTED:aws-access-key]", result.RedactedText);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", result.RedactedText);
        Assert.Contains(result.Matches, m => m.PatternId == "aws-access-key");
    }

    [Fact]
    public void Scan_OpenAiKey_IsRedacted()
    {
        var input = "api_token = sk-proj-abcdefghijklmnopqrstuvwxyz1234567890";
        var result = _scanner.Scan(input);

        Assert.True(result.HasMatches);
        Assert.DoesNotContain("sk-proj-abcdefghijklmnopqrstuvwxyz1234567890", result.RedactedText);
        // May match as openai-key OR generic-secret-assignment; accept either
        Assert.Contains(result.Matches,
            m => m.PatternId == "openai-key" || m.PatternId == "generic-secret-assignment");
    }

    [Fact]
    public void Scan_GitHubPat_IsRedacted()
    {
        var input = "token: ghp_1234567890abcdefghijklmnopqrstuvwxyz";
        var result = _scanner.Scan(input);

        Assert.True(result.HasMatches);
        Assert.DoesNotContain("ghp_1234567890abcdefghijklmnopqrstuvwxyz", result.RedactedText);
    }

    [Fact]
    public void Scan_PemBlock_IsRedacted()
    {
        var input = @"-----BEGIN RSA PRIVATE KEY-----
MIIEpAIBAAKCAQEAexample
-----END RSA PRIVATE KEY-----";
        var result = _scanner.Scan(input);

        Assert.True(result.HasMatches);
        Assert.Contains("[REDACTED:pem-block]", result.RedactedText);
        Assert.DoesNotContain("MIIEpAIBAAKCAQEAexample", result.RedactedText);
    }

    [Fact]
    public void Scan_MultipleSecretsInOneString_RedactsAll()
    {
        var input = "aws=AKIAIOSFODNN7EXAMPLE token=ghp_1234567890abcdefghijklmnopqrstuvwxyz";
        var result = _scanner.Scan(input);

        Assert.True(result.HasMatches);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", result.RedactedText);
        Assert.DoesNotContain("ghp_1234567890abcdefghijklmnopqrstuvwxyz", result.RedactedText);
        Assert.True(result.Matches.Count >= 2);
    }

    [Fact]
    public void Scan_MatchesCarryOffsetsAndPatternIdOnly()
    {
        var input = "leak AKIAIOSFODNN7EXAMPLE here";
        var result = _scanner.Scan(input);

        Assert.True(result.HasMatches);
        var match = result.Matches.First(m => m.PatternId == "aws-access-key");
        Assert.True(match.Start >= 0);
        Assert.True(match.End > match.Start);
        // The SecretMatch record does NOT carry the matched text — this is the log-poisoning guard.
        // Only PatternId / Start / End are public.
    }
}
