namespace Knowz.SelfHosted.Infrastructure.Data.Entities;

/// <summary>Inert public portability evidence. This table has no scheduler, token or key-custody authority.</summary>
public sealed class ShInactiveRecoveryRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid? ImportPlanId { get; set; }
    public string RecoveryState { get; set; } = "suspended-release";
    public string PoliciesJson { get; set; } = "[]";
    public string? DependencyJson { get; set; }
    public long Revision { get; set; } = 1;
}
