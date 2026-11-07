namespace Knowz.SelfHosted.Infrastructure.Data;

public partial class SelfHostedDbContext
{
    /// <summary>Trusted current scope, evaluated after API authentication or worker tenant selection.</summary>
    public Guid CurrentTenantId => _tenantId;
}
