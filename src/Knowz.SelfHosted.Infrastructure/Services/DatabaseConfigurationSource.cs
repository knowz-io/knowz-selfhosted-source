using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>
/// Configuration source that loads config from the SystemConfigurations database table.
/// Added to the IConfigurationBuilder pipeline so DB values override file-based config
/// (except for secret-tier keys — see <see cref="SecretConfigurationKeys"/>).
/// </summary>
public class DatabaseConfigurationSource : IConfigurationSource
{
    public string ConnectionString { get; set; } = string.Empty;
    public DateTimeOffset? HostAiConfigurationUpdatedAt { get; set; }
    public IDataProtectionProvider? DataProtectionProvider { get; set; }

    /// <summary>
    /// Optional logger for Load() warnings (denied secret-tier keys) and errors
    /// (decrypt failures). Wired up in <c>Program.cs</c> after <c>builder.Build()</c>
    /// via a temporary <c>LoggerFactory</c>, since this provider runs at config-build
    /// time before the DI container exists.
    /// </summary>
    public ILogger<DatabaseConfigurationProvider>? Logger { get; set; }

    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        return new DatabaseConfigurationProvider(this);
    }
}
