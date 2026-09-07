using Microsoft.Extensions.Configuration;

namespace Knowz.MCP.Helpers;

/// <summary>
/// Decides which provider-hosted HTTPS callbacks may receive an authorization code.
///
/// Hosted web connectors (Claude, ChatGPT, Cursor cloud agents, ...) complete OAuth on a
/// provider-owned HTTPS URL rather than a loopback port, so they cannot be covered by the
/// RFC 8252 loopback/private-scheme rules. Dynamic Client Registration here is stateless and
/// cannot enforce per-client redirect URIs, so this allowlist is the control that stops an
/// arbitrary site from receiving a code.
///
/// The allowlist is data, not code: <see cref="BuiltInPatterns"/> ships the known connectors so
/// a zero-config deploy works, and <see cref="ConfigurationKey"/> adds more at runtime. Onboarding
/// a new connector is therefore a config change (one env var), not a code change plus image build.
///
/// Pattern grammar (one entry per connector callback):
///   https://host/exact/path     — matches that path exactly
///   https://host/prefix/&#42;   — matches exactly one further path segment of URI-unreserved
///                                 characters (for providers that mint an opaque per-install ID)
///
/// The wildcard is grammar, never a literal: an entry carrying <c>&#42;</c> anywhere other than
/// those final two characters is rejected outright rather than treated as an exact path, so a
/// pattern that cannot match what its author meant fails loudly instead of sitting inert.
///
/// Every candidate must additionally be HTTPS on the default port with no userinfo, query, or
/// fragment. Paths are compared in escaped form, so encoded separators and dot-segments cannot
/// smuggle a match past an exact pattern.
/// </summary>
public sealed class HostedConnectorCallbackPolicy
{
    /// <summary>
    /// Configuration key holding extra callback patterns. Accepts either an indexed array
    /// (<c>MCP__HostedConnectorCallbacks__0</c>) or a single delimited scalar
    /// (<c>MCP__HostedConnectorCallbacks="https://a/cb,https://b/cb/*"</c>), so a new connector
    /// can be added with a single env-var value. Set it through the Bicep
    /// `hostedConnectorCallbacks` param, NOT `az containerapp update --set-env-vars`: a
    /// container-app Bicep deploy replaces the whole env array, so an out-of-band value
    /// self-erases on the next deploy.
    /// </summary>
    public const string ConfigurationKey = "MCP:HostedConnectorCallbacks";

    /// <summary>
    /// Connectors known at build time. Expressed in the same grammar as configured entries so
    /// there is a single matching path and built-ins cannot drift from configured ones.
    ///
    /// <para><b>Bounding rule — do not add an entry here without meeting it.</b> Built-ins contain
    /// DOCUMENTED provider callbacks only. Anything undocumented — including an apex/www sibling
    /// of a documented host — goes through operator config
    /// (<see cref="ConfigurationKey"/>), never code.</para>
    ///
    /// Adding a built-in widens an auth trust boundary for every deployment of this image at once
    /// and can only be narrowed again by another image build; a config entry is scoped to one
    /// deployment and is reviewed as the trust-boundary change it is.
    /// </summary>
    public static readonly IReadOnlyList<string> BuiltInPatterns = new[]
    {
        // claude.ai and claude.com are BOTH independently documented Anthropic callbacks — not an
        // apex/www pair, and not a precedent for adding an undocumented sibling of any other host.
        "https://claude.ai/api/mcp/auth_callback",
        "https://claude.com/api/mcp/auth_callback",
        "https://chatgpt.com/connector/oauth/*",
        // Cursor cloud/web agents. The Cursor desktop app needs no entry: it redirects to a
        // loopback port (http://localhost:8787/callback) or the cursor:// private-use scheme,
        // both already allowed by the RFC 8252 rules. Cursor documents only the www host, so per
        // the bounding rule above the cursor.com apex is deliberately absent — if a deployment
        // ever needs it, add it via MCP:HostedConnectorCallbacks rather than here.
        "https://www.cursor.com/agents/mcp/oauth/callback",
    };

    private readonly List<CallbackPattern> _patterns;

    /// <summary>
    /// Configured entries that failed to parse. They are ignored (fail closed for that entry
    /// only) and surfaced so a typo is logged at startup instead of silently allowing nothing.
    /// </summary>
    public IReadOnlyList<string> InvalidPatterns { get; }

    /// <summary>
    /// Every pattern that parsed, in normalized string form. This is the live trust boundary, and
    /// it is logged at Information on every boot: an allowlist you cannot see in logs is an
    /// allowlist you cannot audit from telemetry.
    /// </summary>
    public IReadOnlyList<string> EffectivePatterns { get; }

