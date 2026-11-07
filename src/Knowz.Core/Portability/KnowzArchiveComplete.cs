using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Knowz.Core.Portability;

public sealed class KnowzArchiveException(string message, Exception? inner = null) : IOException(message, inner);

public sealed class KnowzArchiveManifest
{
    public string FormatId { get; set; } = KnowzArchiveProfile.FormatId;
    public int FormatVersion { get; set; } = KnowzArchiveProfile.FormatVersion;
    public int SchemaVersion { get; set; } = KnowzArchiveProfile.SchemaVersion;
    public string FileExtension { get; set; } = KnowzArchiveProfile.FileExtension;
    public List<string> RequiredCapabilities { get; set; } = [KnowzArchiveProfile.Capability];
    public string SourceEdition { get; set; } = "";
    public Guid SourceTenantId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string PreservationState { get; set; } = "complete";
    public KnowzArchiveInventory Inventory { get; set; } = new();
    [JsonIgnore] public Guid ArchiveId => Inventory.ArchiveId;
}

public sealed class KnowzArchiveInventory
{
    public Guid ArchiveId { get; set; }
    public string SnapshotBoundary { get; set; } = "";
    public List<KnowzArchiveEntry> Parts { get; set; } = [];
    public Dictionary<string, long> PayloadCounts { get; set; } = new(StringComparer.Ordinal);
    public List<PortableOmission> Omissions { get; set; } = [];
    public string? DependencyReport { get; set; }
}

public sealed class KnowzArchiveEntry
{
    public string Role { get; set; } = "";
    public string Path { get; set; } = "";
    public long Length { get; set; }
    public string Sha256 { get; set; } = "";
}

public sealed class KnowzArchiveSignature
{
    public string Algorithm { get; set; } = "ecdsa-p256-sha256-p1363";
    public string PublicKey { get; set; } = "";
    public string PublicKeyFingerprint { get; set; } = "";
    public string Signature { get; set; } = "";
}

