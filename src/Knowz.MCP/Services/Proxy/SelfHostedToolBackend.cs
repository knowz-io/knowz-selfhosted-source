using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Web;
using Knowz.MCP.Config;

namespace Knowz.MCP.Services.Proxy;

/// <summary>
/// Self-hosted mode tool backend: maps MCP tool names to self-hosted REST API endpoints.
/// Unlike ProxyToolBackend (which calls a single /api/v1/mcp/tools/call endpoint),
/// this backend calls individual REST endpoints on the self-hosted API.
/// </summary>
public class SelfHostedToolBackend : IToolBackend
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<SelfHostedToolBackend> _logger;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SearchConcurrencyByApiKey = new();

    private static readonly Dictionary<string, ToolMapping> ToolMappings = new()
    {
        ["search_knowledge"] = new(HttpMethod.Get, "/api/v1/search", null,
            new[] { "query", "limit", "vaultId", "includeChildVaults", "includeSharedVaults", "tags", "requireAllTags", "startDate", "endDate" },
            new Dictionary<string, string> { ["query"] = "q", ["includeChildVaults"] = "includeChildren" }),

        ["advanced_search"] = new(HttpMethod.Get, "/api/v1/search", null,
            new[] { "query", "limit", "vaultId", "includeChildVaults", "includeSharedVaults" },
            new Dictionary<string, string> { ["query"] = "q", ["includeChildVaults"] = "includeChildren" }),

        ["get_knowledge_item"] = new(HttpMethod.Get, "/api/v1/knowledge/{id}",
            new[] { "id" }, null, null),

        ["list_topics"] = new(HttpMethod.Get, "/api/v1/topics", null,
            new[] { "limit" }, null),

        ["get_topic_details"] = new(HttpMethod.Get, "/api/v1/topics/{id}",
            new[] { "id" }, null, null),

        ["list_vaults"] = new(HttpMethod.Get, "/api/v1/vaults", null,
            new[] { "includeStats", "includeSharedVaults" }, null),

        ["list_vault_contents"] = new(HttpMethod.Get, "/api/v1/vaults/{vaultId}/contents",
            new[] { "vaultId" },
            new[] { "includeChildVaults", "limit" },
            new Dictionary<string, string> { ["includeChildVaults"] = "includeChildren" }),

        ["find_entities"] = new(HttpMethod.Get, "/api/v1/entities", null,
            new[] { "entityType", "query", "limit" },
            new Dictionary<string, string> { ["entityType"] = "type", ["query"] = "q" }),

        ["ask_question"] = new(HttpMethod.Post, "/api/v1/ask", null, null,
            null, new[] { "question", "vaultId", "researchMode", "includeSharedVaults" }),

        ["create_knowledge"] = new(HttpMethod.Post, "/api/v1/knowledge", null, null,
            new Dictionary<string, string> { ["knowledgeType"] = "type" },
            new[] { "content", "title", "knowledgeType", "vaultId", "tags", "source" }),

        ["update_knowledge"] = new(HttpMethod.Put, "/api/v1/knowledge/{id}",
            new[] { "id" }, null, null,
            new[] { "content", "title", "tags", "source" }),

        ["create_vault"] = new(HttpMethod.Post, "/api/v1/vaults", null, null, null,
            new[] { "name", "description", "parentVaultId", "vaultType" }),

        ["create_inbox_item"] = new(HttpMethod.Post, "/api/v1/inbox", null, null, null,
            new[] { "body" }),

        // count_knowledge is NOT a mapping: /knowledge/stats is unfiltered, so a filtered
        // question got a tenant-wide total (the wrong-answer defect). It is a special-case branch
        // over GET /api/v1/knowledge?pageSize=1 reading totalItems. Spec: MCP_SelfHostedToolContract R10.

        ["get_statistics"] = new(HttpMethod.Get, "/api/v1/knowledge/stats", null, null, null),

        ["list_knowledge_items"] = new(HttpMethod.Get, "/api/v1/knowledge", null,
            // "vaultId" is forwarded so list_matching_items(vaultId) keeps its vault scope (R11).
            // list_knowledge_items itself never supplies it, so its query string is unchanged.
            new[] { "page", "pageSize", "sortBy", "sortDirection", "knowledgeType", "titlePattern", "fileNamePattern", "startDate", "endDate", "vaultId" },
            new Dictionary<string, string>
            {
                ["sortBy"] = "sort", ["sortDirection"] = "sortDir",
                ["knowledgeType"] = "type", ["titlePattern"] = "title", ["fileNamePattern"] = "fileName"
            }),

        ["search_by_file_pattern"] = new(HttpMethod.Get, "/api/v1/knowledge", null,
            new[] { "pattern", "limit" },
            new Dictionary<string, string> { ["pattern"] = "fileName", ["limit"] = "pageSize" }),

        ["search_by_title_pattern"] = new(HttpMethod.Get, "/api/v1/knowledge", null,
            new[] { "pattern", "limit" },
            new Dictionary<string, string> { ["pattern"] = "title", ["limit"] = "pageSize" }),

        ["add_comment"] = new(HttpMethod.Post, "/api/v1/knowledge/{knowledgeItemId}/comments",
            new[] { "knowledgeItemId" }, null, null,
            new[] { "body", "authorName", "parentCommentId", "sentiment" }),

        ["list_comments"] = new(HttpMethod.Get, "/api/v1/knowledge/{knowledgeItemId}/comments",
            new[] { "knowledgeItemId" }, null, null),

        // DEPRECATED — legacy synchronous path. Now a deprecation shim on the API side
        // that enqueues an async request. Removal no earlier than 2026-08-01.
        // Migrate to amend_knowledge_async. Spec: MCP_AmendKnowledge §1 Rule 2.
        ["amend_knowledge"] = new(HttpMethod.Post, "/api/v1/knowledge/{id}/amend",
            new[] { "id" }, null, null,
            new[] { "instruction" }),

        // amend_knowledge_async and get_amend_request_status had mappings to /amend-requests routes
        // that Knowz.SelfHosted.API does not expose — they surfaced raw 404s. Both are now in
        // SelfHostedToolVisibility.HiddenTools and degrade honestly before any HTTP call.

        ["get_version_history"] = new(HttpMethod.Get, "/api/v1/knowledge/{knowledgeId}/versions",
            new[] { "knowledgeId" }, null, null),
    };

    /// <summary>
    /// Tools dispatched through <see cref="ToolMappings"/>. Exposed for the R13 coverage guard —
    /// every declared [McpServerTool] must be in exactly one of this set, <see cref="SpecialCaseToolNames"/>,
    /// or SelfHostedToolVisibility.HiddenTools.
    /// </summary>
    public static IReadOnlySet<string> MappedToolNames { get; } =
        ToolMappings.Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Tools handled by a bespoke branch in <see cref="ExecuteToolAsync"/> rather than a mapping,
    /// because a mapping cannot inject a constant query parameter, reshape a response, fan out, or
    /// speak multipart. Kept beside the dispatch chain it describes.
    /// </summary>
    public static IReadOnlySet<string> SpecialCaseToolNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "bulk_get_knowledge_items",
        "attach_files",
        "upload_file",
        "list_matching_items",
        "count_knowledge",
    };

    /// <summary>
    /// Every self-hosted API route template each tool can call, mappings and special cases alike.
    /// The route-manifest guard (R14) checks these against a checked-in snapshot of
    /// Knowz.SelfHosted.API so a renamed route fails a test instead of 404-ing in a customer's agent.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> SelfHostedRouteTemplates { get; } =
        BuildRouteTemplates();

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildRouteTemplates()
    {
        var templates = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (toolName, mapping) in ToolMappings)
            templates[toolName] = new[] { mapping.PathTemplate };

        templates["bulk_get_knowledge_items"] = new[] { "/api/v1/knowledge/{id}" };
        templates["attach_files"] = new[] { "/api/v1/knowledge/{knowledgeId}/attachments" };
        templates["upload_file"] = new[] { "/api/v1/files/upload", "/api/v1/knowledge/{knowledgeId}/attachments" };
        templates["count_knowledge"] = new[] { "/api/v1/knowledge" };
        templates["list_matching_items"] = new[] { "/api/v1/knowledge", "/api/v1/search" };
        return templates;
    }

    public SelfHostedToolBackend(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        ILogger<SelfHostedToolBackend> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task<string> ExecuteToolAsync(
        string toolName,
        Dictionary<string, object> arguments,
        CancellationToken cancellationToken = default)
    {
        // R6 — ahead of every other branch: a tool this edition cannot run must never reach the
        // network and must never answer "Unknown tool". Removal from ToolCollection means the SDK
        // will not dispatch these, but a client with a cached tool list still can.
        if (SelfHostedToolVisibility.HiddenTools.Contains(toolName))
        {
            _logger.LogInformation(
                "SelfHostedToolBackend: {ToolName} is hidden in self-hosted mode; degrading honestly", toolName);
            return JsonSerializer.Serialize(new
            {
                error = $"{toolName} is not available in Knowz Self-Hosted.",
                code = "unavailable_in_selfhosted",
                tool = toolName
            });
        }

        // Handle special-case tools that require custom logic (SpecialCaseToolNames mirrors this chain)
        if (toolName == "bulk_get_knowledge_items")
            return await ExecuteBulkGetAsync(arguments, cancellationToken);

        if (toolName == "attach_files")
            return await ExecuteAttachFilesAsync(arguments, cancellationToken);

        if (toolName == "upload_file")
            return await ExecuteUploadFileAsync(arguments, cancellationToken);

        if (toolName == "count_knowledge")
            return await ExecuteCountKnowledgeAsync(arguments, cancellationToken);

        if (toolName == "list_matching_items")
        {
            return await ExecuteToolAsync(
                arguments.ContainsKey("query") ? "advanced_search" : "list_knowledge_items",
                arguments,
                cancellationToken);
        }

        if (!ToolMappings.TryGetValue(toolName, out var mapping))
            return JsonSerializer.Serialize(new { error = $"Unknown tool: {toolName}" });

        var context = _httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("No HttpContext available");
        var apiKey = context.Items["ApiKey"] as string
            ?? throw new InvalidOperationException("No API key available for self-hosted mode");

        var baseUrl = _configuration["Knowz:BaseUrl"]
            ?? throw new InvalidOperationException("Knowz:BaseUrl is not configured");

        var client = _httpClientFactory.CreateClient("McpApiClient");
        using var searchLease = IsSearchTool(toolName)
            ? await TryAcquireSearchLeaseAsync(apiKey, cancellationToken)
            : null;
        if (IsSearchTool(toolName) && searchLease == null)
        {
            var retryAfterSeconds = Math.Clamp(
                _configuration.GetValue<int?>("MCP:Search:RetryAfterSeconds") ?? 2,
                1,
                60);
            return JsonSerializer.Serialize(new
            {
                error = "mcp.search_concurrency_exceeded",
                status = 429,
                retryAfterSeconds,
                degraded = true,
                message = "Too many concurrent MCP knowledge searches for this API key. Retry after the indicated delay."
            });
        }

        // Build the URL with path parameters substituted
        var path = mapping.PathTemplate;
        if (mapping.PathParams != null)
        {
            foreach (var param in mapping.PathParams)
            {
                if (arguments.TryGetValue(param, out var value))
                    path = path.Replace($"{{{param}}}", Uri.EscapeDataString(value?.ToString() ?? ""));
            }
        }

        var url = $"{baseUrl.TrimEnd('/')}{path}";

        // Build query string for GET requests
        if (mapping.Method == HttpMethod.Get && mapping.QueryParams != null)
        {
            var queryParts = new List<string>();
            foreach (var param in mapping.QueryParams)
            {
                if (arguments.TryGetValue(param, out var value) && value != null)
                {
                    var queryName = mapping.ArgRenames?.GetValueOrDefault(param) ?? param;
                    var stringValue = value is JsonElement je ? je.ToString() : value.ToString();
                    if (!string.IsNullOrEmpty(stringValue))
                        queryParts.Add($"{HttpUtility.UrlEncode(queryName)}={HttpUtility.UrlEncode(stringValue)}");
                }
            }
            if (queryParts.Count > 0)
                url += "?" + string.Join("&", queryParts);
        }

        var request = new HttpRequestMessage(mapping.Method, url);
        request.Headers.Add("X-Api-Key", apiKey);

        // Build JSON body for POST/PUT requests
        if (mapping.Method != HttpMethod.Get && mapping.BodyParams != null)
        {
            var body = new Dictionary<string, object>();
            foreach (var param in mapping.BodyParams)
            {
                if (arguments.TryGetValue(param, out var value) && value != null)
                {
                    var bodyName = mapping.ArgRenames?.GetValueOrDefault(param) ?? param;
                    body[bodyName] = value;
                }
            }
            request.Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json");
        }

        _logger.LogInformation("SelfHostedToolBackend: {Method} {Url} for tool {ToolName}",
            mapping.Method, url, toolName);

        try
        {
            var response = await client.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("SelfHostedToolBackend: {ToolName} returned {StatusCode}: {Body}",
                    toolName, response.StatusCode, responseBody);
                var retryAfterSeconds = response.Headers.RetryAfter?.Delta?.TotalSeconds is double delta
                    ? (int)Math.Ceiling(delta)
                    : response.Headers.TryGetValues("Retry-After", out var values) && int.TryParse(values.FirstOrDefault(), out var parsed)
                        ? parsed
                        : (int?)null;
                return JsonSerializer.Serialize(new
                {
                    error = $"API returned {(int)response.StatusCode}",
                    status = (int)response.StatusCode,
                    retryAfterSeconds,
                    details = responseBody
                });
            }

            return string.IsNullOrWhiteSpace(responseBody) ? "{}" : responseBody;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "SelfHostedToolBackend: {ToolName} timed out", toolName);
            return JsonSerializer.Serialize(new { error = $"Request for {toolName} timed out" });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "SelfHostedToolBackend: {ToolName} failed", toolName);
            return JsonSerializer.Serialize(new { error = $"Request for {toolName} failed: {ex.Message}" });
        }
    }

    private static bool IsSearchTool(string toolName) =>
        toolName is "search_knowledge" or "advanced_search";

    // graph_query's REST branch was removed with the tool itself: Knowz.SelfHosted.API exposes no
    // /api/v1/graph/* routes, so every op surfaced a raw 404. It is now in
    // SelfHostedToolVisibility.HiddenTools and degrades before any HTTP call (R6).

    private async Task<IDisposable?> TryAcquireSearchLeaseAsync(string apiKey, CancellationToken cancellationToken)
    {
        var maxConcurrent = Math.Clamp(
            _configuration.GetValue<int?>("MCP:Search:MaxConcurrentPerApiKey") ?? 2,
            1,
            10);
        var semaphore = SearchConcurrencyByApiKey.GetOrAdd(
            apiKey,
            _ => new SemaphoreSlim(maxConcurrent, maxConcurrent));

        if (await semaphore.WaitAsync(0, cancellationToken))
        {
            return new SemaphoreLease(semaphore);
        }

        _logger.LogWarning(
            "SelfHostedToolBackend: MCP search concurrency budget exceeded for API key prefix {ApiKeyPrefix}; maxConcurrent={MaxConcurrent}",
            apiKey.Length <= 8 ? apiKey : apiKey[..8],
            maxConcurrent);
        return null;
    }

    private sealed class SemaphoreLease : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private int _disposed;

        public SemaphoreLease(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _semaphore.Release();
            }
        }
    }

    private async Task<string> ExecuteBulkGetAsync(
        Dictionary<string, object> arguments,
        CancellationToken cancellationToken)
    {
        var context = _httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("No HttpContext available");
        var apiKey = context.Items["ApiKey"] as string
            ?? throw new InvalidOperationException("No API key available for self-hosted mode");
        var baseUrl = (_configuration["Knowz:BaseUrl"]
            ?? throw new InvalidOperationException("Knowz:BaseUrl is not configured")).TrimEnd('/');

        if (!arguments.TryGetValue("ids", out var idsObj) || idsObj == null)
            return JsonSerializer.Serialize(new { error = "ids parameter is required" });

        // Parse the ids - could be a JsonElement array or a List
        var ids = new List<string>();
        if (idsObj is JsonElement je && je.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in je.EnumerateArray())
                ids.Add(item.GetString() ?? item.ToString());
        }
        else if (idsObj is IEnumerable<object> enumerable)
        {
            ids.AddRange(enumerable.Select(o => o.ToString()!));
        }
        else
        {
            ids.Add(idsObj.ToString()!);
        }

        // Limit to 100 items
        if (ids.Count > 100)
            ids = ids.Take(100).ToList();

        var client = _httpClientFactory.CreateClient("McpApiClient");
        var results = new List<object>();

        // Process in batches of 10 (parallel within each batch)
        foreach (var batch in ids.Chunk(10))
        {
            var tasks = batch.Select(async id =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get,
                    $"{baseUrl}/api/v1/knowledge/{Uri.EscapeDataString(id)}");
                request.Headers.Add("X-Api-Key", apiKey);

                try
                {
                    var response = await client.SendAsync(request, cancellationToken);
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(body))
                        return JsonSerializer.Deserialize<object>(body);
                    return (object)new { id, error = $"HTTP {(int)response.StatusCode}" };
                }
                catch (Exception ex)
                {
                    return (object)new { id, error = ex.Message };
                }
            });

            var batchResults = await Task.WhenAll(tasks);
            results.AddRange(batchResults.Where(r => r != null)!);
        }

        return JsonSerializer.Serialize(results);
    }

    private async Task<string> ExecuteUploadFileAsync(
        Dictionary<string, object> arguments,
        CancellationToken cancellationToken)
    {
        var fileName = GetStringArgument(arguments, "fileName");
        if (string.IsNullOrWhiteSpace(fileName))
            return JsonSerializer.Serialize(new { error = "fileName is required" });
        if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..", StringComparison.Ordinal))
            return JsonSerializer.Serialize(new { error = "fileName must not contain path separators or '..' path segments" });

        var contentBase64 = GetStringArgument(arguments, "contentBase64");
        if (string.IsNullOrWhiteSpace(contentBase64))
            return JsonSerializer.Serialize(new { error = "contentBase64 is required and must contain non-empty file bytes" });

        var target = GetStringArgument(arguments, "target")?.Trim().ToLowerInvariant();
        var validTargets = new[] { "knowledge", "new-knowledge", "inbox", "standalone" };
        if (string.IsNullOrWhiteSpace(target) || !validTargets.Contains(target))
            return JsonSerializer.Serialize(new { error = "target is required and must be one of: knowledge, new-knowledge, inbox, standalone" });

        var targetId = GetStringArgument(arguments, "targetId");
        var vaultId = GetStringArgument(arguments, "vaultId");
        var title = GetStringArgument(arguments, "title");
        // `description` is deliberately NOT read: the self-hosted attach route binds
        // AttachFileRequest(Guid FileRecordId) only (FileEndpoints.cs:119), so there is nowhere to
        // put it. Rather than accept it silently, its advertised parameter prose is overlaid at
        // startup to say it has no effect on this edition (SelfHostedToolVisibility, R8-class).
        if (target == "knowledge" && string.IsNullOrWhiteSpace(targetId))
            return JsonSerializer.Serialize(new { error = "targetId is required for target='knowledge'" });
        if (target == "knowledge" && !string.IsNullOrWhiteSpace(vaultId))
            return JsonSerializer.Serialize(new { error = "vaultId is rejected for target='knowledge'; use target='new-knowledge' to create knowledge in a vault" });
        if (target == "knowledge" && !string.IsNullOrWhiteSpace(title))
            return JsonSerializer.Serialize(new { error = "title is rejected for target='knowledge'; the existing knowledge item keeps its title" });
        if (target == "new-knowledge" && string.IsNullOrWhiteSpace(vaultId))
            return JsonSerializer.Serialize(new { error = "vaultId is required for target='new-knowledge'; auto-routing is not available" });
        if (target == "new-knowledge" && !string.IsNullOrWhiteSpace(targetId))
            return JsonSerializer.Serialize(new { error = "targetId is rejected for target='new-knowledge'" });
        if ((target is "inbox" or "standalone") &&
            (!string.IsNullOrWhiteSpace(targetId) || !string.IsNullOrWhiteSpace(vaultId)))
        {
            return JsonSerializer.Serialize(new { error = $"targetId and vaultId are rejected for target='{target}'" });
        }

        var maxInlineBytes = Math.Max(
            1,
            _configuration.GetValue<long?>("Mcp:Upload:MaxInlineBytes") ?? 8_388_608L);
        var encodedUpperBound = EstimateBase64DecodedLength(contentBase64);
        if (encodedUpperBound > maxInlineBytes)
            return InlineUploadSizeError(encodedUpperBound, maxInlineBytes);

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(contentBase64);
        }
        catch (FormatException)
        {
            return JsonSerializer.Serialize(new { error = "contentBase64 is not valid base64" });
        }

        if (bytes.Length == 0)
            return JsonSerializer.Serialize(new { error = "contentBase64 must decode to non-empty file bytes" });
        if (bytes.LongLength > maxInlineBytes)
            return InlineUploadSizeError(bytes.LongLength, maxInlineBytes);

        var contentType = GetStringArgument(arguments, "contentType");
        if (string.IsNullOrWhiteSpace(contentType))
            contentType = InferUploadContentType(fileName);

        var context = _httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("No HttpContext available");
        var apiKey = context.Items["ApiKey"] as string
            ?? throw new InvalidOperationException("No API key available for self-hosted mode");
        var baseUrl = (_configuration["Knowz:BaseUrl"]
            ?? throw new InvalidOperationException("Knowz:BaseUrl is not configured")).TrimEnd('/');
        var client = _httpClientFactory.CreateClient("McpApiClient");

        // MCP_SelfHostedUploadBackend R5-SH: the self-hosted API has exactly ONE upload route —
        // multipart POST /api/v1/files/upload, whose minimal-API handler binds `IFormFile file` BY
        // NAME. The chunked initialize -> streaming -> complete trio this branch used to call has
        // never existed in Knowz.SelfHosted.API, so upload_file 404'd on its first hop from the day
        // it shipped. Do not reintroduce those paths without a SelfHostedRouteManifest entry.
        using var uploadContent = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = ParseContentTypeOrOctetStream(contentType);
        uploadContent.Add(fileContent, "file", fileName);

        using var uploadRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v1/files/upload");
        uploadRequest.Headers.Add("X-Api-Key", apiKey);
        uploadRequest.Content = uploadContent;

        using var uploadResponse = await client.SendAsync(uploadRequest, cancellationToken);
        var uploadBody = await uploadResponse.Content.ReadAsStringAsync(cancellationToken);
        // The route answers 201 Created, not 200 — IsSuccessStatusCode covers both.
        if (!uploadResponse.IsSuccessStatusCode)
            return MapApiError(uploadResponse, uploadBody);

        if (!TryReadUploadedFileRecordId(uploadBody, out var uploadedFileRecordId, out var uploadError))
            return JsonSerializer.Serialize(new { error = uploadError });

        var warnings = new List<object>();
        var status = "success";
        var aiProcessing = "not-queued";
        Guid? knowledgeId = null;
        Guid? inboxItemId = null;

        if (target == "standalone")
        {
            warnings.Add(new
            {
                code = "STANDALONE_ORPHAN",
                message = "This file is not attached to any knowledge item. It appears under the Files browser's orphan filter and is not visible to download-restricted users."
            });
        }
        else if (target == "knowledge")
        {
            var attachBody = new Dictionary<string, object?>
            {
                ["fileRecordId"] = uploadedFileRecordId.ToString()
            };
            using var attachRequest = CreateJsonRequest(
                HttpMethod.Post,
                $"{baseUrl}/api/v1/knowledge/{Uri.EscapeDataString(targetId!)}/attachments",
                apiKey,
                attachBody);
            using var attachResponse = await client.SendAsync(attachRequest, cancellationToken);
            var attachResponseBody = await attachResponse.Content.ReadAsStringAsync(cancellationToken);
            if (attachResponse.IsSuccessStatusCode)
            {
                knowledgeId = Guid.TryParse(targetId, out var parsedTargetId) ? parsedTargetId : null;
                // FileStorageService.AttachToKnowledgeAsync extracts text inline and then calls
                // IEnrichmentWriter.EnqueueAsync for the parent item (best-effort). Read from the
                // code, not assumed — MCP_SelfHostedUploadBackend R9-SH.
                aiProcessing = "queued";
            }
            else
            {
                status = "partial";
                warnings.Add(new
                {
                    code = "ATTACHMENT_LINK_FAILED",
                    message = $"File {uploadedFileRecordId} was stored, but attaching it returned HTTP {(int)attachResponse.StatusCode}. Retry with attach_files. Details: {attachResponseBody}"
                });
            }
        }
        else
        {
            // R6-SH: 'inbox' and 'new-knowledge' are not expressible on this edition — the upload
            // route takes no createAs/vaultId/title, POST /api/v1/inbox takes a text body and cannot
            // carry a file record, and POST /api/v1/knowledge requires content. The bytes ARE stored,
            // so this is a partial, never a 404 and never a "success".
            status = "partial";
            var alternative = target == "inbox"
                ? "create the inbox item with create_inbox_item, convert it to knowledge, then attach this fileRecordId with attach_files"
                : "create the knowledge item with create_knowledge, then attach this fileRecordId with attach_files";
            warnings.Add(new
            {
                code = "TARGET_NOT_SUPPORTED_SELFHOSTED",
                message = $"target '{target}' is not supported in Knowz Self-Hosted. File {uploadedFileRecordId} was stored; to finish, {alternative} (or call upload_file with target 'knowledge' against an existing item)."
            });
        }

        var response = new Dictionary<string, object?>
        {
            ["status"] = status,
            ["fileRecordId"] = uploadedFileRecordId,
            ["fileName"] = SanitizeUploadFileName(fileName),
            ["fileSize"] = bytes.LongLength,
            ["contentType"] = contentType,
            // Hosted emits mediaType unconditionally (nullable) and warnings only when non-empty
            // (src/Knowz.API/Services/Mcp/McpToolService.cs:8599,8613). An agent must not have to
            // know which backend mode it is talking to, so the SH envelope matches key-for-key.
            // The SH FileUploadResult carries no media classification, hence a null here.
            ["mediaType"] = null,
            ["target"] = target,
            ["knowledgeId"] = knowledgeId,
            ["inboxItemId"] = inboxItemId,
            ["aiProcessing"] = aiProcessing,
            ["message"] = BuildUploadMessage(fileName, bytes.LongLength, target, knowledgeId, inboxItemId, aiProcessing, status)
        };
        if (warnings.Count > 0)
            response["warnings"] = warnings;
        return JsonSerializer.Serialize(response);
    }

    private static System.Net.Http.Headers.MediaTypeHeaderValue ParseContentTypeOrOctetStream(string contentType)
    {
        return System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var parsed) && parsed is not null
            ? parsed
            : new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
    }

    /// <summary>
    /// Reads the self-hosted FileUploadResult, which is returned unwrapped (no { data: ... } envelope).
    /// A body carrying success:false is a failure whatever the status code said.
    /// </summary>
    private static bool TryReadUploadedFileRecordId(string body, out Guid fileRecordId, out string? error)
    {
        fileRecordId = Guid.Empty;
        error = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Upload response was not a JSON object";
                return false;
            }

            if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
            {
                error = "The self-hosted API reported the upload as unsuccessful";
                return false;
            }

            if (!root.TryGetProperty("fileRecordId", out var id) ||
                !Guid.TryParse(id.ValueKind == JsonValueKind.String ? id.GetString() : id.ToString(), out fileRecordId))
            {
                error = "Upload response was missing fileRecordId";
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            error = "Upload response was not valid JSON";
            return false;
        }
    }

    /// <summary>
    /// MCP_SelfHostedToolContract R10. count_knowledge cannot be a ToolMapping: a mapping can neither
    /// force pageSize=1 nor reshape the response. The old mapping pointed at the UNFILTERED
    /// /knowledge/stats route with QueryParams:null, so every filtered question got a tenant-wide
    /// total — a wrong answer, which is worse than an error.
    /// </summary>
    private async Task<string> ExecuteCountKnowledgeAsync(
        Dictionary<string, object> arguments,
        CancellationToken cancellationToken)
    {
        var context = _httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("No HttpContext available");
        var apiKey = context.Items["ApiKey"] as string
            ?? throw new InvalidOperationException("No API key available for self-hosted mode");
        var baseUrl = (_configuration["Knowz:BaseUrl"]
            ?? throw new InvalidOperationException("Knowz:BaseUrl is not configured")).TrimEnd('/');

        (string Argument, string Wire)[] filterMap =
        {
            ("knowledgeType", "type"),
            ("titlePattern", "title"),
            ("fileNamePattern", "fileName"),
            ("startDate", "startDate"),
            ("endDate", "endDate"),
            ("vaultId", "vaultId")
        };

        var queryParts = new List<string> { "pageSize=1" };
        var filters = new Dictionary<string, object?>();
        foreach (var (argument, wire) in filterMap)
        {
            if (!arguments.TryGetValue(argument, out var value) || value is null)
                continue;
            var rendered = value is JsonElement element
                ? element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString()
                : value.ToString();
            if (string.IsNullOrWhiteSpace(rendered))
                continue;
            queryParts.Add($"{HttpUtility.UrlEncode(wire)}={HttpUtility.UrlEncode(rendered)}");
            filters[argument] = rendered;
        }

        var url = $"{baseUrl}/api/v1/knowledge?{string.Join("&", queryParts)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Api-Key", apiKey);

        var client = _httpClientFactory.CreateClient("McpApiClient");
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            return MapApiError(response, body);

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("totalItems", out var totalItems) ||
                !totalItems.TryGetInt64(out var count))
            {
                // Never invent a 0: an unreadable body is an error, not "no matches".
                return JsonSerializer.Serialize(new { error = "The knowledge list response did not contain totalItems" });
            }

            return JsonSerializer.Serialize(new { count, filters });
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new { error = "The knowledge list response was not valid JSON" });
        }
    }

    // R12 is inert on self-hosted: a single-call multipart upload has no session to release, so
    // the best-effort DELETE /api/v1/files/upload/{uploadId} cleanup has no counterpart here.

    private static HttpRequestMessage CreateJsonRequest(
        HttpMethod method,
        string url,
        string apiKey,
        object body)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Api-Key", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }

    private static string? GetStringArgument(Dictionary<string, object> arguments, string name)
    {
        if (!arguments.TryGetValue(name, out var value) || value is null)
            return null;
        return value is JsonElement element
            ? element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString()
            : value.ToString();
    }

    private static string MapApiError(HttpResponseMessage response, string body)
    {
        var retryAfterSeconds = response.Headers.RetryAfter?.Delta?.TotalSeconds is double delta
            ? (int)Math.Ceiling(delta)
            : response.Headers.TryGetValues("Retry-After", out var values) && int.TryParse(values.FirstOrDefault(), out var parsed)
                ? parsed
                : (int?)null;
        return JsonSerializer.Serialize(new
        {
            error = $"API returned {(int)response.StatusCode}",
            status = (int)response.StatusCode,
            retryAfterSeconds,
            details = body
        });
    }

    private static string InlineUploadSizeError(long actualBytes, long limitBytes) =>
        JsonSerializer.Serialize(new
        {
            error = $"File is {FormatUploadBytes(actualBytes)}; the inline MCP upload limit is {FormatUploadBytes(limitBytes)}. " +
                "Upload larger files through the web UI or POST them directly to " +
                "/api/v1/files/upload (multipart, 500 MB ceiling), then link the returned fileRecordId with attach_files."
        });

    private static string InferUploadContentType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".md" => "text/markdown",
            ".json" => "application/json",
            ".csv" => "text/csv",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".m4a" => "audio/mp4",
            ".mp4" => "video/mp4",
            ".mov" => "video/quicktime",
            ".zip" => "application/zip",
            _ => "application/octet-stream"
        };

    private static long EstimateBase64DecodedLength(string value)
    {
        long characterCount = 0;
        char previous = '\0';
        char last = '\0';
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
                continue;
            characterCount++;
            previous = last;
            last = character;
        }

        var padding = last == '=' ? previous == '=' ? 2 : 1 : 0;
        return Math.Max(0, ((characterCount + 3L) / 4L * 3L) - padding);
    }

    private static string SanitizeUploadFileName(string fileName) => fileName
        .Replace("<", string.Empty, StringComparison.Ordinal)
        .Replace(">", string.Empty, StringComparison.Ordinal)
        .Replace("{{", string.Empty, StringComparison.Ordinal)
        .Replace("}}", string.Empty, StringComparison.Ordinal)
        .Replace("###", string.Empty, StringComparison.Ordinal);

    private static string BuildUploadMessage(
        string fileName,
        long fileSize,
        string target,
        Guid? knowledgeId,
        Guid? inboxItemId,
        string aiProcessing,
        string status)
    {
        var destination = target switch
        {
            "knowledge" when knowledgeId.HasValue => $" and attached it to knowledge item {knowledgeId}",
            "knowledge" => " but could not attach it to the knowledge item",
            "new-knowledge" or "inbox" => $" but this edition cannot create a '{target}' item from an upload",
            _ => " as a standalone orphan file"
        };
        var processing = aiProcessing == "queued"
            ? " AI processing queued."
            : " AI processing was not queued.";
        return $"Uploaded {SanitizeUploadFileName(fileName)} ({FormatUploadBytes(fileSize)}){destination}.{processing}";
    }

    private static string FormatUploadBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L) return $"{bytes / (1024d * 1024d):0.#} MB";
        if (bytes >= 1024L) return $"{bytes / 1024d:0.#} KB";
        return $"{bytes} bytes";
    }

    private async Task<string> ExecuteAttachFilesAsync(
        Dictionary<string, object> arguments,
        CancellationToken cancellationToken)
    {
        var context = _httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("No HttpContext available");
        var apiKey = context.Items["ApiKey"] as string
            ?? throw new InvalidOperationException("No API key available for self-hosted mode");
        var baseUrl = (_configuration["Knowz:BaseUrl"]
            ?? throw new InvalidOperationException("Knowz:BaseUrl is not configured")).TrimEnd('/');

        if (!arguments.TryGetValue("targetId", out var targetIdObj) || targetIdObj == null)
            return JsonSerializer.Serialize(new { error = "targetId parameter is required" });

        if (!arguments.TryGetValue("targetType", out var targetTypeObj) || targetTypeObj == null)
            return JsonSerializer.Serialize(new { error = "targetType parameter is required" });

        var targetId = targetIdObj is JsonElement teId ? teId.GetString() : targetIdObj.ToString();
        var targetType = targetTypeObj is JsonElement teTy ? teTy.GetString() : targetTypeObj.ToString();

        if (targetType != "knowledge")
            return JsonSerializer.Serialize(new { error = $"targetType '{targetType}' is not supported. Only 'knowledge' is supported." });

        if (!arguments.TryGetValue("fileRecordIds", out var idsObj) || idsObj == null)
            return JsonSerializer.Serialize(new { error = "fileRecordIds parameter is required" });

        var fileRecordIds = new List<string>();
        if (idsObj is JsonElement je && je.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in je.EnumerateArray())
                fileRecordIds.Add(item.GetString() ?? item.ToString());
        }
        else if (idsObj is IEnumerable<object> enumerable)
        {
            fileRecordIds.AddRange(enumerable.Select(o => o.ToString()!));
        }
        else
        {
            fileRecordIds.Add(idsObj.ToString()!);
        }

        if (fileRecordIds.Count > 100)
            fileRecordIds = fileRecordIds.Take(100).ToList();

        var client = _httpClientFactory.CreateClient("McpApiClient");
        var results = new List<object>();
        var errors = new List<object>();
        var attachedCount = 0;

        foreach (var fileRecordId in fileRecordIds)
        {
            var url = $"{baseUrl}/api/v1/knowledge/{Uri.EscapeDataString(targetId!)}/attachments";
            var body = JsonSerializer.Serialize(new { fileRecordId });

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("X-Api-Key", apiKey);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            try
            {
                var response = await client.SendAsync(request, cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    attachedCount++;
                    results.Add(new { fileRecordId, status = "attached" });
                }
                else
                {
                    errors.Add(new { fileRecordId, error = $"HTTP {(int)response.StatusCode}", details = responseBody });
                }
            }
            catch (Exception ex)
            {
                errors.Add(new { fileRecordId, error = ex.Message });
            }
        }

        return JsonSerializer.Serialize(new
        {
            status = errors.Count == 0 ? "success" : "partial",
            targetType,
            targetId,
            attachedCount,
            results,
            errors
        });
    }
}

internal record ToolMapping(
    HttpMethod Method,
    string PathTemplate,
    string[]? PathParams,
    string[]? QueryParams,
    Dictionary<string, string>? ArgRenames,
    string[]? BodyParams = null);
