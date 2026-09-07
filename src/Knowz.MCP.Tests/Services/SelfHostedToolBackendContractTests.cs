using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Knowz.MCP.Services.Proxy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Knowz.MCP.Tests.Services;

/// <summary>
/// MCP_SelfHostedToolContract VERIFY-T3, T7-T12 and MCP_SelfHostedUploadBackend VERIFY-U1-U5.
///
/// Every assertion here is about what the backend puts ON THE WIRE, because the defect these
/// tests exist to prevent was a backend that called routes the self-hosted API never had.
/// </summary>
public class SelfHostedToolBackendContractTests
{
    private const string BaseUrl = "http://sh.local";
    private const string ApiKey = "ukz_test";

    private static (SelfHostedToolBackend Backend, RecordingHttpMessageHandler Handler) Build(
        Func<HttpRequestMessage, int, HttpResponseMessage>? responder = null)
    {
        var handler = new RecordingHttpMessageHandler(
            responder ?? ((_, _) => RecordingHttpMessageHandler.Json(HttpStatusCode.OK, "{}")));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Knowz:BaseUrl"] = BaseUrl })
            .Build();

        var httpContext = new DefaultHttpContext();
        httpContext.Items["ApiKey"] = ApiKey;

        var backend = new SelfHostedToolBackend(
            new StubHttpClientFactory(handler),
            configuration,
            new HttpContextAccessor { HttpContext = httpContext },
            NullLogger<SelfHostedToolBackend>.Instance);

        return (backend, handler);
    }