    public HostedConnectorCallbackPolicy(IEnumerable<string> patterns)
    {
        _patterns = new List<CallbackPattern>();
        var invalid = new List<string>();
        var effective = new List<string>();

        foreach (var pattern in patterns)
        {
            if (TryParsePattern(pattern, out var parsed))
            {
                _patterns.Add(parsed);
                effective.Add(parsed.ToPatternString());
            }
            else
            {
                invalid.Add(pattern);
            }
        }

        InvalidPatterns = invalid;
        EffectivePatterns = effective;
    }

    /// <summary>
    /// Builds the policy from built-in defaults plus anything under <see cref="ConfigurationKey"/>.
    /// Configured entries are additive — they never remove a built-in connector.
    /// </summary>
    public static HostedConnectorCallbackPolicy FromConfiguration(IConfiguration? configuration)
    {
        return new HostedConnectorCallbackPolicy(
            BuiltInPatterns.Concat(ReadConfiguredPatterns(configuration)));
    }

    /// <summary>
    /// Reads configured patterns from either the indexed-array or delimited-scalar form.
    /// </summary>
    public static IReadOnlyList<string> ReadConfiguredPatterns(IConfiguration? configuration)
    {
        if (configuration is null)
            return Array.Empty<string>();

        var section = configuration.GetSection(ConfigurationKey);

        var children = section.GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToArray();

        if (children.Length > 0)
            return children;

        var scalar = section.Value;
        if (string.IsNullOrWhiteSpace(scalar))
            return Array.Empty<string>();

        return scalar.Split(
            new[] { ',', ';', ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// True when the URI is a provider-hosted callback this server is willing to redirect to.
    /// </summary>
    public bool IsAllowed(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        // Escaped form: an encoded separator stays encoded, so it cannot satisfy an exact
        // pattern or masquerade as a single wildcard segment.
        var path = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);

        foreach (var pattern in _patterns)
        {
            if (!uri.Host.Equals(pattern.Host, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!pattern.AllowsTrailingSegment)
            {
                if (string.Equals(path, pattern.EscapedPath, StringComparison.Ordinal))
                    return true;

                continue;
            }

            if (!path.StartsWith(pattern.EscapedPath, StringComparison.Ordinal))
                continue;

            var segment = path[pattern.EscapedPath.Length..];

            // One URI-unreserved segment only. Rejects empty, trailing/extra segments, encoded
            // path separators, dot-segments, controls, Unicode lookalikes, and other path syntax
            // while leaving the provider's opaque callback ID unconstrained.
            if (segment.Length == 0 || segment is "." or "..")
                continue;

            if (segment.All(IsUriUnreservedCharacter))
                return true;
        }

        return false;
    }

    private static bool TryParsePattern(string pattern, out CallbackPattern parsed)
    {
        parsed = default;

        if (string.IsNullOrWhiteSpace(pattern))
            return false;

        var candidate = pattern.Trim();
        var allowsTrailingSegment = candidate.EndsWith("/*", StringComparison.Ordinal);

        if (allowsTrailingSegment)
            candidate = candidate[..^1]; // drop '*', keep the trailing '/' as the prefix

        // '*' is grammar, never a literal. Anything left after the trailing-wildcard marker is
        // stripped — "https://host/a/*/b", "https://host/prefix/*x", a wildcard host — is a
        // pattern that cannot match its author's intent, so it fails loudly instead of being
        // read as an exact path containing a literal asterisk. A genuinely literal '*' in a
        // provider path must be percent-encoded (%2A), which is compared as an ordinary escaped
        // character.
        if (candidate.Contains('*', StringComparison.Ordinal))
            return false;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var path = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);

        // An exact pattern needs a real path; a wildcard pattern needs a prefix ending in '/'.
        // Without this a bare "https://host" or "https://host/*" would match the whole origin.
        if (allowsTrailingSegment)
        {
            if (!path.EndsWith("/", StringComparison.Ordinal) || path.Length <= 1)
                return false;
        }
        else if (path.Length == 0 || path == "/")
        {
            return false;
        }

        parsed = new CallbackPattern(uri.Host, path, allowsTrailingSegment);
        return true;
    }

    private static bool IsUriUnreservedCharacter(char value)
    {
        return value is >= 'A' and <= 'Z' or
               >= 'a' and <= 'z' or
               >= '0' and <= '9' or
               '-' or '.' or '_' or '~';
    }

    private readonly record struct CallbackPattern(string Host, string EscapedPath, bool AllowsTrailingSegment)
    {
        /// <summary>
        /// Normalized round-trip of the parsed pattern, used for startup logging so the logged
        /// allowlist reflects what actually matches rather than the raw configured text.
        /// <see cref="EscapedPath"/> comes from <c>UriComponents.Path</c>, which omits the leading
        /// separator, so it is restored here.
        /// </summary>
        public string ToPatternString()
        {
            return $"https://{Host}/{EscapedPath}{(AllowsTrailingSegment ? "*" : string.Empty)}";
        }
    }
}
