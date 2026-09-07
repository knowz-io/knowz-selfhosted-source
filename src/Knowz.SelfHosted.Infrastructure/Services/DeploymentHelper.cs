namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>
/// Shared helpers for Azure OpenAI deployment configuration.
/// </summary>
internal static class DeploymentHelper
{
    /// <summary>
    /// Returns true for reasoning-series models (o1, o3, o4-mini) and GPT-5,
    /// which do not support the max_tokens parameter via the Azure SDK.
    /// </summary>
    internal static bool IsReasoningModel(string deploymentName)
    {
        return deploymentName.Contains("o1", StringComparison.OrdinalIgnoreCase) ||
               deploymentName.Contains("o3", StringComparison.OrdinalIgnoreCase) ||
               deploymentName.Contains("o4-mini", StringComparison.OrdinalIgnoreCase) ||
               deploymentName.Contains("gpt-5", StringComparison.OrdinalIgnoreCase);
    }
}
