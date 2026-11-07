using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.Core.Schema;
using Knowz.SelfHosted.API.Endpoints;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// ADR_G6 (#1043): HTTP-level coverage of the self-hosted passive receive surface — the routes a
/// peer (portable / platform / another SH) calls to pull from or push to this instance.
/// Mirrors the SaaS contract: schema handshake, vault manifest, delta export (paged), single-item
/// export, delta import with the 100-item cap, register acknowledgement.
/// </summary>
public class SyncReceiveEndpointsTests : IAsyncLifetime
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid VaultId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Knowledge1 = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid Knowledge2 = Guid.Parse("aaaaaaaa-2222-2222-2222-222222222222");
    private const string JwtSecret = "test-jwt-secret-must-be-at-least-32-characters-long!";
    private const string JwtIssuer = "knowz-selfhosted";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dbName;

    public SyncReceiveEndpointsTests()
    {
        _dbName = $"SyncReceiveEndpointsTests-{Guid.NewGuid():N}";
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:McpDb", "Server=(localdb);Database=fake;");
                builder.UseSetting("SelfHosted:ApiKey", "");
                builder.UseSetting("SelfHosted:JwtSecret", JwtSecret);
                builder.UseSetting("SelfHosted:JwtIssuer", JwtIssuer);
                builder.UseSetting("SelfHosted:EnableSwagger", "true");
                builder.UseSetting("Database:AutoMigrate", "false");
                builder.UseSetting("AzureKeyVault:Enabled", "false");

                builder.ConfigureServices(services =>
                {
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
                    // The app registers its own ITenantProvider; override so the DbContext filter
                    // and the endpoints agree on the tenant regardless of the JWT claim path.
                    services.AddSingleton(tenantProvider);

                    services.AddSingleton(_ =>
                    {
                        var optionsBuilder = new DbContextOptionsBuilder<SelfHostedDbContext>();
                        optionsBuilder.UseInMemoryDatabase(_dbName);
                        optionsBuilder.ConfigureWarnings(w => w.Ignore(
                            Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning));
                        return optionsBuilder.Options;
                    });

                    services.AddScoped(sp =>
                    {
                        var options = sp.GetRequiredService<DbContextOptions<SelfHostedDbContext>>();
                        return new SelfHostedDbContext(options, tenantProvider);
                    });

                    services.AddSingleton<IDbContextFactory<SelfHostedDbContext>>(sp =>
                    {
                        var options = sp.GetRequiredService<DbContextOptions<SelfHostedDbContext>>();
                        return new TestDbContextFactory(options, tenantProvider);
                    });
                });
            });
    }

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
        db.Vaults.Add(new Vault { Id = VaultId, TenantId = TestTenantId, Name = "Receive Vault", Description = "peer target" });
        var t0 = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        db.KnowledgeItems.Add(new Knowledge { Id = Knowledge1, TenantId = TestTenantId, Title = "One", Content = "first", CreatedAt = t0, UpdatedAt = t0 });
        db.KnowledgeItems.Add(new Knowledge { Id = Knowledge2, TenantId = TestTenantId, Title = "Two", Content = "second", CreatedAt = t0.AddDays(1), UpdatedAt = t0.AddDays(1) });
        db.KnowledgeVaults.Add(new KnowledgeVault { TenantId = TestTenantId, KnowledgeId = Knowledge1, VaultId = VaultId, IsPrimary = true });
        db.KnowledgeVaults.Add(new KnowledgeVault { TenantId = TestTenantId, KnowledgeId = Knowledge2, VaultId = VaultId, IsPrimary = true });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Theory]
    [InlineData("export")]
    [InlineData("items")]
    [InlineData("import")]
    public async Task ProtectedGraphsRequireRegisteredCompleteArchiveTransport(string operation)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
            db.ShSealedKnowledge.Add(new() { TenantId = TestTenantId, KnowledgeId = Knowledge1, OriginalItemId = Knowledge1, Ciphertext = "preserved-encrypted-body", LineageScope = "complete" }); await db.SaveChangesAsync();
        }
        using var client = ClientFor("User");
        var response = operation == "import"
            ? await client.PostAsJsonAsync($"/api/v1/sync/vaults/{VaultId}/import", new PortableExportPackage { SchemaVersion = EncryptedGraphProfile.SchemaVersion, SourceEdition = "platform", Data = new() { KnowledgeItems = [new() { Id = Knowledge1, Content = "must not overwrite ciphertext placeholder" }] } })
            : await client.GetAsync(operation == "items" ? $"/api/v1/sync/vaults/{VaultId}/items/{Knowledge1}/export" : $"/api/v1/sync/vaults/{VaultId}/export");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Contains("archive-peer", await response.Content.ReadAsStringAsync());
        using var check = _factory.Services.CreateScope(); var data = check.ServiceProvider.GetRequiredService<SelfHostedDbContext>(); Assert.Equal("first", (await data.KnowledgeItems.SingleAsync(k => k.Id == Knowledge1)).Content);
        var handshake = await ReadJsonAsync(await client.GetAsync("/api/v1/sync/schema")); Assert.Contains(handshake.RootElement.GetProperty("data").GetProperty("capabilities").EnumerateArray(), x => x.GetString() == "knowz.archive-peer/v1");
    }

    // ---------- schema ----------

    [Fact]
    public async Task Schema_Anonymous_Returns401()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/sync/schema");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Schema_User_ReturnsEnvelope_WithVersionAndTenant()
    {
        using var client = ClientFor("User");
        var response = await client.GetAsync("/api/v1/sync/schema");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = await ReadJsonAsync(response);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(CoreSchema.Version, data.GetProperty("version").GetInt32());
        Assert.Equal(CoreSchema.MinReadableVersion, data.GetProperty("minReadableVersion").GetInt32());
        Assert.Equal(TestTenantId, data.GetProperty("tenantId").GetGuid());
        Assert.Equal("selfhosted", data.GetProperty("sourceEdition").GetString());
        Assert.True(data.GetProperty("userIdClaimRequired").GetBoolean());
    }

    [Fact]
    public async Task Schema_PrincipalWithoutUserId_Returns403_WithAuthRequirementHeader()
    {
        using var client = ClientFor("User", includeSub: false);
        var response = await client.GetAsync("/api/v1/sync/schema");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("user-api-key", response.Headers.GetValues(SyncReceiveEndpoints.AuthRequirementHeader).Single());
    }

    // ---------- vault manifest ----------

    [Fact]
    public async Task VaultSchema_ReturnsManifest_WithItemCountAndCursor()
    {
        using var client = ClientFor("User");
        var response = await client.GetAsync($"/api/v1/sync/vaults/{VaultId}/schema");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await ReadJsonAsync(response)).RootElement.GetProperty("data");
        Assert.Equal(VaultId, data.GetProperty("remoteVaultId").GetGuid());
        Assert.Equal("Receive Vault", data.GetProperty("name").GetString());
        Assert.Equal(2, data.GetProperty("itemCount").GetInt32());
        Assert.False(string.IsNullOrEmpty(data.GetProperty("cursor").GetString()));
    }

    [Fact]
    public async Task VaultSchema_UnknownVault_Returns404Envelope()
    {
        using var client = ClientFor("User");
        var response = await client.GetAsync($"/api/v1/sync/vaults/{Guid.NewGuid()}/schema");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var doc = await ReadJsonAsync(response);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("Vault not found", doc.RootElement.GetProperty("error").GetString());
    }

    // ---------- export ----------

    [Fact]
    public async Task Export_ReturnsPackage_WithVaultItems()
    {
        using var client = ClientFor("User");
        var response = await client.GetAsync($"/api/v1/sync/vaults/{VaultId}/export");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await ReadJsonAsync(response)).RootElement.GetProperty("data");
        Assert.Equal("selfhosted", data.GetProperty("sourceEdition").GetString());
        var items = data.GetProperty("data").GetProperty("knowledgeItems");
        Assert.Equal(2, items.GetArrayLength());
    }

    [Fact]
    public async Task Export_PageBeyondItems_ReturnsEmptyKnowledgeList()
    {
        using var client = ClientFor("User");
        var response = await client.GetAsync($"/api/v1/sync/vaults/{VaultId}/export?page=2&pageSize=500");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await ReadJsonAsync(response)).RootElement.GetProperty("data");
        Assert.Equal(0, data.GetProperty("data").GetProperty("knowledgeItems").GetArrayLength());
    }

    [Fact]
    public async Task Export_PageSizeOne_PagesDeterministically()
    {
        using var client = ClientFor("User");
        var p1 = (await ReadJsonAsync(await client.GetAsync($"/api/v1/sync/vaults/{VaultId}/export?page=1&pageSize=1")))
            .RootElement.GetProperty("data").GetProperty("data").GetProperty("knowledgeItems");
        var p2 = (await ReadJsonAsync(await client.GetAsync($"/api/v1/sync/vaults/{VaultId}/export?page=2&pageSize=1")))
            .RootElement.GetProperty("data").GetProperty("data").GetProperty("knowledgeItems");

        Assert.Equal(1, p1.GetArrayLength());
        Assert.Equal(1, p2.GetArrayLength());
        Assert.NotEqual(p1[0].GetProperty("id").GetGuid(), p2[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task ItemExport_Found_ReturnsSingleItemPackage()
    {
        using var client = ClientFor("User");
        var response = await client.GetAsync($"/api/v1/sync/vaults/{VaultId}/items/{Knowledge1}/export");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = (await ReadJsonAsync(response)).RootElement
            .GetProperty("data").GetProperty("data").GetProperty("knowledgeItems");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(Knowledge1, items[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task ItemExport_NotInVault_Returns404()
    {
        using var client = ClientFor("User");
        var response = await client.GetAsync($"/api/v1/sync/vaults/{VaultId}/items/{Guid.NewGuid()}/export");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------- import ----------

    [Fact]
    public async Task Import_OneNewItem_Accepted1_AndPersisted()
    {
        using var client = ClientFor("User");
        var newId = Guid.NewGuid();
        var package = MakePackage(new PortableKnowledge
        {
            Id = newId, Title = "Pushed", Content = "from a peer",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            VaultIds = new List<Guid> { VaultId }, PrimaryVaultId = VaultId,
        });

        var response = await client.PostAsJsonAsync($"/api/v1/sync/vaults/{VaultId}/import", package);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await ReadJsonAsync(response)).RootElement.GetProperty("data");
        Assert.True(data.GetProperty("success").GetBoolean());
        Assert.Equal(1, data.GetProperty("accepted").GetInt32());
        Assert.Equal(0, data.GetProperty("skipped").GetInt32());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
        var stored = await db.KnowledgeItems.IgnoreQueryFilters().SingleAsync(k => k.Id == newId);
        Assert.Equal("Pushed", stored.Title);
        Assert.True(await db.KnowledgeVaults.AnyAsync(kv => kv.KnowledgeId == newId && kv.VaultId == VaultId));
    }

    [Fact]
    public async Task Import_MoreThanBulkItemCap_Returns400_AndPersistsNothing()
    {
        using var client = ClientFor("User");
        var items = Enumerable.Range(0, Knowz.SelfHosted.Application.Services.VaultSyncOrchestrator.BulkItemCap + 1)
            .Select(i => new PortableKnowledge
            {
                Id = Guid.NewGuid(), Title = $"Bulk {i}", Content = "x",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            }).ToArray();

        var response = await client.PostAsJsonAsync($"/api/v1/sync/vaults/{VaultId}/import", MakePackage(items));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var doc = await ReadJsonAsync(response);
        Assert.Contains("100-item", doc.RootElement.GetProperty("error").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
        Assert.Equal(2, await db.KnowledgeItems.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Import_UnreadableSchemaVersion_Returns400()
    {
        using var client = ClientFor("User");
        var package = MakePackage(new PortableKnowledge { Id = Guid.NewGuid(), Title = "v99", Content = "x", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        package.SchemaVersion = 99;

        var response = await client.PostAsJsonAsync($"/api/v1/sync/vaults/{VaultId}/import", package);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_UnknownVault_Returns404()
    {
        using var client = ClientFor("User");
        var package = MakePackage(new PortableKnowledge { Id = Guid.NewGuid(), Title = "x", Content = "x", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });

        var response = await client.PostAsJsonAsync($"/api/v1/sync/vaults/{Guid.NewGuid()}/import", package);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Import_Anonymous_Returns401()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync($"/api/v1/sync/vaults/{VaultId}/import", MakePackage());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------- register ----------

    [Fact]
    public async Task Register_Acknowledged_NotPersisted()
    {
        using var client = ClientFor("User");
        var response = await client.PostAsJsonAsync($"/api/v1/sync/vaults/{VaultId}/register", new { partnerName = "selfhosted-peer" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await ReadJsonAsync(response)).RootElement.GetProperty("data");
        Assert.Equal(VaultId, data.GetProperty("vaultId").GetGuid());
        Assert.Equal("selfhosted-peer", data.GetProperty("partnerName").GetString());
        Assert.False(data.GetProperty("persisted").GetBoolean());
    }

    // ---------- helpers ----------

    private static PortableExportPackage MakePackage(params PortableKnowledge[] items) => new()
    {
        SchemaVersion = CoreSchema.Version,
        SourceEdition = "selfhosted",
        SourceTenantId = Guid.NewGuid(),
        ExportedAt = DateTime.UtcNow,
        IsIncrementalSync = true,
        SyncCursor = DateTime.UtcNow,
        Metadata = new PortableExportMetadata { TotalKnowledgeItems = items.Length },
        Data = new PortableExportData { KnowledgeItems = items.ToList() },
    };

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private HttpClient ClientFor(string role, bool includeSub = true)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", BuildJwt(Guid.NewGuid(), TestTenantId, role, includeSub));
        return client;
    }

    private static string BuildJwt(Guid userId, Guid tenantId, string role, bool includeSub)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSecret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Name, "test-user"),
            new(ClaimTypes.Role, role),
            new("role", role),
            new("tenantId", tenantId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        if (includeSub)
            claims.Add(new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()));

        var token = new JwtSecurityToken(
            issuer: JwtIssuer,
            audience: JwtIssuer,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
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
}
