using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Knowz.MCP.Middleware;
using Knowz.MCP.Services;
using Knowz.MCP.Services.Session;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Knowz.MCP.Tools;

/// <summary>
/// MCP tools that delegate to IToolBackend.
/// The backend forwards all requests to the Knowz API (proxy mode).
/// </summary>
[McpServerToolType]
public class KnowzProxyTools
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum GraphQueryOperation
    {
        [JsonStringEnumMemberName("scope")] Scope,
        [JsonStringEnumMemberName("neighbors")] Neighbors,
        [JsonStringEnumMemberName("search")] Search,
        [JsonStringEnumMemberName("path")] Path,
        [JsonStringEnumMemberName("cross-repo")] CrossRepo,
        [JsonStringEnumMemberName("god-nodes")] GodNodes,
        [JsonStringEnumMemberName("communities")] Communities,
        [JsonStringEnumMemberName("surprising-connections")] SurprisingConnections,
        [JsonStringEnumMemberName("impact_radius")] ImpactRadius,
        [JsonStringEnumMemberName("endpoints")] Endpoints,
        [JsonStringEnumMemberName("endpoint-orphans")] EndpointOrphans
    }

    private readonly IToolBackend _backend;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<KnowzProxyTools> _logger;

    public KnowzProxyTools(
        IToolBackend backend,
        IHttpContextAccessor httpContextAccessor,
        ILogger<KnowzProxyTools> logger)
    {
        _backend = backend;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    private HttpContext GetHttpContext()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null)
        {
            _logger.LogError("HttpContext is null in tool execution");
            throw new UnauthorizedAccessException("HttpContext not available");
        }
        return httpContext;
    }

    private static CallToolResult CreateInvalidArgumentsResult(string parameterName)
    {
        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = JsonSerializer.Serialize(new
                    {
                        error = $"The '{parameterName}' argument is required.",
                        code = "invalid_arguments"
                    })
                }
            ],
            IsError = true
        };
    }

    /// <summary>
    /// Shared required-arg guard (#847 / #592). A missing C# parameter with no default
    /// makes the MCP SDK marshaller throw ArgumentException before the tool runs.
    /// Default the parameter and return a structured tool error instead.
    /// </summary>
    private static bool TryRequireArg(
        [NotNullWhen(true)] string? value,
        string parameterName,
        [NotNullWhen(false)] out CallToolResult? error)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            error = null;
            return true;
        }

        error = CreateInvalidArgumentsResult(parameterName);
        return false;
    }

    private static bool TryRequireArg<T>(
        [NotNullWhen(true)] T[]? value,
        string parameterName,
        [NotNullWhen(false)] out CallToolResult? error)
    {
        if (value is { Length: > 0 })
        {
            error = null;
            return true;
        }

        error = CreateInvalidArgumentsResult(parameterName);
        return false;
    }

    [McpServerTool(Name = "search_knowledge")]
    [Description("Find, search, look up, locate, or retrieve any information, code, documentation, specs, notes, files, transcripts, or content in the knowledge base. Use this for any query about what exists, what was written, what was saved, or to find something by keyword, topic, or concept. Supports filtering by tags and date range. Status, current, board, and ops questions are recency-prioritized server-side; pass startDate only when the user asked for a date range.")]
    public async Task<object> SearchKnowledge(
        [Description("Natural language search query")] string? query = null,
        [Description("Maximum number of results (default: 10)")] int limit = 10,
        [Description("Vault ID to scope the search. RECOMMENDED: without this, results from ALL vaults are mixed together which may produce blended answers across unrelated projects. Pass the vault ID when the user is working within a specific vault or project.")] string? vaultId = null,
        [Description("When searching within a vault, also include results from child vaults (default: true)")] bool includeChildVaults = true,
        [Description("Optional array of tag names to filter results")] string[]? tags = null,
        [Description("If true, require all tags (AND); if false, match any tag (OR). Default: false")] bool requireAllTags = false,
        [Description("Optional start date for filtering (ISO 8601 format, e.g., 2026-01-08T22:52:47+00:00)")] string? startDate = null,
        [Description("Optional end date for filtering (ISO 8601 format, e.g., 2026-01-08T22:52:47+00:00)")] string? endDate = null,
        [Description("Include vaults shared with this identity from other tenants. Only set true when the user explicitly asks to include shared vaults.")] bool includeSharedVaults = false,
        CancellationToken cancellationToken = default)
    {
        if (!TryRequireArg(query, "query", out var missing)) return missing;

        var httpContext = GetHttpContext();
        var effectiveVaultId = httpContext.ResolveVaultId(vaultId);

        _logger.LogInformation("SDK Tool: search_knowledge - query='{Query}', limit={Limit}, vault={VaultId}, includeChildren={IncludeChildren}, includeShared={IncludeShared}, tags={Tags}",
            query, limit, effectiveVaultId ?? "all", includeChildVaults, includeSharedVaults, tags != null ? string.Join(",", tags) : "none");

        var args = new Dictionary<string, object>
        {
            ["query"] = query,
            ["limit"] = limit,
            ["includeChildVaults"] = includeChildVaults,
            ["includeSharedVaults"] = includeSharedVaults
        };
        if (!string.IsNullOrEmpty(effectiveVaultId))
        {
            args["vaultId"] = effectiveVaultId;
        }
        if (tags != null && tags.Length > 0)
        {
            args["tags"] = tags;
            args["requireAllTags"] = requireAllTags;
        }
        if (!string.IsNullOrEmpty(startDate))
        {
            args["startDate"] = startDate;
        }
        if (!string.IsNullOrEmpty(endDate))
        {
            args["endDate"] = endDate;
        }

        return await _backend.ExecuteToolAsync("search_knowledge", args, cancellationToken);
    }

    [McpServerTool(Name = "graph_query")]
    [Description("Query the tenant- and vault-scoped code and knowledge graph. Returns bounded structural evidence and opaque graph intelligence identifiers; results are not a completeness proof.")]
    public async Task<object> GraphQuery(
        [Description("Graph operation")] GraphQueryOperation? op = null,
        [Description("Optional vault GUID. A connection sandbox overrides this value.")] string? vaultId = null,
        [Description("Repository GUID for scope or cross-repo operations")] string? repositoryId = null,
        [Description("Repository-relative folder filter for scope")] string? path = null,
        [Description("Traversal depth")] int? depth = null,
        [Description("Comma-separated node types")] string? types = null,
        [Description("Comma-separated additive node types")] string? includeNodeTypes = null,
        [Description("Include code-symbol nodes")] bool? includeCode = null,
        [Description("Comma-separated additive graph kinds")] string? includeKinds = null,
        [Description("Node GUID for neighbors")] string? nodeId = null,
        [Description("Search text")] string? q = null,
        [Description("Result limit")] int? limit = null,
        [Description("Source node GUID for path")] string? fromId = null,
        [Description("Target node GUID for path")] string? toId = null,
        [Description("Maximum path hops")] int? maxHops = null,
        [Description("Impact-radius entity GUID")] string? entityId = null,
        [Description("Impact-radius file GUIDs")] string[]? fileIds = null,
        [Description("Impact-radius sync run GUID")] string? runId = null,
        [Description("Optional response token budget")] int? budgetTokens = null,
        [Description("HTTP endpoint verb filter")] string? verb = null,
        [Description("HTTP endpoint method filter")] string? method = null,
        [Description("HTTP endpoint route filter")] string? route = null,
        [Description("Return only orphaned endpoints")] bool? orphansOnly = null,
        CancellationToken cancellationToken = default)
    {
        if (!op.HasValue)
        {
            return CreateInvalidArgumentsResult("op");
        }

        var httpContext = GetHttpContext();
        var args = new Dictionary<string, object>
        {
            ["op"] = GraphOperationName(op.Value)
        };
        AddPresent(args, "vaultId", httpContext.ResolveVaultId(vaultId));
        AddPresent(args, "repositoryId", repositoryId);
        AddPresent(args, "path", path);
        AddPresent(args, "depth", depth);
        AddPresent(args, "types", types);
        AddPresent(args, "includeNodeTypes", includeNodeTypes);
        AddPresent(args, "includeCode", includeCode);
        AddPresent(args, "includeKinds", includeKinds);
        AddPresent(args, "nodeId", nodeId);
        AddPresent(args, "q", q);
        AddPresent(args, "limit", limit);
        AddPresent(args, "fromId", fromId);
        AddPresent(args, "toId", toId);
        AddPresent(args, "maxHops", maxHops);
        AddPresent(args, "entityId", entityId);
        AddPresent(args, "fileIds", fileIds is { Length: > 0 } ? fileIds : null);
        AddPresent(args, "runId", runId);
        AddPresent(args, "budgetTokens", budgetTokens);
        AddPresent(args, "verb", verb);
        AddPresent(args, "method", method);
        AddPresent(args, "route", route);
        AddPresent(args, "orphansOnly", orphansOnly);

        var result = await _backend.ExecuteToolAsync("graph_query", args, cancellationToken);
        return NormalizeGraphUnavailable(result, GraphOperationName(op.Value));
    }

    private static void AddPresent(Dictionary<string, object> args, string key, object? value)
    {
        if (value is string text && string.IsNullOrWhiteSpace(text)) return;
        if (value is not null) args[key] = value;
    }

    private static string GraphOperationName(GraphQueryOperation op) => op switch
    {
        GraphQueryOperation.Scope => "scope",
        GraphQueryOperation.Neighbors => "neighbors",
        GraphQueryOperation.Search => "search",
        GraphQueryOperation.Path => "path",
        GraphQueryOperation.CrossRepo => "cross-repo",
        GraphQueryOperation.GodNodes => "god-nodes",
        GraphQueryOperation.Communities => "communities",
        GraphQueryOperation.SurprisingConnections => "surprising-connections",
        GraphQueryOperation.ImpactRadius => "impact_radius",
        GraphQueryOperation.Endpoints => "endpoints",
        GraphQueryOperation.EndpointOrphans => "endpoint-orphans",
        _ => throw new ArgumentOutOfRangeException(nameof(op))
    };

    private static string NormalizeGraphUnavailable(string result, string op)
    {
        try
        {
            using var json = JsonDocument.Parse(result);
            if (!json.RootElement.TryGetProperty("error", out var errorElement)) return result;
            var error = errorElement.GetString();
            if (error is "impact_radius unavailable" or "HTTP surface map unavailable")
            {
                return JsonSerializer.Serialize(new
                {
                    error,
                    code = "unavailable",
                    op = op is "endpoints" or "endpoint-orphans" ? "endpoints" : "impact_radius"
                });
            }
        }
        catch (JsonException)
        {
            // Preserve successful non-JSON graph render formats such as token-budgeted TSV.
        }
        return result;
    }

    [McpServerTool(Name = "advanced_search")]
    [Description("Agentic-compatible advanced search over knowledge with deterministic source summaries. Use when a higher-quality answer needs inspectable evidence before synthesis.")]
    public async Task<object> AdvancedSearch(
        [Description("Natural language or exact search query")] string? query = null,
        [Description("Maximum number of results (default: 10)")] int limit = 10,
        [Description("Vault ID to scope the search. May be overridden by connection settings.")] string? vaultId = null,
        [Description("When searching within a vault, also include child vaults (default: true)")] bool includeChildVaults = true,
        [Description("Include shared vaults only when the user explicitly asks for them.")] bool includeSharedVaults = false,
        CancellationToken cancellationToken = default)
    {
        if (!TryRequireArg(query, "query", out var missing)) return missing;

        var httpContext = GetHttpContext();
        var effectiveVaultId = httpContext.ResolveVaultId(vaultId);

        var args = new Dictionary<string, object>
        {
            ["query"] = query,
            ["limit"] = limit,
            ["includeChildVaults"] = includeChildVaults,
            ["includeSharedVaults"] = includeSharedVaults
        };
        if (!string.IsNullOrEmpty(effectiveVaultId))
        {
            args["vaultId"] = effectiveVaultId;
        }

        return await _backend.ExecuteToolAsync("advanced_search", args, cancellationToken);
    }

    [McpServerTool(Name = "list_matching_items")]
    [Description("List or search matching knowledge items for Agentic collection workflows.")]
    public async Task<string> ListMatchingItems(
        [Description("Optional search query. When supplied, behaves like advanced_search.")] string? query = null,
        [Description("Page number when listing (default: 1)")] int page = 1,
        [Description("Items per page when listing (default: 20)")] int pageSize = 20,
        [Description("Maximum search results when query is supplied (default: 10)")] int limit = 10,
        [Description("Vault ID to scope results. May be overridden by connection settings.")] string? vaultId = null,
        [Description("When scoped to a vault, include child vaults (default: true)")] bool includeChildVaults = true,
        CancellationToken cancellationToken = default)
    {
        var httpContext = GetHttpContext();
        var effectiveVaultId = httpContext.ResolveVaultId(vaultId);

        var args = new Dictionary<string, object>
        {
            ["page"] = page,
            ["pageSize"] = pageSize,
            ["limit"] = limit,
            ["includeChildVaults"] = includeChildVaults
        };
        if (!string.IsNullOrWhiteSpace(query)) args["query"] = query;
        if (!string.IsNullOrEmpty(effectiveVaultId)) args["vaultId"] = effectiveVaultId;

        return await _backend.ExecuteToolAsync("list_matching_items", args, cancellationToken);
    }

    [McpServerTool(Name = "inspect_document_map")]
    [Description("Inspect a bounded structural map of a knowledge document without dumping the full document.")]
    public async Task<object> InspectDocumentMap(
        [Description("Knowledge item GUID")] string? knowledgeId = null,
        [Description("Maximum sections to return (default: 50, max: 200)")] int maxSections = 50,
        CancellationToken cancellationToken = default)
    {
        if (!TryRequireArg(knowledgeId, "knowledgeId", out var missing)) return missing;

        var args = new Dictionary<string, object>
        {
            ["knowledgeId"] = knowledgeId,
            ["maxSections"] = maxSections
        };
        return await _backend.ExecuteToolAsync("inspect_document_map", args, cancellationToken);
    }

    [McpServerTool(Name = "get_document_window")]
    [Description("Get a bounded text window from a knowledge document by character offset.")]
    public async Task<object> GetDocumentWindow(
        [Description("Knowledge item GUID")] string? knowledgeId = null,
        [Description("Zero-based character offset (default: 0)")] int startChar = 0,
        [Description("Maximum characters to return (default: 4000, max: 20000)")] int length = 4000,
        CancellationToken cancellationToken = default)
    {
        if (!TryRequireArg(knowledgeId, "knowledgeId", out var missing)) return missing;

        var args = new Dictionary<string, object>
        {
            ["knowledgeId"] = knowledgeId,
            ["startChar"] = startChar,
            ["length"] = length
        };
        return await _backend.ExecuteToolAsync("get_document_window", args, cancellationToken);
    }

    [McpServerTool(Name = "search_document_text")]
    [Description("Search exact text inside one document or accessible knowledge content. Returns bounded snippets and character offsets.")]
    public async Task<object> SearchDocumentText(
        [Description("Exact text to search for")] string? query = null,
        [Description("Optional knowledge item GUID to search within one document")] string? knowledgeId = null,
        [Description("Maximum snippets to return (default: 20, max: 100)")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (!TryRequireArg(query, "query", out var missing)) return missing;

        var args = new Dictionary<string, object>
        {
            ["query"] = query,
            ["limit"] = limit
        };
        if (!string.IsNullOrWhiteSpace(knowledgeId)) args["knowledgeId"] = knowledgeId;

        return await _backend.ExecuteToolAsync("search_document_text", args, cancellationToken);
    }

    [McpServerTool(Name = "get_knowledge_item")]
    [Description("Get, fetch, read, open, view, or retrieve the full details of a specific knowledge item by its ID. Use when you have an ID and need the complete content, metadata, or related items. Also use to drill into a search result.")]
    public async Task<object> GetKnowledgeItem(
        [Description("Knowledge item GUID")] string? id = null,
        [Description("Include related knowledge items (default: false)")] bool includeRelated = false,
        [Description("Maximum number of related items to return (default: 10)")] int relatedLimit = 10,
        CancellationToken cancellationToken = default)
    {
        if (!TryRequireArg(id, "id", out var missing)) return missing;

        _logger.LogInformation("SDK Tool: get_knowledge_item - id={Id}, includeRelated={IncludeRelated}", id, includeRelated);

        var args = new Dictionary<string, object> { ["id"] = id };
        if (includeRelated)
        {
            args["includeRelated"] = true;
            args["relatedLimit"] = relatedLimit;
        }
        return await _backend.ExecuteToolAsync("get_knowledge_item", args, cancellationToken);
    }

    [McpServerTool(Name = "list_topics")]
    [Description("List, show, browse, display, or see all topics, categories, themes, or subject areas in the knowledge base. Use when asking 'what topics exist', 'show me categories', 'what's organized', or exploring the knowledge structure. Timestamps are in ISO 8601 format.")]
    public async Task<string> ListTopics(
        [Description("Maximum number of topics (default: 50)")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("SDK Tool: list_topics - limit={Limit}", limit);

        var args = new Dictionary<string, object> { ["limit"] = limit };
        return await _backend.ExecuteToolAsync("list_topics", args, cancellationToken);
    }

    [McpServerTool(Name = "get_topic_details")]
    [Description("Get, view, explore, or retrieve detailed information about a specific topic, category, or theme including all related knowledge items. Use when drilling into a topic, exploring a category, or seeing everything related to a subject.")]
    public async Task<object> GetTopicDetails(
        [Description("Topic GUID")] string? id = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryRequireArg(id, "id", out var missing)) return missing;

        _logger.LogInformation("SDK Tool: get_topic_details - id={Id}", id);

        var args = new Dictionary<string, object> { ["id"] = id };
        return await _backend.ExecuteToolAsync("get_topic_details", args, cancellationToken);
    }

    [McpServerTool(Name = "list_vaults")]
    [Description("List, show, browse, display, or see all vaults, workspaces, projects, or collections available. Use when asking 'what vaults exist', 'show my projects', 'which workspaces', or exploring what's accessible. May be filtered if connection is scoped to a specific vault. Timestamps are in ISO 8601 format.")]
    public async Task<string> ListVaults(
        [Description("Include item counts (default: false)")] bool includeStats = false,
        [Description("Include vaults shared with this identity from other tenants. Only set true when the user explicitly asks to include shared vaults.")] bool includeSharedVaults = false,
        CancellationToken cancellationToken = default)
    {
        var httpContext = GetHttpContext();
        var sandboxVaultId = httpContext.GetSandboxVaultId();

        _logger.LogInformation("SDK Tool: list_vaults - includeStats={IncludeStats}, includeShared={IncludeShared}, sandboxed={IsSandboxed}",
            includeStats, includeSharedVaults, sandboxVaultId != null);

        var args = new Dictionary<string, object>
        {
            ["includeStats"] = includeStats,
            ["includeSharedVaults"] = includeSharedVaults
        };

        if (!string.IsNullOrEmpty(sandboxVaultId))
        {
            args["vaultId"] = sandboxVaultId;
        }

        return await _backend.ExecuteToolAsync("list_vaults", args, cancellationToken);
    }

    [McpServerTool(Name = "list_vault_contents")]
    [Description("List, show, browse, display, view, or see what's inside a vault, workspace, project, or collection. Use when asking 'what's in this vault', 'show me everything', 'list all items', 'recent additions', or browsing contents. Supports explicit shared vault IDs when the caller has a read grant. Supports filtering by tags, type (Note, Document, Transcript, Image, Video, Audio), and date range. Vault ID may be overridden by connection settings.")]
    public async Task<string> ListVaultContents(
        [Description("Vault GUID (may be overridden by connection settings)")] string? vaultId = null,
        [Description("Also include items from child/sub-vaults (default: true)")] bool includeChildVaults = true,
        [Description("Maximum number of items (default: 100)")] int limit = 100,
        [Description("Optional array of tag names to filter results")] string[]? tags = null,
        [Description("If true, require all tags (AND); if false, match any tag (OR). Default: false")] bool requireAllTags = false,
        [Description("Optional knowledge type filter (Note, Document, Transcript, Image, Video, Audio)")] string? knowledgeType = null,
        [Description("Optional start date for filtering (ISO 8601 format, e.g., 2026-01-08T22:52:47+00:00)")] string? startDate = null,
        [Description("Optional end date for filtering (ISO 8601 format, e.g., 2026-01-08T22:52:47+00:00)")] string? endDate = null,
        CancellationToken cancellationToken = default)
    {
        var httpContext = GetHttpContext();
        var effectiveVaultId = httpContext.ResolveVaultId(vaultId);

        if (string.IsNullOrEmpty(effectiveVaultId))
        {
            return JsonSerializer.Serialize(new { error = "Vault ID is required. Specify vaultId parameter or configure defaultVaultId in connection." });
        }

        _logger.LogInformation("SDK Tool: list_vault_contents - vaultId={VaultId}, includeChildren={IncludeChildren}, limit={Limit}, tags={Tags}",
            effectiveVaultId, includeChildVaults, limit, tags != null ? string.Join(",", tags) : "none");

        var args = new Dictionary<string, object> { ["vaultId"] = effectiveVaultId, ["includeChildVaults"] = includeChildVaults, ["limit"] = limit };
        if (tags != null && tags.Length > 0)
        {
            args["tags"] = tags;
            args["requireAllTags"] = requireAllTags;
        }
        if (!string.IsNullOrEmpty(knowledgeType))
        {
            args["knowledgeType"] = knowledgeType;
        }
        if (!string.IsNullOrEmpty(startDate))
        {
            args["startDate"] = startDate;
        }
        if (!string.IsNullOrEmpty(endDate))
        {
            args["endDate"] = endDate;
        }
        return await _backend.ExecuteToolAsync("list_vault_contents", args, cancellationToken);
    }

    [McpServerTool(Name = "find_entities")]
    [Description("Find, list, discover, or look up people, names, authors, contributors, places, locations, companies, organizations, events, dates, or any named entities mentioned anywhere in the knowledge base. Use when asking 'who', 'where', 'when', or looking for specific named things.")]
    public async Task<object> FindEntities(
        [Description("Type of entity to find (person, location, event)")] string? entityType = null,
        [Description("Optional search query to filter entity names")] string? query = null,
        [Description("Maximum results (default: 50)")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (!TryRequireArg(entityType, "entityType", out var missing)) return missing;

        _logger.LogInformation("SDK Tool: find_entities - entityType={EntityType}, query={Query}", entityType, query);

        var args = new Dictionary<string, object> { ["entityType"] = entityType, ["limit"] = limit };
        if (!string.IsNullOrEmpty(query))
        {
            args["query"] = query;
        }
        return await _backend.ExecuteToolAsync("find_entities", args, cancellationToken);
    }

    [McpServerTool(Name = "ask_question")]
    [Description("Answer any question, explain something, help understand, analyze, summarize, compare, or reason about code, documentation, architecture, decisions, or knowledge. Use for 'what is', 'how does', 'why', 'explain', 'tell me about', 'help me understand', or any inquiry requiring AI-powered reasoning with source references. Enable researchMode for complex questions needing thorough analysis.")]
    public async Task<object> AskQuestion(
        [Description("The question to ask about your knowledge base (required)")] string? question = null,
        [Description("Optional vault name to limit search scope (may be overridden by connection settings)")] string? vaultName = null,
        [Description("Vault ID to scope the answer. RECOMMENDED: without this, the AI searches ALL vaults and may blend answers from unrelated projects. Use list_vaults first to find the right vault ID if needed.")] string? vaultId = null,
        [Description("When scoped to a vault, also search child vaults (default: true)")] bool includeChildVaults = true,
        [Description("Optional conversation ID for multi-turn dialogue")] string? conversationId = null,
        [Description("Enable creative/adaptive reasoning mode")] bool creativeMode = false,
        [Description("Enable research mode for comprehensive, detailed answers with higher token limits (8000+). Use this for complex questions requiring thorough analysis.")] bool researchMode = false,
        [Description("Include vaults shared with this identity from other tenants. Only set true when the user explicitly asks to include shared vaults.")] bool includeSharedVaults = false,
        CancellationToken cancellationToken = default)
    {
        // Issue #592 / KNOWZ-DEV-1M2: a required C# parameter makes the MCP SDK marshaller
        // throw ArgumentException before this method runs. McpServerImpl then logs
        // ToolCallError at Error ("ask_question threw an unhandled exception"), which
        // Sentry records as a Sentry Error. Default the parameter and return a structured
        // tool error instead -- never throw for a missing client argument.
        if (!TryRequireArg(question, "question", out var missing)) return missing;

        var httpContext = GetHttpContext();
        var effectiveVaultId = httpContext.ResolveVaultId(vaultId);

        _logger.LogInformation("SDK Tool: ask_question - question length={Length}, vaultName={VaultName}, vaultId={VaultId}, includeChildren={IncludeChildren}, includeShared={IncludeShared}, researchMode={ResearchMode}",
            question.Length, vaultName, effectiveVaultId, includeChildVaults, includeSharedVaults, researchMode);

        var args = new Dictionary<string, object>
        {
            ["question"] = question,
            ["creativeMode"] = creativeMode,
            ["researchMode"] = researchMode,
            ["includeChildVaults"] = includeChildVaults,
            ["includeSharedVaults"] = includeSharedVaults
        };

        if (!string.IsNullOrEmpty(effectiveVaultId))
        {
            args["vaultId"] = effectiveVaultId;
        }
        else if (!string.IsNullOrEmpty(vaultName))
        {
            args["vaultName"] = vaultName;
        }

        if (!string.IsNullOrEmpty(conversationId))
        {
            args["conversationId"] = conversationId;
        }
        return await _backend.ExecuteToolAsync("ask_question", args, cancellationToken);
    }

    [McpServerTool(Name = "create_knowledge")]
    [Description("Save, store, create, add, remember, document, record, write, persist, or capture any information, notes, decisions, learnings, specs, documentation, or content for later retrieval. Use when the user wants to save something, document a decision, create a note, or store information. AI processing (summarization, entity extraction) runs automatically.")]
    public async Task<string> CreateKnowledge(
        [Description("Content of the knowledge item (optional if file attachments will be added separately)")] string? content = null,
        [Description("Title for the knowledge item (optional)")] string? title = null,
        [Description("Knowledge type: Note, Document, Transcript, Image, Video, Audio (default: Note)")] string knowledgeType = "Note",
        [Description("Optional vault ID to store the item (defaults to tenant's default vault)")] string? vaultId = null,
        [Description("Optional topic ID to associate with")] string? topicId = null,
        [Description("Optional array of tag names to apply")] string[]? tags = null,
        [Description("Optional source/reference for the content")] string? source = null,
        CancellationToken cancellationToken = default)
    {
        var httpContext = GetHttpContext();
        var effectiveVaultId = httpContext.ResolveVaultId(vaultId);

        _logger.LogInformation("SDK Tool: create_knowledge - title={Title}, type={Type}, vault={VaultId}",
            title ?? "(untitled)", knowledgeType, effectiveVaultId ?? "default");

        var args = new Dictionary<string, object>
        {
            ["knowledgeType"] = knowledgeType
        };

        if (!string.IsNullOrEmpty(content))
        {
            args["content"] = content;
        }
        if (!string.IsNullOrEmpty(title))
        {
            args["title"] = title;
        }
        if (!string.IsNullOrEmpty(effectiveVaultId))
        {
            args["vaultId"] = effectiveVaultId;
        }
        if (!string.IsNullOrEmpty(topicId))
        {
            args["topicId"] = topicId;
        }
        if (tags != null && tags.Length > 0)
        {
            args["tags"] = tags;
        }
        if (!string.IsNullOrEmpty(source))
        {
            args["source"] = source;
        }

        return await _backend.ExecuteToolAsync("create_knowledge", args, cancellationToken);
    }

    [McpServerTool(Name = "create_vault")]
    [Description("Create a new vault, workspace, project, or collection to organize knowledge. Supports creating child vaults under an existing parent vault for hierarchical organization.")]
    public async Task<object> CreateVault(
        [Description("Name for the new vault (required)")] string? name = null,
        [Description("Optional description of the vault's purpose")] string? description = null,
        [Description("Optional parent vault ID to create a child vault")] string? parentVaultId = null,
        [Description("Optional vault type: GeneralKnowledge, Business, Product, CodeBase, DailyDiary, QuestionAnswer, PersonBound, LocationBound")] string? vaultType = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryRequireArg(name, "name", out var missing)) return missing;

        var httpContext = GetHttpContext();
        var sandboxVaultId = httpContext.GetSandboxVaultId();

        if (!string.IsNullOrEmpty(sandboxVaultId) && string.IsNullOrEmpty(parentVaultId))
        {
            parentVaultId = sandboxVaultId;
        }

        _logger.LogInformation("SDK Tool: create_vault - name={Name}, parentVaultId={ParentVaultId}, vaultType={VaultType}",
            name, parentVaultId ?? "none", vaultType ?? "default");

        var args = new Dictionary<string, object> { ["name"] = name };
        if (!string.IsNullOrEmpty(description))
        {
            args["description"] = description;
        }
        if (!string.IsNullOrEmpty(parentVaultId))
        {
            args["parentVaultId"] = parentVaultId;
        }
        if (!string.IsNullOrEmpty(vaultType))
        {
            args["vaultType"] = vaultType;
        }

        return await _backend.ExecuteToolAsync("create_vault", args, cancellationToken);
    }

    [McpServerTool(Name = "update_knowledge")]
    [Description("Replace content, title, tags, or metadata of an existing knowledge item. Use when overwriting a document with entirely new content, changing tags, or moving items between vaults. All fields except ID are optional for partial updates. AI re-processing is triggered if content changes.")]
    public async Task<object> UpdateKnowledge(
        [Description("Knowledge item ID (required)")] string? id = null,
        [Description("Updated content (optional)")] string? content = null,
        [Description("Updated title (optional)")] string? title = null,
        [Description("Move to different vault (optional)")] string? vaultId = null,
        [Description("Change associated topic (optional)")] string? topicId = null,
        [Description("Replace tags (optional)")] string[]? tags = null,
        [Description("Update source/reference (optional)")] string? source = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryRequireArg(id, "id", out var missing)) return missing;

        var httpContext = GetHttpContext();
        var effectiveVaultId = !string.IsNullOrEmpty(vaultId) ? httpContext.ResolveVaultId(vaultId) : null;

        _logger.LogInformation("SDK Tool: update_knowledge - id={Id}, hasContent={HasContent}, hasTitle={HasTitle}",
            id, content != null, title != null);

        var args = new Dictionary<string, object> { ["id"] = id };

        if (!string.IsNullOrEmpty(content))
        {
            args["content"] = content;
        }
        if (!string.IsNullOrEmpty(title))
        {
            args["title"] = title;
        }
        if (!string.IsNullOrEmpty(effectiveVaultId))
        {
            args["vaultId"] = effectiveVaultId;
        }
        if (!string.IsNullOrEmpty(topicId))
        {
            args["topicId"] = topicId;
        }
        if (tags != null)
        {
            args["tags"] = tags;
        }
        if (!string.IsNullOrEmpty(source))
        {
            args["source"] = source;
        }

        return await _backend.ExecuteToolAsync("update_knowledge", args, cancellationToken);
    }

    [McpServerTool(Name = "amend_knowledge")]
    [Description("DEPRECATED (removal no earlier than 2026-08-01). Use amend_knowledge_async instead. This tool now returns immediately with { status: 'queued', amendRequestId, message } and the amendment is applied in the background. Poll via get_amend_request_status. Apply a natural language edit instruction to an existing knowledge item — add a section, update part of the content, remove a paragraph, fix information, or make any incremental change — without replacing the entire content.")]
    public async Task<object> AmendKnowledge(
        [Description("Knowledge item ID (required)")] string? id = null,
        [Description("Natural language instruction describing what to change, add, or remove from the existing content")] string? instruction = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryRequireArg(id, "id", out var missing)) return missing;
        if (!TryRequireArg(instruction, "instruction", out missing)) return missing;

        _logger.LogInformation("SDK Tool: amend_knowledge (DEPRECATED shim) - id={Id}, instructionLength={Length}",
            id, instruction.Length);

        var args = new Dictionary<string, object>
        {
            ["id"] = id,
            ["instruction"] = instruction
        };

        return await _backend.ExecuteToolAsync("amend_knowledge", args, cancellationToken);
    }

    [McpServerTool(Name = "amend_knowledge_async")]
    [Description("Apply a natural language edit instruction to an existing knowledge item. Returns immediately with { status, amendRequestId, knowledgeId, pollUrl }; the amendment is applied in the background via Service Bus. Poll with get_amend_request_status. Amendments against the same knowledge item are processed serially. Bursting many amends at one item will queue them; expect linear latency growth. Independent items amend in parallel. Supply an optional amendRequestId (idempotency key, up to 128 chars, GUID format) to deduplicate retries — repeated calls with the same (tenant, amendRequestId) return the existing request's current state without re-enqueuing.")]
    public async Task<object> AmendKnowledgeAsync(
        [Description("Knowledge item ID (required)")] string? id = null,
        [Description("Natural language instruction describing what to change, add, or remove from the existing content")] string? instruction = null,
        [Description("Optional client-supplied idempotency key (up to 128 chars, GUID format). Repeated calls with the same key return the existing request's current state.")] string? amendRequestId = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryRequireArg(id, "id", out var missing)) return missing;
        if (!TryRequireArg(instruction, "instruction", out missing)) return missing;

        _logger.LogInformation("SDK Tool: amend_knowledge_async - id={Id}, instructionLength={Length}, hasAmendRequestId={HasId}",
            id, instruction.Length, amendRequestId != null);

        var args = new Dictionary<string, object>
        {
            ["id"] = id,
            ["instruction"] = instruction
        };
        if (!string.IsNullOrWhiteSpace(amendRequestId))
        {
            args["amendRequestId"] = amendRequestId;
        }

        return await _backend.ExecuteToolAsync("amend_knowledge_async", args, cancellationToken);
    }

    /// <summary>
    /// Issue #522: both parameters used to be non-nullable with no default, so the MCP SDK
    /// marshaller rejected any call that omitted <c>knowledgeId</c> before this method ran —
    /// surfacing as an unhandled ArgumentException rather than a structured tool error. Callers
    /// had every reason to omit it: <c>amend_knowledge</c> and <c>amend_knowledge_async</c> both
    /// take the knowledge GUID as <c>id</c>, while this tool demanded <c>knowledgeId</c>.
    ///
    /// <c>amendRequestId</c> alone is a sufficient, tenant-safe key — it is the amend request row's
    /// primary key, not the client-supplied idempotency key (that is a separate nullable column).
    /// The optional parameters trail the required one because C# demands it; MCP passes arguments
    /// by name, so the reorder is wire-safe.
    /// </summary>
    [McpServerTool(Name = "get_amend_request_status")]
    [Description("Poll the status of a specific amend request by ID. Returns the full request row: status (queued | processing | completed | failed | staleBase | cancelled), timestamps, attempt count, last error, and resulting content hash (once complete). Only amendRequestId is required; knowledgeId (alias: id) is optional and narrows the lookup.")]
    public async Task<object> GetAmendRequestStatus(
        [Description("ID of the amend request to poll (required, GUID)")] string? amendRequestId = null,
        [Description("Optional ID of the knowledge item the amend request targets (GUID). Narrows the lookup; omit it and the request is resolved by amendRequestId alone.")] string? knowledgeId = null,
        [Description("Alias for knowledgeId, matching the 'id' parameter used by amend_knowledge and amend_knowledge_async (optional, GUID)")] string? id = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedKnowledgeId = !string.IsNullOrWhiteSpace(knowledgeId) ? knowledgeId : id;

        _logger.LogInformation("SDK Tool: get_amend_request_status - amendRequestId={AmendRequestId}, hasKnowledgeId={HasKnowledgeId}",
            amendRequestId, !string.IsNullOrWhiteSpace(resolvedKnowledgeId));

        if (!TryRequireArg(amendRequestId, "amendRequestId", out var missing)) return missing;

        var args = new Dictionary<string, object>
        {
            ["amendRequestId"] = amendRequestId
        };
        if (!string.IsNullOrWhiteSpace(resolvedKnowledgeId))
        {
            args["knowledgeId"] = resolvedKnowledgeId;
        }

        return await _backend.ExecuteToolAsync("get_amend_request_status", args, cancellationToken);
    }

    [McpServerTool(Name = "bulk_get_knowledge_items")]
    [Description("Get, fetch, or retrieve multiple knowledge items at once by their IDs. Use when you need to load several items efficiently, compare multiple documents, or gather context from multiple sources. Maximum 100 IDs per request.")]
    public async Task<object> BulkGetKnowledgeItems(
        [Description("Array of knowledge item GUIDs to fetch (required, max 100)")] string[]? ids = null,
        [Description("Include related knowledge items for each (default: false)")] bool includeRelated = false,
        CancellationToken cancellationToken = default)
    {
        if (!TryRequireArg(ids, "ids", out var missing)) return missing;

        _logger.LogInformation("SDK Tool: bulk_get_knowledge_items - count={Count}, includeRelated={IncludeRelated}",
            ids.Length, includeRelated);

        var args = new Dictionary<string, object>
        {
            ["ids"] = ids,
            ["includeRelated"] = includeRelated
        };

        return await _backend.ExecuteToolAsync("bulk_get_knowledge_items", args, cancellationToken);
    }

    [McpServerTool(Name = "create_inbox_item")]
    [Description("Save something to the inbox for later review. Inbox items are staging content that hasn't been committed to a knowledge vault yet -- useful for recommendations, things to follow up on, uncertain items, or quick captures that need processing later. Supports optional file attachments via pre-uploaded FileRecord IDs.")]
    public async Task<object> CreateInboxItem(
        [Description("Text content for the inbox item (required)")] string? body = null,
        [Description("Optional array of FileRecord GUIDs for pre-uploaded files to attach")] string[]? fileRecordIds = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryRequireArg(body, "body", out var missing)) return missing;

        _logger.LogInformation("SDK Tool: create_inbox_item - bodyLength={Length}, fileCount={FileCount}",
            body.Length, fileRecordIds?.Length ?? 0);

        var args = new Dictionary<string, object>
        {
            ["body"] = body
        };
        if (fileRecordIds != null && fileRecordIds.Length > 0)
        {
            args["fileRecordIds"] = fileRecordIds;
        }

        return await _backend.ExecuteToolAsync("create_inbox_item", args, cancellationToken);
    }

    [McpServerTool(Name = "attach_files")]
    [Description("Attach one or more pre-uploaded files to an existing knowledge item or inbox item. Files must be uploaded first via the file upload API. Triggers AI processing (OCR, transcription, entity extraction) for knowledge item attachments.")]
    public async Task<object> AttachFiles(
        [Description("ID of the knowledge item or inbox item to attach files to (required)")] string? targetId = null,
        [Description("Type of target: 'knowledge' or 'inbox' (required)")] string? targetType = null,
        [Description("Array of FileRecord GUIDs to attach (required, 1-100 files)")] string[]? fileRecordIds = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryRequireArg(targetId, "targetId", out var missing)) return missing;
        if (!TryRequireArg(targetType, "targetType", out missing)) return missing;
        if (!TryRequireArg(fileRecordIds, "fileRecordIds", out missing)) return missing;

        _logger.LogInformation("SDK Tool: attach_files - targetType={TargetType}, targetId={TargetId}, fileCount={FileCount}",
            targetType, targetId, fileRecordIds.Length);

        var args = new Dictionary<string, object>
        {
            ["targetId"] = targetId,
            ["targetType"] = targetType,
            ["fileRecordIds"] = fileRecordIds
        };

        return await _backend.ExecuteToolAsync("attach_files", args, cancellationToken);
    }

    [McpServerTool(Name = "upload_file")]
    [Description("Upload a file's bytes into Knowz. Provide content as base64 in 'contentBase64' — this tool does NOT read files from disk and accepts no file path. Inline uploads are capped at 8 MB decoded; for larger files use the HTTP chunked upload API and then attach_files. Choose 'target': 'knowledge' attaches to an existing knowledge item (needs targetId) and queues AI processing; 'new-knowledge' creates a knowledge item in a vault (needs vaultId) and queues AI processing; 'inbox' creates an inbox item for later triage (no AI processing); 'standalone' stores an unattached file with no AI processing.")]
    public async Task<object> UploadFile(
        [Description("File name including extension, e.g. 'design-notes.pdf' (required). No path separators.")] string? fileName = null,
        [Description("Base64-encoded file bytes (required). Max 8 MB decoded.")] string? contentBase64 = null,
        [Description("Where the file goes (required): 'knowledge', 'new-knowledge', 'inbox', or 'standalone'")] string? target = null,
        [Description("MIME type, e.g. 'application/pdf'. Inferred from the file extension when omitted.")] string? contentType = null,
        [Description("Existing knowledge item GUID. Required for target='knowledge', rejected otherwise.")] string? targetId = null,
        [Description("Vault GUID for the new knowledge item. Required for target='new-knowledge', rejected otherwise.")] string? vaultId = null,
        [Description("Title for the created knowledge or inbox item. Defaults to the file name.")] string? title = null,
        [Description("Optional attachment description stored on the file link.")] string? description = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryRequireArg(fileName, "fileName", out var missing)) return missing;
        if (!TryRequireArg(contentBase64, "contentBase64", out missing)) return missing;
        if (!TryRequireArg(target, "target", out missing)) return missing;

        _logger.LogInformation(
            "SDK Tool: upload_file - target={Target}, fileName={FileName}, encodedLength={Length}",
            target,
            fileName,
            contentBase64.Length);

        var args = new Dictionary<string, object>
        {
            ["fileName"] = fileName,
            ["contentBase64"] = contentBase64,
            ["target"] = target
        };
        if (!string.IsNullOrEmpty(contentType)) args["contentType"] = contentType;
        if (!string.IsNullOrEmpty(targetId)) args["targetId"] = targetId;
        if (!string.IsNullOrEmpty(vaultId)) args["vaultId"] = vaultId;
        if (!string.IsNullOrEmpty(title)) args["title"] = title;
        if (!string.IsNullOrEmpty(description)) args["description"] = description;

        return await _backend.ExecuteToolAsync("upload_file", args, cancellationToken);
    }

    [McpServerTool(Name = "list_todos")]
    [Description("List user-facing todos from Knowz task data. Use for pending, due, completed, assigned, created, or extracted todo questions.")]
    public async Task<string> ListTodos(
        [Description("Optional status filter: Open, InProgress, Completed, or Cancelled")] string? status = null,
        [Description("Optional priority filter: None, Low, Medium, High, or Urgent")] string? priority = null,
        [Description("Vault ID to scope todos. May be overridden by connection settings.")] string? vaultId = null,
        [Description("Optional assignee user GUID")] string? assignedToUserId = null,
        [Description("When true, return todos assigned to or originated by the current user")] bool assignedToMe = false,
        [Description("Optional originator user GUID")] string? originatingUserId = null,
        [Description("When true, return todos created or originated by the current user")] bool createdByMe = false,
        [Description("Optional creator user GUID")] string? createdByUserId = null,
        [Description("Optional created-at lower bound (ISO 8601)")] string? createdAfter = null,
        [Description("Optional created-at upper bound (ISO 8601)")] string? createdBefore = null,
        [Description("Optional due-date upper bound (ISO 8601)")] string? dueBefore = null,
        [Description("Optional due-date lower bound (ISO 8601)")] string? dueAfter = null,
        [Description("Optional source filter: Manual, AiExtracted, or ChatSuggested")] string? source = null,
        [Description("Optional title search text")] string? search = null,
        [Description("When true, only return todos whose source checkbox was removed")] bool orphaned = false,
        [Description("Page number (1-based, default: 1)")] int page = 1,
        [Description("Page size (1-100, default: 20)")] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var httpContext = GetHttpContext();
        var effectiveVaultId = httpContext.ResolveVaultId(vaultId);

        _logger.LogInformation("SDK Tool: list_todos - status={Status}, vault={VaultId}, page={Page}, pageSize={PageSize}",
            status ?? "any", effectiveVaultId ?? "all", page, pageSize);

        var args = new Dictionary<string, object>
        {
            ["page"] = page,
            ["pageSize"] = pageSize
        };

        if (!string.IsNullOrEmpty(status)) args["status"] = status;
        if (!string.IsNullOrEmpty(priority)) args["priority"] = priority;
        if (!string.IsNullOrEmpty(effectiveVaultId)) args["vaultId"] = effectiveVaultId;
        if (!string.IsNullOrEmpty(assignedToUserId)) args["assignedToUserId"] = assignedToUserId;
        if (assignedToMe) args["assignedToMe"] = true;
        if (!string.IsNullOrEmpty(originatingUserId)) args["originatingUserId"] = originatingUserId;
        if (createdByMe) args["createdByMe"] = true;
        if (!string.IsNullOrEmpty(createdByUserId)) args["createdByUserId"] = createdByUserId;
        if (!string.IsNullOrEmpty(createdAfter)) args["createdAfter"] = createdAfter;
        if (!string.IsNullOrEmpty(createdBefore)) args["createdBefore"] = createdBefore;
        if (!string.IsNullOrEmpty(dueBefore)) args["dueBefore"] = dueBefore;
        if (!string.IsNullOrEmpty(dueAfter)) args["dueAfter"] = dueAfter;
        if (!string.IsNullOrEmpty(source)) args["source"] = source;
        if (!string.IsNullOrEmpty(search)) args["search"] = search;
        if (orphaned) args["orphaned"] = true;

        return await _backend.ExecuteToolAsync("list_todos", args, cancellationToken);
    }

    [McpServerTool(Name = "get_todo_summary")]
    [Description("Get deterministic todo counts by status, due bucket, priority, and vault.")]
    public async Task<string> GetTodoSummary(
        [Description("Vault ID to scope the summary. May be overridden by connection settings.")] string? vaultId = null,
        CancellationToken cancellationToken = default)
    {
        var httpContext = GetHttpContext();
        var effectiveVaultId = httpContext.ResolveVaultId(vaultId);

        _logger.LogInformation("SDK Tool: get_todo_summary - vault={VaultId}", effectiveVaultId ?? "all");

        var args = new Dictionary<string, object>();
        if (!string.IsNullOrEmpty(effectiveVaultId))
        {
            args["vaultId"] = effectiveVaultId;
        }

        return await _backend.ExecuteToolAsync("get_todo_summary", args, cancellationToken);
    }

    [McpServerTool(Name = "create_todo")]
    [Description("Create a standalone user-facing todo. Tenant and current user are inherited from authentication.")]
    public async Task<object> CreateTodo(
        [Description("Todo title")] string? title = null,
        [Description("Optional longer description")] string? description = null,
        [Description("Optional priority: None, Low, Medium, High, or Urgent")] string? priority = null,
        [Description("Optional due date (ISO 8601)")] string? dueDate = null,
        [Description("Optional vault GUID. May be overridden by connection settings.")] string? vaultId = null,
        [Description("Optional knowledge item GUID to link")] string? knowledgeId = null,
        [Description("Optional assignee user GUID")] string? assignedToUserId = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryRequireArg(title, "title", out var missing)) return missing;

        var httpContext = GetHttpContext();
        var effectiveVaultId = httpContext.ResolveVaultId(vaultId);

        _logger.LogInformation("SDK Tool: create_todo - titleLength={Length}, vault={VaultId}",
            title.Length, effectiveVaultId ?? "none");

        var args = new Dictionary<string, object> { ["title"] = title };
        if (!string.IsNullOrEmpty(description)) args["description"] = description;
        if (!string.IsNullOrEmpty(priority)) args["priority"] = priority;
        if (!string.IsNullOrEmpty(dueDate)) args["dueDate"] = dueDate;
        if (!string.IsNullOrEmpty(effectiveVaultId)) args["vaultId"] = effectiveVaultId;
        if (!string.IsNullOrEmpty(knowledgeId)) args["knowledgeId"] = knowledgeId;
        if (!string.IsNullOrEmpty(assignedToUserId)) args["assignedToUserId"] = assignedToUserId;

        return await _backend.ExecuteToolAsync("create_todo", args, cancellationToken);
    }

    [McpServerTool(Name = "update_todo_status")]
    [Description("Update a todo status. Completing an anchored todo updates the source document checkbox.")]
    public async Task<object> UpdateTodoStatus(
        [Description("Todo GUID")] string? id = null,
        [Description("New status: Open, InProgress, Completed, or Cancelled")] string? status = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryRequireArg(id, "id", out var missing)) return missing;
        if (!TryRequireArg(status, "status", out missing)) return missing;

        _logger.LogInformation("SDK Tool: update_todo_status - id={Id}, status={Status}", id, status);

        var args = new Dictionary<string, object>
        {
            ["id"] = id,
            ["status"] = status
        };

        return await _backend.ExecuteToolAsync("update_todo_status", args, cancellationToken);
    }

    // Additional non-tool-attributed tools that were added after initial implementation

    [McpServerTool(Name = "count_knowledge")]
    [Description("Count how many knowledge items match specific criteria. Use for analytics, progress tracking, or understanding the scope of content. Supports filtering by type, title pattern, file pattern, and date range.")]
    public async Task<string> CountKnowledge(
        [Description("Optional knowledge type filter")] string? knowledgeType = null,
        [Description("Optional title pattern (supports * and ? wildcards)")] string? titlePattern = null,
        [Description("Optional file name pattern (supports * and ? wildcards)")] string? fileNamePattern = null,
        [Description("Optional start date (ISO 8601)")] string? startDate = null,
        [Description("Optional end date (ISO 8601)")] string? endDate = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("SDK Tool: count_knowledge");

        var args = new Dictionary<string, object>();
        if (!string.IsNullOrEmpty(knowledgeType)) args["knowledgeType"] = knowledgeType;
        if (!string.IsNullOrEmpty(titlePattern)) args["titlePattern"] = titlePattern;
        if (!string.IsNullOrEmpty(fileNamePattern)) args["fileNamePattern"] = fileNamePattern;
        if (!string.IsNullOrEmpty(startDate)) args["startDate"] = startDate;
        if (!string.IsNullOrEmpty(endDate)) args["endDate"] = endDate;

        return await _backend.ExecuteToolAsync("count_knowledge", args, cancellationToken);
    }

    [McpServerTool(Name = "get_statistics")]
    [Description("Get aggregate statistics about the knowledge base including total items, breakdown by type, breakdown by vault, and date range of content.")]
    public async Task<string> GetStatistics(
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("SDK Tool: get_statistics");
        return await _backend.ExecuteToolAsync("get_statistics", new Dictionary<string, object>(), cancellationToken);
    }

    [McpServerTool(Name = "search_by_file_pattern")]
    [Description("Search for knowledge items by file path pattern. Supports wildcards: * matches any characters, ? matches a single character. Example: '*.cs' finds all C# files, 'src/**/*.ts' finds TypeScript files under src.")]
    public async Task<object> SearchByFilePattern(
        [Description("File path pattern with wildcards (* and ?)")] string? pattern = null,
        [Description("If true, return only the count (default: false)")] bool countOnly = false,
        [Description("Maximum results (default: 50, max: 100)")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (!TryRequireArg(pattern, "pattern", out var missing)) return missing;

        _logger.LogInformation("SDK Tool: search_by_file_pattern - pattern={Pattern}", pattern);

        var args = new Dictionary<string, object>
        {
            ["pattern"] = pattern,
            ["countOnly"] = countOnly,
            ["limit"] = limit
        };

        return await _backend.ExecuteToolAsync("search_by_file_pattern", args, cancellationToken);
    }

    [McpServerTool(Name = "search_by_title_pattern")]
    [Description("Search for knowledge items by title pattern. Supports wildcards: * matches any characters, ? matches a single character.")]
    public async Task<object> SearchByTitlePattern(
        [Description("Title pattern with wildcards (* and ?)")] string? pattern = null,
        [Description("If true, return only the count (default: false)")] bool countOnly = false,
        [Description("Maximum results (default: 50, max: 100)")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (!TryRequireArg(pattern, "pattern", out var missing)) return missing;

        _logger.LogInformation("SDK Tool: search_by_title_pattern - pattern={Pattern}", pattern);

        var args = new Dictionary<string, object>
        {
            ["pattern"] = pattern,
            ["countOnly"] = countOnly,
            ["limit"] = limit
        };

        return await _backend.ExecuteToolAsync("search_by_title_pattern", args, cancellationToken);
    }

    [McpServerTool(Name = "add_comment")]
    [Description("Add a comment or contribution to a knowledge item. Comments are included in the knowledge item's AI summary and search index. Use this to add notes, corrections, additional context, or answers to a knowledge item.")]
    public async Task<object> AddComment(
        [Description("Knowledge item GUID to comment on (required)")] string? knowledgeItemId = null,
        [Description("Comment text/body (required)")] string? body = null,
        [Description("Author name (defaults to 'AI Assistant')")] string? authorName = null,
        [Description("Optional parent comment ID for threaded replies")] string? parentCommentId = null,
        [Description("Optional sentiment label (e.g., 'positive', 'negative', 'neutral')")] string? sentiment = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryRequireArg(knowledgeItemId, "knowledgeItemId", out var missing)) return missing;
        if (!TryRequireArg(body, "body", out missing)) return missing;

        _logger.LogInformation("SDK Tool: add_comment - knowledgeItemId={Id}, bodyLength={Length}",
            knowledgeItemId, body.Length);

        var args = new Dictionary<string, object>
        {
            ["knowledgeItemId"] = knowledgeItemId,
            ["body"] = body,
            ["authorName"] = authorName ?? "AI Assistant"
        };

        if (!string.IsNullOrEmpty(parentCommentId))
            args["parentCommentId"] = parentCommentId;
        if (!string.IsNullOrEmpty(sentiment))
            args["sentiment"] = sentiment;

        return await _backend.ExecuteToolAsync("add_comment", args, cancellationToken);
    }

    [McpServerTool(Name = "list_comments")]
    [Description("List all comments and contributions on a knowledge item. Returns threaded comments with replies, author names, and attachment counts. Use this to see what others have contributed or to check for existing answers before adding a new comment.")]
    public async Task<object> ListComments(
        [Description("Knowledge item GUID to list comments for (required)")] string? knowledgeItemId = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryRequireArg(knowledgeItemId, "knowledgeItemId", out var missing)) return missing;

        _logger.LogInformation("SDK Tool: list_comments - knowledgeItemId={Id}", knowledgeItemId);

        var args = new Dictionary<string, object>
        {
            ["knowledgeItemId"] = knowledgeItemId
        };

        return await _backend.ExecuteToolAsync("list_comments", args, cancellationToken);
    }

    [McpServerTool(Name = "get_version_history")]
    [Description("Get the version history and change log for a specific knowledge item. Shows what changed between versions, when, and why — including content diffs, change summaries, and line-level statistics. Use when the user asks about changes, edits, revisions, or history of a knowledge item.")]
    public async Task<object> GetVersionHistory(
        [Description("Knowledge item GUID to get version history for")] string? knowledgeId = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryRequireArg(knowledgeId, "knowledgeId", out var missing)) return missing;

        _logger.LogInformation("SDK Tool: get_version_history - knowledgeId={KnowledgeId}", knowledgeId);

        var args = new Dictionary<string, object> { ["knowledgeId"] = knowledgeId };
        return await _backend.ExecuteToolAsync("get_version_history", args, cancellationToken);
    }

    [McpServerTool(Name = "list_knowledge_items")]
    [Description("List knowledge items with pagination and sorting. Supports filtering by type, title/file patterns, and date range. Use for browsing or paginating through large result sets.")]
    public async Task<string> ListKnowledgeItems(
        [Description("Page number (1-based, default: 1)")] int page = 1,
        [Description("Items per page (1-100, default: 20)")] int pageSize = 20,
        [Description("Sort by: created, updated, title (default: created)")] string sortBy = "created",
        [Description("Sort direction: asc, desc (default: desc)")] string sortDirection = "desc",
        [Description("Optional knowledge type filter")] string? knowledgeType = null,
        [Description("Optional title pattern")] string? titlePattern = null,
        [Description("Optional file name pattern")] string? fileNamePattern = null,
        [Description("Optional start date (ISO 8601)")] string? startDate = null,
        [Description("Optional end date (ISO 8601)")] string? endDate = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("SDK Tool: list_knowledge_items - page={Page}, pageSize={PageSize}", page, pageSize);

        var args = new Dictionary<string, object>
        {
            ["page"] = page,
            ["pageSize"] = pageSize,
            ["sortBy"] = sortBy,
            ["sortDirection"] = sortDirection
        };
        if (!string.IsNullOrEmpty(knowledgeType)) args["knowledgeType"] = knowledgeType;
        if (!string.IsNullOrEmpty(titlePattern)) args["titlePattern"] = titlePattern;
        if (!string.IsNullOrEmpty(fileNamePattern)) args["fileNamePattern"] = fileNamePattern;
        if (!string.IsNullOrEmpty(startDate)) args["startDate"] = startDate;
        if (!string.IsNullOrEmpty(endDate)) args["endDate"] = endDate;

        return await _backend.ExecuteToolAsync("list_knowledge_items", args, cancellationToken);
    }
}
