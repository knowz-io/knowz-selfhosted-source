using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace Knowz.SelfHosted.Tests.GitCommitHistoryTests;

/// <summary>
/// HTTP-level integration tests for the commit relink backfill endpoint
/// (<c>POST /api/v1/vaults/{vaultId:guid}/git-sync/repositories/{repositoryId:guid}/commits/relink</c>).
///
/// Covers VERIFY-3.5 (non-admin 403 + no edge mutation) from
/// <c>FEAT_CommitBackfillEndpoint.md</c>. Uses the same in-memory DB harness as
/// <see cref="CommitHistoryEndpointTests"/> — JWT auth with controlled role claim.
///
/// WorkGroupID: kc-feat-commit-history-polish-20260411-051000
/// NodeID: NODE-3 CommitBackfillEndpoint
/// </summary>
public class CommitRelinkEndpointTests : IAsyncLifetime
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private const string JwtSecret = "test-jwt-secret-must-be-at-least-32-characters-long!";
    private const string JwtIssuer = "knowz-selfhosted";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly IVaultAccessService _vaultAccessMock;
    private readonly string _dbName;

    public CommitRelinkEndpointTests()
    {
        _dbName = $"CommitRelinkEndpointTests-{Guid.NewGuid():N}";
        _vaultAccessMock = Substitute.For<IVaultAccessService>();

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:McpDb", "Server=(localdb);Database=fake;");
                builder.UseSetting("SelfHosted:ApiKey", ""); // force JWT path
                builder.UseSetting("SelfHosted:JwtSecret", JwtSecret);
                builder.UseSetting("SelfHosted:JwtIssuer", JwtIssuer);
                builder.UseSetting("SelfHosted:EnableSwagger", "true");
                builder.UseSetting("Database:AutoMigrate", "false");
                builder.UseSetting("AzureKeyVault:Enabled", "false");

                builder.ConfigureServices(services =>
                {
                    // Swap DbContext for InMemory — mirrors CommitHistoryEndpointTests.
                    var descriptorsToRemove = services
                        .Where(d =>
                            d.ServiceType == typeof(DbContextOptions<SelfHostedDbContext>) ||
                            d.ServiceType == typeof(DbContextOptions) ||
                            d.ServiceType == typeof(SelfHostedDbContext) ||
                            d.ServiceType == typeof(IDbContextFactory<SelfHostedDbContext>) ||
                            (d.ServiceType.IsGenericType &&
                             d.ServiceType.GetGenericTypeDefinition() == typeof(DbContextOptions<>) &&
                             d.ServiceType.GenericTypeArguments[0] == typeof(SelfHostedDbContext)))
                        .ToList();
                    foreach (var d in descriptorsToRemove)
                        services.Remove(d);

                    var tenantProvider = Substitute.For<ITenantProvider>();
                    tenantProvider.TenantId.Returns(TestTenantId);

                    services.AddSingleton(sp =>
                    {
                        var optionsBuilder = new DbContextOptionsBuilder<SelfHostedDbContext>();
                        optionsBuilder.UseInMemoryDatabase(_dbName);
                        return optionsBuilder.Options;
                    });

                    services.AddScoped<SelfHostedDbContext>(sp =>
                    {
                        var options = sp.GetRequiredService<DbContextOptions<SelfHostedDbContext>>();
                        return new SelfHostedDbContext(options, tenantProvider);
                    });

                    services.AddSingleton<IDbContextFactory<SelfHostedDbContext>>(sp =>
                    {
                        var options = sp.GetRequiredService<DbContextOptions<SelfHostedDbContext>>();
                        return new TestDbContextFactory(options, tenantProvider);
                    });

                    var vaultAccessDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IVaultAccessService));
                    if (vaultAccessDescriptor != null)
                        services.Remove(vaultAccessDescriptor);
                    services.AddScoped<IVaultAccessService>(_ => _vaultAccessMock);
                });
            });
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private sealed class TestDbContextFactory : IDbContextFactory<SelfHostedDbContext>
    {
        private readonly DbContextOptions<SelfHostedDbContext> _options;
        private readonly ITenantProvider _tenantProvider;

        public TestDbContextFactory(DbContextOptions<SelfHostedDbContext> options, ITenantProvider tenantProvider)
        {
            _options = options;
            _tenantProvider = tenantProvider;
        }

        public SelfHostedDbContext CreateDbContext() => new(_options, _tenantProvider);
    }

    private static string BuildJwt(Guid userId, Guid tenantId, string role)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSecret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.Name, "test-user"),
            new Claim(ClaimTypes.Role, role),
            new Claim("role", role),
            new Claim("tenantId", tenantId.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: JwtIssuer,
            audience: JwtIssuer,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Seeds a vault, a git repo pinned to that vault, a file knowledge row, and a
    /// commit knowledge row whose PlatformData contains a changedFilePaths entry that
    /// WOULD link to the file if the endpoint were to run. This lets the test assert that
    /// a forbidden (403) response leaves the graph untouched.
    /// </summary>
    private (Guid vaultId, Guid repoId, Guid commitId, Guid fileId) SeedRelinkableFixture()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();

        var vault = new Vault { Id = Guid.NewGuid(), TenantId = TestTenantId, Name = "v" };
        db.Vaults.Add(vault);

        var repo = new GitRepository
        {
            Id = Guid.NewGuid(),
            TenantId = TestTenantId,
            VaultId = vault.Id,
            RepositoryUrl = "https://git.example.com/org/repo.git",
            Branch = "main",
            Status = "Synced",
            TrackCommitHistory = true
        };
        db.GitRepositories.Add(repo);

        var file = new Knowledge
        {
            Id = Guid.NewGuid(),
            TenantId = TestTenantId,
            Title = "target.cs",
            Content = "file content",
            FilePath = "src/target.cs",
            Source = "git-sync",
            Type = KnowledgeType.Code,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.KnowledgeItems.Add(file);
        db.KnowledgeVaults.Add(new KnowledgeVault
        {
            TenantId = TestTenantId,
            KnowledgeId = file.Id,
            VaultId = vault.Id,
            IsPrimary = true
        });

        // Seed a commit child with changedFilePaths pointing to the file (but NO edge yet).
        var platformData = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["commitSha"] = "cafe0001",
            ["unlinkedFiles"] = new[] { "src/target.cs" },
            ["changedFilePaths"] = new[] { "src/target.cs" }
        });
        var commit = new Knowledge
        {
            Id = Guid.NewGuid(),
            TenantId = TestTenantId,
            Title = "Commit cafe000: test",
            Content = "pending",
            Source = $"{repo.RepositoryUrl}:{repo.Branch}:commit:cafe0001",
            Type = KnowledgeType.Commit,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            PlatformData = platformData
        };
        db.KnowledgeItems.Add(commit);
        db.KnowledgeVaults.Add(new KnowledgeVault
        {
            TenantId = TestTenantId,
            KnowledgeId = commit.Id,
            VaultId = vault.Id,
            IsPrimary = true
        });
        db.SaveChanges();

        return (vault.Id, repo.Id, commit.Id, file.Id);
    }

    private int CountReferencesEdges(Guid sourceId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
        return db.KnowledgeRelationships
            .IgnoreQueryFilters()
            .Count(r => r.SourceKnowledgeId == sourceId
                && r.RelationshipType == KnowledgeRelationshipType.References);
    }

    // ─── VERIFY-3.5: Non-admin gets 403 and no edges are created ─────────────

    [Fact]
    public async Task Verify_3_5_NonAdmin_Returns403_AndNoEdgesCreated()
    {
        var (vaultId, repoId, commitId, fileId) = SeedRelinkableFixture();

        // Grant the user unrestricted vault access so the admin gate is the ONLY thing
        // that can produce a 403 here. Proves R-6 order: admin check runs first.
        var userId = Guid.NewGuid();
        _vaultAccessMock.HasAllVaultsAccessAsync(userId, Arg.Any<CancellationToken>()).Returns(true);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", BuildJwt(userId, TestTenantId, role: "User"));

        var response = await client.PostAsync(
            $"/api/v1/vaults/{vaultId}/git-sync/repositories/{repoId}/commits/relink",
            content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Admin access required for commit relink.", body.GetProperty("error").GetString());

        // Graph was NOT mutated — no edges exist for the commit.
        Assert.Equal(0, CountReferencesEdges(commitId));
    }

    // ─── Admin happy path: backfill succeeds and returns the result ──────────

    [Fact]
    public async Task Admin_CanBackfill_And_Endpoint_Returns200WithResult()
    {
        var (vaultId, repoId, commitId, _) = SeedRelinkableFixture();

        var userId = Guid.NewGuid();
        _vaultAccessMock.HasAllVaultsAccessAsync(userId, Arg.Any<CancellationToken>()).Returns(true);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", BuildJwt(userId, TestTenantId, role: "Admin"));

        var response = await client.PostAsync(
            $"/api/v1/vaults/{vaultId}/git-sync/repositories/{repoId}/commits/relink",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("processed").GetInt32());
        Assert.Equal(1, body.GetProperty("linked").GetInt32());
        Assert.Equal(0, body.GetProperty("skipped").GetInt32());

        // Graph mutated — exactly one new edge for the commit.
        Assert.Equal(1, CountReferencesEdges(commitId));
    }

    // ─── Admin + repository in DIFFERENT vault → 404 ─────────────────────────

    [Fact]
    public async Task Admin_Repo_NotInVault_Returns404()
    {
        var (_, repoId, commitId, _) = SeedRelinkableFixture();
        var otherVaultId = Guid.NewGuid(); // repo is NOT in this vault

        var userId = Guid.NewGuid();
        _vaultAccessMock.HasAllVaultsAccessAsync(userId, Arg.Any<CancellationToken>()).Returns(true);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", BuildJwt(userId, TestTenantId, role: "Admin"));

        var response = await client.PostAsync(
            $"/api/v1/vaults/{otherVaultId}/git-sync/repositories/{repoId}/commits/relink",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, CountReferencesEdges(commitId));
    }
}
