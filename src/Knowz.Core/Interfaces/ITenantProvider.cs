namespace Knowz.Core.Interfaces;

/// <summary>
/// Provides the current tenant ID for the active request.
/// Resolved per-scope: reads from JWT claims, X-Tenant-Id header (SuperAdmin), or config fallback.
/// </summary>
public interface ITenantProvider
{
    Guid TenantId { get; }
}
