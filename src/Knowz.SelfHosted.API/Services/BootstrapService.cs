using Azure;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;
using Knowz.SelfHosted.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Knowz.SelfHosted.API.Services;

/// <summary>
/// SH_ENTERPRISE_CREDENTIAL_BOOTSTRAP §2.1: first-run surface for enterprise
/// self-hosted deploys. Runs once at startup:
///
/// 1. Seeds the SuperAdmin via <see cref="IAuthService.EnsureSuperAdminExistsAsync"/>
///    (which refuses weak passwords and fails closed if the KV-provided password
///    is missing — SEC_P0Triage §Rule 3).
/// 2. Mints a single bootstrap API key for the SuperAdmin and writes it to the
///    customer's Key Vault at <c>SelfHosted--BootstrapApiKey</c> with a 24h TTL.
/// 3. Does NOT log the plaintext — it only reaches KV, bounded by KV RBAC.
///
/// The operator retrieves the key via <c>az keyvault secret show --name
/// SelfHosted--BootstrapApiKey</c> and uses it to drive the rest of the
/// deploy-selfhosted smoke test / bootstrap flow.
///
/// Idempotent — subsequent boots detect an existing ApiKey on the SuperAdmin
/// and skip minting, so container restarts don't churn KV.
/// </summary>
public sealed class BootstrapService : IHostedService
{
    public const string BootstrapSecretName = "SelfHosted--BootstrapApiKey";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly TokenCredential _tokenCredential;
    private readonly ILogger<BootstrapService> _logger;

    public BootstrapService(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        TokenCredential tokenCredential,
        ILogger<BootstrapService> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _tokenCredential = tokenCredential;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Bootstrap key minting runs only when Key Vault is configured. SuperAdmin
        // seeding itself is handled by the existing `EnsureSuperAdminExistsAsync`
        // call in Program.cs (gated on `migrationSucceeded`) — we do NOT duplicate
        // that here, because BootstrapService starts before the Program.cs seed
        // block finishes, and throwing here would crash tests that never configure
        // a SuperAdmin.
        var kvUri = _config["AzureKeyVault:VaultUri"];
        if (string.IsNullOrWhiteSpace(kvUri))
        {
            _logger.LogInformation(
                "BootstrapService: AzureKeyVault:VaultUri not set — skipping bootstrap key mint.");
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var authService = scope.ServiceProvider.GetRequiredService<IAuthService>();

            var plaintext = await authService.IssueBootstrapApiKeyForSuperAdminAsync();
            if (plaintext is null)
            {
                _logger.LogInformation(
                    "BootstrapService: SuperAdmin absent or already keyed — skipping bootstrap key mint (idempotent).");
                return;
            }

            var client = new SecretClient(new Uri(kvUri), _tokenCredential);
            var secret = new KeyVaultSecret(BootstrapSecretName, plaintext);
            secret.Properties.ExpiresOn = DateTimeOffset.UtcNow.AddHours(24);

            try
            {
                await client.SetSecretAsync(secret, cancellationToken);
                _logger.LogInformation(
                    "BootstrapService: wrote bootstrap API key to KV secret '{Name}' (24h TTL).",
                    BootstrapSecretName);
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError(ex,
                    "BootstrapService: failed to write bootstrap secret to KV. Status={Status}",
                    ex.Status);
                // Don't throw — the SuperAdmin + API key rows are now in the DB;
                // the operator can recover via the admin API-key rotation flow.
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never fail the host because bootstrap couldn't talk to KV. The
            // operator will notice missing `SelfHosted--BootstrapApiKey` when
            // they try to retrieve it, and has a recovery path.
            _logger.LogError(ex, "BootstrapService: non-fatal error during first-run bootstrap.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
