using System.Net.Http.Json;
using System.Text.Json;
using Knowz.Core.Interfaces;
using Knowz.Core.Models;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// SH_OfflineAiHonesty VERIFY-O1..O6 — the unconfigured-AI state is a typed
/// signal on the wire, never prose rendered as a successful model answer.
/// </summary>
public class OfflineAiHonestyTests
{
    // ---- VERIFY-O1 -------------------------------------------------------
    [Fact]
    public async Task Should_ReturnEmptyAnswer_WhenNoOpAnswersQuestion()
    {
        var svc = new NoOpOpenAIService(Substitute.For<ILogger<NoOpOpenAIService>>());

        var result = await svc.AnswerQuestionAsync("anything", new List<SearchResultItem>());

        Assert.Equal(string.Empty, result.Answer);
        Assert.Equal(0, result.Confidence);
        Assert.Empty(result.SourceKnowledgeIds);
    }

    // ---- VERIFY-O2 -------------------------------------------------------
    [Fact]
    public async Task Should_YieldZeroTokens_WhenNoOpStreamsAnswer()
    {
        var svc = new NoOpOpenAIService(Substitute.For<ILogger<NoOpOpenAIService>>());

        var tokens = new List<string>();
        await foreach (var t in svc.AnswerQuestionStreamingAsync("anything", new List<SearchResultItem>()))
            tokens.Add(t);

        Assert.Empty(tokens);
    }

    [Fact]
    public void Should_ThrowProductCopy_WhenNoOpAmends()
    {
        var svc = new NoOpOpenAIService(Substitute.For<ILogger<NoOpOpenAIService>>());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            svc.ApplyContentUpdateAsync("existing", "instruction").GetAwaiter().GetResult());

