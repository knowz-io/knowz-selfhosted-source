using Knowz.SelfHosted.Setup.Models;
using Knowz.SelfHosted.Setup.Services;
using Spectre.Console;

namespace Knowz.SelfHosted.Setup.Steps;

public static class CredentialsStep
{
    public static Task RunAsync(SetupConfig config)
    {
        AnsiConsole.MarkupLine("[bold]Credentials[/]");

        // SEC_P0Triage §Rule 2 (Option A expansion): no default for Admin
        // username or password. The prior "admin"/"changeme" defaults shipped
        // with the Setup CLI meant an operator hitting Enter at both prompts
        // created an account with the most-guessed credential on the internet.
        // Require input, validate against the shared strong-password policy
        // used at runtime (ConfigValidator.IsStrongAdminPassword ↔
        // AuthService.IsWeakPassword). Reject and re-prompt on failure — no
        // warn-and-continue path.

        config.AdminUsername = AnsiConsole.Prompt(
            new TextPrompt<string>("Admin [green]username[/]:")
                .Validate(u => string.IsNullOrWhiteSpace(u)
                    ? ValidationResult.Error("Admin username is required.")
                    : ValidationResult.Success()));

        config.AdminPassword = AnsiConsole.Prompt(
            new TextPrompt<string>("Admin [green]password[/] (12+ chars, upper+lower+digit+symbol, not on weak-password list):")
                .Secret('*')
                .Validate(p => ConfigValidator.IsStrongAdminPassword(p)
                    ? ValidationResult.Success()
                    : ValidationResult.Error(
                        "Password must be >=12 chars with upper/lower/digit/symbol, " +
                        "and must not contain common fragments (admin, changeme, password, ...). " +
                        "AuthService will reject the same password at first boot.")));

        var autoJwt = AnsiConsole.Confirm("Auto-generate JWT secret? (64 chars, cryptographically random)", defaultValue: true);
        config.JwtSecret = autoJwt
            ? ConfigValidator.GenerateJwtSecret()
            : AnsiConsole.Prompt(
                new TextPrompt<string>("JWT [green]secret[/] (min 32 chars):")
                    .Secret('*')
                    .Validate(s => ConfigValidator.IsValidJwtSecret(s)
                        ? ValidationResult.Success()
                        : ValidationResult.Error("JWT secret must be at least 32 characters")));

        config.SaPassword = AnsiConsole.Prompt(
            new TextPrompt<string>("Postgres [green]password[/]:")
                .Secret('*')
                .Validate(p => ConfigValidator.IsValidSqlPassword(p)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("Must be 8+ chars with 3 of: uppercase, lowercase, digit, symbol")));

        config.McpServiceKey = AnsiConsole.Prompt(
            new TextPrompt<string>("MCP service [green]key[/] (32+ chars recommended):")
                .Validate(k => string.IsNullOrWhiteSpace(k)
                    ? ValidationResult.Error("MCP service key is required.")
                    : ValidationResult.Success()));

        return Task.CompletedTask;
    }
}
