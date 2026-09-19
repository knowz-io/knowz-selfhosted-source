namespace Knowz.MCP.Endpoints;

/// <summary>
/// OpenAI Apps / Plugins directory domain verification challenge.
/// </summary>
public static class OpenAiAppsChallengeEndpoints
{
    public const string Path = "/.well-known/openai-apps-challenge";
    public const string EnvVarName = "OPENAI_APPS_CHALLENGE_TOKEN";
    public const string ConfigKey = "OpenAI:AppsChallengeToken";

    public static WebApplication MapOpenAiAppsChallengeEndpoints(this WebApplication app)
    {
        // Token is issued by the OpenAI Apps portal — do not hardcode; supply via
        // OPENAI_APPS_CHALLENGE_TOKEN or OpenAI:AppsChallengeToken. Path is under
        // /.well-known so McpAuthMiddleware leaves it public.
        app.MapGet(Path, (IConfiguration configuration) =>
        {
            var token = ResolveChallengeToken(configuration);
            if (string.IsNullOrEmpty(token))
            {
                return Results.NotFound();
            }

            return Results.Text(token, "text/plain");
        });

        return app;
    }

    /// <summary>
    /// Returns the configured challenge token, or null/empty when unset (caller should 404).
    /// Prefers the env-style key, then the hierarchical config key.
    /// </summary>
    public static string? ResolveChallengeToken(IConfiguration configuration)
    {
        var token = configuration[EnvVarName]
                    ?? configuration[ConfigKey];
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }
}
