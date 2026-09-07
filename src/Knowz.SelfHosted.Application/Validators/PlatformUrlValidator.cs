namespace Knowz.SelfHosted.Application.Validators;

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Default <see cref="IUrlValidator"/> — enforces HTTPS, public DNS allowlist, and SSRF guards.
/// Pattern mirrored from the main platform's Knowz.Application.Validators.UrlValidator but
/// scoped to the selfhosted needs (no full Application-layer dependency).
/// </summary>
public class PlatformUrlValidator : IUrlValidator
{
    private readonly IHostEnvironment _env;

    // Allowlisted production/dev platform hosts. Localhost is allowed ONLY in Development.
    private static readonly HashSet<string> AllowlistedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.knowz.io",
        "api.dev.knowz.io",
    };

    public PlatformUrlValidator(IHostEnvironment env)
    {
        _env = env;
    }

    public UrlValidationResult ValidatePlatformUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return Fail("Platform URL is required.", UrlValidationErrorCode.InvalidFormat);

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return Fail("Platform URL is not a valid absolute URL.", UrlValidationErrorCode.InvalidFormat);

        // Scheme check — HTTPS only (except http://localhost in Development).
        var isLocalhostHost = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                              || uri.Host == "127.0.0.1"
                              || uri.Host == "::1";
        var isDev = _env.IsDevelopment();

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            if (!(isDev && isLocalhostHost && uri.Scheme == Uri.UriSchemeHttp))
                return Fail("Platform URL must use https://.", UrlValidationErrorCode.NonHttpsScheme);
        }

        // Allowlist check. Localhost only permitted in Development.
        if (isLocalhostHost)
        {
            if (!isDev)
                return Fail("Loopback addresses are not permitted.", UrlValidationErrorCode.LoopbackAddress);
            return Success();
        }

        if (!AllowlistedHosts.Contains(uri.Host))
            return Fail($"Host '{uri.Host}' is not in the platform allowlist.", UrlValidationErrorCode.NotAllowlisted);

        // Defense-in-depth: if the allowlisted host literal resolves to a private/metadata
        // address (e.g. DNS rebinding, internal DNS poisoning), reject. We resolve ONCE and
        // compare — we do NOT trust a later resolution for the actual outbound call; the
        // HttpClient will re-resolve under its own TLS enforcement.
        var rebindResult = CheckForPrivateAddresses(uri.Host);
        if (!rebindResult.IsValid)
            return rebindResult;

        return Success();
    }

    private static UrlValidationResult CheckForPrivateAddresses(string host)
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

        foreach (var addr in addresses)
        {
            // Metadata endpoints — AWS/Azure/GCP
            if (addr.ToString() == "169.254.169.254")
                return Fail("Metadata endpoints are not permitted.", UrlValidationErrorCode.MetadataEndpoint);
            if (addr.ToString().StartsWith("fd00:ec2:", StringComparison.OrdinalIgnoreCase))
                return Fail("Metadata endpoints are not permitted.", UrlValidationErrorCode.MetadataEndpoint);

            if (IPAddress.IsLoopback(addr))
                return Fail("Loopback addresses are not permitted.", UrlValidationErrorCode.LoopbackAddress);

            if (addr.AddressFamily == AddressFamily.InterNetwork && IsPrivateIpv4(addr))
                return Fail("Private IP addresses are not permitted.", UrlValidationErrorCode.PrivateIpAddress);

            if (addr.AddressFamily == AddressFamily.InterNetworkV6 && IsPrivateIpv6(addr))
                return Fail("Private IP addresses are not permitted.", UrlValidationErrorCode.PrivateIpAddress);

            // Link-local 169.254.x.x (also catches metadata)
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
