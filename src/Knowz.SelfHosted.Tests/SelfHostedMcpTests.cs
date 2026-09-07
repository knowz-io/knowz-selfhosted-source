using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// Integration tests for the Self-Hosted MCP server running in Direct mode.
/// Requires the MCP server to be running locally on port 8080 with valid configuration.
/// </summary>
[Trait("Category", "Integration")]
public class SelfHostedMcpTests
{
    private readonly HttpClient _client;
    private readonly string _apiKey = "test-api-key";
    private readonly string _baseUrl = "http://localhost:8080";

    public SelfHostedMcpTests()
    {
        _client = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        _client.DefaultRequestHeaders.Add("X-Api-Key", _apiKey);
    }

    [Fact(Skip = "Requires locally running MCP server on port 8080. Run manually with --filter Category=Integration")]
    public async Task Health_ReturnsDirectMode()
    {
        var response = await _client.GetAsync("/health");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("healthy", body.GetProperty("status").GetString());
        Assert.Equal("direct", body.GetProperty("mode").GetString());
        Assert.Equal("2.0.0", body.GetProperty("version").GetString());
    }

    [Fact(Skip = "Requires locally running MCP server on port 8080. Run manually with --filter Category=Integration")]
    public async Task McpEndpoint_Exists()
    {
        // The /mcp endpoint exists and requires POST with proper MCP protocol
        // A GET should return 405 or similar, confirming the endpoint is mapped
        var response = await _client.GetAsync("/mcp");
        // MCP endpoint typically returns 405 for GET or may handle it differently
        Assert.True(response.StatusCode != HttpStatusCode.NotFound,
            "MCP endpoint should exist at /mcp");
    }

    [Fact(Skip = "Requires locally running MCP server on port 8080. Run manually with --filter Category=Integration")]
    public async Task McpEndpoint_RequiresProperContentType()
    {
        // MCP Streamable HTTP transport requires Accept: application/json, text/event-stream
        // A plain POST without proper headers returns 406 NotAcceptable
        var initRequest = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { },
                clientInfo = new
                {
                    name = "test-client",
                    version = "1.0.0"
                }
            }
        };

        var response = await _client.PostAsJsonAsync("/mcp", initRequest);
        // Without proper Accept header, MCP SDK returns 406 NotAcceptable
        Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
    }

    [Fact(Skip = "Requires locally running MCP server on port 8080. Run manually with --filter Category=Integration")]
    public async Task McpEndpoint_InitializeWithProperHeaders()
    {
        // Send MCP initialize with proper Streamable HTTP transport headers
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Content = JsonContent.Create(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { },
                clientInfo = new
                {
                    name = "test-client",
                    version = "1.0.0"
                }
            }
        });

        var response = await _client.SendAsync(request);
        // Should return 200 OK with MCP session
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
