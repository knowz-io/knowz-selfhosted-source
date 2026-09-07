using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Knowz.SelfHosted.Infrastructure.Extensions;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class NoOpAttachmentAIProviderTests
{
    [Fact]
    public void Should_HaveProviderName_NoOp()
    {
        var provider = new NoOpAttachmentAIProvider();
        Assert.Equal("NoOp", provider.ProviderName);
    }

    [Fact]
    public async Task Should_AnalyzeImage_ReturnNotAvailable()
    {
        var provider = new NoOpAttachmentAIProvider();

        var result = await provider.AnalyzeImageAsync(
            new byte[] { 1, 2, 3 }, "image/png");

        Assert.False(result.Success);
        Assert.True(result.NotAvailable);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("not configured", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Should_ExtractDocument_ReturnNotAvailable()
    {
        var provider = new NoOpAttachmentAIProvider();

        var result = await provider.ExtractDocumentAsync(
            new byte[] { 1, 2, 3 }, "application/pdf");

        Assert.False(result.Success);
        Assert.True(result.NotAvailable);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("not configured", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }
}

public class PlatformAttachmentAIProviderTests
{
    private readonly ILogger<PlatformAttachmentAIProvider> _logger =
        Substitute.For<ILogger<PlatformAttachmentAIProvider>>();

    private static PlatformAttachmentAIProvider CreateProvider(
        HttpMessageHandler handler,
        ILogger<PlatformAttachmentAIProvider> logger)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://platform.test") };
        factory.CreateClient("KnowzPlatformClient").Returns(client);
        return new PlatformAttachmentAIProvider(factory, logger);
    }

    [Fact]
    public void Should_HaveProviderName_Platform()
    {
        var handler = new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var provider = CreateProvider(handler, _logger);
        Assert.Equal("Platform", provider.ProviderName);
    }

    [Fact]
    public async Task Should_AnalyzeImage_PostToVisionEndpoint()
    {
        // Match the REAL platform VisionResponse contract:
        // Description (not Caption), Tags as VisionTag[] (not string[]), no Objects field
        var responsePayload = new
        {
            success = true,
            data = new
            {
                description = "A cat on a mat",
                extractedText = "Hello World",
                tags = new[]
                {
                    new { name = "cat", confidence = 0.95 },
                    new { name = "mat", confidence = 0.85 }
                },
                faces = (string[]?)null
            }
        };
        var handler = new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(responsePayload),
                Encoding.UTF8, "application/json")
        });

        var provider = CreateProvider(handler, _logger);
        var result = await provider.AnalyzeImageAsync(
            new byte[] { 0x89, 0x50 }, "image/png");

        Assert.True(result.Success);
        Assert.Equal("A cat on a mat", result.Caption); // mapped from Description
        Assert.Equal("Hello World", result.ExtractedText);
        Assert.Contains("cat", result.Tags!); // extracted from VisionTag[].Name
        Assert.Contains("mat", result.Tags!);
        Assert.Null(result.Objects); // platform doesn't return Objects for images

        // Verify correct endpoint
        Assert.Contains("/api/v1/ai-services/vision", handler.LastRequest!.RequestUri!.PathAndQuery);
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
    }

    [Fact]
    public async Task Should_ExtractDocument_PostToDocUnderstandingEndpoint()
    {
        var responsePayload = new
        {
            success = true,
            data = new
            {
                extractedText = "Document content here",
                layoutDataJson = "{}"
            }
        };
        var handler = new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(responsePayload),
                Encoding.UTF8, "application/json")
        });

        var provider = CreateProvider(handler, _logger);
        var result = await provider.ExtractDocumentAsync(
            new byte[] { 0x25, 0x50 }, "application/pdf");

        Assert.True(result.Success);
        Assert.Equal("Document content here", result.ExtractedText);

        Assert.Contains("/api/v1/ai-services/document-understanding",
            handler.LastRequest!.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task Should_AnalyzeImage_ReturnFailed_WhenPlatformReturnsError()
    {
        var handler = new FakeHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var provider = CreateProvider(handler, _logger);

        var result = await provider.AnalyzeImageAsync(new byte[] { 1 }, "image/png");

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task Should_AnalyzeImage_SendBase64InBody()
    {
        var responsePayload = new { success = true, data = new { caption = "test" } };
        var handler = new FakeHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(responsePayload),
                Encoding.UTF8, "application/json")
        });

        var imageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var provider = CreateProvider(handler, _logger);
        await provider.AnalyzeImageAsync(imageBytes, "image/png");

        var body = handler.LastRequestBody!;
        Assert.Contains(Convert.ToBase64String(imageBytes), body);
        Assert.Contains("image/png", body);
    }
}

