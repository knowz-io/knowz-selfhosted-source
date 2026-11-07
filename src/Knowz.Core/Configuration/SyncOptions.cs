namespace Knowz.Core.Configuration;

/// <summary>
/// Operator-configured settings for the Destinations live plane (vault sync between running
/// instances). Bound from the <c>Sync</c> configuration section
/// (<c>Sync__AllowedPlatformHosts__0=peer.example.com</c> as an environment variable).
/// See <c>knowzcode/specs/ADR_G6_Allowlisted_Peer_Destinations.md</c> (#1043).
/// </summary>
public class SyncOptions
{
    public const string SectionName = "Sync";

    /// <summary>
    /// Additional platform / peer hosts this instance may connect to, on top of the built-in
    /// Knowz Cloud hosts (<c>api.knowz.io</c>, <c>api.dev.knowz.io</c>). Host names only,
    /// compared case-insensitively; an entry written as a URL is reduced to its host.
    /// Hosts NOT in this list are rejected — this is an allowlist, never a bypass of the
    /// HTTPS / loopback / private-address / metadata SSRF guards.
    /// </summary>
    public List<string> AllowedPlatformHosts { get; set; } = new();

    /// <summary>
    /// Narrow LAN / smoke opt-in (ADR follow-up <c>SEC_SyncAllowPrivatePeerAddresses</c>).
    /// Default <c>false</c>. When <c>true</c>, hosts that are already in
    /// <see cref="AllowedPlatformHosts"/> (or the built-in Cloud defaults) may resolve to
    /// loopback or RFC1918 / ULA addresses, and <c>http://</c> is accepted for those
    /// allowlisted private/loopback peers so Docker/LAN stacks without TLS can be exercised.
    /// Metadata endpoints (<c>169.254.169.254</c>, <c>fd00:ec2:</c>) stay rejected. This never
    /// bypasses the allowlist and must not be enabled on internet-facing Production without an
    /// explicit operator decision.
    /// </summary>
    public bool AllowPrivatePeerAddresses { get; set; }
}
