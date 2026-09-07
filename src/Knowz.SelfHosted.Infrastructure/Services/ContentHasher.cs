using System.Security.Cryptography;
using System.Text;

namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>
/// Shared content hashing utility for embedding cache and chunk persistence.
/// </summary>
public static class ContentHasher
{
    /// <summary>
    /// Computes a SHA256 hash of the given content, returned as lowercase hex string.
    /// </summary>
    public static string Hash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }
}
