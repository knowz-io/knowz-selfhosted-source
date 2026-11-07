namespace Knowz.SelfHosted.Application.Validators;

using System.Net;
using System.Net.Sockets;
using Knowz.Core.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

/// <summary>
/// Default <see cref="IUrlValidator"/> — enforces HTTPS, host allowlist, and SSRF guards.
/// Pattern mirrored from the main platform's Knowz.Application.Validators.UrlValidator but
/// scoped to the selfhosted needs (no full Application-layer dependency).
///
/// The allowlist is the built-in Knowz Cloud hosts PLUS the operator-configured
/// <c>Sync:AllowedPlatformHosts</c> (ADR_G6_Allowlisted_Peer_Destinations, #1043). Hosts not in
/// the union are rejected; the configured list never bypasses the HTTPS / loopback /
/// private-address / metadata guards below — except the narrow
/// <c>Sync:AllowPrivatePeerAddresses</c> smoke/LAN opt-in, which still requires allowlisting and
/// still rejects metadata.
/// </summary>
public class PlatformUrlValidator : IUrlValidator
{
    private readonly IHostEnvironment _env;
    private readonly bool _allowPrivatePeerAddresses;

    // Built-in Knowz Cloud hosts — always allowed. Localhost is allowed ONLY in Development
    // (or when AllowPrivatePeerAddresses is on and the host is allowlisted).
    private static readonly string[] DefaultAllowlistedHosts =
    {
        "api.knowz.io",
        "api.dev.knowz.io",
    };

    private readonly HashSet<string> _allowlistedHosts;

    public PlatformUrlValidator(IHostEnvironment env)
        : this(env, options: null)
    {
    }

    public PlatformUrlValidator(IHostEnvironment env, IOptions<SyncOptions>? options)
    {
        _env = env;
        _allowlistedHosts = BuildAllowlist(options?.Value?.AllowedPlatformHosts);
        _allowPrivatePeerAddresses = options?.Value?.AllowPrivatePeerAddresses == true;
    }

    /// <summary>
    /// Hosts this validator accepts (defaults ∪ configured). Exposed for diagnostics/tests;
    /// never mutate.
    /// </summary>
    public IReadOnlyCollection<string> AllowlistedHosts => _allowlistedHosts;

    /// <summary>
    /// Whether the smoke/LAN private-peer opt-in is active.
    /// </summary>
    public bool AllowPrivatePeerAddresses => _allowPrivatePeerAddresses;

    /// <summary>
    /// Union of the built-in hosts and the operator entries. Entries are trimmed, blanks are
    /// dropped, and an entry written as a URL (or host:port) is reduced to its host so
    /// <c>https://peer.example.com/</c> and <c>peer.example.com</c> mean the same thing.
    /// </summary>
    internal static HashSet<string> BuildAllowlist(IEnumerable<string>? configuredHosts)
    {
        var set = new HashSet<string>(DefaultAllowlistedHosts, StringComparer.OrdinalIgnoreCase);
        if (configuredHosts is null) return set;

        foreach (var raw in configuredHosts)
        {
            var host = NormalizeHostEntry(raw);
            if (host is not null) set.Add(host);
        }
        return set;
    }

