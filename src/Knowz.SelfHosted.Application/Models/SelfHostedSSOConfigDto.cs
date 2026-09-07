namespace Knowz.SelfHosted.Application.Models;

public class SelfHostedSSOConfigDto
{
    public bool IsEnabled { get; set; }
    public string? ClientId { get; set; }
    public bool HasClientSecret { get; set; }
    public string? DirectoryTenantId { get; set; }
    public bool AutoProvisionUsers { get; set; }
    public string DefaultRole { get; set; } = "User";
    public string DetectedMode { get; set; } = "Disabled";
    public DateTime? LastTestedAt { get; set; }
    public bool? LastTestSucceeded { get; set; }
}

public class SelfHostedSSOConfigRequest
{
    public bool IsEnabled { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? DirectoryTenantId { get; set; }
    public bool AutoProvisionUsers { get; set; }
    public string DefaultRole { get; set; } = "User";
}

public class SelfHostedSSOTestResultDto
{
    public bool Success { get; set; }
    public string? DetectedMode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? Status { get; set; }
    public List<string>? ValidTenantIds { get; set; }
    public DateTime TestedAt { get; set; }
}
