using System.Security.Claims;
using Knowz.Core.Configuration;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Knowz.SelfHosted.API.Services;

/// <summary>
/// Request-scoped tenant provider that resolves the tenant ID from:
/// 0. TenantContext.CurrentTenantId (AsyncLocal — background service override)
/// 1. X-Tenant-Id header (SuperAdmin override only)
/// 2. JWT "tenantId" claim (normal authenticated users)
/// 3. SelfHostedOptions.TenantId fallback (legacy API key auth, startup, migrations)
/// </summary>
public class HttpTenantProvider : ITenantProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly SelfHostedOptions _options;

    public HttpTenantProvider(
        IHttpContextAccessor httpContextAccessor,
        IOptions<SelfHostedOptions> options)
    {
        _httpContextAccessor = httpContextAccessor;
        _options = options.Value;
    }

    public Guid TenantId => ResolveTenantId();

    private Guid ResolveTenantId()
    {
        // 0. Background service override (AsyncLocal)
        if (TenantContext.CurrentTenantId.HasValue)
            return TenantContext.CurrentTenantId.Value;

        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext is null)
            return _options.TenantId;

        // 1. SuperAdmin header override
        if (httpContext.User.IsInRole("SuperAdmin") &&
            httpContext.Request.Headers.TryGetValue("X-Tenant-Id", out var headerValue) &&
            Guid.TryParse(headerValue.FirstOrDefault(), out var headerTenantId))
        {
            return headerTenantId;
        }

        // 2. JWT tenantId claim
        var tenantClaim = httpContext.User.FindFirst("tenantId");
        if (tenantClaim is not null && Guid.TryParse(tenantClaim.Value, out var claimTenantId))
        {
            return claimTenantId;
        }

        // 3. Fallback to config (legacy API key, unauthenticated, startup)
        return _options.TenantId;
    }
}
