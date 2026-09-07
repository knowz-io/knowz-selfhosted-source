using Knowz.SelfHosted.Setup.Models;
using Knowz.SelfHosted.Setup.Services;
using Spectre.Console;

namespace Knowz.SelfHosted.Setup.Steps;

public static class AiConfigStep
{
    public static async Task RunAsync(SetupConfig config)
    {
        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]How should AI features (search, Q&A, summarization) work?[/]")
                .AddChoices(
                    "No AI                 — CRUD only, no search/Q&A/summarization",
                    "Direct Azure          — Your own Azure OpenAI + AI Search resources",
                    "Platform Proxy        — Proxy AI through a Knowz Platform API key",
                    "Auto-detect (Azure)   — Pull credentials from an Azure Key Vault"));

        config.AiMode = choice switch
        {
            _ when choice.StartsWith("No AI") => AiMode.NoAi,
            _ when choice.StartsWith("Direct") => AiMode.DirectAzure,
            _ when choice.StartsWith("Platform") => AiMode.PlatformProxy,
            _ when choice.StartsWith("Auto") => AiMode.AutoDetect,
            _ => AiMode.NoAi
        };

        switch (config.AiMode)
        {
            case AiMode.DirectAzure:
                PromptDirectAzure(config);
                break;
            case AiMode.PlatformProxy:
                PromptPlatformProxy(config);
                break;
            case AiMode.AutoDetect:
                await RunAutoDetectAsync(config);
                break;
        }

        AnsiConsole.MarkupLine($"[green]AI mode:[/] {config.AiMode}");
    }

    private static void PromptDirectAzure(SetupConfig config)
    {
        config.AzureOpenAiEndpoint = AnsiConsole.Prompt(
            new TextPrompt<string>("Azure OpenAI [green]endpoint[/]:")
                .Validate(url => ConfigValidator.IsValidHttpsUrl(url)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("Must be a valid HTTPS URL")));

        config.AzureOpenAiApiKey = AnsiConsole.Prompt(
            new TextPrompt<string>("Azure OpenAI [green]API key[/]:")
                .Secret());

        config.AzureOpenAiDeployment = AnsiConsole.Prompt(
            new TextPrompt<string>("Chat deployment name:")
                .DefaultValue("gpt-4o"));

        config.AzureOpenAiEmbedding = AnsiConsole.Prompt(
            new TextPrompt<string>("Embedding deployment name:")
                .DefaultValue("text-embedding-3-small"));

        config.AzureAiVisionEndpoint = AnsiConsole.Prompt(
            new TextPrompt<string>("Azure AI Vision [green]endpoint[/] (optional but recommended for images/diagrams):")
                .AllowEmpty()
                .Validate(url => string.IsNullOrWhiteSpace(url) || ConfigValidator.IsValidHttpsUrl(url)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("Must be a valid HTTPS URL")));

        config.AzureAiVisionApiKey = AnsiConsole.Prompt(
            new TextPrompt<string>("Azure AI Vision [green]API key[/] (optional):")
                .Secret()
                .AllowEmpty());

        config.AzureDocumentIntelligenceEndpoint = AnsiConsole.Prompt(
            new TextPrompt<string>("Azure Document Intelligence [green]endpoint[/] (optional but recommended for PDFs/docs):")
                .AllowEmpty()
                .Validate(url => string.IsNullOrWhiteSpace(url) || ConfigValidator.IsValidHttpsUrl(url)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("Must be a valid HTTPS URL")));

        config.AzureDocumentIntelligenceApiKey = AnsiConsole.Prompt(
            new TextPrompt<string>("Azure Document Intelligence [green]API key[/] (optional):")
                .Secret()
                .AllowEmpty());

        config.AzureSearchEndpoint = AnsiConsole.Prompt(
            new TextPrompt<string>("Azure AI Search [green]endpoint[/]:")
                .Validate(url => ConfigValidator.IsValidHttpsUrl(url)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("Must be a valid HTTPS URL")));

        config.AzureSearchApiKey = AnsiConsole.Prompt(
            new TextPrompt<string>("Azure AI Search [green]admin key[/]:")
                .Secret());

        config.AzureSearchIndex = AnsiConsole.Prompt(
            new TextPrompt<string>("Search index name:")
                .DefaultValue("knowz-selfhosted"));
    }

    private static void PromptPlatformProxy(SetupConfig config)
    {
        config.PlatformProxyUrl = AnsiConsole.Prompt(
            new TextPrompt<string>("Knowz Platform [green]URL[/]:")
                .DefaultValue("https://api.knowz.io")
                .Validate(url => ConfigValidator.IsValidUrl(url)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("Must be a valid URL")));

        config.PlatformProxyApiKey = AnsiConsole.Prompt(
            new TextPrompt<string>("Knowz Platform [green]API key[/]:")
                .Secret());
    }

    private static async Task RunAutoDetectAsync(SetupConfig config)
    {
        AnsiConsole.MarkupLine("[bold]Scanning for Azure Key Vaults...[/]");

        var vaults = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Querying Azure CLI...", async _ =>
                await AzureKeyVaultService.ListVaultsAsync());

        if (vaults.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No Key Vaults found. Falling back to manual entry.[/]");
            config.AiMode = AiMode.DirectAzure;
            PromptDirectAzure(config);
            return;
        }

        // Build vault selection choices
        var choices = vaults
            .Select(v => $"{v.Name}  ({v.ResourceGroup})")
            .Append("Enter vault name manually")
            .ToList();

        var vaultChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Select a Key Vault:[/]")
                .PageSize(15)
                .AddChoices(choices));

        string selectedVaultName;
        if (vaultChoice == "Enter vault name manually")
        {
            selectedVaultName = AnsiConsole.Prompt(
                new TextPrompt<string>("Key Vault [green]name[/]:"));
        }
        else
        {
            // Extract vault name (everything before the first double-space)
            selectedVaultName = vaultChoice.Split("  ")[0];
        }

        // Pull secrets from selected vault
        AnsiConsole.MarkupLine($"[bold]Pulling secrets from[/] [cyan]{selectedVaultName.EscapeMarkup()}[/]...");

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Reading secrets...", async _ =>
                await AzureKeyVaultService.PullSecretsAsync(selectedVaultName, config));

        // If OpenAI endpoint was found, probe for deployments
        if (!string.IsNullOrEmpty(config.AzureOpenAiEndpoint) && !string.IsNullOrEmpty(config.AzureOpenAiApiKey))
        {
            await ProbeAndSelectDeploymentsAsync(config);
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]OpenAI endpoint/key not found in vault — skipping deployment probe.[/]");
            PromptDeploymentNames(config);
        }

        // Search index name
        config.AzureSearchIndex = AnsiConsole.Prompt(
            new TextPrompt<string>("Search index name:")
                .DefaultValue("knowz-selfhosted-test"));

        // Confirmation summary
        ShowAutoDetectSummary(config);
    }

    private static async Task ProbeAndSelectDeploymentsAsync(SetupConfig config)
    {
        AnsiConsole.MarkupLine("[bold]Probing OpenAI endpoint for deployments...[/]");

        var deployments = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Listing deployments...", async _ =>
                await OpenAiProber.ListDeploymentsAsync(config.AzureOpenAiEndpoint, config.AzureOpenAiApiKey));

        if (deployments.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No deployments found — enter names manually.[/]");
            PromptDeploymentNames(config);
            return;
        }

        // Show discovered deployments
        var deployTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Deployment")
            .AddColumn("Model");

        foreach (var (name, model) in deployments)
            deployTable.AddRow(name.EscapeMarkup(), model.EscapeMarkup());

        AnsiConsole.Write(deployTable);

        // Chat deployment selection
        var chatChoices = deployments
            .Select(d => $"{d.DeploymentName}  ({d.ModelName})")
            .Append("Enter manually")
            .ToList();

        var chatChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Select [green]chat[/] deployment:[/]")
                .AddChoices(chatChoices));

        config.AzureOpenAiDeployment = chatChoice == "Enter manually"
            ? AnsiConsole.Prompt(new TextPrompt<string>("Chat deployment name:").DefaultValue("gpt-4o"))
            : chatChoice.Split("  ")[0];

        // Embedding deployment selection
        var embedChoices = deployments
            .Select(d => $"{d.DeploymentName}  ({d.ModelName})")
            .Append("Enter manually")
            .ToList();

        var embedChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Select [green]embedding[/] deployment:[/]")
                .AddChoices(embedChoices));

        config.AzureOpenAiEmbedding = embedChoice == "Enter manually"
            ? AnsiConsole.Prompt(new TextPrompt<string>("Embedding deployment name:").DefaultValue("text-embedding-3-small"))
            : embedChoice.Split("  ")[0];
    }

    private static void PromptDeploymentNames(SetupConfig config)
    {
        config.AzureOpenAiDeployment = AnsiConsole.Prompt(
            new TextPrompt<string>("Chat deployment name:")
                .DefaultValue("gpt-4o"));

        config.AzureOpenAiEmbedding = AnsiConsole.Prompt(
            new TextPrompt<string>("Embedding deployment name:")
                .DefaultValue("text-embedding-3-small"));
    }

    private static void ShowAutoDetectSummary(SetupConfig config)
    {
        var summary = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Auto-detect Summary[/]")
            .AddColumn("Setting")
            .AddColumn("Value");

        summary.AddRow("Key Vault", config.KeyVaultName.EscapeMarkup());
        summary.AddRow("OpenAI Endpoint", MaskOrEmpty(config.AzureOpenAiEndpoint));
        summary.AddRow("OpenAI Key", string.IsNullOrEmpty(config.AzureOpenAiApiKey) ? "[red]Not set[/]" : "[green]Set[/]");
        summary.AddRow("Chat Deployment", config.AzureOpenAiDeployment.EscapeMarkup());
        summary.AddRow("Embedding Deployment", config.AzureOpenAiEmbedding.EscapeMarkup());
        summary.AddRow("Vision Endpoint", MaskOrEmpty(config.AzureAiVisionEndpoint));
        summary.AddRow("Vision Key", string.IsNullOrEmpty(config.AzureAiVisionApiKey) ? "[red]Not set[/]" : "[green]Set[/]");
        summary.AddRow("DocInt Endpoint", MaskOrEmpty(config.AzureDocumentIntelligenceEndpoint));
        summary.AddRow("DocInt Key", string.IsNullOrEmpty(config.AzureDocumentIntelligenceApiKey) ? "[red]Not set[/]" : "[green]Set[/]");
        summary.AddRow("Search Endpoint", MaskOrEmpty(config.AzureSearchEndpoint));
        summary.AddRow("Search Key", string.IsNullOrEmpty(config.AzureSearchApiKey) ? "[red]Not set[/]" : "[green]Set[/]");
        summary.AddRow("Search Index", config.AzureSearchIndex.EscapeMarkup());

        AnsiConsole.Write(summary);

        if (!AnsiConsole.Confirm("Accept these values?", defaultValue: true))
        {
            AnsiConsole.MarkupLine("[yellow]Switching to manual entry.[/]");
            config.AiMode = AiMode.DirectAzure;
            PromptDirectAzure(config);
        }
    }

    private static string MaskOrEmpty(string value)
    {
        if (string.IsNullOrEmpty(value)) return "[red]Not set[/]";
        return value.EscapeMarkup();
    }
}
