using Microsoft.Extensions.Configuration;

namespace Knowz.Core.Interfaces;

/// <summary>
/// Service for managing system configuration entries in self-hosted deployments.
/// Provides CRUD operations with encryption, masking, and config reload support.
/// </summary>
public interface IConfigurationManagementService
{
    Task<List<ConfigCategoryDto>> GetAllCategoriesAsync();
    Task<ConfigCategoryDto?> GetCategoryAsync(string category);
    Task<ConfigUpdateResult> UpdateCategoryAsync(string category, List<ConfigEntryUpdateDto> entries, string modifiedBy);
    Task<ServiceHealthResult> TestConnectionAsync(string category, CancellationToken cancellationToken = default);
    Task<List<ServiceHealthResult>> TestAllConnectionsAsync();
    Task SeedFromConfigurationAsync(IConfiguration configuration);
    DeploymentStatusDto GetDeploymentStatus();
}

public class ConfigCategoryDto
{
    public string ProbeKind { get; set; } = "unsupported";
    public string ConfigurationStatus { get; set; } = "unconfigured";
    public string Category { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool RequiresRestart { get; set; }
    public List<ConfigEntryDto> Entries { get; set; } = new();
}

public class ConfigEntryDto
{
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
    public bool IsSecret { get; set; }
    public bool RequiresRestart { get; set; }
    public string? Description { get; set; }
    public bool IsSet { get; set; }
    public DateTime? LastModifiedAt { get; set; }
    public string? LastModifiedBy { get; set; }
    public bool Editable { get; set; }
    public string? Authority { get; set; }
    public bool EffectiveIsSet { get; set; }
    public bool PendingRestart { get; set; }
    public string? ApplicationError { get; set; }
    public string? Source { get; set; } // "database", "keyvault", "environment", "appsettings", null
}

public class ConfigEntryUpdateDto
{
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
}

public class ConfigUpdateResult
{
    public Dictionary<string, string> FieldErrors { get; set; } = new();
    public bool Success { get; set; }
    public bool RestartRequired { get; set; }
    public List<string> Errors { get; set; } = new();
    public int EntriesUpdated { get; set; }
}

public class ServiceHealthResult
{
    public string ProbeKind { get; set; } = "unsupported";
    public string ProbeStatus { get; set; } = "untested";
    public DateTimeOffset? CheckedAt { get; set; }
    public string Category { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsHealthy { get; set; }
    public string Status { get; set; } = string.Empty;
    public int? LatencyMs { get; set; }
}

public class DeploymentStatusDto
{
    public Dictionary<string, string> ConfigurationErrors { get; set; } = new();
    public string ActiveProvider { get; set; } = "offline";
    public string[] Capabilities { get; set; } = [];
    public string Mode { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTime StartupTime { get; set; }
    public bool RestartRequired { get; set; }
    public List<string> RestartReasons { get; set; } = new();
}
