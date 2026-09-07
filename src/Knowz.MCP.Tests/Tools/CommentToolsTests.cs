using FluentAssertions;
using Knowz.MCP.Services;
using Knowz.MCP.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Knowz.MCP.Tests.Tools;

public class CommentToolsTests
{
    private readonly TestToolBackend _backend = new();
    private readonly KnowzProxyTools _tools;

    public CommentToolsTests()
    {
        var httpContext = new DefaultHttpContext();
        var httpContextAccessor = new TestHttpContextAccessor(httpContext);
        var logger = new TestLogger<KnowzProxyTools>();
        _tools = new KnowzProxyTools(_backend, httpContextAccessor, logger);
    }

    #region AddComment Tests

    [Fact]
    public async Task AddComment_CallsBackend_WithCorrectToolName()
    {
        await _tools.AddComment("abc-123", "Great insight!");

        _backend.LastToolName.Should().Be("add_comment");
    }

    [Fact]
    public async Task AddComment_RequiredParams_SentCorrectly()
    {
        var knowledgeItemId = Guid.NewGuid().ToString();
        var body = "This is a test comment";

        await _tools.AddComment(knowledgeItemId, body);

        _backend.LastArguments.Should().ContainKey("knowledgeItemId");
        _backend.LastArguments["knowledgeItemId"].Should().Be(knowledgeItemId);
        _backend.LastArguments.Should().ContainKey("body");
        _backend.LastArguments["body"].Should().Be(body);
    }

    [Fact]
    public async Task AddComment_DefaultsAuthorName_ToAIAssistant()
    {
        await _tools.AddComment("abc-123", "Some comment");

        _backend.LastArguments.Should().ContainKey("authorName");
        _backend.LastArguments["authorName"].Should().Be("AI Assistant");
    }

    [Fact]
    public async Task AddComment_CustomAuthorName_UsedWhenProvided()
    {
        await _tools.AddComment("abc-123", "Some comment", authorName: "John Doe");

        _backend.LastArguments["authorName"].Should().Be("John Doe");
    }

    [Fact]
    public async Task AddComment_ParentCommentId_IncludedWhenProvided()
    {
        var parentId = Guid.NewGuid().ToString();

        await _tools.AddComment("abc-123", "Reply", parentCommentId: parentId);

        _backend.LastArguments.Should().ContainKey("parentCommentId");
        _backend.LastArguments["parentCommentId"].Should().Be(parentId);
    }

    [Fact]
    public async Task AddComment_ParentCommentId_ExcludedWhenNull()
    {
        await _tools.AddComment("abc-123", "Top-level comment");

        _backend.LastArguments.Should().NotContainKey("parentCommentId");
    }

    [Fact]
    public async Task AddComment_Sentiment_IncludedWhenProvided()
    {
        await _tools.AddComment("abc-123", "Looks good!", sentiment: "positive");

        _backend.LastArguments.Should().ContainKey("sentiment");
        _backend.LastArguments["sentiment"].Should().Be("positive");
    }

    [Fact]
    public async Task AddComment_Sentiment_ExcludedWhenNull()
    {
        await _tools.AddComment("abc-123", "A comment");

        _backend.LastArguments.Should().NotContainKey("sentiment");
    }

    [Fact]
    public async Task AddComment_AllOptionalParams_IncludedWhenProvided()
    {
        var parentId = Guid.NewGuid().ToString();

        await _tools.AddComment("abc-123", "Full comment",
            authorName: "Alice",
            parentCommentId: parentId,
            sentiment: "neutral");

        _backend.LastArguments["authorName"].Should().Be("Alice");
        _backend.LastArguments["parentCommentId"].Should().Be(parentId);
        _backend.LastArguments["sentiment"].Should().Be("neutral");
    }

    [Fact]
    public async Task AddComment_ReturnsBackendResult()
    {
        _backend.NextResult = "{\"id\":\"comment-1\",\"body\":\"Test\"}";

        var result = await _tools.AddComment("abc-123", "Test");

        result.Should().Be("{\"id\":\"comment-1\",\"body\":\"Test\"}");
    }

    #endregion

    #region ListComments Tests

    [Fact]
    public async Task ListComments_CallsBackend_WithCorrectToolName()
    {
        await _tools.ListComments("abc-123");

        _backend.LastToolName.Should().Be("list_comments");
    }

    [Fact]
    public async Task ListComments_PassesKnowledgeItemId()
    {
        var knowledgeItemId = Guid.NewGuid().ToString();

        await _tools.ListComments(knowledgeItemId);

        _backend.LastArguments.Should().ContainKey("knowledgeItemId");
        _backend.LastArguments["knowledgeItemId"].Should().Be(knowledgeItemId);
    }

    [Fact]
    public async Task ListComments_OnlyPassesKnowledgeItemId()
    {
        await _tools.ListComments("abc-123");

        _backend.LastArguments.Should().HaveCount(1);
        _backend.LastArguments.Should().ContainKey("knowledgeItemId");
    }

    [Fact]
    public async Task ListComments_ReturnsBackendResult()
    {
        _backend.NextResult = "[{\"id\":\"c1\",\"body\":\"Hello\"}]";

        var result = await _tools.ListComments("abc-123");

        result.Should().Be("[{\"id\":\"c1\",\"body\":\"Hello\"}]");
    }

    #endregion

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
