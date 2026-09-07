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
/// HTTP-level integration tests for the per-item commit-history endpoint
/// (<c>GET /api/v1/vaults/{vaultId:guid}/knowledge/{knowledgeId:guid}/commit-history</c>).
/// Covers VERIFY-B.3 (403 auth gate, JWT + restricted user) and VERIFY-B.2
/// (zero-commit → 200 empty, not 404).
///
/// Uses WebApplicationFactory with the same InMemory DB harness as
/// <see cref="SelfHostedApiTests"/>. Overrides <see cref="IVaultAccessService"/>
/// with an NSubstitute mock so the test can return controlled vault access
/// lists without requiring real UserPermissions rows.
///
/// WorkGroupID: kc-feat-commit-knowledge-link-20260410-230500
/// NodeID: SelfHostedKnowledgeCommitHistoryQuery
/// </summary>
public class CommitHistoryEndpointTests : IAsyncLifetime
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private const string JwtSecret = "test-jwt-secret-must-be-at-least-32-characters-long!";
    private const string JwtIssuer = "knowz-selfhosted";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly IVaultAccessService _vaultAccessMock;
    private readonly string _dbName;

    public CommitHistoryEndpointTests()
    {
        _dbName = $"CommitHistoryEndpointTests-{Guid.NewGuid():N}";
        _vaultAccessMock = Substitute.For<IVaultAccessService>();

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:McpDb", "Server=(localdb);Database=fake;");
                builder.UseSetting("SelfHosted:ApiKey", ""); // disable legacy API key path so JWT is required
                builder.UseSetting("SelfHosted:JwtSecret", JwtSecret);
                builder.UseSetting("SelfHosted:JwtIssuer", JwtIssuer);
                builder.UseSetting("SelfHosted:EnableSwagger", "true");
                builder.UseSetting("Database:AutoMigrate", "false");
                builder.UseSetting("AzureKeyVault:Enabled", "false");

                builder.ConfigureServices(services =>
                {
                    // Replace SelfHostedDbContext registrations with InMemory (mirror SelfHostedApiTests pattern).
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

                    // Replace IVaultAccessService with the mock so we can drive the 403 path.
                    var vaultAccessDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IVaultAccessService));
                    if (vaultAccessDescriptor != null)
                    {
                        services.Remove(vaultAccessDescriptor);
                    }
                    services.AddScoped<IVaultAccessService>(_ => _vaultAccessMock);
                });
            });
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
    }

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

    /// <summary>
    /// Creates a signed JWT matching the selfhosted middleware's expected algorithm
    /// (<c>HmacSha256</c>) and claims (<c>sub</c>, <c>tenantId</c>). Matches
    /// <c>JwtTokenHelper.GenerateToken</c>.
    /// </summary>
    private static string BuildJwt(Guid userId, Guid tenantId)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSecret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.Name, "test-user"),
            new Claim(ClaimTypes.Role, "User"),
            new Claim("role", "User"),
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

    /// <summary>Seeds a vault + a file knowledge item in the test InMemory DB.</summary>
    private (Guid vaultId, Guid fileId) SeedVaultAndFile()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();

        var vault = new Vault { Id = Guid.NewGuid(), TenantId = TestTenantId, Name = "v" };
        db.Vaults.Add(vault);

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
        db.SaveChanges();

        return (vault.Id, file.Id);
    }

    // ─── VERIFY-B.3: HTTP 403 on forbidden vault ─────────────────────────────

    [Fact]
    public async Task Verify_B3_VaultAccess_Forbidden_Returns403AndErrorBody()
    {
        // Seed a vault + file, then drive the mock so the JWT user has access to
        // a DIFFERENT vault (not the one in the URL).
        var (vaultId, fileId) = SeedVaultAndFile();

        var userId = Guid.NewGuid();
        var otherVaultId = Guid.NewGuid(); // user has access to this vault, NOT {vaultId}

        _vaultAccessMock.HasAllVaultsAccessAsync(userId, Arg.Any<CancellationToken>())
            .Returns(false);
        _vaultAccessMock.GetAccessibleVaultIdsAsync(
                userId, TestTenantId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { otherVaultId });

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", BuildJwt(userId, TestTenantId));

        var response = await client.GetAsync(
            $"/api/v1/vaults/{vaultId}/knowledge/{fileId}/commit-history");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Access denied to this vault.", body.GetProperty("error").GetString());
    }

    // ─── VERIFY-B.2: HTTP 200 empty body when no commits ────────────────────

    [Fact]
    public async Task Verify_B2_ZeroCommits_Returns200EmptyNotFound()
    {
        // Seed a vault + file with ZERO commit-history references, then drive the
        // mock so the JWT user has unrestricted vault access (gate passes).
        var (vaultId, fileId) = SeedVaultAndFile();

        var userId = Guid.NewGuid();

        _vaultAccessMock.HasAllVaultsAccessAsync(userId, Arg.Any<CancellationToken>())
            .Returns(true); // unrestricted → ResolveAccessibleVaultIdsAsync returns null → gate skipped

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", BuildJwt(userId, TestTenantId));

        var response = await client.GetAsync(
            $"/api/v1/vaults/{vaultId}/knowledge/{fileId}/commit-history");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("total").GetInt32());
        Assert.Equal(1, body.GetProperty("page").GetInt32());
        Assert.Equal(20, body.GetProperty("pageSize").GetInt32());
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Empty(items);
    }
}
