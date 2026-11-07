using System.Data.Common;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Knowz.SelfHosted.Tests;

public sealed partial class RecoveredCustodyTests
{
    [PostgresFact]
    public async Task RetryEnabledPostgresAcceptsExactlyOnceIncludingTransientAndLostCommitResponses()
    {
        var configured = Environment.GetEnvironmentVariable("KNOWZ_TEST_POSTGRES");
        Assert.False(string.IsNullOrWhiteSpace(configured), "Owned PostgreSQL connection required for runtime custody proof.");
        var connection = new NpgsqlConnectionStringBuilder(configured);
        Assert.True(connection.Host is "127.0.0.1" or "localhost" && connection.Port == 18460);
        connection.Database = "postgres"; connection.Pooling = false;
        await using var admin = new NpgsqlConnection(connection.ToString()); await admin.OpenAsync();
        foreach (var phase in new[] { "none", "before", "after" })
        {
            var database = "kc_portability_custody_" + Guid.NewGuid().ToString("N");
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin)) await create.ExecuteNonQueryAsync();
            connection.Database = database; var fault = new CustodyCommitFault(phase);
            var options = new DbContextOptionsBuilder<SelfHostedDbContext>().UseNpgsql(connection.ToString(), pg => pg.EnableRetryOnFailure(2, TimeSpan.FromMilliseconds(25), null))
                .AddInterceptors(fault, new CustodySaveFault(fault)).Options;
            try
            {
                await using var f = await Fixture.Create(options);
                var challenge = await f.Issue(); var proof = f.Prove(challenge); fault.Armed = true;
                var accepted = await f.Accept(challenge, proof);
                Assert.Equal(phase != "none", fault.Injected);
                Assert.Equal("canary-verified", accepted.KeyPossession);
                Assert.Single(await f.Db.ShRecoveredKeyCustodyAcceptances.ToListAsync());
                Assert.Equal("accepted", (await f.Db.ShCryptoCustodyChallenges.SingleAsync()).Status);
                // The transaction retry is internal; the external one-time proof cannot be spent again.
                await Assert.ThrowsAsync<ArchiveOperationException>(() => f.Accept(challenge, proof));
                Assert.Single(await f.Db.ShRecoveredKeyCustodyAcceptances.ToListAsync());
            }
            finally
            {
                await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin); await drop.ExecuteNonQueryAsync();
            }
        }
    }

    private sealed class CustodyCommitFault(string phase) : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public bool Injected { get; private set; }
        public bool TryInject(string point)
        {
            if (!Armed || Injected || phase != point) return false;
            Injected = true; return true;
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<ShRecoveredKeyCustodyAcceptance>().Any() == true && TryInject("after"))
                throw new TimeoutException("Synthetic lost custody COMMIT response.");
            return Task.CompletedTask;
        }
    }

    private sealed class CustodySaveFault(CustodyCommitFault fault) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<ShRecoveredKeyCustodyAcceptance>().Any(e => e.State == EntityState.Added) == true && fault.TryInject("before"))
                throw new TimeoutException("Synthetic transient custody write.");
            return ValueTask.FromResult(result);
        }
    }
}
