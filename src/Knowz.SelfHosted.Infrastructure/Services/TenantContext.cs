namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>
/// AsyncLocal-based ambient tenant context for background services.
/// When set, HttpTenantProvider checks this before HTTP context.
/// </summary>
public static class TenantContext
{
    private static readonly AsyncLocal<Guid?> _currentTenantId = new();

    public static Guid? CurrentTenantId
    {
        get => _currentTenantId.Value;
        set => _currentTenantId.Value = value;
    }
}
