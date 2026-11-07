using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Knowz.Core.Portability;

/// <summary>Public challenge issued by the receiving edition after authenticating and authorizing the live destination.</summary>
public sealed record ArchivePeerChallenge
{
    public string Format { get; init; } = ArchivePeerProof.Format;
    public int Version { get; init; } = 1;
    public string Purpose { get; init; } = ArchivePeerProof.Purpose;
    public string Algorithm { get; init; } = ArchivePeerProof.Algorithm;
    public Guid ChallengeId { get; init; }
    public string Nonce { get; init; } = "";
    public DateTimeOffset IssuedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public Guid DestinationTenantId { get; init; }
    public Guid DestinationVaultId { get; init; }
    public Guid SourceTenantId { get; init; }
    public Guid SourceVaultId { get; init; }
    public string SourceBaseUrl { get; init; } = "";
    public string SignerFingerprint { get; init; } = "";
    public string SignerPublicKey { get; init; } = "";
    public string CredentialIdentitySha256 { get; init; } = "";
    public Guid? ConnectionId { get; init; }
    public string? ConnectionRevision { get; init; }
    public Guid? ExistingBindingId { get; init; }
    public string? ExpectedBindingRevision { get; init; }
}

/// <summary>Public registration intent. The receiver supplies actor/credential identity and the destination route vault.</summary>
public sealed record ArchivePeerRegistrationRequest
{
    public Guid SourceTenantId { get; init; }
    public Guid SourceVaultId { get; init; }
    public string SourceBaseUrl { get; init; } = "";
    public string SignerFingerprint { get; init; } = "";
    public string SignerPublicKey { get; init; } = "";
    public Guid? ConnectionId { get; init; }
    public string? ConnectionRevision { get; init; }
    public Guid? ExistingBindingId { get; init; }
    public string? ExpectedBindingRevision { get; init; }
}

public sealed record ArchivePeerPossessionProof(string Algorithm, string ChallengeBytes, string Signature);

