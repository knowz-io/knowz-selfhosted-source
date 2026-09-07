using Knowz.MCP.Helpers;
using Knowz.MCP.Services;
using Knowz.MCP.Services.Proxy;

namespace Knowz.MCP.Config;

/// <summary>
/// DI composition for MCP server (proxy or self-hosted mode).
/// </summary>
public static class McpStartupExtensions
{
    /// <summary>
    /// Registers backend services based on MCP:BackendMode configuration.
    /// "proxy" (default): forwards tool calls to Knowz Platform API via McpApiProxyService.
    /// "selfhosted": maps tool calls to individual self-hosted REST API endpoints.
    /// Call this after AddMcpServer() and before Build().
    /// </summary>
    public static IServiceCollection AddMcpBackend(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var baseUrl = configuration["Knowz:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("Knowz:BaseUrl is required");

        services.AddHostedConnectorCallbackPolicy(configuration);

        var backendMode = configuration["MCP:BackendMode"] ?? "proxy";

        if (backendMode.Equals("selfhosted", StringComparison.OrdinalIgnoreCase))
        {
            // Self-hosted mode: map tools directly to REST API endpoints
            services.AddScoped<IToolBackend, SelfHostedToolBackend>();
        }
        else
        {
            // Proxy mode (default): forward all tool calls through McpApiProxyService
            services.AddScoped<IMcpApiProxyService, McpApiProxyService>();
            services.AddScoped<IToolBackend, ProxyToolBackend>();
        }

        return services;
    }

    /// <summary>
    /// Registers the hosted-connector redirect allowlist as a singleton so configured patterns
    /// are parsed exactly once, at startup.
    ///
    /// This is a startup SNAPSHOT: the process never re-reads
    /// <see cref="HostedConnectorCallbackPolicy.ConfigurationKey"/>, so a config change takes
    /// effect only on restart (for Container Apps, a new revision). Deliberate — no per-request
    /// config reads on an auth hot path, and exactly one loggable trust boundary per boot.
    ///
    /// The startup filter is registered UNCONDITIONALLY. On a healthy boot it logs the effective
    /// allowlist at Information, because an allowlist that only appears in logs when it is broken
    /// is one nobody can audit from telemetry. A malformed entry is additionally warned: a
    /// silently-dropped pattern would present as "this connector still gets 400" with nothing in
    /// the logs to explain it.
    /// </summary>
    public static IServiceCollection AddHostedConnectorCallbackPolicy(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var policy = HostedConnectorCallbackPolicy.FromConfiguration(configuration);
        services.AddSingleton(policy);

        services.AddSingleton<IStartupFilter>(
            new HostedConnectorCallbackPolicyLoggingFilter(policy));

        return services;
    }

    private sealed class HostedConnectorCallbackPolicyLoggingFilter : IStartupFilter
    {
        private readonly HostedConnectorCallbackPolicy _policy;

        public HostedConnectorCallbackPolicyLoggingFilter(HostedConnectorCallbackPolicy policy)
        {
            _policy = policy;
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return builder =>
            {
                var logger = builder.ApplicationServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(HostedConnectorCallbackPolicy).FullName!);

                logger.LogInformation(
                    "Hosted connector callback allowlist ({Count}): {Patterns}",
                    _policy.EffectivePatterns.Count,
                    string.Join(", ", _policy.EffectivePatterns));

                if (_policy.InvalidPatterns.Count > 0)
                {
                    logger.LogWarning(
                        "Ignoring {Count} malformed {Key} entries: {Patterns}. Expected 'https://host/exact/path' or 'https://host/prefix/*'.",
                        _policy.InvalidPatterns.Count,
                        HostedConnectorCallbackPolicy.ConfigurationKey,
                        string.Join(", ", _policy.InvalidPatterns));
                }

                next(builder);
            };
        }
    }
}
