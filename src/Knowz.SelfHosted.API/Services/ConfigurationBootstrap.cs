using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;

namespace Knowz.SelfHosted.API.Services;

/// <summary>One protected key ring shared by startup configuration and runtime writes.</summary>
public static class ConfigurationBootstrap
{
    public static IDataProtectionProvider CreateDataProtection(IConfiguration configuration)
    {
        var directory = configuration["DataProtection:KeysPath"]
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspnet", "DataProtection-Keys");
        return DataProtectionProvider.Create(new DirectoryInfo(directory), protection =>
        {
            protection.SetApplicationName("Knowz.SelfHosted");
            var blob = configuration["Storage:AzureBlob:AccountUrl"];
            var vault = configuration["AzureKeyVault:VaultUri"];
            if (!string.IsNullOrWhiteSpace(blob) && !string.IsNullOrWhiteSpace(vault))
            {
                var credential = new DefaultAzureCredential();
                var key = configuration["AzureKeyVault:DataProtectionKeyName"] ?? "selfhosted-dp-key";
                protection.PersistKeysToAzureBlobStorage(new Uri($"{blob.TrimEnd('/')}/dataprotection/keys.xml"), credential)
                    .ProtectKeysWithAzureKeyVault(new Uri($"{vault.TrimEnd('/')}/keys/{key}"), credential);
            }
        });
    }
}