/// <summary>
/// Archive signer possession only. This does not prove encryption-key custody or grant sync authority.
/// The edition must retain the issued challenge, authorize its actor, consume it once, and fence the stored binding revision.
/// </summary>
public static class ArchivePeerProof
{
    public const string Format = "knowz.archive-peer-proof/v1";
    public const string Purpose = "register-sync-peer";
    public const string Algorithm = "ecdsa-p256-sha256-p1363";
    public const int MaximumChallengeBytes = 8192;
    private const string P256Oid = "1.2.840.10045.3.1.7";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 4 };

    public static string Fingerprint(ReadOnlySpan<byte> spki) => Convert.ToHexStringLower(SHA256.HashData(spki));

    /// <summary>HTTPS or loopback HTTP; no credentials, query, fragment, dot segments, backslashes or encoded separators.
    /// Scheme/ASCII IDN host are lower case, default port omitted, escaped path retained and trailing slashes removed.
    /// This is descriptive identity normalization, never permission to fetch an untrusted URL.</summary>
    public static string NormalizeBaseUrl(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 2048 || input != input.Trim() || input.Any(char.IsControl)
            || input.Contains('\\') || !Uri.TryCreate(input, UriKind.Absolute, out var uri)
            || (uri.Scheme != "https" && uri.Scheme != "http") || uri.UserInfo.Length != 0
            || input.Contains('?') || input.Contains('#') || uri.Host.Length == 0)
            throw Invalid("Invalid peer base URL.");
        if (uri.Scheme == "http" && !(uri.IdnHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip)))
            throw Invalid("Peer HTTP URL must be loopback.");
        // Inspect the original path before Uri removes dot segments.
        var authorityEnd = input.IndexOf('/', input.IndexOf("://", StringComparison.Ordinal) + 3);
        var originalPath = authorityEnd < 0 ? "" : input[authorityEnd..];
        foreach (var segment in originalPath.Split('/'))
        {
            var decoded = Uri.UnescapeDataString(segment);
            if (decoded is "." or ".." || decoded.Contains('/') || decoded.Contains('\\') || decoded.Any(char.IsControl))
                throw Invalid("Peer base URL path is ambiguous.");
        }
        var host = uri.IdnHost.ToLowerInvariant();
        if (uri.HostNameType == UriHostNameType.IPv6) host = $"[{host.Trim('[', ']')}]";
        var port = uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
        var path = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/');
        var normalized = $"{uri.Scheme}://{host}{port}{(path.Length == 0 ? "" : "/" + path)}";
        if (normalized.Length > 2048) throw Invalid("Peer base URL is too long.");
        return normalized;
    }

    public static byte[] Canonicalize(ArchivePeerChallenge challenge)
    {
        Validate(challenge);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", challenge.Format); writer.WriteNumber("version", challenge.Version);
            writer.WriteString("purpose", challenge.Purpose); writer.WriteString("algorithm", challenge.Algorithm);
            writer.WriteString("challengeId", Id(challenge.ChallengeId)); writer.WriteString("nonce", challenge.Nonce);
            writer.WriteString("issuedAt", Stamp(challenge.IssuedAt)); writer.WriteString("expiresAt", Stamp(challenge.ExpiresAt));
            writer.WriteString("destinationTenantId", Id(challenge.DestinationTenantId)); writer.WriteString("destinationVaultId", Id(challenge.DestinationVaultId));
            writer.WriteString("sourceTenantId", Id(challenge.SourceTenantId)); writer.WriteString("sourceVaultId", Id(challenge.SourceVaultId));
            writer.WriteString("sourceBaseUrl", challenge.SourceBaseUrl); writer.WriteString("signerFingerprint", challenge.SignerFingerprint);
            writer.WriteString("signerPublicKey", challenge.SignerPublicKey); writer.WriteString("credentialIdentitySha256", challenge.CredentialIdentitySha256);
            writer.WriteString("connectionId", challenge.ConnectionId is { } connection ? Id(connection) : null);
            writer.WriteString("connectionRevision", challenge.ConnectionRevision);
            writer.WriteString("existingBindingId", challenge.ExistingBindingId is { } binding ? Id(binding) : null);
            writer.WriteString("expectedBindingRevision", challenge.ExpectedBindingRevision);
            writer.WriteEndObject();
        }
        return output.ToArray();
    }

    public static ArchivePeerChallenge ReadCanonicalChallenge(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaximumChallengeBytes) throw Invalid("Invalid peer challenge length.");
        try
        {
            var challenge = JsonSerializer.Deserialize<ArchivePeerChallenge>(bytes, Json) ?? throw Invalid("Missing peer challenge.");
            if (!bytes.SequenceEqual(Canonicalize(challenge))) throw Invalid("Peer challenge is not canonical.");
            return challenge;
        }
        catch (JsonException ex) { throw Invalid("Invalid peer challenge JSON.", ex); }
    }

    public static ArchivePeerPossessionProof Sign(ArchivePeerChallenge expected, ECDsa archiveSigner)
    {
        var bytes = Canonicalize(expected);
        var actual = archiveSigner.ExportSubjectPublicKeyInfo();
        if (archiveSigner.ExportParameters(false).Curve.Oid.Value != P256Oid || !actual.AsSpan().SequenceEqual(Decode(expected.SignerPublicKey, 91)))
            throw Invalid("Archive signer does not match the challenged public identity.");
        return new(Algorithm, Convert.ToBase64String(bytes), Convert.ToBase64String(archiveSigner.SignData(bytes,
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
    }

    /// <summary>Expected challenge and pin must come from authenticated saved state, not from this proof.</summary>
    public static void Verify(ArchivePeerPossessionProof proof, ArchivePeerChallenge expected, string independentlyExpectedFingerprint, DateTimeOffset now)
    {
        if (proof is null || proof.Algorithm != Algorithm || !IsHash(independentlyExpectedFingerprint)
            || !string.Equals(expected.SignerFingerprint, independentlyExpectedFingerprint, StringComparison.Ordinal))
            throw Invalid("Peer proof does not match the expected archive signer.");
        var canonical = Canonicalize(expected);
        if (now < expected.IssuedAt || now >= expected.ExpiresAt) throw Invalid("Peer challenge is not currently valid.");
        var signed = Decode(proof.ChallengeBytes, null, MaximumChallengeBytes);
        if (!CryptographicOperations.FixedTimeEquals(signed, canonical)) throw Invalid("Peer proof does not match the issued challenge.");
        using var publicKey = ReadP256(Decode(expected.SignerPublicKey, 91));
        if (!publicKey.VerifyData(signed, Decode(proof.Signature, 64), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw Invalid("Invalid peer possession signature.");
    }

    /// <summary>Stable registration slot excludes replaceable signer/credential values, preventing silent duplicate registration after rotation.</summary>
    public static string BindingSlot(Guid tenant, Guid vault, Guid sourceTenant, Guid sourceVault, string baseUrl)
    {
        if (new[] { tenant, vault, sourceTenant, sourceVault }.Any(x => x == Guid.Empty)) throw Invalid("Missing peer slot identity.");
        var text = $"knowz.archive-peer-slot/v1|{Id(tenant)}|{Id(vault)}|{Id(sourceTenant)}|{Id(sourceVault)}|{NormalizeBaseUrl(baseUrl)}";
        return Fingerprint(System.Text.Encoding.UTF8.GetBytes(text));
    }

    private static void Validate(ArchivePeerChallenge value)
    {
        if (value is null || value.Format != Format || value.Version != 1 || value.Purpose != Purpose || value.Algorithm != Algorithm
            || new[] { value.ChallengeId, value.DestinationTenantId, value.DestinationVaultId, value.SourceTenantId, value.SourceVaultId }.Any(x => x == Guid.Empty)
            || value.IssuedAt.Offset != TimeSpan.Zero || value.ExpiresAt.Offset != TimeSpan.Zero
            || value.IssuedAt.Ticks % TimeSpan.TicksPerMillisecond != 0 || value.ExpiresAt.Ticks % TimeSpan.TicksPerMillisecond != 0
            || value.ExpiresAt <= value.IssuedAt || value.ExpiresAt - value.IssuedAt > TimeSpan.FromMinutes(5)
            || !IsHash(value.SignerFingerprint) || !IsHash(value.CredentialIdentitySha256)
            || NormalizeBaseUrl(value.SourceBaseUrl) != value.SourceBaseUrl)
            throw Invalid("Invalid peer challenge identity, lifetime, or format.");
        ValidateRevision(value.ConnectionId, value.ConnectionRevision);
        ValidateRevision(value.ExistingBindingId, value.ExpectedBindingRevision);
        _ = Decode(value.Nonce, 32);
        var spki = Decode(value.SignerPublicKey, 91);
        if (Fingerprint(spki) != value.SignerFingerprint) throw Invalid("Peer signer fingerprint mismatch.");
        using var key = ReadP256(spki);
    }
    private static void ValidateRevision(Guid? id, string? revision)
    {
        if (id == Guid.Empty || id.HasValue != (revision is not null) || revision is not null
            && (revision.Length is 0 or > 256 || revision.Any(c => c < 0x21 || c > 0x7e)))
            throw Invalid("Invalid peer revision binding.");
    }
    private static ECDsa ReadP256(byte[] spki)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(spki, out var read);
            if (read != spki.Length || key.ExportParameters(false).Curve.Oid.Value != P256Oid
                || !key.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(spki)) throw Invalid("Peer signer must be a canonical P256 SPKI.");
            return key;
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException)
        { key.Dispose(); throw Invalid("Invalid peer signer public key.", ex); }
    }
    private static byte[] Decode(string? value, int? length, int maximum = 8192)
    {
        if (value is null || value.Length > (maximum + 2) / 3 * 4) throw Invalid("Invalid peer proof encoding.");
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (Convert.ToBase64String(bytes) != value || length.HasValue && bytes.Length != length || bytes.Length > maximum)
                throw Invalid("Peer proof encoding is not canonical.");
            return bytes;
        }
        catch (FormatException ex) { throw Invalid("Invalid peer proof encoding.", ex); }
    }
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Id(Guid value) => value.ToString("D");
    private static string Stamp(DateTimeOffset value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    private static InvalidDataException Invalid(string message, Exception? inner = null) => new(message, inner);
}
