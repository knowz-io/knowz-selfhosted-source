using System.Diagnostics;
using System.Text.Json;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Knowz.SelfHosted.Setup.Models;
using Spectre.Console;

namespace Knowz.SelfHosted.Setup.Services;

public static class AzureKeyVaultService
{
    private static readonly Dictionary<string, Action<SetupConfig, string>> SecretMappings = new()
    {
        ["openai--endpoint"] = (c, v) => c.AzureOpenAiEndpoint = v,
        ["openai--primary--key"] = (c, v) => c.AzureOpenAiApiKey = v,
        ["vision--endpoint"] = (c, v) => c.AzureAiVisionEndpoint = v,
        ["vision--primary--key"] = (c, v) => c.AzureAiVisionApiKey = v,
        ["documentintelligence--endpoint"] = (c, v) => c.AzureDocumentIntelligenceEndpoint = v,
        ["documentintelligence--primary--key"] = (c, v) => c.AzureDocumentIntelligenceApiKey = v,
        ["search--endpoint"] = (c, v) => c.AzureSearchEndpoint = v,
        ["search--primary--key"] = (c, v) => c.AzureSearchApiKey = v,
    };

    public static async Task<List<(string Name, string ResourceGroup)>> ListVaultsAsync()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "az",
                    Arguments = "keyvault list --query \"[].{name:name, resourceGroup:resourceGroup}\" -o json",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            };

            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                AnsiConsole.MarkupLine($"[yellow]Azure CLI returned exit code {process.ExitCode}.[/]");
                if (!string.IsNullOrWhiteSpace(stderr))
                    AnsiConsole.MarkupLine($"[dim]{stderr.Trim().EscapeMarkup()}[/]");
                return [];
            }

            var vaults = JsonSerializer.Deserialize<List<VaultInfo>>(stdout, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

            if (vaults is null || vaults.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No Key Vaults found in current Azure subscription.[/]");
                return [];
            }

            return vaults.Select(v => (v.Name, v.ResourceGroup)).ToList();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            AnsiConsole.MarkupLine("[yellow]Azure CLI (az) is not installed or not on PATH.[/]");
            AnsiConsole.MarkupLine("[dim]Install it from https://aka.ms/install-azure-cli and run 'az login'.[/]");
            return [];
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Failed to list Key Vaults: {ex.Message.EscapeMarkup()}[/]");
            return [];
        }
    }

    public static async Task<SetupConfig> PullSecretsAsync(string vaultName, SetupConfig config)
    {
        config.KeyVaultName = vaultName;
        var vaultUri = new Uri($"https://{vaultName}.vault.azure.net/");
        var client = new SecretClient(vaultUri, new DefaultAzureCredential());

        var results = new List<(string SecretName, bool Found)>();

        foreach (var (secretName, setter) in SecretMappings)
        {
            try
            {
                var response = await client.GetSecretAsync(secretName);
                setter(config, response.Value.Value);
                results.Add((secretName, true));
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                results.Add((secretName, false));
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: could not read '{secretName}': {ex.Message.EscapeMarkup()}[/]");
                results.Add((secretName, false));
            }
        }

        // Display results table
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Secret")
            .AddColumn("Status")
            .AddColumn("Value");

        foreach (var (secretName, found) in results)
        {
            var status = found ? "[green]Found[/]" : "[red]Not Found[/]";
            var maskedValue = found ? MaskValue(GetConfigValue(config, secretName)) : "-";
            table.AddRow(secretName.EscapeMarkup(), status, maskedValue.EscapeMarkup());
        }

        AnsiConsole.Write(table);

        return config;
    }

    private static string MaskValue(string value)
    {
        if (string.IsNullOrEmpty(value)) return "-";
        if (value.Length <= 8) return new string('*', value.Length);
        return value[..4] + new string('*', Math.Min(value.Length - 8, 20)) + value[^4..];
    }

    private static string GetConfigValue(SetupConfig config, string secretName) => secretName switch
    {
        "openai--endpoint" => config.AzureOpenAiEndpoint,
        "openai--primary--key" => config.AzureOpenAiApiKey,
        "vision--endpoint" => config.AzureAiVisionEndpoint,
        "vision--primary--key" => config.AzureAiVisionApiKey,
        "documentintelligence--endpoint" => config.AzureDocumentIntelligenceEndpoint,
        "documentintelligence--primary--key" => config.AzureDocumentIntelligenceApiKey,
        "search--endpoint" => config.AzureSearchEndpoint,
        "search--primary--key" => config.AzureSearchApiKey,
        _ => string.Empty,
    };

    private sealed record VaultInfo(string Name, string ResourceGroup);
}
