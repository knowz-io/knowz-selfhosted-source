namespace Knowz.SelfHosted.Application.Services;

/// <summary>
/// Plaintext knowledge APIs must not mutate, index, or delete a sealed envelope.
/// </summary>
public sealed class SealedItemLockedException : InvalidOperationException
{
    public SealedItemLockedException()
        : base("This item is a preserved locked envelope. Use the encrypted lifecycle API. The server does not decrypt it.")
    {
    }
}
