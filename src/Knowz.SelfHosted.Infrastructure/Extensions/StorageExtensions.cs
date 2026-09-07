using Azure.Core;
using Azure.Storage.Blobs;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Infrastructure.Extensions;

public static class StorageExtensions
{
    /// <summary>
    /// Registers file storage provider based on configuration.
    /// Fallback to LocalFileStorageProvider if Azure not configured.
    ///
    /// Auth precedence (matches OpenAI + Search + AttachmentAI pattern):
    ///   1. `Storage:Azure:ConnectionString` → AccountKey/SAS — works for external-mode
    ///      deploys where the UAMI lacks Blob Data roles on the target account.
    ///   2. `Storage:Azure:AccountUrl` + DI-injected `TokenCredential` — enterprise
    ///      MI-only deploys per SH_ENTERPRISE_MI_SWAP §2.2.
    /// Either path registers AzureBlobStorageProvider; otherwise falls back to local.
    /// </summary>
    public static IServiceCollection AddSelfHostedFileStorage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var provider = configuration["Storage:Provider"];
        var accountUrl = configuration["Storage:Azure:AccountUrl"];
        var connectionString = configuration["Storage:Azure:ConnectionString"];
        var containerName = configuration["Storage:Azure:ContainerName"];

        // Azure Blob Storage (primary) — requires either a connection string OR
        // endpoint URL + MI role assignment, plus a container name.
        var hasConnectionString = !string.IsNullOrWhiteSpace(connectionString);
        var hasAccountUrl = !string.IsNullOrWhiteSpace(accountUrl);
        var hasContainer = !string.IsNullOrWhiteSpace(containerName);
        if (string.Equals(provider, "AzureBlob", StringComparison.OrdinalIgnoreCase) &&
            hasContainer &&
            (hasConnectionString || hasAccountUrl))
        {
            services.AddSingleton(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<AzureBlobStorageProvider>>();
                try
                {
                    if (hasConnectionString)
                    {
                        return new BlobServiceClient(connectionString);
                    }
                    var credential = sp.GetRequiredService<TokenCredential>();
                    return new BlobServiceClient(new Uri(accountUrl!), credential);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to initialize BlobServiceClient. AccountUrl={Url} UsedConnectionString={UsedCs}",
                        accountUrl, hasConnectionString);
                    throw;
                }
            });

            services.AddScoped<IFileStorageProvider, AzureBlobStorageProvider>();
            return services;
        }

        // Local Filesystem (fallback)
        services.AddScoped<IFileStorageProvider, LocalFileStorageProvider>();

        return services;
    }
}
