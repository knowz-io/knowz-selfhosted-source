using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// VERIFY-M1: authenticated User can open mothership connect (GET /connection is
/// 404 empty-state, not 403). Admin extras remain available (Admin also 404).
/// </summary>
public class SyncConnectionAuthorizationTests : IAsyncLifetime
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private const string JwtSecret = "test-jwt-secret-must-be-at-least-32-characters-long!";
    private const string JwtIssuer = "knowz-selfhosted";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dbName;
    private readonly IPlatformConnectionService _connectionService = Substitute.For<IPlatformConnectionService>();

    public SyncConnectionAuthorizationTests()
    {
        _dbName = $"SyncConnectionAuthorizationTests-{Guid.NewGuid():N}";
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
                    services.AddSingleton(_connectionService);
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

                    services.AddSingleton(_ =>
                    {
                        var optionsBuilder = new DbContextOptionsBuilder<SelfHostedDbContext>();
                        optionsBuilder.UseInMemoryDatabase(_dbName);
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

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task GetConnection_UserRole_Returns404_Not403()
    {
        using var client = ClientFor("User");
        var response = await client.GetAsync("/api/v1/sync/connection");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetConnection_AdminRole_Returns404_Not403()
    {
        using var client = ClientFor("Admin");
        var response = await client.GetAsync("/api/v1/sync/connection");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetConnection_Anonymous_Returns401()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/sync/connection");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/v1/sync/connections", "http://localhost")]
    [InlineData("PUT", "/api/v1/sync/connection", "http://LOCALHOST:80/")]
    [InlineData("PUT", "/api/v1/sync/connections/00000000-0000-0000-0000-000000000003", "http://localhost/another/path")]
    [InlineData("POST", "/api/v1/sync/connection/test-candidate", "http://localhost/")]
    [InlineData("POST", "/api/v1/sync/connection/test-candidate", "http://LOCALHOST:80/api/v1/")]
    public async Task ConnectionWriteOrCandidate_SelfPoint_Returns400BeforeServiceCall(string method, string path, string destination)
    {
        using var client = ClientFor("User");
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = JsonContent.Create(new { platformApiUrl = destination, apiKey = "kz_testtenant1234" })
        };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("itself", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_connectionService.ReceivedCalls());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TestStoredConnection_SelfPoint_Returns400BeforeOutboundTest(bool multiPeer)
    {
        var connectionId = Guid.NewGuid();
        var dto = new PlatformConnectionDto(connectionId, "http://LOCALHOST:80/path/", null, true,
            "kz_****1234", null, null, PlatformConnectionTestStatus.Untested, null, DateTime.UtcNow);
        _connectionService.GetAsync(Arg.Any<CancellationToken>()).Returns(dto);
        _connectionService.GetByIdAsync(connectionId, Arg.Any<CancellationToken>()).Returns(dto);
        using var client = ClientFor("User");
        var path = multiPeer ? $"/api/v1/sync/connections/{connectionId}/test" : "/api/v1/sync/connection/test";

        var response = await client.PostAsync(path, null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("itself", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        await _connectionService.DidNotReceiveWithAnyArgs().TestAsync(default, default, default);
        await _connectionService.DidNotReceiveWithAnyArgs().TestByIdAsync(default, default, default, default);
    }

    [Theory]
    [InlineData("http://peer.example.com")]
    [InlineData("http://localhost:5001")]
    public async Task TestCandidate_DifferentHostOrPort_StillUsesConnectionValidation(string destination)
    {
        var result = new PlatformConnectionTestResult(PlatformConnectionTestStatus.Ok, null, Guid.NewGuid(), "1");
        _connectionService.TestCandidateAsync(destination, "kz_testtenant1234", Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(result);
        using var client = ClientFor("User");

        var response = await client.PostAsJsonAsync("/api/v1/sync/connection/test-candidate",
            new { platformApiUrl = destination, apiKey = "kz_testtenant1234" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _connectionService.Received(1).TestCandidateAsync(destination, "kz_testtenant1234",
            Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    private HttpClient ClientFor(string role)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", BuildJwt(Guid.NewGuid(), TestTenantId, role));
        return client;
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
