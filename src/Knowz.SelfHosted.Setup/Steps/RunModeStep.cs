using Knowz.SelfHosted.Setup.Models;
using Spectre.Console;

namespace Knowz.SelfHosted.Setup.Steps;

public static class RunModeStep
{
    public static Task RunAsync(SetupConfig config)
    {
        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]How do you want to run Knowz Self-Hosted?[/]")
                .AddChoices(
                    "Docker Compose        — Customer deployment, containers for everything",
                    "Aspire Local           — Developer mode, Postgres container + local .NET services",
                    "Aspire Azure           — Developer mode, Azure-hosted backing services",
                    "Direct Run             — Run projects individually with dotnet run"));

        config.RunMode = choice switch
        {
            _ when choice.StartsWith("Docker") => RunMode.DockerCompose,
            _ when choice.StartsWith("Aspire Local") => RunMode.AspireLocal,
            _ when choice.StartsWith("Aspire Azure") => RunMode.AspireAzure,
            _ when choice.StartsWith("Direct") => RunMode.DirectRun,
            _ when choice.StartsWith("Azure Cloud") => RunMode.AzureCloudDeploy,
            _ => RunMode.DockerCompose
        };

        AnsiConsole.MarkupLine($"[green]Run mode:[/] {config.RunMode}");
        return Task.CompletedTask;
    }
}
