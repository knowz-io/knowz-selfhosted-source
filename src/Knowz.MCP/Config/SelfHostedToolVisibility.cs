using System.Text.Json;
using System.Text.Json.Nodes;
using Knowz.MCP.Services.Proxy;
using ModelContextProtocol.Server;

namespace Knowz.MCP.Config;

/// <summary>
/// The self-hosted MCP tool contract (spec: MCP_SelfHostedToolContract).
///
/// Knowz.MCP is ONE binary shared by hosted MCP (api.knowz.io) and the self-hosted edition; only
/// <c>MCP:BackendMode</c> differs. Everything in this class is inert unless that key says
/// "selfhosted". Read by the Program.cs registration filter, by SelfHostedToolBackend's call-time
/// guard, and by the coverage-guard tests. Nothing else may hard-code these names.
/// </summary>
public static class SelfHostedToolVisibility
{
    public const string SelfHostedMode = "selfhosted";

    /// <summary>
    /// Tools with no working self-hosted implementation. Frozen by
    /// kc-fix-selfhosted-publish-honesty-20260904-013320; adding or removing a name is a spec
    /// change (and must move the README list in the same commit — DEBT-4).
    /// </summary>
    public static readonly IReadOnlySet<string> HiddenTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "graph_query",
        "list_todos", "get_todo_summary", "create_todo", "update_todo_status",
        "inspect_document_map", "get_document_window", "search_document_text",
        "amend_knowledge_async", "get_amend_request_status",
    };

    /// <summary>
    /// SH-true tool descriptions applied to the advertised list at startup (R7, R8).
    /// The hosted text for these three is wrong on self-hosted: amend_knowledge is synchronous and
    /// is the only amend (its advertised replacement is hidden), and cross-tenant vault sharing
    /// does not exist in this edition.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> SelfHostedDescriptions
        = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["amend_knowledge"] =
                "Apply a natural-language edit instruction to an existing knowledge item — add a section, " +
                "update part of the content, remove a paragraph, fix information, or make any incremental " +
                "change — without replacing the entire content. Applies synchronously and returns the updated item.",

            ["ask_question"] =
                "Answer any question, explain something, help understand, analyze, summarize, compare, or reason " +
                "about code, documentation, architecture, decisions, or knowledge. Use for 'what is', 'how does', " +
                "'why', 'explain', 'tell me about', 'help me understand', or any inquiry requiring AI-powered " +
                "reasoning with source references. Enable researchMode for complex questions needing thorough " +
                "analysis. Answers are drawn from the vaults on this instance.",

            ["list_vaults"] =
                "List, show, browse, display, or see all vaults, workspaces, projects, or collections on this " +
                "instance. Use when asking 'what vaults exist', 'show my projects', 'which workspaces', or " +
                "exploring what's accessible. May be filtered if connection is scoped to a specific vault. " +
                "Timestamps are in ISO 8601 format.",
        };

    /// <summary>
    /// Replacement schema prose for the <c>includeSharedVaults</c> parameter. The parameter itself
    /// stays in the schema — removing it would change the signature the hosted binary advertises
    /// (R5/R8) — only the prose stops promising a capability this edition does not have.
    /// </summary>
    public const string IncludeSharedVaultsSelfHostedDescription =
        "Accepted for compatibility with hosted Knowz. This edition has a single tenant and no " +
        "cross-tenant vault sharing, so the value has no effect.";

    /// <summary>
    /// Tool-scoped parameter prose. Unlike <see cref="IncludeSharedVaultsSelfHostedDescription"/>
    /// (which applies wherever that parameter appears), these are keyed by tool because the same
    /// parameter name is load-bearing elsewhere — create_vault's `description` IS forwarded.
    /// The parameters stay in the schema; only the promise changes.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> SelfHostedParameterDescriptions
        = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["upload_file"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["description"] =
                    "Accepted for compatibility with hosted Knowz. This edition's attach route stores only " +
                    "the file link, so the value has no effect.",
            },
        };

    public static bool IsSelfHosted(IConfiguration configuration) =>
        string.Equals(configuration["MCP:BackendMode"], SelfHostedMode, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Registers the self-hosted tool filter. Call AFTER <c>AddMcpServer(...).WithTools&lt;T&gt;()</c>
    /// so the SDK's own options setup has already populated <c>ToolCollection</c>.
    ///
    /// R3: <c>McpServerHandlers.ListToolsHandler</c> AUGMENTS <c>ToolCollection</c> — the SDK returns
    /// the collection's tools and THEN the handler's — so a list handler cannot remove a registered
    /// tool. <c>WithListToolsHandler</c> is therefore rejected for this purpose; do not "simplify"
    /// this back to one. The filter mutates the collection itself, once, at startup.
    /// </summary>
    public static IServiceCollection AddSelfHostedToolVisibility(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (!IsSelfHosted(configuration))
            return services;

        services.Configure<McpServerOptions>(Apply);
        return services;
    }

    /// <summary>Mutates the advertised tool surface in place. Idempotent.</summary>
    public static void Apply(McpServerOptions options)
    {
        var tools = options.ToolCollection;
        if (tools is null)
            return;

        foreach (var name in HiddenTools)
        {
            if (tools.TryGetPrimitive(name, out var hidden) && hidden is not null)
                tools.Remove(hidden);
        }

        foreach (var (name, description) in SelfHostedDescriptions)
        {
            if (tools.TryGetPrimitive(name, out var tool) && tool is not null)
                tool.ProtocolTool.Description = description;
        }

        foreach (var tool in tools.ToList())
        {
            var overrides = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["includeSharedVaults"] = IncludeSharedVaultsSelfHostedDescription
            };
            if (SelfHostedParameterDescriptions.TryGetValue(tool.ProtocolTool.Name, out var toolScoped))
                foreach (var (property, prose) in toolScoped) overrides[property] = prose;

            RewriteParameterProse(tool, overrides);
        }
    }

    /// <summary>
    /// Rewrites parameter descriptions in a tool's input schema. Properties that are absent are
    /// skipped; nothing is added or removed, so the advertised signature is unchanged (R5/R8).
    /// </summary>
    private static void RewriteParameterProse(McpServerTool tool, IReadOnlyDictionary<string, string> overrides)
    {
        var schema = tool.ProtocolTool.InputSchema;
        if (schema.ValueKind != JsonValueKind.Object)
            return;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(schema.GetRawText());
        }
        catch (JsonException)
        {
            return;
        }

        var rewritten = false;
        foreach (var (propertyName, prose) in overrides)
        {
            if (node?["properties"]?[propertyName] is not JsonObject property)
                continue;
            property["description"] = prose;
            rewritten = true;
        }

        if (!rewritten)
            return;

        using var document = JsonDocument.Parse(node!.ToJsonString());
        tool.ProtocolTool.InputSchema = document.RootElement.Clone();
    }

    /// <summary>
    /// R13 coverage guard. Every declared tool must be in EXACTLY ONE of: the SelfHostedToolBackend
    /// route mappings, its special-case branches, or <see cref="HiddenTools"/>. A name in zero sets
    /// answers "Unknown tool" or 404s; a name in two has ambiguous dispatch.
    /// Returns a human-readable problem per offending name; empty means covered.
    /// </summary>
    public static IReadOnlyList<string> ClassifyCoverage(
        IEnumerable<string> declaredToolNames,
        IEnumerable<string>? extraHidden = null)
    {
        var hidden = new HashSet<string>(HiddenTools, StringComparer.Ordinal);
        if (extraHidden is not null)
            foreach (var name in extraHidden) hidden.Add(name);

        var problems = new List<string>();
        foreach (var name in declaredToolNames)
        {
            var sets = new List<string>();
            if (SelfHostedToolBackend.MappedToolNames.Contains(name)) sets.Add("ToolMappings");
            if (SelfHostedToolBackend.SpecialCaseToolNames.Contains(name)) sets.Add("special-case");
            if (hidden.Contains(name)) sets.Add("HiddenTools");

            if (sets.Count != 1)
                problems.Add($"{name} is in {sets.Count} sets ({string.Join(", ", sets)}); expected exactly 1");
        }

        return problems;
    }
}
