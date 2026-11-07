using System.Reflection;
using FluentAssertions;
using Knowz.MCP.Services;
using Knowz.MCP.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Knowz.MCP.Tests.Tools;

public sealed class EnrichmentToolTests
{
    [Theory]
    [InlineData(nameof(KnowzProxyTools.CreateKnowledge))]
    [InlineData(nameof(KnowzProxyTools.UpdateKnowledge))]
    public void KnowledgeTools_EnrichmentIsOptionalAndDefaultsToTrue(string methodName)
    {
        var parameter = typeof(KnowzProxyTools).GetMethod(methodName)!.GetParameters()
            .SingleOrDefault(p => p.Name == "enrichment");
        parameter.Should().NotBeNull();
        parameter!.ParameterType.Should().Be(typeof(bool));
        parameter.HasDefaultValue.Should().BeTrue();
        parameter.DefaultValue.Should().Be(true);
    }

    [Theory]
    [InlineData(nameof(KnowzProxyTools.CreateKnowledge), "create_knowledge", false)]
    [InlineData(nameof(KnowzProxyTools.UpdateKnowledge), "update_knowledge", false)]
    [InlineData(nameof(KnowzProxyTools.CreateKnowledge), "create_knowledge", true)]
    [InlineData(nameof(KnowzProxyTools.UpdateKnowledge), "update_knowledge", true)]
    public async Task KnowledgeTools_ForwardEnrichmentChoice(string methodName, string toolName, bool enrichment)
    {
        var backend = new RecordingBackend();
        var tools = new KnowzProxyTools(backend,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, NullLogger<KnowzProxyTools>.Instance);
        var method = typeof(KnowzProxyTools).GetMethod(methodName)!;
        method.GetParameters().Should().Contain(p => p.Name == "enrichment");
        var arguments = method.GetParameters().Select(parameter => parameter.Name switch
        {
            "enrichment" => (object)enrichment,
            "id" => Guid.NewGuid().ToString(),
            "content" => "Searchable content",
            _ => parameter.HasDefaultValue ? parameter.DefaultValue : Type.Missing
        }).ToArray();

        await (Task)method.Invoke(tools, arguments)!;

        backend.ToolName.Should().Be(toolName);
        backend.Arguments.Should().ContainKey("enrichment").WhoseValue.Should().Be(enrichment);
        backend.Arguments["content"].Should().Be("Searchable content");
    }

    private sealed class RecordingBackend : IToolBackend
    {
        public string? ToolName { get; private set; }
        public Dictionary<string, object> Arguments { get; private set; } = new();

        public Task<string> ExecuteToolAsync(string toolName, Dictionary<string, object> arguments,
            CancellationToken cancellationToken = default)
        {
            ToolName = toolName;
            Arguments = arguments;
            return Task.FromResult("{}");
        }
    }
}
