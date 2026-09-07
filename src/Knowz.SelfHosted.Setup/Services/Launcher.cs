using System.Diagnostics;
using Knowz.SelfHosted.Setup.Models;
using Spectre.Console;

namespace Knowz.SelfHosted.Setup.Services;

public static class Launcher
{
    public static async Task OfferLaunchAsync(SetupConfig config)
    {
        var (command, args, description) = config.RunMode switch
        {
            RunMode.DockerCompose => ("docker", "compose up --build -d", "Docker Compose"),
            RunMode.AspireLocal or RunMode.AspireAzure =>
                ("dotnet", "run --project src/Knowz.SelfHosted.AppHost", "Aspire AppHost"),
            RunMode.DirectRun => ("dotnet", "run --project src/Knowz.SelfHosted.API", "API directly"),
            RunMode.AzureCloudDeploy => (string.Empty, string.Empty, "Azure deployment"),
            _ => (string.Empty, string.Empty, "unknown")
        };

        if (string.IsNullOrEmpty(command))
        {
            AnsiConsole.MarkupLine("[yellow]Azure Cloud Deploy requires running the deployment script separately.[/]");
            return;
        }

        var launch = AnsiConsole.Confirm($"Start {description} now?", defaultValue: true);
        if (!launch) return;

        AnsiConsole.MarkupLine($"[green]Running:[/] {command} {args}");

        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = FindSelfHostedRoot()
                }
            };

            process.Start();

            // Stream output
            var outputTask = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                    AnsiConsole.WriteLine(line);
            });

            var errorTask = Task.Run(async () =>
            {
                while (await process.StandardError.ReadLineAsync() is { } line)
                    AnsiConsole.MarkupLine($"[red]{line.EscapeMarkup()}[/]");
            });

            await Task.WhenAll(outputTask, errorTask);
            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
                AnsiConsole.MarkupLine("[green]Started successfully.[/]");
            else
                AnsiConsole.MarkupLine($"[red]Process exited with code {process.ExitCode}.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to launch: {ex.Message.EscapeMarkup()}[/]");
        }
    }

    private static string FindSelfHostedRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "docker-compose.yml")) &&
                File.Exists(Path.Combine(dir, "Knowz.SelfHosted.sln")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}