    private static Dictionary<string, object> Args(params (string Key, object Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ---- VERIFY-T3 -------------------------------------------------------

    [Theory]
    [InlineData("list_todos")]
    [InlineData("get_todo_summary")]
    [InlineData("create_todo")]
    [InlineData("update_todo_status")]
    [InlineData("get_document_window")]
    [InlineData("inspect_document_map")]
    [InlineData("search_document_text")]
    [InlineData("graph_query")]
    [InlineData("amend_knowledge_async")]
    [InlineData("get_amend_request_status")]
    public async Task HiddenTool_DegradesHonestly_AndIssuesNoHttpRequest(string toolName)
    {
        var (backend, handler) = Build();

        var raw = await backend.ExecuteToolAsync(toolName, Args());

        raw.Should().NotContain("Unknown tool");
        var response = Parse(raw);
        response.GetProperty("code").GetString().Should().Be("unavailable_in_selfhosted");
        response.GetProperty("tool").GetString().Should().Be(toolName);
        response.GetProperty("error").GetString().Should().Contain("not available in Knowz Self-Hosted");
        handler.Requests.Should().BeEmpty("a hidden tool must never reach the network");
    }

    // ---- VERIFY-T7 / U1 / U2 --------------------------------------------

    [Fact]
    public async Task UploadFile_Standalone_IssuesExactlyOneMultipartPost()
    {
        var fileRecordId = Guid.NewGuid();
        var (backend, handler) = Build((_, _) => RecordingHttpMessageHandler.Json(
            HttpStatusCode.Created,
            $$"""{"fileRecordId":"{{fileRecordId}}","fileName":"a.txt","contentType":"text/plain","sizeBytes":5,"blobUri":"x","success":true}"""));

        var raw = await backend.ExecuteToolAsync("upload_file", Args(
            ("fileName", "a.txt"),
            ("contentBase64", Convert.ToBase64String(Encoding.UTF8.GetBytes("hello"))),
            ("target", "standalone")));

        handler.Requests.Should().ContainSingle();
        var request = handler.Requests[0];
        request.Method.Should().Be(HttpMethod.Post);
        request.Path.Should().Be("/api/v1/files/upload");
        request.ContentType.Should().Be("multipart/form-data");
        request.Body.Should().Contain("name=file", "the SH minimal-API handler binds IFormFile by the part name 'file'");
        request.Body.Should().Contain("hello");
        request.ApiKey.Should().Be(ApiKey);

        Parse(raw).GetProperty("fileRecordId").GetGuid().Should().Be(fileRecordId);
    }

    [Theory]
    [InlineData("standalone")]
    [InlineData("knowledge")]
    [InlineData("inbox")]
    [InlineData("new-knowledge")]
    public async Task UploadFile_NeverCallsAChunkedUploadRoute(string target)
    {
        var (backend, handler) = Build((_, _) => RecordingHttpMessageHandler.Json(
            HttpStatusCode.Created,
            $$"""{"fileRecordId":"{{Guid.NewGuid()}}","fileName":"a.txt","contentType":"text/plain","sizeBytes":5,"blobUri":"x","success":true}"""));

        await backend.ExecuteToolAsync("upload_file", UploadArgs(target));

        handler.Requests.Select(r => r.Path).Should().NotContain(p =>
            p.Contains("/upload/initialize") || p.Contains("/upload/streaming") || p.Contains("/upload/complete"));
        handler.Requests.Should().NotBeEmpty("the file bytes must still be stored");
    }

    // ---- VERIFY-T8 / U3 --------------------------------------------------

    [Fact]
    public async Task UploadFile_Knowledge_UploadsThenAttaches_AcceptingA201()
    {
        var fileRecordId = Guid.NewGuid();
        var knowledgeId = Guid.NewGuid();
        var (backend, handler) = Build((_, index) => index == 0
            ? RecordingHttpMessageHandler.Json(HttpStatusCode.Created,
                $$"""{"fileRecordId":"{{fileRecordId}}","fileName":"a.txt","contentType":"text/plain","sizeBytes":5,"blobUri":"x","success":true}""")
            : RecordingHttpMessageHandler.Json(HttpStatusCode.Created,
                $$"""{"id":"{{Guid.NewGuid()}}","fileRecordId":"{{fileRecordId}}","knowledgeId":"{{knowledgeId}}"}"""));

        var raw = await backend.ExecuteToolAsync("upload_file", Args(
            ("fileName", "a.txt"),
            ("contentBase64", Convert.ToBase64String(Encoding.UTF8.GetBytes("hello"))),
            ("target", "knowledge"),
            ("targetId", knowledgeId.ToString())));

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Path.Should().Be("/api/v1/files/upload");
        handler.Requests[1].Method.Should().Be(HttpMethod.Post);
        handler.Requests[1].Path.Should().Be($"/api/v1/knowledge/{knowledgeId}/attachments");
        handler.Requests[1].Body.Should().Contain(fileRecordId.ToString());

        var response = Parse(raw);
        response.GetProperty("status").GetString().Should().Be("success");
        response.GetProperty("knowledgeId").GetGuid().Should().Be(knowledgeId);
        response.GetProperty("aiProcessing").GetString().Should().Be("queued");
    }

    // ---- VERIFY-T9 / U4 --------------------------------------------------

    [Theory]
    [InlineData("inbox")]
    [InlineData("new-knowledge")]
    public async Task UploadFile_UnsupportedTarget_DegradesToPartial(string target)
    {
        var fileRecordId = Guid.NewGuid();
        var (backend, handler) = Build((_, _) => RecordingHttpMessageHandler.Json(
            HttpStatusCode.Created,
            $$"""{"fileRecordId":"{{fileRecordId}}","fileName":"a.txt","contentType":"text/plain","sizeBytes":5,"blobUri":"x","success":true}"""));

        var raw = await backend.ExecuteToolAsync("upload_file", UploadArgs(target));

        var response = Parse(raw);
        response.GetProperty("status").GetString().Should().Be("partial");
        response.GetProperty("fileRecordId").GetGuid().Should().Be(fileRecordId);
        response.GetProperty("aiProcessing").GetString().Should().Be("not-queued");
        var warnings = response.GetProperty("warnings").EnumerateArray().ToList();
        warnings.Should().ContainSingle();
        warnings[0].GetProperty("code").GetString().Should().Be("TARGET_NOT_SUPPORTED_SELFHOSTED");
        warnings[0].GetProperty("message").GetString().Should().Contain(target);

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Path.Should().Be("/api/v1/files/upload");
    }

    // ---- VERIFY-T10 / U5 -------------------------------------------------

    /// <summary>
    /// The hosted envelope, copied from the response dictionary literal in
    /// src/Knowz.API/Services/Mcp/McpToolService.cs:8592-8613. Hosted emits `mediaType`
    /// UNCONDITIONALLY (nullable) and adds `warnings` ONLY when the list is non-empty. An agent must
    /// not have to know which backend mode it is talking to, so this fixture — not the spec's prose
    /// enumeration — is what the self-hosted envelope is measured against.
    /// </summary>
    private static readonly string[] HostedEnvelopeKeys =
    {
        "status", "fileRecordId", "fileName", "fileSize", "contentType", "mediaType",
        "target", "knowledgeId", "inboxItemId", "aiProcessing", "message"
    };

    [Theory]
    [InlineData("standalone")]
    [InlineData("inbox")]
    [InlineData("new-knowledge")]
    public async Task UploadFile_EnvelopeKeySet_MatchesHosted_WhenWarningsArePresent(string target)
    {
        var (backend, _) = Build((_, _) => RecordingHttpMessageHandler.Json(
            HttpStatusCode.Created,
            $$"""{"fileRecordId":"{{Guid.NewGuid()}}","fileName":"a.txt","contentType":"text/plain","sizeBytes":5,"blobUri":"x","success":true}"""));

        var raw = await backend.ExecuteToolAsync("upload_file", UploadArgs(target));

        Parse(raw).EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo(HostedEnvelopeKeys.Append("warnings"));
    }

    [Fact]
    public async Task UploadFile_EnvelopeKeySet_OmitsWarnings_WhenThereAreNone_LikeHosted()
    {
        var knowledgeId = Guid.NewGuid();
        var (backend, _) = Build((_, index) => RecordingHttpMessageHandler.Json(
            HttpStatusCode.Created,
            index == 0
                ? $$"""{"fileRecordId":"{{Guid.NewGuid()}}","fileName":"a.txt","contentType":"text/plain","sizeBytes":5,"blobUri":"x","success":true}"""
                : "{}"));

        var raw = await backend.ExecuteToolAsync("upload_file", Args(
            ("fileName", "a.txt"),
            ("contentBase64", Convert.ToBase64String(Encoding.UTF8.GetBytes("hello"))),
            ("target", "knowledge"),
            ("targetId", knowledgeId.ToString())));

        var response = Parse(raw);
        response.GetProperty("status").GetString().Should().Be("success");
        response.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(HostedEnvelopeKeys);
    }

    [Fact]
    public async Task UploadFile_OverInlineCap_RejectsBeforeAnyHttpCall()
    {
        var (backend, handler) = Build();

        var raw = await backend.ExecuteToolAsync("upload_file", Args(
            ("fileName", "big.bin"),
            ("contentBase64", Convert.ToBase64String(new byte[9 * 1024 * 1024])),
            ("target", "standalone")));

        raw.Should().Contain("inline MCP upload limit");
        handler.Requests.Should().BeEmpty();
    }

    // ---- VERIFY-T11 ------------------------------------------------------

    [Fact]
    public async Task CountKnowledge_ForwardsFilters_AndReadsTotalItems()
    {
        var (backend, handler) = Build((_, _) => RecordingHttpMessageHandler.Json(
            HttpStatusCode.OK,
            """{"items":[],"page":1,"pageSize":1,"totalItems":42,"totalPages":42}"""));

        var raw = await backend.ExecuteToolAsync("count_knowledge", Args(
            ("knowledgeType", "Document"),
            ("titlePattern", "road*")));

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Path.Should().Be("/api/v1/knowledge");
        var query = Uri.UnescapeDataString(handler.Requests[0].Query);
        query.Should().Contain("type=Document");
        query.Should().Contain("title=road*");
        query.Should().Contain("pageSize=1");
        handler.Requests.Should().NotContain(r => r.Path.Contains("/knowledge/stats"),
            "returning a tenant-wide total for a filtered question is the wrong-answer defect");

        var response = Parse(raw);
        response.GetProperty("count").GetInt32().Should().Be(42);
        response.GetProperty("filters").GetProperty("knowledgeType").GetString().Should().Be("Document");
    }

    [Fact]
    public async Task CountKnowledge_UpstreamFailure_IsSurfaced_NotInventedAsZero()
    {
        var (backend, _) = Build((_, _) => RecordingHttpMessageHandler.Json(HttpStatusCode.BadGateway, "boom"));

        var raw = await backend.ExecuteToolAsync("count_knowledge", Args());

        var response = Parse(raw);
        response.GetProperty("status").GetInt32().Should().Be(502);
        response.TryGetProperty("count", out _).Should().BeFalse();
    }

    // ---- VERIFY-T12 ------------------------------------------------------

    [Fact]
    public async Task ListMatchingItems_WithoutQuery_ForwardsVaultId()
    {
        var vaultId = Guid.NewGuid().ToString();
        var (backend, handler) = Build();

        await backend.ExecuteToolAsync("list_matching_items", Args(("vaultId", vaultId)));

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Path.Should().Be("/api/v1/knowledge");
        Uri.UnescapeDataString(handler.Requests[0].Query).Should().Contain($"vaultId={vaultId}");
    }

    [Fact]
    public async Task ListMatchingItems_WithQuery_StillRoutesToSearch()
    {
        var (backend, handler) = Build();

        await backend.ExecuteToolAsync("list_matching_items", Args(("query", "roads"), ("vaultId", Guid.NewGuid().ToString())));

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Path.Should().Be("/api/v1/search");
    }

    [Fact]
    public async Task ListKnowledgeItems_WithoutVaultId_EmitsNoVaultIdKey()
    {
        var (backend, handler) = Build();

        await backend.ExecuteToolAsync("list_knowledge_items", Args(("page", 1)));

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Query.Should().NotContain("vaultId");
    }

    private static Dictionary<string, object> UploadArgs(string target)
    {
        var args = Args(
            ("fileName", "a.txt"),
            ("contentBase64", Convert.ToBase64String(Encoding.UTF8.GetBytes("hello"))),
            ("target", target));

        if (target == "knowledge") args["targetId"] = Guid.NewGuid().ToString();
        if (target == "new-knowledge") args["vaultId"] = Guid.NewGuid().ToString();
        return args;
    }
}
