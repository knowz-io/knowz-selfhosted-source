namespace Knowz.SelfHosted.Infrastructure.Interfaces;

/// <summary>
/// Applies natural-language edit instructions to existing content using AI.
/// </summary>
public interface IContentAmendmentService
{
    /// <summary>
    /// Applies an AI edit instruction to the existing content and returns the updated content.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when AI services are not configured.</exception>
    Task<string> ApplyContentUpdateAsync(
        string existingContent,
        string instruction,
        CancellationToken cancellationToken = default);
}
