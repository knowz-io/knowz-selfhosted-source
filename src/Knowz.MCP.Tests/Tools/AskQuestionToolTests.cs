using System.Text.Json;
using FluentAssertions;
using Knowz.MCP.Services;
using Knowz.MCP.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Knowz.MCP.Tests.Tools;

/// <summary>
/// Regression tests for GitHub #592 / Sentry KNOWZ-DEV-1M2.
///
/// The MCP C# SDK marshaller throws ArgumentException when a required C# parameter
/// is omitted. That becomes McpServerImpl ToolCallError (Error-level) and a Sentry Error.
/// <c>question</c> must have a default so the marshaller never throws; the tool body
/// returns a structured invalid-arguments CallToolResult instead.
/// </summary>
public class AskQuestionToolTests
{
    private readonly TestToolBackend _backend = new();
    private readonly KnowzProxyTools _tools;

    public AskQuestionToolTests()
    {
        var httpContext = new DefaultHttpContext();
        var httpContextAccessor = new TestHttpContextAccessor(httpContext);
        var logger = new TestLogger<KnowzProxyTools>();
        _tools = new KnowzProxyTools(_backend, httpContextAccessor, logger);
    }

    [Fact]
    public async Task AskQuestion_MissingQuestion_ReturnsInvalidArgumentsToolError()
    {
        var result = await _tools.AskQuestion();

        AssertInvalidArguments(result);
        _backend.LastToolName.Should().BeNull("the backend must not be called when question is missing");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AskQuestion_EmptyOrWhitespaceQuestion_ReturnsInvalidArgumentsToolError(string question)
    {
        var result = await _tools.AskQuestion(question);

        AssertInvalidArguments(result);
        _backend.LastToolName.Should().BeNull("the backend must not be called when question is blank");
    }

    [Fact]
    public async Task AskQuestion_PresentQuestion_ForwardsToBackend()
    {
        _backend.NextResult = """{"answer":"Knowz is a knowledge platform."}""";

        var result = await _tools.AskQuestion("What is Knowz?");

        result.Should().Be("""{"answer":"Knowz is a knowledge platform."}""");
        _backend.LastToolName.Should().Be("ask_question");
        _backend.LastArguments.Should().ContainKey("question");
        _backend.LastArguments["question"].Should().Be("What is Knowz?");
    }

    [Fact]
    public async Task AskQuestion_SdkInvokeWithoutQuestion_DoesNotThrowArgumentException()
    {
        var method = typeof(KnowzProxyTools).GetMethod(nameof(KnowzProxyTools.AskQuestion));
        method.Should().NotBeNull();
        var questionParam = method!.GetParameters().Single(p => p.Name == "question");
        questionParam.HasDefaultValue.Should().BeTrue(
            "question must have a default so AIFunctionFactory does not throw ArgumentException");

        var function = AIFunctionFactory.Create(method, _tools);
        Func<Task<object?>> invoke = async () => await function.InvokeAsync(new AIFunctionArguments());

        // Direct AIFunctionFactory JSON-serializes the return value. The hosted MCP server
        // uses MarshalResult that preserves CallToolResult. Either way, the marshaller
        // must not throw ArgumentException for a missing required parameter.
        var result = await invoke.Should().NotThrowAsync();
        AssertInvalidArgumentsPayload(result.Subject);
        _backend.LastToolName.Should().BeNull();
    }

    private static void AssertInvalidArguments(object? result)
    {
        result.Should().BeOfType<CallToolResult>();
        var toolResult = (CallToolResult)result!;
        toolResult.IsError.Should().BeTrue();
        AssertInvalidArgumentsPayload(toolResult);
    }

    /// <summary>
    /// Accepts either a <see cref="CallToolResult"/> (direct tool call) or the
    /// JSON-serialized shape returned by <see cref="AIFunctionFactory.InvokeAsync"/>.
    /// </summary>
    private static void AssertInvalidArgumentsPayload(object? result)
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
        payload.Should().Contain("question");
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
