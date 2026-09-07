using Knowz.SelfHosted.API.Services;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// VERIFY (SH_ENTERPRISE_RUNTIME_RESILIENCE §3.1 / §Rule 3):
/// The startup migration loop fails CLOSED after exhausted retries — throws
/// InvalidOperationException wrapping the last inner exception, so the host
/// aborts and the orchestrator restarts the container. This was code-only
/// verified after the 2026-04-15/16 partner registration outage; G3 (review
/// audit) flagged the missing unit coverage.
/// </summary>
public class MigrationRetryTests
{
    [Fact]
    public async Task RunWithRetry_Success_OnFirstAttempt_NoRetry()
    {
        var attempts = 0;
        await MigrationRunner.RunWithRetryAsync(
            migrateAsync: _ => { attempts++; return Task.CompletedTask; },
            maxRetries: 3,
            delay: _ => TimeSpan.Zero);

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task RunWithRetry_Succeeds_AfterTransientFailures()
    {
        var attempts = 0;
        await MigrationRunner.RunWithRetryAsync(
            migrateAsync: _ =>
            {
                attempts++;
                if (attempts < 3) throw new InvalidOperationException("DB not ready");
                return Task.CompletedTask;
            },
            maxRetries: 5,
            delay: _ => TimeSpan.Zero);

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ExhaustedRetriesThrow_WrappingInner_FailClosed()
    {
        // Mock a migration that always fails — simulates the 2026-04-15/16
        // partner outage where DB was unreachable and the old loop logged-
        // and-continued silently.
        var inner = new InvalidOperationException("Connection refused");
        var attempts = 0;
        var failureObserved = false;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MigrationRunner.RunWithRetryAsync(
                migrateAsync: _ => { attempts++; throw inner; },
                maxRetries: 3,
                delay: _ => TimeSpan.Zero,
                onFailure: _ => failureObserved = true));

        Assert.Equal(3, attempts);
        Assert.True(failureObserved, "onFailure callback fires on the final attempt before throwing");
        Assert.Same(inner, ex.InnerException);
        Assert.Contains("3 attempts", ex.Message);
    }

    [Fact]
    public async Task RunWithRetry_InvokesOnRetry_ForEachIntermediateFailure()
    {
        var retries = new List<int>();

        try
        {
            await MigrationRunner.RunWithRetryAsync(
                migrateAsync: _ => throw new InvalidOperationException("boom"),
                maxRetries: 4,
                delay: _ => TimeSpan.Zero,
                onRetry: (attempt, _) => retries.Add(attempt));
        }
        catch (InvalidOperationException) { /* expected after retries exhausted */ }

        Assert.Equal(new[] { 1, 2, 3 }, retries);
    }
}
