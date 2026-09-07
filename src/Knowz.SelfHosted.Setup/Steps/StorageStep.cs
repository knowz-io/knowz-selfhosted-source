using Knowz.SelfHosted.Setup.Models;
using Knowz.SelfHosted.Setup.Services;
using Spectre.Console;

namespace Knowz.SelfHosted.Setup.Steps;

public static class StorageStep
{
    public static Task RunAsync(SetupConfig config)
    {
        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Where should uploaded files be stored?[/]")
                .AddChoices(
                    "Local filesystem      — Docker volume or local directory (default)",
                    "Azure Blob Storage    — Requires connection string + container name"));

        config.StorageMode = choice.StartsWith("Local")
            ? StorageMode.LocalFileSystem
            : StorageMode.AzureBlobStorage;

        if (config.StorageMode == StorageMode.AzureBlobStorage)
        {
            config.AzureStorageConnectionString = AnsiConsole.Prompt(
                new TextPrompt<string>("Azure Storage [green]connection string[/]:")
                    .Secret());

            config.AzureStorageContainer = AnsiConsole.Prompt(
                new TextPrompt<string>("Container name:")
                    .DefaultValue("selfhosted-files"));
        }

        AnsiConsole.MarkupLine($"[green]Storage:[/] {config.StorageMode}");
        return Task.CompletedTask;
    }
}
