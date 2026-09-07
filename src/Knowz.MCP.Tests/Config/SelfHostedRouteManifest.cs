using System.Text.RegularExpressions;
using Knowz.MCP.Config;
using Knowz.MCP.Services.Proxy;

namespace Knowz.MCP.Tests.Config;

/// <summary>
/// MCP_SelfHostedToolContract R14 / DEBT-1.
///
/// A checked-in snapshot of the routes <c>Knowz.SelfHosted.API</c> exposes, generated once by
/// grepping <c>Map(Get|Post|Put|Delete)</c> + <c>MapGroup</c> under
/// <c>selfhosted/src/Knowz.SelfHosted.API/Endpoints/</c> (2026-09-04) and then checked in.
///
/// It is a manifest rather than live reflection ON PURPOSE: <c>Knowz.MCP</c> is the shared hosted
/// binary and must never take a project reference on the self-hosted API. The cost is that a NEW
/// SH route is not noticed here; the benefit is that a RENAMED SH route fails this test loudly
/// instead of 404-ing inside a customer's agent — which is exactly how the never-existed
/// <c>/api/v1/files/upload/initialize</c> shipped (MCP_SelfHostedUploadBackend, R5 correction).
///
/// Route parameters are normalised to <c>{}</c> so <c>{id:guid}</c> vs <c>{knowledgeId:guid}</c>
/// is not a spurious mismatch.
/// </summary>
public static class SelfHostedRouteManifest
{
    public static readonly string[] Routes =
    {
        "/api/v1/search",
        "/api/v1/ask",
        "/api/v1/ask/stream",
        "/api/v1/chat",
        "/api/v1/inbox",
        "/api/v1/inbox/{}",
        "/api/v1/knowledge",
        "/api/v1/knowledge/{}",
        "/api/v1/knowledge/stats",
        "/api/v1/knowledge/{}/amend",
        "/api/v1/knowledge/{}/attachments",
        "/api/v1/knowledge/{}/comments",
        "/api/v1/knowledge/{}/versions",
        "/api/v1/topics",
        "/api/v1/topics/{}",
        "/api/v1/entities",
        "/api/v1/vaults",
        "/api/v1/vaults/{}",
        "/api/v1/vaults/{}/contents",
        "/api/v1/files",
        "/api/v1/files/upload",
        "/api/v1/files/{}",
        "/api/v1/comments",
        "/api/v1/tags",
    };

    /// <summary>
    /// Every route template an ADVERTISED self-hosted tool can call that is absent from
    /// <paramref name="manifest"/>. Empty means the advertised surface is route-complete.
    /// </summary>
    public static IReadOnlyList<string> MissingRoutes(IEnumerable<string> manifest)
    {
        var known = new HashSet<string>(manifest.Select(Normalise), StringComparer.Ordinal);
        var missing = new List<string>();

        foreach (var (toolName, templates) in SelfHostedToolBackend.SelfHostedRouteTemplates)
        {
            if (SelfHostedToolVisibility.HiddenTools.Contains(toolName))
                continue;

            foreach (var template in templates)
            {
                var normalised = Normalise(template);
                if (!known.Contains(normalised))
                    missing.Add($"{toolName} -> {template}");
            }
        }

        return missing;
    }

    private static string Normalise(string template) =>
        Regex.Replace(template, "\\{[^}]*\\}", "{}").TrimEnd('/');
}
