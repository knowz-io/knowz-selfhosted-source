using Knowz.SelfHosted.Setup.Models;
using Spectre.Console;

namespace Knowz.SelfHosted.Setup.Steps;

public static class AdvancedStep
{
    public static Task RunAsync(SetupConfig config)
    {
        if (!AnsiConsole.Confirm("Configure advanced settings?", defaultValue: false))
            return Task.CompletedTask;

        config.CorsOrigin = AnsiConsole.Prompt(
            new TextPrompt<string>("CORS allowed [green]origin[/]:")
                .DefaultValue("http://localhost:3000"));

        config.RateLimitingEnabled = AnsiConsole.Confirm("Enable rate limiting?", defaultValue: true);
        config.SwaggerEnabled = AnsiConsole.Confirm("Enable Swagger UI? (local debug only)", defaultValue: false);

        config.McpPort = AnsiConsole.Prompt(
            new TextPrompt<int>("MCP server [green]port[/]:")
                .DefaultValue(3001)
                .Validate(p => p is > 0 and < 65536
                    ? ValidationResult.Success()
                    : ValidationResult.Error("Port must be between 1 and 65535")));

        return Task.CompletedTask;
    }
}
