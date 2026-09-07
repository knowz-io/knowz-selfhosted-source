using Knowz.SelfHosted.Setup.Models;
using Knowz.SelfHosted.Setup.Services;
using Knowz.SelfHosted.Setup.Steps;
using Knowz.SelfHosted.Setup.Writers;
using Spectre.Console;

AnsiConsole.Write(new FigletText("Knowz Setup").Color(Color.Cyan1));
AnsiConsole.MarkupLine("[dim]Self-Hosted Configuration Wizard[/]");
AnsiConsole.WriteLine();

var config = new SetupConfig();

// Step 1: Run Mode
await RunModeStep.RunAsync(config);
AnsiConsole.WriteLine();

// Step 2: AI Configuration
await AiConfigStep.RunAsync(config);
AnsiConsole.WriteLine();

// Step 3: Storage
await StorageStep.RunAsync(config);
AnsiConsole.WriteLine();

// Step 4: Credentials
await CredentialsStep.RunAsync(config);
AnsiConsole.WriteLine();

// Step 5: Advanced (optional)
await AdvancedStep.RunAsync(config);
AnsiConsole.WriteLine();

// Step 6: Review + Confirm
if (!await ReviewStep.RunAsync(config))
{
    AnsiConsole.MarkupLine("[yellow]Configuration cancelled.[/]");
    return;
}

// Generate configuration based on run mode
AnsiConsole.WriteLine();
var selfHostedRoot = FindSelfHostedRoot();

switch (config.RunMode)
{
    case RunMode.DockerCompose:
        await EnvFileWriter.WriteAsync(config, Path.Combine(selfHostedRoot, ".env"));
        break;

    case RunMode.AspireLocal:
    case RunMode.AspireAzure:
        await UserSecretsWriter.WriteAsync(config);
        break;

    case RunMode.DirectRun:
        await AppSettingsWriter.WriteAsync(config,
            Path.Combine(selfHostedRoot, "src", "Knowz.SelfHosted.API", "appsettings.Local.json"));
        break;

    case RunMode.AzureCloudDeploy:
        await DeployParamsWriter.WriteAsync(config,
            Path.Combine(selfHostedRoot, "infrastructure", "selfhosted-deploy-params.json"));
        break;
}

AnsiConsole.WriteLine();

// Offer to launch
await Launcher.OfferLaunchAsync(config);

static string FindSelfHostedRoot()
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
