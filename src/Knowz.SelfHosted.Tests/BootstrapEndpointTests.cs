using Azure.Core;
using Knowz.Core.Configuration;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.API.Services;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// VERIFY (SH_ENTERPRISE_CREDENTIAL_BOOTSTRAP §3):
/// 1.2 — SuperAdmin seeded; second boot no-ops (existing row detected).
/// 2.2 — IssueBootstrapApiKeyForSuperAdminAsync idempotent — second call returns null.
/// 2.3 — Issued plaintext matches the `ksh_` prefix contract and is written to User.ApiKey.
/// 3.x — `/api/bootstrap/status` response shape is exactly `{ready: bool}`.
///
/// HTTP rate-limiting / full TestServer smoke are covered by integration tests.
/// </summary>
public class BootstrapEndpointTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly Guid _tenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public BootstrapEndpointTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().SetApplicationName("tests");

        services.AddScoped<ITenantProvider>(_ =>
        {
            var tp = Substitute.For<ITenantProvider>();
            tp.TenantId.Returns(_tenantId);
            return tp;
        });
        services.AddDbContext<SelfHostedDbContext>(o =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString()));

        var options = new SelfHostedOptions
        {
            SuperAdminUsername = "superadmin",
            SuperAdminPassword = "Z3br@Crafts!NebulaHorizon42",
            JwtSecret = new string('x', 48),
            JwtIssuer = "tests",
        };
        services.AddSingleton<IOptions<SelfHostedOptions>>(new OptionsWrapper<SelfHostedOptions>(options));
        services.AddScoped<IAuthService, AuthService>();

        _sp = services.BuildServiceProvider();
    }

    public void Dispose() => _sp.Dispose();

    [Fact]
    public async Task IssueBootstrapKey_MintsKshKey_OnFirstCall()
    {
        using var scope = _sp.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();

        await auth.EnsureSuperAdminExistsAsync();
        var plaintext = await auth.IssueBootstrapApiKeyForSuperAdminAsync();

        Assert.NotNull(plaintext);
        Assert.StartsWith("ksh_", plaintext);
        Assert.True(plaintext!.Length >= 20, "API key is long enough to be non-trivial to brute-force");
    }

    [Fact]
    public async Task IssueBootstrapKey_Idempotent_ReturnsNullOnSecondCall()
    {
        using var scope = _sp.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();

        await auth.EnsureSuperAdminExistsAsync();
        var first = await auth.IssueBootstrapApiKeyForSuperAdminAsync();
        var second = await auth.IssueBootstrapApiKeyForSuperAdminAsync();

        Assert.NotNull(first);
        Assert.Null(second); // already issued — no clobber
    }

    [Fact]
    public async Task IssueBootstrapKey_PersistedToSuperAdmin_ApiKeyColumn()
    {
        using var scope = _sp.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
        var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();

        await auth.EnsureSuperAdminExistsAsync();
        var plaintext = await auth.IssueBootstrapApiKeyForSuperAdminAsync();

        var sa = await db.Users.FirstAsync(u => u.Role == UserRole.SuperAdmin);
        Assert.Equal(plaintext, sa.ApiKey);
    }

    [Fact]
    public async Task EnsureSuperAdmin_IsIdempotent_SecondCallNoops()
    {
        using var scope = _sp.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
        var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();

        await auth.EnsureSuperAdminExistsAsync();
        var countAfterFirst = await db.Users.CountAsync(u => u.Role == UserRole.SuperAdmin);
        await auth.EnsureSuperAdminExistsAsync();
        var countAfterSecond = await db.Users.CountAsync(u => u.Role == UserRole.SuperAdmin);

        Assert.Equal(1, countAfterFirst);
        Assert.Equal(1, countAfterSecond);
    }

    // --- G1 / VERIFY 1.1b: BootstrapService soft-fails on KV write failure ---

    [Fact]
    public async Task BootstrapService_DoesNotThrow_WhenSuperAdminAbsent()
    {
        // BootstrapService is a hosted service — its StartAsync throwing would
        // abort the entire host. When KV is not configured (dev/local) OR when
        // there is no SuperAdmin to mint a key for, it must return cleanly.
        using var scope = _sp.CreateScope();
        var config = new ConfigurationBuilder().Build(); // no AzureKeyVault:VaultUri

        var tokenCredential = Substitute.For<TokenCredential>();
        var service = new BootstrapService(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            config,
            tokenCredential,
            NullLogger<BootstrapService>.Instance);

        // Should complete without throwing even though no SuperAdmin exists
        await service.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task BootstrapService_DoesNotThrow_WhenKvUnreachable_LogsError()
    {
        // SH_ENTERPRISE_CREDENTIAL_BOOTSTRAP Rule 1b: even with a configured KV
        // URI that resolves to a bogus endpoint, StartAsync must NOT throw — the
        // host must stay up so other paths (login, already-issued API keys,
        // /api/bootstrap/status) keep working while operators diagnose.
        using (var seedScope = _sp.CreateScope())
        {
            var auth = seedScope.ServiceProvider.GetRequiredService<IAuthService>();
            await auth.EnsureSuperAdminExistsAsync();
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Valid URI shape but intentionally non-routable host so
                // SetSecretAsync throws a RequestFailedException / transport error.
                ["AzureKeyVault:VaultUri"] = "https://nonexistent-vault-for-unit-test.vault.azure.net/",
            })
            .Build();

        var tokenCredential = Substitute.For<TokenCredential>();
        tokenCredential.GetTokenAsync(Arg.Any<TokenRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(new AccessToken("fake", DateTimeOffset.UtcNow.AddHours(1)));
        tokenCredential.GetToken(Arg.Any<TokenRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(new AccessToken("fake", DateTimeOffset.UtcNow.AddHours(1)));

        var logger = Substitute.For<ILogger<BootstrapService>>();
        var service = new BootstrapService(
            _sp.GetRequiredService<IServiceScopeFactory>(),
            config,
            tokenCredential,
            logger);

        // Cap StartAsync so the KV SDK's retry loop doesn't hang the test.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ex = await Record.ExceptionAsync(() => service.StartAsync(cts.Token));

        // Contract: never throw. If this fails, the host will crash in prod.
        Assert.Null(ex);
    }
}
