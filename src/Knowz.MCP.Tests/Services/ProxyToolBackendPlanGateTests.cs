using System.Text.Json;
using Knowz.MCP.Services.Proxy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Knowz.MCP.Tests.Services;

/// <summary>
/// A Free-plan tenant calling an AI tool through the proxy backend must see the API's own
/// AI_UPGRADE_REQUIRED message (errors[0]), not a generic "request failed (HTTP 402)".
/// </summary>
public sealed class ProxyToolBackendPlanGateTests
{
    private const string ApiMessage = "AI chat is not included in the Free plan. Upgrade to Basic or higher to chat with your knowledge — manage your plan at Settings → Plan.";

    [Fact]
    public async Task PaymentRequired_SurfacesTheApisErrorsZero_AndStatusCode()
    {
        var body = JsonSerializer.Serialize(new { success = false, code = "AI_UPGRADE_REQUIRED", errors = new[] { ApiMessage }, message = (string?)null });
        var proxy = new Mock<IMcpApiProxyService>();
        proxy.Setup(p => p.ProxyRequestAsync<Dictionary<string, object>, object>(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new McpProxyException("payment required", 402, body));
        var http = new DefaultHttpContext();
        http.Items["ApiKey"] = "ukz_test";
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns(http);
        var backend = new ProxyToolBackend(proxy.Object, accessor.Object, NullLogger<ProxyToolBackend>.Instance);

        var result = await backend.ExecuteToolAsync("ask_question", new Dictionary<string, object> { ["question"] = "hi" });

        using var doc = JsonDocument.Parse(result);
        Assert.Equal(402, doc.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Contains("not included in the Free plan", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal("AI_UPGRADE_REQUIRED", doc.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"errors\":[123],\"code\":7}")]
    [InlineData("[1,2,3]")]
    [InlineData("")]
    public async Task MalformedOrNonEnvelopeBody_FallsBackToTheStatusMessage(string body)
    {
        var proxy = new Mock<IMcpApiProxyService>();
        proxy.Setup(p => p.ProxyRequestAsync<Dictionary<string, object>, object>(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new McpProxyException("payment required", 402, body));
        var http = new DefaultHttpContext();
        http.Items["ApiKey"] = "ukz_test";
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns(http);
        var backend = new ProxyToolBackend(proxy.Object, accessor.Object, NullLogger<ProxyToolBackend>.Instance);

        var result = await backend.ExecuteToolAsync("ask_question", new Dictionary<string, object>());

        using var doc = JsonDocument.Parse(result);
        Assert.Equal(402, doc.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Contains("does not include AI chat", doc.RootElement.GetProperty("error").GetString());
        Assert.False(doc.RootElement.TryGetProperty("code", out _));
    }
}