        Assert.Equal("AI is not configured on this instance.", ex.Message);
        Assert.DoesNotContain("AzureOpenAI", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- VERIFY-O6 (configured provider => flag false) --------------------
    [Fact]
    public async Task Should_ReportAiAvailable_WhenRealProviderInjected()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        using var db = new SelfHostedDbContext(options, tenantProvider);

        var openAi = Substitute.For<IOpenAIService>();
        openAi.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f });
        openAi.AnswerQuestionAsync(Arg.Any<string>(), Arg.Any<List<SearchResultItem>>(),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AnswerResponse { Answer = "real", SourceKnowledgeIds = new List<Guid>(), Confidence = 0.9 });

        var facade = new SearchFacade(db, Substitute.For<ISearchService>(), openAi,
            Substitute.For<IStreamingOpenAIService>(), Substitute.For<ILogger<SearchFacade>>());

        var ask = await facade.AskQuestionAsync("q", null, false, CancellationToken.None);
        Assert.False(ask.AiUnavailable);
        Assert.Equal("real", ask.Answer);
    }

    // ---- VERIFY-O3/O4/O5 (HTTP, default NoOp tier) -----------------------
    private static WebApplicationFactory<Program> CreateFactory(string dbName, string apiKey)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:McpDb", "Server=(localdb);Database=fake;");
            builder.UseSetting("SelfHosted:ApiKey", apiKey);
            builder.UseSetting("SelfHosted:JwtSecret", "test-jwt-secret-must-be-at-least-32-characters-long!");
            builder.UseSetting("Database:AutoMigrate", "false");
            builder.UseSetting("AzureKeyVault:Enabled", "false");
            builder.UseSetting("SelfHosted:RateLimiting:Enabled", "false");

            builder.ConfigureServices(services =>
            {
                var toRemove = services.Where(d =>
                        d.ServiceType == typeof(DbContextOptions<SelfHostedDbContext>) ||
                        d.ServiceType == typeof(DbContextOptions) ||
                        d.ServiceType == typeof(SelfHostedDbContext) ||
                        d.ServiceType == typeof(IDbContextFactory<SelfHostedDbContext>) ||
                        (d.ServiceType.IsGenericType &&
                         d.ServiceType.GetGenericTypeDefinition() == typeof(DbContextOptions<>) &&
                         d.ServiceType.GenericTypeArguments[0] == typeof(SelfHostedDbContext)))
                    .ToList();
                foreach (var d in toRemove) services.Remove(d);

                var tenantProvider = Substitute.For<ITenantProvider>();
                tenantProvider.TenantId.Returns(Guid.Parse("00000000-0000-0000-0000-000000000001"));

                services.AddSingleton(sp =>
                {
                    var ob = new DbContextOptionsBuilder<SelfHostedDbContext>();
                    ob.UseInMemoryDatabase(dbName);
                    return ob.Options;
                });
                services.AddScoped(sp => new SelfHostedDbContext(
                    sp.GetRequiredService<DbContextOptions<SelfHostedDbContext>>(), tenantProvider));
                services.AddSingleton<IDbContextFactory<SelfHostedDbContext>>(sp =>
                    new OfflineAiTestDbContextFactory(
                        sp.GetRequiredService<DbContextOptions<SelfHostedDbContext>>(), tenantProvider));
            });
        });

    private sealed class OfflineAiTestDbContextFactory : IDbContextFactory<SelfHostedDbContext>
    {
        private readonly DbContextOptions<SelfHostedDbContext> _o;
        private readonly ITenantProvider _t;
        public OfflineAiTestDbContextFactory(DbContextOptions<SelfHostedDbContext> o, ITenantProvider t) { _o = o; _t = t; }
        public SelfHostedDbContext CreateDbContext() => new(_o, _t);
    }

    [Fact]
    public async Task Should_ReturnAiUnavailable_WhenChatPostedWithNoProvider()
    {
        const string key = "test-api-key";
        using var factory = CreateFactory($"OfflineAi-chat-{Guid.NewGuid():N}", key);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);

        var resp = await client.PostAsJsonAsync("/api/v1/chat", new { question = "hello" });
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.True(root.GetProperty("aiUnavailable").GetBoolean());
        Assert.Equal(string.Empty, root.GetProperty("answer").GetString());
        Assert.Equal(0, root.GetProperty("confidence").GetDouble());
        Assert.Empty(root.GetProperty("sources").EnumerateArray());
    }

    [Fact]
    public async Task Should_ReturnAiUnavailable_WhenAskPostedWithNoProvider()
    {
        const string key = "test-api-key";
        using var factory = CreateFactory($"OfflineAi-ask-{Guid.NewGuid():N}", key);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);

        var resp = await client.PostAsJsonAsync("/api/v1/ask", new { question = "hello" });
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.True(root.GetProperty("aiUnavailable").GetBoolean());
        Assert.Equal(string.Empty, root.GetProperty("answer").GetString());
        Assert.Equal(0, root.GetProperty("confidence").GetDouble());
    }

    [Theory]
    [InlineData("/api/v1/chat/stream")]
    [InlineData("/api/v1/ask/stream")]
    public async Task Should_CarryAiUnavailableInSourcesPreamble_WhenStreamingWithNoProvider(string route)
    {
        const string key = "test-api-key";
        using var factory = CreateFactory($"OfflineAi-stream-{Guid.NewGuid():N}", key);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);

        var resp = await client.PostAsJsonAsync(route, new { question = "hello" });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();

        var events = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Replace("data: ", string.Empty).Trim())
            .Where(l => l.StartsWith("{"))
            .ToList();

        Assert.NotEmpty(events);
        using var first = JsonDocument.Parse(events[0]);
        Assert.Equal("sources", first.RootElement.GetProperty("type").GetString());
        Assert.True(first.RootElement.GetProperty("aiUnavailable").GetBoolean());

        // no token events, but the terminal done event still arrives
        Assert.DoesNotContain(events, e => e.Contains("\"token\""));
        Assert.Contains(events, e => e.Contains("\"done\""));
    }
}
