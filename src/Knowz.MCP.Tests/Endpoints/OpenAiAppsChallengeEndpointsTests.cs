using FluentAssertions;
using Knowz.MCP.Endpoints;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Knowz.MCP.Tests.Endpoints;

public class OpenAiAppsChallengeEndpointsTests
{
    [Fact]
    public void ResolveChallengeToken_Unset_ReturnsNull()
    {
        var config = new ConfigurationBuilder().Build();

        OpenAiAppsChallengeEndpoints.ResolveChallengeToken(config).Should().BeNull();
    }

    [Fact]
    public void ResolveChallengeToken_Empty_ReturnsNull()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OpenAiAppsChallengeEndpoints.EnvVarName] = "   ",
            })
            .Build();

        OpenAiAppsChallengeEndpoints.ResolveChallengeToken(config).Should().BeNull();
    }

    [Fact]
    public void ResolveChallengeToken_FromEnvKey_ReturnsExactToken()
    {
        const string token = "portal-issued-challenge-token-abc";
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OpenAiAppsChallengeEndpoints.EnvVarName] = token,
            })
            .Build();

        OpenAiAppsChallengeEndpoints.ResolveChallengeToken(config).Should().Be(token);
    }

    [Fact]
    public void ResolveChallengeToken_FromHierarchicalKey_ReturnsExactToken()
    {
        const string token = "hierarchical-token-xyz";
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OpenAiAppsChallengeEndpoints.ConfigKey] = token,
            })
            .Build();

        OpenAiAppsChallengeEndpoints.ResolveChallengeToken(config).Should().Be(token);
    }

    [Fact]
    public void ResolveChallengeToken_EnvKeyPreferredOverHierarchical()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OpenAiAppsChallengeEndpoints.EnvVarName] = "from-env",
                [OpenAiAppsChallengeEndpoints.ConfigKey] = "from-config",
            })
            .Build();

        OpenAiAppsChallengeEndpoints.ResolveChallengeToken(config).Should().Be("from-env");
    }

    [Fact]
    public void ChallengePath_IsWellKnownPublicRoute()
    {
        OpenAiAppsChallengeEndpoints.Path.Should().Be("/.well-known/openai-apps-challenge");
        OpenAiAppsChallengeEndpoints.Path.Should().StartWith("/.well-known");
    }
}