public sealed class ArchiveSourceReceipt
{
    public Guid ArchiveId { get; set; }
    public string ManifestSha256 { get; set; } = "";
    public string SourceSnapshot { get; set; } = "";
    public string SignerFingerprint { get; set; } = "";
    public string SignerPublicKey { get; set; } = "";
    public int EntryCount { get; set; }
    public long PayloadCount { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class KnowzArchiveWriteOptions
{
    public Guid ArchiveId { get; init; } = Guid.NewGuid();
    public string SourceSnapshot { get; init; } = "";
    public ECDsa? SigningKey { get; init; }
    public bool AllowPartial { get; init; }
}

public sealed class KnowzArchiveReadOptions
{
    public string? ExpectedSignerFingerprint { get; init; }
    public bool AllowPartial { get; init; }
    public bool LeaveOpen { get; init; }
}

public static class KnowzArchiveLimits
{
    public const int MaxManifestBytes = 4 * 1024 * 1024;
    public const int MaxPackageBytes = 8 * 1024 * 1024;
    public const int MaxMetadataBytes = 64 * 1024 * 1024;
    public const int MaxEntries = 10_000;
    public const long MaxArchiveBytes = 32L * 1024 * 1024 * 1024;
    public const long MaxOrdinaryFileBytes = MaxArchiveBytes;
    public const int MaxExpansionRatio = 200;
}

internal static class ArchiveJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();
    public static readonly JsonSerializerOptions MetadataOptions = CreateOptions(omitBinary: true);
    private static JsonSerializerOptions CreateOptions(bool omitBinary = false)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { MaxDepth = 64, PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        if (omitBinary)
        {
            options.TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers = { info => { foreach (var property in info.Properties)
                    if (property.Name == "binaryContentBase64") property.ShouldSerialize = (_, _) => false; } }
            };
        }
        return options;
    }
    public static string Digest(ReadOnlySpan<byte> value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    public static T Deserialize<T>(byte[] bytes, int limit)
    {
        using var document = StrictJson.Parse(bytes, limit);
        return document.RootElement.Deserialize<T>(Options) ?? throw new KnowzArchiveException("Archive metadata is null.");
    }

    public static void ValidatePackageShape(PortableExportPackage package)
    {
        try { PortableZipReader.ValidatePackage(package); }
        catch (PortableZipSecurityException ex) { throw new KnowzArchiveException("Archive package shape is invalid.", ex); }

        IEnumerable<object?>?[] collections = [package.CryptoContexts, package.CryptoKeyCatalog, package.SealedItems,
            package.SealedVersions, package.SealedFiles, package.SealedComments, package.Omissions,
            package.VaultCryptoContextLinks, package.PayloadKeyReferences, package.SealedAttachments,
            package.SuspendedReleasePolicies, package.DependencyBoundItems, package.PlainVersions];
        if (collections.Any(rows => rows?.Any(row => row is null) == true)
            || package.DependencyBoundItems?.Any(item => item.Versions == null || item.Versions.Any(version => version == null)) == true)
            throw new KnowzArchiveException("Archive package collections must contain non-null records and history arrays.");
    }
}

/// <summary>Streams actual package metadata and binaries, then authenticates their exact inventory.</summary>
public static class KnowzArchiveWriter
{
    public static async Task<ArchiveSourceReceipt> WriteAsync(Stream output, IReadOnlyList<PortableExportPackage> packages,
        Func<Guid, CancellationToken, Task<Stream>>? openBinary, KnowzArchiveWriteOptions options, CancellationToken ct = default)
    {
        if (packages.Count == 0 || packages.Count > 128 || options.ArchiveId == Guid.Empty || string.IsNullOrWhiteSpace(options.SourceSnapshot))
            throw new KnowzArchiveException("Archive requires packages and an authoritative snapshot.");
        var combinedSource = PortableGraphPackages.Combine(packages);
        var declaredTotal = 0L;
        foreach (var file in combinedSource.Data.FileRecords.Select(f => (f.SizeBytes, Maximum: KnowzArchiveLimits.MaxOrdinaryFileBytes))
            .Concat((combinedSource.SealedFiles ?? []).Select(f => (f.SizeBytes, Maximum: EncryptedGraphProfile.MaxFileBytes))))
        {
            if (file.SizeBytes < 0 || file.SizeBytes > file.Maximum || file.SizeBytes > KnowzArchiveLimits.MaxArchiveBytes - declaredTotal)
                throw new KnowzArchiveException("Binary sizes exceed the supported archive limits.");
            declaredTotal += file.SizeBytes;
        }
        var sealedPaths = (combinedSource.SealedFiles ?? []).Where(f => f.BinaryEntryPath != null).Select(f => f.BinaryEntryPath!).ToHashSet(StringComparer.Ordinal);
        if (combinedSource.Data.FileRecords.Any(f => f.BinaryEntryPath != null && sealedPaths.Contains(f.BinaryEntryPath)))
            throw new KnowzArchiveException("A binary path has conflicting plain and encrypted owners.");
        foreach (var package in packages)
        {
            declaredTotal += JsonSerializer.SerializeToUtf8Bytes(package, ArchiveJson.MetadataOptions).LongLength;
            if (declaredTotal > KnowzArchiveLimits.MaxArchiveBytes)
                throw new KnowzArchiveException("Archive metadata and binaries exceed the aggregate limit.");
        }
        var signer = options.SigningKey ?? throw new KnowzArchiveException("A source signing identity is required.");
        if (signer.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
            throw new KnowzArchiveException("The source signing identity must use P-256.");
        var manifest = new KnowzArchiveManifest
        {
            SourceEdition = packages[0].SourceEdition, SourceTenantId = packages[0].SourceTenantId,
            Inventory = new() { ArchiveId = options.ArchiveId, SnapshotBoundary = options.SourceSnapshot },
        };
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filesWritten = new Dictionary<Guid, KnowzArchiveEntry>();
        var metadataBytes = 0L; var totalBytes = 0L; var requiredPayloadCount = 0L;
        var normalizedPackages = new List<PortableExportPackage>();
        byte[]? manifestBytes = null; KnowzArchiveSignature? signature = null;
        using (var bounded = new BoundedArchiveOutput(output))
        using (var zip = new ZipArchive(bounded, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var part = 0; part < packages.Count; part++)
            {
                ct.ThrowIfCancellationRequested();
                var source = packages[part];
                if (source.SourceTenantId != manifest.SourceTenantId || source.SourceEdition != manifest.SourceEdition)
                    throw new KnowzArchiveException("Archive packages have conflicting source identity.");
                var metadata = JsonSerializer.SerializeToUtf8Bytes(source, ArchiveJson.MetadataOptions);
                if (metadata.Length > KnowzArchiveLimits.MaxPackageBytes)
                    throw new KnowzArchiveException("A package exceeds the supported metadata limit.");
                var package = ArchiveJson.Deserialize<PortableExportPackage>(metadata, KnowzArchiveLimits.MaxPackageBytes);
                foreach (var file in package.SealedFiles ?? [])
                {
                    var original = source.SealedFiles!.Single(f => f.Id == file.Id);
                    var entry = await WriteFileAsync(zip, file.Id, file.SizeBytes, EncryptedGraphProfile.MaxFileBytes, original.BinaryContentBase64, openBinary, filesWritten, ct);
                    file.OriginalFileId = file.OriginalFileId == Guid.Empty ? file.Id : file.OriginalFileId;
                    file.BinaryEntryPath = entry.Path; file.BinarySha256 = entry.Sha256; file.BinaryContentBase64 = null;
                    if (original.BinarySha256 != null && original.BinarySha256 != entry.Sha256)
                        throw new KnowzArchiveException("Source binary changed during archive capture.");
                }
                foreach (var file in package.Data.FileRecords)
                {
                    var original = source.Data.FileRecords.Single(f => f.Id == file.Id);
                    var entry = await WriteFileAsync(zip, file.Id, file.SizeBytes, KnowzArchiveLimits.MaxOrdinaryFileBytes, original.BinaryContentBase64, openBinary, filesWritten, ct);
                    if (original.BinarySha256 != null && original.BinarySha256 != entry.Sha256)
                        throw new KnowzArchiveException("Source ordinary binary changed during archive capture.");
                    file.BinaryEntryPath = entry.Path; file.BinarySha256 = entry.Sha256; file.BinaryContentBase64 = null; file.BlobUri = null;
                }
                normalizedPackages.Add(package);
                if (package.PreservationState is not ("complete" or "partial")) throw new KnowzArchiveException("Unsupported preservation state.");
                manifest.Inventory.Omissions.AddRange(package.Omissions ?? []);
                requiredPayloadCount += (package.PayloadKeyReferences?.Count ?? 0) + package.Data.KnowledgeItems.Count + package.Data.Comments.Count + package.Data.FileRecords.Count + (package.PlainVersions?.Count ?? 0) + (package.DependencyBoundItems?.Sum(i => 1 + i.Versions.Count) ?? 0);
                metadata = JsonSerializer.SerializeToUtf8Bytes(package, ArchiveJson.Options);
                if (metadata.Length > KnowzArchiveLimits.MaxPackageBytes || (metadataBytes += metadata.Length) > KnowzArchiveLimits.MaxMetadataBytes)
                    throw new KnowzArchiveException("Archive package metadata exceeds its size limit.");
                var path = $"packages/{part + 1:D6}.json";
                await WriteBytesAsync(zip, path, metadata, ct);
                manifest.Inventory.Parts.Add(new() { Role = "package", Path = path, Length = metadata.Length, Sha256 = ArchiveJson.Digest(metadata) });
            }
            var problem = EncryptedGraphProfile.Validate(PortableGraphPackages.Combine(normalizedPackages), requireComplete: !options.AllowPartial);
            if (problem != null) throw new KnowzArchiveException(problem);
            foreach (var file in filesWritten.Values) manifest.Inventory.Parts.Add(file);
            foreach (var entry in manifest.Inventory.Parts)
            {
                if (!paths.Add(entry.Path) || (totalBytes += entry.Length) > KnowzArchiveLimits.MaxArchiveBytes)
                    throw new KnowzArchiveException("Archive inventory exceeds its supported limits.");
            }
            if (paths.Count + 2 > KnowzArchiveLimits.MaxEntries) throw new KnowzArchiveException("Archive has too many members.");
            manifest.Inventory.Parts = manifest.Inventory.Parts.OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
            manifest.Inventory.PayloadCounts["required"] = requiredPayloadCount;
            manifest.Inventory.PayloadCounts["packages"] = packages.Count;
            manifest.Inventory.PayloadCounts["files"] = filesWritten.Count;
            if (manifest.Inventory.Omissions.Count > 0 || packages.Any(p => p.PreservationState == "partial"))
            {
                if (!options.AllowPartial) throw new KnowzArchiveException("Complete export has unresolved dependencies.");
                manifest.PreservationState = "partial"; manifest.Inventory.DependencyReport = "partial";
            }
            manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ArchiveJson.Options);
            if (manifestBytes.Length > KnowzArchiveLimits.MaxManifestBytes) throw new KnowzArchiveException("Archive manifest exceeds its size limit.");
            var publicKey = signer.ExportSubjectPublicKeyInfo();
            signature = new() { PublicKey = Convert.ToBase64String(publicKey), PublicKeyFingerprint = ArchiveJson.Digest(publicKey),
                Signature = Convert.ToBase64String(signer.SignData(manifestBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) };
            await WriteBytesAsync(zip, "manifest.json", manifestBytes, ct);
            await WriteBytesAsync(zip, "signature.json", JsonSerializer.SerializeToUtf8Bytes(signature, ArchiveJson.Options), ct);
        }
        return new()
        {
            ArchiveId = options.ArchiveId, ManifestSha256 = ArchiveJson.Digest(manifestBytes!), SourceSnapshot = options.SourceSnapshot,
            SignerFingerprint = signature!.PublicKeyFingerprint, SignerPublicKey = signature.PublicKey,
            EntryCount = manifest.Inventory.Parts.Count, PayloadCount = requiredPayloadCount, CreatedAt = manifest.CreatedAt,
        };
    }

    internal static void ValidatePackageSet(IReadOnlyList<PortableExportPackage> packages)
    {
        var owners = new HashSet<(string Kind, Guid Id)>();
        var contexts = new Dictionary<Guid, string>(); var catalog = new Dictionary<(Guid Context, int Generation), string>();
        foreach (var package in packages)
        {
            var records = (package.SealedItems ?? []).Select(x => ("knowledge", x.Id))
                .Concat((package.DependencyBoundItems ?? []).Select(x => ("knowledge", x.Id)))
                .Concat(package.Data.KnowledgeItems.Select(x => ("knowledge", x.Id)))
                .Concat((package.SealedComments ?? []).Select(x => ("comment", x.Id)))
                .Concat(package.Data.Comments.Select(x => ("comment", x.Id)))
                .Concat((package.SealedFiles ?? []).Select(x => ("file", x.Id)))
                .Concat(package.Data.FileRecords.Select(x => ("file", x.Id)))
                .Concat((package.SealedVersions ?? []).Select(x => ("version", x.Id)))
                .Concat((package.DependencyBoundItems ?? []).SelectMany(x => x.Versions).Select(x => ("version", x.Id)))
                .Concat((package.PlainVersions ?? []).Select(x => ("version", x.Id)))
                .Concat((package.SealedAttachments ?? []).Select(x => ("attachment", x.Id)));
            foreach (var record in records)
                if (!owners.Add(record)) throw new KnowzArchiveException("Archive repeats an owner across package members.");
            foreach (var context in package.CryptoContexts ?? [])
            {
                var bytes = JsonSerializer.Serialize(context, ArchiveJson.Options);
                if (contexts.TryGetValue(context.ContextId, out var previous) && previous != bytes)
                    throw new KnowzArchiveException("Archive has conflicting crypto contexts across packages.");
                contexts[context.ContextId] = bytes;
            }
            foreach (var key in package.CryptoKeyCatalog ?? [])
            {
                var bytes = JsonSerializer.Serialize(key, ArchiveJson.Options); var identity = (key.ContextId, key.Generation);
                if (catalog.TryGetValue(identity, out var previous) && previous != bytes)
                    throw new KnowzArchiveException("Archive has conflicting historical public keys across packages.");
                catalog[identity] = bytes;
            }
        }
    }

    private static async Task<KnowzArchiveEntry> WriteFileAsync(ZipArchive zip, Guid id, long expectedLength, long maxBytes, string? inline,
        Func<Guid, CancellationToken, Task<Stream>>? open, Dictionary<Guid, KnowzArchiveEntry> written, CancellationToken ct)
    {
        if (written.TryGetValue(id, out var prior))
        {
            if (prior.Length != expectedLength || prior.Length > maxBytes) throw new KnowzArchiveException("Shared binary has conflicting metadata.");
            return prior;
        }
        if (id == Guid.Empty || expectedLength < 0 || expectedLength > maxBytes)
            throw new KnowzArchiveException("Binary size exceeds the declared supported limit.");
        await using var input = open != null ? await open(id, ct)
            : inline != null ? new MemoryStream(Convert.FromBase64String(inline), writable: false)
            : throw new KnowzArchiveException("Required binary bytes are unavailable.");
        var path = $"files/{id:D}.bin";
        using var destination = zip.CreateEntry(path, CompressionLevel.NoCompression).Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024]; var length = 0L;
        while (true)
        {
            var read = await input.ReadAsync(buffer, ct); if (read == 0) break;
            length += read;
            if (length > expectedLength || length > maxBytes) throw new KnowzArchiveException("Binary length changed during capture.");
            hash.AppendData(buffer, 0, read); await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        if (length != expectedLength) throw new KnowzArchiveException("Required binary is truncated.");
        var result = new KnowzArchiveEntry { Role = "file", Path = path, Length = length, Sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() };
        written.Add(id, result); return result;
    }
    private sealed class BoundedArchiveOutput(Stream output) : Stream
    {
        private long _length;
        private void Reserve(int count)
        {
            if (count > KnowzArchiveLimits.MaxArchiveBytes - _length) throw new KnowzArchiveException("Archive container exceeds its aggregate size limit.");
            _length += count;
        }
        public override void Write(byte[] buffer, int offset, int count) { Reserve(count); output.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Reserve(buffer.Length); output.Write(buffer); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        { Reserve(buffer.Length); await output.WriteAsync(buffer, ct); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override void Flush() => output.Flush();
        public override Task FlushAsync(CancellationToken ct) => output.FlushAsync(ct);
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => output.CanWrite;
        public override long Length => _length; public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static async Task WriteBytesAsync(ZipArchive zip, string path, byte[] bytes, CancellationToken ct)
    {
        using var output = zip.CreateEntry(path, CompressionLevel.NoCompression).Open();
        await output.WriteAsync(bytes, ct);
    }
}

public sealed class ValidatedKnowzArchive : IDisposable
{
    private readonly ZipArchive _zip;
    private readonly Stream _input;
    private readonly bool _leaveOpen;
    private readonly Dictionary<string, KnowzArchiveEntry> _entries;
    private readonly SemaphoreSlim _binaryGate = new(1);
    private bool _disposed;
    internal ValidatedKnowzArchive(ZipArchive zip, Stream input, bool leaveOpen, KnowzArchiveManifest manifest,
        IReadOnlyList<PortableExportPackage> packages, string trust, string digest, string signerFingerprint, string signerPublicKey)
    {
        _zip = zip; _input = input; _leaveOpen = leaveOpen; Manifest = manifest; Packages = packages;
        SourceTrust = trust; ManifestSha256 = digest; SignerFingerprint = signerFingerprint; SignerPublicKey = signerPublicKey;
        // Public inspection DTOs are mutable; they must never redefine the authenticated inventory.
        _entries = manifest.Inventory.Parts.ToDictionary(e => e.Path,
            e => new KnowzArchiveEntry { Path = e.Path, Role = e.Role, Length = e.Length, Sha256 = e.Sha256 }, StringComparer.Ordinal);
    }
    public KnowzArchiveManifest Manifest { get; }
    public IReadOnlyList<PortableExportPackage> Packages { get; }
    public string SourceTrust { get; }
    public string ManifestSha256 { get; }
    public string SignerFingerprint { get; }
    public string SignerPublicKey { get; }
    public async Task<Stream> OpenBinaryAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_entries.TryGetValue(path, out var entry) || entry.Role is not ("file" or "variant"))
            throw new KnowzArchiveException("Binary reference is outside the validated inventory.");
        await _binaryGate.WaitAsync(ct);
        ProtectedSpoolStream? snapshot = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var input = _zip.GetEntry(path)!.Open();
            snapshot = await ProtectedSpoolStream.CreateAsync(input, ct: ct, maxBytes: entry.Length);
            if (snapshot.Length != entry.Length) throw new KnowzArchiveException("Archive binary changed after inspection.");
            var digest = await SHA256.HashDataAsync(snapshot, ct);
            if (Convert.ToHexString(digest).ToLowerInvariant() != entry.Sha256)
                throw new KnowzArchiveException("Archive binary changed after inspection.");
            snapshot.Position = 0;
            var result = snapshot; snapshot = null;
            return result;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException)
        {
            throw ex is KnowzArchiveException ? ex : new KnowzArchiveException("Archive binary verification failed.", ex);
        }
        finally { snapshot?.Dispose(); _binaryGate.Release(); }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _zip.Dispose(); if (!_leaveOpen) _input.Dispose(); }
}

public static class KnowzArchiveReader
{
    public static async Task<ValidatedKnowzArchive> OpenAsync(Stream input, KnowzArchiveReadOptions? options = null, CancellationToken ct = default)
    {
        options ??= new(); ZipArchive? zip = null;
        try
        {
            if (!input.CanSeek) throw new KnowzArchiveException("A protected seekable spool is required for archive inspection.");
            if (input.Length > KnowzArchiveLimits.MaxArchiveBytes) throw new KnowzArchiveException("Archive exceeds its supported size limit.");
            zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count is < 3 or > KnowzArchiveLimits.MaxEntries) throw new KnowzArchiveException("Invalid archive member count.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var total = 0L;
            foreach (var entry in zip.Entries)
            {
                if (!EncryptedGraphProfile.IsSafePath(entry.FullName) || !names.Add(entry.FullName)
                    || entry.FullName.EndsWith('/') || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                    throw new KnowzArchiveException("Duplicate, unsafe or symlink archive member.");
                if (entry.Length < 0 || (total += entry.Length) > KnowzArchiveLimits.MaxArchiveBytes
                    || entry.Length > 0 && (entry.CompressedLength == 0 || entry.Length / (double)entry.CompressedLength > KnowzArchiveLimits.MaxExpansionRatio))
                    throw new KnowzArchiveException("Archive expansion exceeds its supported limits.");
            }
            var manifestBytes = await ReadMetadataAsync(zip.GetEntry("manifest.json"), KnowzArchiveLimits.MaxManifestBytes, ct);
            var manifest = ArchiveJson.Deserialize<KnowzArchiveManifest>(manifestBytes, KnowzArchiveLimits.MaxManifestBytes);
            if (manifest.Inventory == null || manifest.Inventory.Parts == null || manifest.Inventory.Parts.Any(p => p == null)
                || manifest.Inventory.PayloadCounts == null || manifest.Inventory.Omissions == null || manifest.Inventory.Omissions.Any(o => o == null)
                || manifest.FormatId != KnowzArchiveProfile.FormatId || manifest.FormatVersion != 1 || manifest.SchemaVersion != 4
                || manifest.FileExtension != KnowzArchiveProfile.FileExtension || manifest.RequiredCapabilities is not { Count: 1 }
                || manifest.RequiredCapabilities[0] != KnowzArchiveProfile.Capability || manifest.Inventory.ArchiveId == Guid.Empty
                || string.IsNullOrWhiteSpace(manifest.Inventory.SnapshotBoundary) || manifest.SourceTenantId == Guid.Empty
                || manifest.PreservationState is not ("complete" or "partial")) throw new KnowzArchiveException("Unsupported or incomplete archive profile.");
            if (manifest.PreservationState == "partial" && !options.AllowPartial || manifest.PreservationState == "complete" && manifest.Inventory.Omissions.Count > 0)
                throw new KnowzArchiveException("Archive is partial; explicit partial import is required.");
            var signatureBytes = await ReadMetadataAsync(zip.GetEntry("signature.json"), KnowzArchiveLimits.MaxManifestBytes, ct);
            var signature = ArchiveJson.Deserialize<KnowzArchiveSignature>(signatureBytes, KnowzArchiveLimits.MaxManifestBytes);
            VerifySignature(manifestBytes, signature, options.ExpectedSignerFingerprint);
            var entries = new Dictionary<string, KnowzArchiveEntry>(StringComparer.Ordinal);
            var packages = new List<PortableExportPackage>(); var metadataTotal = 0L; var payloadCount = 0L;
            foreach (var descriptor in manifest.Inventory.Parts)
            {
                ct.ThrowIfCancellationRequested();
                if (!EncryptedGraphProfile.IsSafePath(descriptor.Path) || !entries.TryAdd(descriptor.Path, descriptor)
                    || descriptor.Path is "manifest.json" or "signature.json" || !EncryptedGraphProfile.IsSha256(descriptor.Sha256)
                    || descriptor.Role is not ("package" or "file" or "variant") || descriptor.Length < 0)
                    throw new KnowzArchiveException("Invalid archive inventory entry.");
                var member = zip.GetEntry(descriptor.Path) ?? throw new KnowzArchiveException("A required archive member is missing.");
                if (member.Length != descriptor.Length)
                    throw new KnowzArchiveException("Archive member length does not match its inventory.");
                if (descriptor.Role == "package")
                {
                    var bytes = await ReadMetadataAsync(member, KnowzArchiveLimits.MaxPackageBytes, ct);
                    if ((metadataTotal += bytes.Length) > KnowzArchiveLimits.MaxMetadataBytes || packages.Count >= 128)
                        throw new KnowzArchiveException("Archive metadata exceeds its aggregate limit.");
                    if (ArchiveJson.Digest(bytes) != descriptor.Sha256) throw new KnowzArchiveException("Archive package digest failed.");
                    var package = ArchiveJson.Deserialize<PortableExportPackage>(bytes, KnowzArchiveLimits.MaxPackageBytes);
                    ArchiveJson.ValidatePackageShape(package);
                    if (package.SourceTenantId != manifest.SourceTenantId || package.SourceEdition != manifest.SourceEdition)
                        throw new KnowzArchiveException("Package identity disagrees with its signed inventory.");
                    if (package.PreservationState is not ("complete" or "partial")
                        || manifest.PreservationState == "complete" && package.PreservationState != "complete")
                        throw new KnowzArchiveException("Package preservation state disagrees with its signed inventory.");

                    payloadCount += (package.PayloadKeyReferences?.Count ?? 0) + package.Data.KnowledgeItems.Count + package.Data.Comments.Count + package.Data.FileRecords.Count + (package.PlainVersions?.Count ?? 0) + (package.DependencyBoundItems?.Sum(i => 1 + i.Versions.Count) ?? 0);
                    packages.Add(package);
                }

            }
            if (packages.Count == 0 || entries.Count + 2 != zip.Entries.Count
                || !manifest.Inventory.PayloadCounts.TryGetValue("required", out var declared) || declared != payloadCount
                || !manifest.Inventory.PayloadCounts.TryGetValue("packages", out var declaredPackages) || declaredPackages != packages.Count
                || !manifest.Inventory.PayloadCounts.TryGetValue("files", out var declaredFiles) || declaredFiles != entries.Values.Count(e => e.Role != "package"))
                throw new KnowzArchiveException("Archive inventory is incomplete or has undeclared members/counts.");
            var problem = EncryptedGraphProfile.Validate(PortableGraphPackages.Combine(packages), requireComplete: manifest.PreservationState == "complete");
            if (problem != null) throw new KnowzArchiveException(problem);
            var referenced = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var package in packages)
            {
                foreach (var file in package.SealedFiles ?? [])
                {
                    if (file.BinaryContentBase64 != null || file.BinaryEntryPath == null || !entries.TryGetValue(file.BinaryEntryPath, out var entry)
                        || entry.Role != "file" || entry.Length != file.SizeBytes || entry.Sha256 != file.BinarySha256 || entry.Length > EncryptedGraphProfile.MaxFileBytes)
                        throw new KnowzArchiveException("Encrypted file is outside the authenticated binary graph.");
                    if (referenced.TryGetValue(file.BinaryEntryPath, out var encrypted) && !encrypted)
                        throw new KnowzArchiveException("A binary path has conflicting plain and encrypted owners.");
                    referenced[file.BinaryEntryPath] = true;
                }
                foreach (var file in package.Data.FileRecords)
                {
                    if (file.BinaryContentBase64 != null || file.BinaryEntryPath == null || !entries.TryGetValue(file.BinaryEntryPath, out var entry)
                        || entry.Role != "file" || entry.Length != file.SizeBytes || entry.Sha256 != file.BinarySha256 || entry.Length > KnowzArchiveLimits.MaxOrdinaryFileBytes)
                        throw new KnowzArchiveException("Plain file is outside the authenticated binary graph.");
                    if (referenced.TryGetValue(file.BinaryEntryPath, out var encrypted) && encrypted)
                        throw new KnowzArchiveException("A binary path has conflicting plain and encrypted owners.");
                    referenced[file.BinaryEntryPath] = false;
                }
            }
            if (entries.Values.Any(e => e.Role != "package" && !referenced.ContainsKey(e.Path)))
                throw new KnowzArchiveException("Archive has an unowned binary member.");
            foreach (var descriptor in entries.Values.Where(e => e.Role != "package"))
            {
                ct.ThrowIfCancellationRequested();
                var member = zip.GetEntry(descriptor.Path)!;
                    using var content = member.Open(); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[64 * 1024]; var length = 0L;
                    while (true)
                    {
                        var read = await content.ReadAsync(buffer, ct); if (read == 0) break;
                        if ((length += read) > descriptor.Length) throw new KnowzArchiveException("Archive binary exceeds its declared length.");
                        hash.AppendData(buffer, 0, read);
                    }
                    if (length != descriptor.Length || Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != descriptor.Sha256)
                        throw new KnowzArchiveException("Archive binary digest failed.");
            }
            return new(zip, input, options.LeaveOpen, manifest, packages,
                options.ExpectedSignerFingerprint != null ? "trusted" : "unverified", ArchiveJson.Digest(manifestBytes), signature.PublicKeyFingerprint, signature.PublicKey);
        }
        catch (Exception ex)
        {
            zip?.Dispose(); if (!options.LeaveOpen) input.Dispose();
            if (ex is KnowzArchiveException) throw;
            if (ex is IOException or InvalidDataException or JsonException or CryptographicException or FormatException or ArgumentException or InvalidOperationException)
                throw new KnowzArchiveException("Archive validation failed.", ex);
            throw;
        }
    }

