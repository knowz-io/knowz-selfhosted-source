using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using Azure;
using Azure.AI.OpenAI;
using Azure.Core;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Npgsql;
using OpenAI;
using OpenAI.Chat;

namespace Knowz.SelfHosted.Application.Services;

/// <summary>Small bounded checks using running settings. Validation never claims connectivity.</summary>
public static class ConfigurationProbe
{
    public static string Kind(string category) => category switch
    {
        "ConnectionStrings" or "AzureOpenAI" or "OpenAiCompatible" => "connectivity",
        "AzureAIVision" or "AzureDocumentIntelligence" or "AzureAISearch" or "KnowzPlatform" or "Storage" or "SSO" => "configuration",
        _ => "unsupported"
    };

    public static async Task<ServiceHealthResult> RunAsync(string category, IConfiguration config,
        HttpClient http, TokenCredential? credential = null, CancellationToken cancellationToken = default)
    {
        var result = new ServiceHealthResult { Category = category, DisplayName = category,
            ProbeKind = Kind(category), ProbeStatus = "unavailable", CheckedAt = DateTimeOffset.UtcNow };
        if (AiRuntimePolicy.IsDisabled(config) && AiRuntimePolicy.IsAiCategory(category))
        { result.ProbeStatus = "disabled"; result.Status = AiRuntimePolicy.Guidance; return result; }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var timer = Stopwatch.StartNew();
        try
        {
            if (category == "ConnectionStrings")
            {
                var connectionString = config.GetConnectionString("McpDb");
                if (string.IsNullOrWhiteSpace(connectionString)) return Missing(result);
                var options = new NpgsqlConnectionStringBuilder(connectionString) { Timeout = 10, CommandTimeout = 10 };
                await using var connection = new NpgsqlConnection(options.ConnectionString);
                await connection.OpenAsync(deadline.Token);
                await using var command = new NpgsqlCommand("SELECT 1", connection);
                await command.ExecuteScalarAsync(deadline.Token);
            }
            else if (category is "AzureOpenAI" or "OpenAiCompatible")
            {
                var endpoint = config[$"{category}:Endpoint"];
                var chat = config[$"{category}:" + (category == "AzureOpenAI" ? "DeploymentName" : "ChatModel")];
                var embedding = config[$"{category}:" + (category == "AzureOpenAI" ? "EmbeddingDeploymentName" : "EmbeddingModel")];
                if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(chat) || string.IsNullOrWhiteSpace(embedding)) return Missing(result);
                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return Missing(result);
                var key = config[$"{category}:ApiKey"];
                var transport = new HttpClientPipelineTransport(http);
                OpenAIClient client;
                if (category == "AzureOpenAI")
                {
                    var options = new AzureOpenAIClientOptions { Transport = transport, RetryPolicy = new ClientRetryPolicy(0) };
                    client = !string.IsNullOrWhiteSpace(key)
                        ? new AzureOpenAIClient(uri, new AzureKeyCredential(key), options)
                        : credential is not null ? new AzureOpenAIClient(uri, credential, options)
                        : throw new InvalidOperationException("Managed identity unavailable");
                }
                else client = new OpenAIClient(new ApiKeyCredential(string.IsNullOrWhiteSpace(key) ? "knowz-local-keyless" : key),
                    new OpenAIClientOptions { Endpoint = uri, Transport = transport, RetryPolicy = new ClientRetryPolicy(0) });
                await client.GetChatClient(chat).CompleteChatAsync([new UserChatMessage("Reply OK")],
                    new ChatCompletionOptions { MaxOutputTokenCount = 1 }, deadline.Token);
                await client.GetEmbeddingClient(embedding).GenerateEmbeddingAsync("connection check", cancellationToken: deadline.Token);
            }
            else
            {
                if (result.ProbeKind == "unsupported")
                { result.ProbeStatus = "unsupported"; result.Status = "No connectivity test available"; return result; }
                // These providers require service-specific permission/file operations. Report
                // only configuration validation until a dedicated authenticated probe exists.
                var value = category switch { "Storage" => config["Storage:Provider"], "SSO" => config["SSO:Enabled"],
                    "KnowzPlatform" => config["KnowzPlatform:BaseUrl"], _ => config[$"{category}:Endpoint"] };
                if (string.IsNullOrWhiteSpace(value)) return Missing(result);
                if (category is not ("Storage" or "SSO") && (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))) return Missing(result);
                result.ProbeStatus = "configuration-valid";
                result.Status = "Configuration present; connectivity not verified";
                return result;
            }
            result.IsHealthy = true; result.ProbeStatus = "connected"; result.Status = "Connected";
            result.LatencyMs = (int)timer.ElapsedMilliseconds;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            result.IsHealthy = false;
            var unauthorized = error is ClientResultException { Status: 401 or 403 } or RequestFailedException { Status: 401 or 403 }
                or PostgresException { SqlState: "28P01" or "28000" };
            result.ProbeStatus = unauthorized ? "unauthorized" : "unavailable";
            result.Status = unauthorized ? "Authentication failed; check the managed credential and permissions."
                : "Connection not verified; check endpoint, model, network and configuration.";
        }
        return result;
    }
    private static ServiceHealthResult Missing(ServiceHealthResult result)
    { result.Status = "Not Configured"; result.ProbeStatus = "unconfigured"; return result; }
}
