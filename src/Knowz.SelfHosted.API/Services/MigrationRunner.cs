namespace Knowz.SelfHosted.API.Services;

/// <summary>
/// SH_ENTERPRISE_RUNTIME_RESILIENCE §Rule 3: retry policy for startup DB
/// migrations. Extracted from inline <c>Program.cs</c> so the fail-closed
/// behavior can be unit-tested without spinning an app host.
///
/// Contract:
/// - Attempts up to <c>maxRetries</c> invocations of <c>migrateAsync</c>.
/// - On success, returns normally.
/// - On transient failure (attempt &lt; maxRetries), waits <c>delay(attempt)</c>
///   and retries.
/// - On exhausted retries, throws <see cref="InvalidOperationException"/>
///   wrapping the last inner exception. The orchestrator restarts the
///   container, which is the correct action when the DB just wasn't ready.
///
/// Exception classes other than the retryable migration-path ones are
/// intentionally NOT special-cased here — the caller chooses what to retry.
/// </summary>
public static class MigrationRunner
{
    public static async Task RunWithRetryAsync(
        Func<int, Task> migrateAsync,
        int maxRetries,
        Func<int, TimeSpan> delay,
        Action<int, Exception>? onRetry = null,
        Action<Exception>? onFailure = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(migrateAsync);
        ArgumentNullException.ThrowIfNull(delay);
        if (maxRetries < 1) throw new ArgumentOutOfRangeException(nameof(maxRetries));

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await migrateAsync(attempt).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                if (attempt == maxRetries)
                {
                    onFailure?.Invoke(ex);
                    throw new InvalidOperationException(
                        $"Database migration failed after {maxRetries} attempts. Fix connection/migration issue and restart.",
                        ex);
                }

                onRetry?.Invoke(attempt, ex);
                await Task.Delay(delay(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
