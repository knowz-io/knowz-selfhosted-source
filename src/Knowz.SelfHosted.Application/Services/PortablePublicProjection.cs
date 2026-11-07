using Knowz.Core.Portability;
using Knowz.SelfHosted.Infrastructure.Data.Entities;

namespace Knowz.SelfHosted.Application.Services;

/// <summary>Explicit public wire projections; persistence identifiers never enter strict crypto descriptors.</summary>
public static class PortablePublicProjection
{
    public static ExportedCryptoContext Context(ShPortableCryptoContext row) => new()
    { ContextId = row.Id, BindingKind = row.BindingKind, OriginalBindingId = row.OriginalBindingId, AlgorithmProfile = row.AlgorithmProfile, ProvenanceStatus = row.ProvenanceStatus, SourceTenantId = row.SourceTenantId };
    public static ExportedCryptoKeyIdentity Key(ShCryptoKeyIdentity row) => new()
    { ContextId = row.PortableCryptoContextId, Fingerprint = row.Fingerprint, PublicKey = row.PublicKey, KeyType = row.KeyType, Generation = row.Generation, Lifecycle = row.Lifecycle, Custody = row.Custody, SourceLifecycle = row.SourceLifecycle };
    public static PortablePayloadKeyReference Reference(ShPayloadKeyReference row) => new()
    { OwnerKind = row.OwnerKind, OwnerId = row.OwnerId, Role = row.Role, FieldName = row.FieldName, VersionId = row.VersionId,
        ContextId = row.ContextId, Fingerprint = row.Fingerprint, LegacyUnattributed = row.LegacyUnattributed, OriginalBindingKind = row.OriginalBindingKind,
        OriginalBindingId = row.OriginalBindingId, OriginalItemId = row.OriginalItemId, KeyType = row.KeyType, AlgorithmProfile = row.AlgorithmProfile, EnvelopeVersion = row.EnvelopeVersion, Generation = row.Generation };
    internal static object? Project<T>(object value) => value switch
    {
        ShPortableCryptoContext row when typeof(T) == typeof(ExportedCryptoContext) => Context(row),
        ShCryptoKeyIdentity row when typeof(T) == typeof(ExportedCryptoKeyIdentity) => Key(row),
        ShPayloadKeyReference row when typeof(T) == typeof(PortablePayloadKeyReference) => Reference(row),
        _ => null
    };
}