    private static void VerifySignature(byte[] manifest, KnowzArchiveSignature signature, string? expectedFingerprint)
    {
        if (signature.Algorithm != "ecdsa-p256-sha256-p1363" || !EncryptedGraphProfile.IsSha256(signature.PublicKeyFingerprint))
            throw new KnowzArchiveException("Unsupported manifest authentication profile.");
        var key = Convert.FromBase64String(signature.PublicKey); var bytes = Convert.FromBase64String(signature.Signature);
        if (key.Length > 256 || bytes.Length != 64 || ArchiveJson.Digest(key) != signature.PublicKeyFingerprint)
            throw new KnowzArchiveException("Invalid manifest signing identity.");
        if (expectedFingerprint != null && (!EncryptedGraphProfile.IsSha256(expectedFingerprint) || expectedFingerprint != signature.PublicKeyFingerprint))
            throw new KnowzArchiveException("The archive signer does not match the independently trusted source.");
        using var publicKey = ECDsa.Create(); publicKey.ImportSubjectPublicKeyInfo(key, out var consumed);
        if (consumed != key.Length || publicKey.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7"
            || !publicKey.VerifyData(manifest, bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new KnowzArchiveException("Manifest signature verification failed.");
    }

    private static async Task<byte[]> ReadMetadataAsync(ZipArchiveEntry? entry, int limit, CancellationToken ct)
    {
        if (entry == null || entry.Length > limit) throw new KnowzArchiveException("Archive metadata is missing or exceeds its size limit.");
        using var input = entry.Open(); using var output = new MemoryStream((int)entry.Length); var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, ct); if (read == 0) break;
            if (output.Length + read > limit || output.Length + read > entry.Length) throw new KnowzArchiveException("Archive metadata exceeds its declared size.");
            output.Write(buffer, 0, read);
        }
        if (output.Length != entry.Length) throw new KnowzArchiveException("Archive metadata is truncated.");
        return output.ToArray();
    }
}
