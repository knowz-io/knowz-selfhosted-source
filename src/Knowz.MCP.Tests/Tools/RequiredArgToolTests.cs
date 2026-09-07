using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Knowz.MCP.Services;
using Knowz.MCP.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Knowz.MCP.Tests.Tools;

/// <summary>
/// Regression tests for GitHub #847 / Sentry KNOWZ-DEV-1MY and reopened #592 / KNOWZ-DEV-1M2.
/// Missing required MCP args must be structured tool errors, never SDK ArgumentException.
/// </summary>
public class RequiredArgToolTests
{
    private readonly TestToolBackend _backend = new();
    private readonly KnowzProxyTools _tools;

    public RequiredArgToolTests()
    {
        var httpContext = new DefaultHttpContext();
        var httpContextAccessor = new TestHttpContextAccessor(httpContext);
        var logger = new TestLogger<KnowzProxyTools>();
        _tools = new KnowzProxyTools(_backend, httpContextAccessor, logger);
    }

    [Fact]
    public async Task AddComment_MissingKnowledgeItemId_ReturnsInvalidArgumentsToolError()
    {
        var result = await _tools.AddComment();

        AssertInvalidArguments(result, "knowledgeItemId");
        _backend.LastToolName.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AddComment_EmptyOrWhitespaceKnowledgeItemId_ReturnsInvalidArgumentsToolError(string id)
    {
        var result = await _tools.AddComment(id, "a comment");

        AssertInvalidArguments(result, "knowledgeItemId");
        _backend.LastToolName.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AddComment_MissingBody_ReturnsInvalidArgumentsToolError(string? body)
    {
        var result = await _tools.AddComment("abc-123", body);

        AssertInvalidArguments(result, "body");
        _backend.LastToolName.Should().BeNull();
    }

    [Fact]
    public async Task AddComment_PresentArgs_ForwardsToBackend()
    {
        _backend.NextResult = """{"id":"c1"}""";

        var result = await _tools.AddComment("abc-123", "Great insight!");

        result.Should().Be("""{"id":"c1"}""");
        _backend.LastToolName.Should().Be("add_comment");
        _backend.LastArguments["knowledgeItemId"].Should().Be("abc-123");
        _backend.LastArguments["body"].Should().Be("Great insight!");
    }

    [Fact]
    public async Task GetKnowledgeItem_MissingId_ReturnsInvalidArgumentsToolError()
    {
        var result = await _tools.GetKnowledgeItem();

        AssertInvalidArguments(result, "id");
        _backend.LastToolName.Should().BeNull();
    }

    [Fact]
    public async Task GetKnowledgeItem_PresentId_ForwardsToBackend()
    {
        _backend.NextResult = """{"id":"abc-123","title":"Note"}""";

        var result = await _tools.GetKnowledgeItem("abc-123");

        result.Should().Be("""{"id":"abc-123","title":"Note"}""");
        _backend.LastToolName.Should().Be("get_knowledge_item");
        _backend.LastArguments["id"].Should().Be("abc-123");
    }

    [Fact]
    public async Task SearchKnowledge_MissingQuery_ReturnsInvalidArgumentsToolError()
    {
        var result = await _tools.SearchKnowledge();

        AssertInvalidArguments(result, "query");
        _backend.LastToolName.Should().BeNull();
    }

    [Fact]
    public async Task SearchByTitlePattern_MissingPattern_ReturnsInvalidArgumentsToolError()
    {
        var result = await _tools.SearchByTitlePattern();

        AssertInvalidArguments(result, "pattern");
        _backend.LastToolName.Should().BeNull();
    }

    [Fact]
    public async Task AskQuestion_UsesSharedInvalidArgumentsGuard()
    {
        var result = await _tools.AskQuestion();

        AssertInvalidArguments(result, "question");
        _backend.LastToolName.Should().BeNull();
    }

    [Theory]
    [InlineData(nameof(KnowzProxyTools.AddComment))]
    [InlineData(nameof(KnowzProxyTools.GetKnowledgeItem))]
    [InlineData(nameof(KnowzProxyTools.SearchKnowledge))]
    [InlineData(nameof(KnowzProxyTools.SearchByTitlePattern))]
    [InlineData(nameof(KnowzProxyTools.AskQuestion))]
    public async Task SdkInvoke_EmptyArguments_DoesNotThrowArgumentException(string methodName)
    {
        var method = typeof(KnowzProxyTools).GetMethod(methodName);
        method.Should().NotBeNull();

        var function = AIFunctionFactory.Create(method!, _tools);
        Func<Task<object?>> invoke = async () => await function.InvokeAsync(new AIFunctionArguments());

        var result = await invoke.Should().NotThrowAsync();
        AssertInvalidArgumentsPayload(result.Subject);
        _backend.LastToolName.Should().BeNull();
    }

    [Fact]
    public void McpServerTools_ReferenceTypeParameters_HaveDefaults()
    {
        var offenders = typeof(KnowzProxyTools)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null)
            .SelectMany(m => m.GetParameters()
                .Where(p => p.ParameterType != typeof(CancellationToken)
                            && !p.ParameterType.IsValueType
                            && Nullable.GetUnderlyingType(p.ParameterType) == null
                            && !p.HasDefaultValue)
                .Select(p => $"{m.Name}.{p.Name} ({p.ParameterType.Name})"))
            .ToList();

        offenders.Should().BeEmpty(
            "required MCP args must have C# defaults so AIFunctionFactory cannot throw ArgumentException");
    }

    private static void AssertInvalidArguments(object? result, string parameterName)
    {
        result.Should().BeOfType<CallToolResult>();
        var toolResult = (CallToolResult)result!;
        toolResult.IsError.Should().BeTrue();
        AssertInvalidArgumentsPayload(toolResult, parameterName);
    }

    private static void AssertInvalidArgumentsPayload(object? result, string? parameterName = null)
    {
        result.Should().NotBeNull();
        var payload = result switch
        {
            CallToolResult toolResult => toolResult.Content
                .Should().ContainSingle().Which
                .Should().BeOfType<TextContentBlock>().Subject.Text,
            JsonElement element => element.GetRawText(),
            _ => result!.ToString() ?? string.Empty
        };
        payload.Should().Contain("invalid_arguments");
        if (parameterName != null)
        {
            payload.Should().Contain(parameterName);
        }
    }

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
}
