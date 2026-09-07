using System.Collections;
using System.Reflection;
using FluentAssertions;
using Knowz.MCP.Services.Proxy;
using Xunit;

namespace Knowz.MCP.Tests.Tools;

public class CommentToolMappingsTests
{
    private static readonly IDictionary ToolMappings;

    static CommentToolMappingsTests()
    {
        var field = typeof(SelfHostedToolBackend)
            .GetField("ToolMappings", BindingFlags.NonPublic | BindingFlags.Static);
        ToolMappings = (IDictionary)field!.GetValue(null)!;
    }

    [Fact]
    public void ToolMappings_ContainsAddComment()
    {
        ToolMappings.Contains("add_comment").Should().BeTrue();
    }

    [Fact]
    public void ToolMappings_ContainsListComments()
    {
        ToolMappings.Contains("list_comments").Should().BeTrue();
    }

    [Fact]
    public void AddComment_UsesPostMethod()
    {
        var mapping = ToolMappings["add_comment"]!;
        GetProperty<HttpMethod>(mapping, "Method").Should().Be(HttpMethod.Post);
    }

    [Fact]
    public void AddComment_UsesCorrectPathTemplate()
    {
        var mapping = ToolMappings["add_comment"]!;
        GetProperty<string>(mapping, "PathTemplate").Should().Be("/api/v1/knowledge/{knowledgeItemId}/comments");
    }

    [Fact]
    public void AddComment_HasKnowledgeItemIdAsPathParam()
    {
        var mapping = ToolMappings["add_comment"]!;
        var pathParams = GetProperty<string[]?>(mapping, "PathParams");
        pathParams.Should().NotBeNull();
        pathParams.Should().Contain("knowledgeItemId");
    }

    [Fact]
    public void AddComment_HasBodyParams()
    {
        var mapping = ToolMappings["add_comment"]!;
        var bodyParams = GetProperty<string[]?>(mapping, "BodyParams");
        bodyParams.Should().NotBeNull();
        bodyParams.Should().Contain("body");
        bodyParams.Should().Contain("authorName");
        bodyParams.Should().Contain("parentCommentId");
        bodyParams.Should().Contain("sentiment");
    }

    [Fact]
    public void ListComments_UsesGetMethod()
    {
        var mapping = ToolMappings["list_comments"]!;
        GetProperty<HttpMethod>(mapping, "Method").Should().Be(HttpMethod.Get);
    }

    [Fact]
    public void ListComments_UsesCorrectPathTemplate()
    {
        var mapping = ToolMappings["list_comments"]!;
        GetProperty<string>(mapping, "PathTemplate").Should().Be("/api/v1/knowledge/{knowledgeItemId}/comments");
    }

    [Fact]
    public void ListComments_HasKnowledgeItemIdAsPathParam()
    {
        var mapping = ToolMappings["list_comments"]!;
        var pathParams = GetProperty<string[]?>(mapping, "PathParams");
        pathParams.Should().NotBeNull();
        pathParams.Should().Contain("knowledgeItemId");
    }

    private static T GetProperty<T>(object obj, string propertyName)
    {
        return (T)obj.GetType().GetProperty(propertyName)!.GetValue(obj)!;
    }
}