public class AzureAttachmentAIProviderTests
{
    private readonly ILogger<AzureAttachmentAIProvider> _logger =
        Substitute.For<ILogger<AzureAttachmentAIProvider>>();
    private readonly IHttpClientFactory _httpClientFactory =
        Substitute.For<IHttpClientFactory>();
    private readonly TokenCredential _tokenCredential = BuildFakeTokenCredential();

    private static TokenCredential BuildFakeTokenCredential()
    {
        var cred = Substitute.For<TokenCredential>();
        cred.GetTokenAsync(Arg.Any<TokenRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(new AccessToken("fake", DateTimeOffset.UtcNow.AddHours(1)));
        return cred;
    }

    [Fact]
    public void Should_HaveProviderName_WithAzureAIVision()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureAIVision:Endpoint"] = "https://vision.test",
                ["AzureAIVision:ApiKey"] = "test-key"
            })
            .Build();

        var provider = new AzureAttachmentAIProvider(config, _logger, _httpClientFactory, _tokenCredential);
        Assert.Equal("AzureAIVision", provider.ProviderName);
    }

    [Fact]
    public void Should_HaveProviderName_AzureOpenAI_WhenNoVisionEndpoint()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureOpenAI:Endpoint"] = "https://openai.test",
                ["AzureOpenAI:ApiKey"] = "test-key",
                ["AzureOpenAI:DeploymentName"] = "gpt-4o"
            })
            .Build();

        var provider = new AzureAttachmentAIProvider(config, _logger, _httpClientFactory, _tokenCredential);
        Assert.Equal("AzureOpenAI", provider.ProviderName);
    }

    [Fact]
    public async Task Should_AnalyzeImage_ReturnNotAvailable_WhenNoVisionConfig()
    {
        // Only doc intelligence configured, no vision
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureDocumentIntelligence:Endpoint"] = "https://docint.test",
                ["AzureDocumentIntelligence:ApiKey"] = "test-key"
            })
            .Build();

        var provider = new AzureAttachmentAIProvider(config, _logger, _httpClientFactory, _tokenCredential);
        var result = await provider.AnalyzeImageAsync(new byte[] { 1 }, "image/png");

        Assert.False(result.Success);
        Assert.True(result.NotAvailable);
    }

    [Fact]
    public async Task Should_ExtractDocument_ReturnNotAvailable_WhenNoDocIntelligenceConfig()
    {
        // Only vision configured, no doc intelligence
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureAIVision:Endpoint"] = "https://vision.test",
                ["AzureAIVision:ApiKey"] = "test-key"
            })
            .Build();

        var provider = new AzureAttachmentAIProvider(config, _logger, _httpClientFactory, _tokenCredential);
        var result = await provider.ExtractDocumentAsync(new byte[] { 1 }, "application/pdf");

        Assert.False(result.Success);
        Assert.True(result.NotAvailable);
    }
}

public class AttachmentAIDIRoutingTests
{
    private static ServiceCollection BuildServices(IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddHttpClient();
        // MI swap SH_ENTERPRISE_MI_SWAP §2.1: AzureAttachmentAIProvider requires a
        // TokenCredential from DI. Tests register a fake that never gets called.
        var fakeCred = Substitute.For<TokenCredential>();
        fakeCred.GetTokenAsync(Arg.Any<TokenRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(new AccessToken("fake", DateTimeOffset.UtcNow.AddHours(1)));
        services.AddSingleton(fakeCred);
        services.AddAttachmentAI(config);
        return services;
    }

    [Fact]
    public void Should_ResolveNoOp_WhenOnlyPlatformConfigExists()
    {
        // Direct-Azure-only routing: platform proxy config alone does not
        // activate attachment intelligence for self-hosted images/documents.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KnowzPlatform:Enabled"] = "true",
                ["KnowzPlatform:BaseUrl"] = "https://platform.test",
                ["KnowzPlatform:ApiKey"] = "test-key"
            })
            .Build();