    internal static string? NormalizeHostEntry(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var entry = raw.Trim();

        // Entries may be given as a bare host, host:port, or a full URL — reduce all to host.
        var candidate = entry.Contains("://", StringComparison.Ordinal) ? entry : $"https://{entry}";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.DnsSafeHost))
            return null;

        return uri.DnsSafeHost.ToLowerInvariant();
    }

    public UrlValidationResult ValidatePlatformUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return Fail("Platform URL is required.", UrlValidationErrorCode.InvalidFormat);

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return Fail("Platform URL is not a valid absolute URL.", UrlValidationErrorCode.InvalidFormat);

        // Scheme check — HTTPS only (except http://localhost in Development, or http for
        // allowlisted private/loopback peers when Sync:AllowPrivatePeerAddresses is on).
        var isLocalhostHost = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                              || uri.Host == "127.0.0.1"
                              || uri.Host == "::1";
        var isDev = _env.IsDevelopment();
        var hostAllowlisted = isLocalhostHost || _allowlistedHosts.Contains(uri.DnsSafeHost);
        var privateOptIn = _allowPrivatePeerAddresses && hostAllowlisted;

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            // Smoke/LAN opt-in: cleartext HTTP is allowed for allowlisted *custom* peers only.
            // Built-in Cloud hosts still require HTTPS even when AllowPrivatePeerAddresses is on.
            var isBuiltInCloud = DefaultAllowlistedHosts.Contains(uri.DnsSafeHost, StringComparer.OrdinalIgnoreCase);
            var allowHttp = (isDev && isLocalhostHost && uri.Scheme == Uri.UriSchemeHttp)
                            || (privateOptIn && uri.Scheme == Uri.UriSchemeHttp && hostAllowlisted && !isBuiltInCloud);
            if (!allowHttp)
                return Fail("Platform URL must use https://.", UrlValidationErrorCode.NonHttpsScheme);
        }

        // Allowlist check. Localhost only permitted in Development (or private-peer opt-in when allowlisted).
        if (isLocalhostHost)
        {
            if (!isDev && !(_allowPrivatePeerAddresses && _allowlistedHosts.Contains(uri.DnsSafeHost)))
                return Fail("Loopback addresses are not permitted.", UrlValidationErrorCode.LoopbackAddress);
            return Success();
        }

        if (!_allowlistedHosts.Contains(uri.DnsSafeHost))
            return Fail($"Host '{uri.Host}' is not in the platform allowlist.", UrlValidationErrorCode.NotAllowlisted);

        // Defense-in-depth: if the allowlisted host literal resolves to a private/metadata
        // address (e.g. DNS rebinding, internal DNS poisoning), reject — unless the operator
        // explicitly opted into Sync:AllowPrivatePeerAddresses for LAN/smoke peers.
        // Metadata is never permitted.
        var rebindResult = IPAddress.TryParse(uri.DnsSafeHost, out var literal)
            ? CheckAddresses(new[] { literal }, allowPrivate: _allowPrivatePeerAddresses)
            : CheckForPrivateAddresses(uri.DnsSafeHost, allowPrivate: _allowPrivatePeerAddresses);
        if (!rebindResult.IsValid)
            return rebindResult;

        return Success();
    }

    /// <summary>
    /// Hostnames used for Docker Desktop / LAN smoke (not literals). HTTP is only accepted for
    /// these when <c>AllowPrivatePeerAddresses</c> is on and the host is allowlisted; the
    /// subsequent DNS check still enforces private-or-public resolution with metadata blocked.
    /// </summary>
    private static bool LooksLikePrivatePeerHost(string host) =>
        host.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase);

    private static bool IsPrivateOrLoopbackLiteral(string host)
    {
        if (!IPAddress.TryParse(host, out var addr)) return false;
        if (IPAddress.IsLoopback(addr)) return true;
        if (addr.AddressFamily == AddressFamily.InterNetwork && IsPrivateIpv4(addr)) return true;
        if (addr.AddressFamily == AddressFamily.InterNetworkV6 && IsPrivateIpv6(addr)) return true;
        return false;
    }

    private static UrlValidationResult CheckForPrivateAddresses(string host, bool allowPrivate)
    {
        IPAddress[] addresses;
        try
        {
            addresses = Dns.GetHostAddresses(host);
        }
        catch
        {
            // If DNS fails we defer to the outbound call's own error handling.
            return Success();
        }

        return CheckAddresses(addresses, allowPrivate);
    }

    private static UrlValidationResult CheckAddresses(IEnumerable<IPAddress> addresses, bool allowPrivate)
    {
        foreach (var addr in addresses)
        {
            // Metadata endpoints — AWS/Azure/GCP — never opt-in.
            if (addr.ToString() == "169.254.169.254")
                return Fail("Metadata endpoints are not permitted.", UrlValidationErrorCode.MetadataEndpoint);
            if (addr.ToString().StartsWith("fd00:ec2:", StringComparison.OrdinalIgnoreCase))
                return Fail("Metadata endpoints are not permitted.", UrlValidationErrorCode.MetadataEndpoint);

            if (IPAddress.IsLoopback(addr))
            {
                if (!allowPrivate)
                    return Fail("Loopback addresses are not permitted.", UrlValidationErrorCode.LoopbackAddress);
                continue;
            }

            if (addr.AddressFamily == AddressFamily.InterNetwork && IsPrivateIpv4(addr))
            {
                if (!allowPrivate)
                    return Fail("Private IP addresses are not permitted.", UrlValidationErrorCode.PrivateIpAddress);
                continue;
            }

            if (addr.AddressFamily == AddressFamily.InterNetworkV6 && IsPrivateIpv6(addr))
            {
                if (!allowPrivate)
                    return Fail("Private IP addresses are not permitted.", UrlValidationErrorCode.PrivateIpAddress);
                continue;
            }

            // Link-local 169.254.x.x (also catches metadata) — never opt-in.
            var bytes = addr.GetAddressBytes();
            if (addr.AddressFamily == AddressFamily.InterNetwork && bytes[0] == 169 && bytes[1] == 254)
                return Fail("Link-local addresses are not permitted.", UrlValidationErrorCode.PrivateIpAddress);
        }

        return Success();
    }

    private static bool IsPrivateIpv4(IPAddress addr)
    {
        var b = addr.GetAddressBytes();
        // 10.0.0.0/8
        if (b[0] == 10) return true;
        // 172.16.0.0/12
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        // 192.168.0.0/16
        if (b[0] == 192 && b[1] == 168) return true;
        return false;
    }

    private static bool IsPrivateIpv6(IPAddress addr)
    {
        var b = addr.GetAddressBytes();
        // Unique local fc00::/7 — first byte 0xFC or 0xFD
        if ((b[0] & 0xFE) == 0xFC) return true;
        // Link-local fe80::/10
        if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return true;
        return false;
    }

    private static UrlValidationResult Success() => new(true, null);
    private static UrlValidationResult Fail(string message, UrlValidationErrorCode code) => new(false, message, code);
}
