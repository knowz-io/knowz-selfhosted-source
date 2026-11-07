using System.Security.Cryptography;
using System.Text;
using Knowz.Core.Portability;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Knowz.SelfHosted.API.Endpoints;

/// <summary>Bounded public metadata and an encrypted temporary binary, without framework form buffering.</summary>
public static class ProtectedFileMultipartReader
{
    public const int MaximumMetadataBytes = KnowzArchiveLimits.MaxPackageBytes;
    public const long MaximumBinaryBytes = EncryptedGraphProfile.MaxFileBytes;
    // Four bounded section headers plus boundary overhead; content budgets are enforced separately.
    public const long MaximumRequestBytes = MaximumBinaryBytes + MaximumMetadataBytes + 128 * 1024;
    public const long MaximumOrdinaryRequestBytes = KnowzArchiveLimits.MaxOrdinaryFileBytes + MaximumMetadataBytes + 128 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task<ProtectedFileMultipart> ReadAsync(HttpRequest request, CancellationToken ct = default, string? spoolDirectory = null, long maximumBinaryBytes = MaximumBinaryBytes)
    {
        if (maximumBinaryBytes is not (MaximumBinaryBytes or KnowzArchiveLimits.MaxOrdinaryFileBytes))
            throw new ArgumentOutOfRangeException(nameof(maximumBinaryBytes));
        if (request.ContentLength > maximumBinaryBytes + MaximumMetadataBytes + 128 * 1024) throw TooLarge();
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var media)
            || !string.Equals(media.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A multipart/form-data upload is required.");
        var boundary = HeaderUtilities.RemoveQuotes(media.Boundary).Value;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 128)
            throw new InvalidDataException("A supported multipart boundary is required.");
        var reader = new MultipartReader(boundary, request.Body)
        {
            HeadersCountLimit = 16, HeadersLengthLimit = 16 * 1024,
            BodyLengthLimit = maximumBinaryBytes,
        };
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        ProtectedSpoolStream? binary = null;
        var metadataBytes = 0;
        try
        {
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(ct)) != null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                    || !string.Equals(disposition.DispositionType.Value, "form-data", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Every upload section requires a form-data name.");
                var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
                if (name is not ("vaultId" or "record" or "payloadKeyReferences" or "binary") || !names.Add(name))
                    throw new InvalidDataException("Upload fields must be exactly one vaultId, record, payloadKeyReferences and binary.");
                if (name == "binary")
                {
                    binary = await ProtectedSpoolStream.CreateAsync(section.Body, spoolDirectory, ct, maximumBinaryBytes);
                }
                else
                {
                    if (disposition.FileName.HasValue || disposition.FileNameStar.HasValue)
                        throw new InvalidDataException("Public metadata must be ordinary named fields.");
                    var (value, length) = await ReadMetadataAsync(section.Body, MaximumMetadataBytes - metadataBytes, ct);
                    metadataBytes += length;
                    fields.Add(name, value);
                }
            }
            if (binary == null || names.Count != 4 || !Guid.TryParse(fields["vaultId"], out var vaultId) || vaultId == Guid.Empty)
                throw new InvalidDataException("Exactly one binary and matching public file metadata are required.");
            return new(vaultId, fields["record"], fields["payloadKeyReferences"], binary);
        }
        catch (KnowzArchiveException)
        {
            binary?.Dispose(); throw TooLarge();
        }
        catch (InvalidDataException ex) when (ex.Message.StartsWith("Multipart body length limit", StringComparison.Ordinal))
        {
            binary?.Dispose(); throw TooLarge();
        }
        catch (IOException)
        {
            binary?.Dispose(); throw new InvalidDataException("Multipart upload is incomplete or malformed.");
        }
        catch
        {
            binary?.Dispose(); throw;
        }
    }

    private static async Task<(string Value, int Length)> ReadMetadataAsync(Stream input, int remaining, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        try
        {
            int count;
            while ((count = await input.ReadAsync(buffer, ct)) > 0)
            {
                if (output.Length + count > remaining) throw TooLarge();
                output.Write(buffer, 0, count);
            }
            try { return (Utf8.GetString(output.GetBuffer(), 0, checked((int)output.Length)), checked((int)output.Length)); }
            catch (DecoderFallbackException) { throw new InvalidDataException("Public metadata must be valid UTF-8."); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            CryptographicOperations.ZeroMemory(output.GetBuffer());
        }
    }

    private static ArchiveOperationException TooLarge() => new(413, "Upload exceeds its supported binary or 8 MiB aggregate metadata limit.");
}

public sealed class ProtectedFileMultipart(Guid vaultId, string record, string payloadKeyReferences, ProtectedSpoolStream binary) : IAsyncDisposable
{
    public Guid VaultId { get; } = vaultId;
    public string Record { get; } = record;
    public string PayloadKeyReferences { get; } = payloadKeyReferences;
    public ProtectedSpoolStream Binary { get; } = binary;
    public ValueTask DisposeAsync() => Binary.DisposeAsync();
}