        var services = BuildServices(config);
        using var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IAttachmentAIProvider>();

        Assert.IsType<NoOpAttachmentAIProvider>(provider);
        Assert.Equal("NoOp", provider.ProviderName);
    }

    [Fact]
    public void Should_ResolveAzureProvider_WhenAzureVisionConfigured()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureAIVision:Endpoint"] = "https://vision.test",
                ["AzureAIVision:ApiKey"] = "test-key"
            })
            .Build();

        var services = BuildServices(config);
        using var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IAttachmentAIProvider>();

        Assert.IsType<AzureAttachmentAIProvider>(provider);
    }

    [Fact]
    public void Should_ResolveAzureProvider_WhenOnlyOpenAIConfigured()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureOpenAI:Endpoint"] = "https://openai.test",
                ["AzureOpenAI:ApiKey"] = "test-key",
                ["AzureOpenAI:DeploymentName"] = "gpt-4o"
            })
            .Build();

        var services = BuildServices(config);
        using var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IAttachmentAIProvider>();

        Assert.IsType<AzureAttachmentAIProvider>(provider);
    }

    [Fact]
    public void Should_ResolveAzureProvider_WhenOnlyDocIntelligenceConfigured()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureDocumentIntelligence:Endpoint"] = "https://docint.test",
                ["AzureDocumentIntelligence:ApiKey"] = "test-key"
            })
            .Build();

        var services = BuildServices(config);
        using var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IAttachmentAIProvider>();

        Assert.IsType<AzureAttachmentAIProvider>(provider);
    }

    [Fact]
    public void Should_ResolveNoOpProvider_WhenNoAIConfig()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var services = BuildServices(config);
        using var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IAttachmentAIProvider>();

        Assert.IsType<NoOpAttachmentAIProvider>(provider);
    }

    [Fact]
    public void Should_PreferAzure_OverPlatform_WhenBothConfigured()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KnowzPlatform:Enabled"] = "true",
                ["KnowzPlatform:BaseUrl"] = "https://platform.test",
                ["KnowzPlatform:ApiKey"] = "test-key",
                ["AzureAIVision:Endpoint"] = "https://vision.test",
                ["AzureAIVision:ApiKey"] = "test-key"
            })
            .Build();

        var services = BuildServices(config);
        using var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IAttachmentAIProvider>();

        Assert.IsType<AzureAttachmentAIProvider>(provider);
    }

    [Fact]
    public void Should_ResolveNoOp_WhenPlatformEnabledButMissingBaseUrl()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KnowzPlatform:Enabled"] = "true",
                // Missing BaseUrl and ApiKey
            })
            .Build();

        var services = BuildServices(config);
        using var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IAttachmentAIProvider>();

        Assert.IsType<NoOpAttachmentAIProvider>(provider);
    }

    [Fact]
    public void Should_NotAffectExistingOpenAIRegistrations()
    {
        // This test verifies that AddAttachmentAI is additive and doesn't
        // interfere with OpenAIExtensions registrations
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var services = BuildServices(config);
        // Should only register IAttachmentAIProvider, not IOpenAIService
        using var sp = services.BuildServiceProvider();
        var openAiService = sp.GetService<Knowz.Core.Interfaces.IOpenAIService>();
        Assert.Null(openAiService); // not registered by AddAttachmentAI
    }
}

/// <summary>
/// Simple fake HTTP handler for testing — captures last request and returns configured response.
/// </summary>
internal class FakeHandler : HttpMessageHandler
{
    private readonly HttpResponseMessage _response;
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    public FakeHandler(HttpResponseMessage response) => _response = response;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (request.Content != null)
            LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
        return _response;
    }
}
