using System.Text.Json;
using FluentAssertions;
using Knowz.MCP.Services;
using Knowz.MCP.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Knowz.MCP.Tests.Tools;

/// <summary>
/// Regression tests for issue #522 (Sentry KNOWZ-DEV-1M2).
///
/// <c>GetAmendRequestStatus</c> declared both <c>knowledgeId</c> and <c>amendRequestId</c> as
/// non-nullable strings with no default, so the MCP SDK marshaller rejected any call that omitted
/// <c>knowledgeId</c> before the tool body ran — surfacing as an unhandled ArgumentException rather
/// than a structured tool error. Callers omitted it because the sibling tools
/// (<c>amend_knowledge</c>, <c>amend_knowledge_async</c>) name the same GUID <c>id</c>.
/// </summary>
public class AmendStatusToolTests
{
    private readonly TestToolBackend _backend = new();
    private readonly KnowzProxyTools _tools;

    public AmendStatusToolTests()
    {
        var httpContext = new DefaultHttpContext();
        var httpContextAccessor = new TestHttpContextAccessor(httpContext);
        var logger = new TestLogger<KnowzProxyTools>();
        _tools = new KnowzProxyTools(_backend, httpContextAccessor, logger);
    }

    [Fact]
    public async Task GetAmendRequestStatus_AmendRequestIdOnly_DoesNotThrow_AndOmitsKnowledgeId()
    {
        var amendRequestId = Guid.NewGuid().ToString();

        // The defect: this call shape used to be unrepresentable — the marshaller threw first.
        await _tools.GetAmendRequestStatus(amendRequestId);

        _backend.LastToolName.Should().Be("get_amend_request_status");
        _backend.LastArguments.Should().ContainKey("amendRequestId");
        _backend.LastArguments["amendRequestId"].Should().Be(amendRequestId);
        _backend.LastArguments.Should().NotContainKey("knowledgeId",
            "an absent knowledgeId must not be forwarded as a blank value");
    }

    [Fact]
    public async Task GetAmendRequestStatus_IdAlias_PopulatesKnowledgeId()
    {
        var amendRequestId = Guid.NewGuid().ToString();
        var knowledgeGuid = Guid.NewGuid().ToString();

        await _tools.GetAmendRequestStatus(amendRequestId, id: knowledgeGuid);

        _backend.LastArguments.Should().ContainKey("knowledgeId");
        _backend.LastArguments["knowledgeId"].Should().Be(knowledgeGuid);
        _backend.LastArguments["amendRequestId"].Should().Be(amendRequestId);
    }

    [Fact]
    public async Task GetAmendRequestStatus_BothSupplied_BehaviourUnchanged()
    {
        var amendRequestId = Guid.NewGuid().ToString();
        var knowledgeId = Guid.NewGuid().ToString();

        await _tools.GetAmendRequestStatus(amendRequestId, knowledgeId: knowledgeId);

        _backend.LastToolName.Should().Be("get_amend_request_status");
        _backend.LastArguments.Should().HaveCount(2);
        _backend.LastArguments["knowledgeId"].Should().Be(knowledgeId);
        _backend.LastArguments["amendRequestId"].Should().Be(amendRequestId);
    }

    [Fact]
    public async Task GetAmendRequestStatus_KnowledgeIdWins_WhenBothItAndAliasSupplied()
    {
        var amendRequestId = Guid.NewGuid().ToString();
        var knowledgeId = Guid.NewGuid().ToString();
        var aliasId = Guid.NewGuid().ToString();

        await _tools.GetAmendRequestStatus(amendRequestId, knowledgeId: knowledgeId, id: aliasId);

        _backend.LastArguments["knowledgeId"].Should().Be(knowledgeId);
    }

    [Fact]
    public async Task GetAmendRequestStatus_MissingAmendRequestId_ReturnsStructuredError()
    {
        var result = await _tools.GetAmendRequestStatus("   ");

        _backend.LastToolName.Should().BeNull("the backend must not be called for an invalid request");

        result.Should().BeOfType<CallToolResult>();
        var toolResult = (CallToolResult)result!;
        toolResult.IsError.Should().BeTrue();
        var text = toolResult.Content.Should().ContainSingle().Which
            .Should().BeOfType<TextContentBlock>().Subject.Text;
        text.Should().Contain("amendRequestId");
        text.Should().Contain("invalid_arguments");
    }

    #region Test Helpers

    private class TestToolBackend : IToolBackend
    {
        public string? LastToolName { get; private set; }
        public Dictionary<string, object> LastArguments { get; private set; } = new();
        public string NextResult { get; set; } = "{}";

        public Task<string> ExecuteToolAsync(
            string toolName,
            Dictionary<string, object> arguments,
            CancellationToken cancellationToken = default)
        {
            LastToolName = toolName;
            LastArguments = new Dictionary<string, object>(arguments);
            return Task.FromResult(NextResult);
        }
    }

    private class TestHttpContextAccessor : IHttpContextAccessor
    {
        public TestHttpContextAccessor(HttpContext httpContext)
        {
            HttpContext = httpContext;
        }

        public HttpContext? HttpContext { get; set; }
    }

    private class TestLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }

    #endregion
}
