using System.Net.Http.Headers;
using System.Text.Json;
using Spectre.Console;

namespace Knowz.SelfHosted.Setup.Services;

public static class OpenAiProber
{
    public static async Task<List<(string DeploymentName, string ModelName)>> ListDeploymentsAsync(
        string endpoint, string apiKey)
    {
        try
        {
            var baseUrl = endpoint.TrimEnd('/');
            var requestUrl = $"{baseUrl}/openai/deployments?api-version=2024-10-21";

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("api-key", apiKey);
            httpClient.Timeout = TimeSpan.FromSeconds(15);

            var response = await httpClient.GetAsync(requestUrl);

            if (!response.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]Failed to list deployments (HTTP {(int)response.StatusCode}). " +
                    $"Check endpoint and API key.[/]");
                return [];
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var deployments = new List<(string DeploymentName, string ModelName)>();

            if (doc.RootElement.TryGetProperty("data", out var dataArray))
            {
                foreach (var item in dataArray.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                    var model = item.TryGetProperty("model", out var modelProp) ? modelProp.GetString() : null;

                    if (!string.IsNullOrEmpty(id))
                        deployments.Add((id, model ?? "unknown"));
                }
            }

            return deployments;
        }
        catch (TaskCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Request timed out while listing OpenAI deployments.[/]");
            return [];
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Network error listing deployments: {ex.Message.EscapeMarkup()}[/]");
            return [];
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Failed to list deployments: {ex.Message.EscapeMarkup()}[/]");
            return [];
        }
    }
}
