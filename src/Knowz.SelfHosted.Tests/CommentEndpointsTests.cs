using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// HTTP endpoint-level integration tests for DELETE /api/v1/comments/{id}?deleteFiles={bool}.
/// VERIFY-16: validates minimal-API query-param binding which service-layer tests cannot catch.
/// WorkGroupID: kc-fix-attach-delete-transcript-20260411-080000 — FEAT_CommentDeleteAttachmentChoice
/// </summary>
public class CommentEndpointsTests : IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly string _apiKey = "test-api-key";
    private readonly string _dbName = $"CommentEndpointsTests-{Guid.NewGuid():N}";
    private readonly IFileStorageProvider _mockStorage;

    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public CommentEndpointsTests()
    {
        _mockStorage = Substitute.For<IFileStorageProvider>();
        _mockStorage.DeleteAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _mockStorage.ExistsAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:McpDb", "Server=(localdb);Database=fake;");
                builder.UseSetting("SelfHosted:ApiKey", _apiKey);
                builder.UseSetting("SelfHosted:JwtSecret", "test-jwt-secret-must-be-at-least-32-characters-long!");
                builder.UseSetting("SelfHosted:EnableSwagger", "true");
                builder.UseSetting("Database:AutoMigrate", "false");
                builder.UseSetting("AzureKeyVault:Enabled", "false");

                builder.ConfigureServices(services =>
                {
                    // Remove all DbContext registrations to avoid SQL Server + InMemory conflict
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

                    // InMemory DbContextOptions
                    services.AddSingleton(sp =>
                    {
                        var optionsBuilder = new DbContextOptionsBuilder<SelfHostedDbContext>();
                        optionsBuilder.UseInMemoryDatabase(_dbName);
                        return optionsBuilder.Options;
                    });

                    // Scoped DbContext with tenant provider
                    services.AddScoped<SelfHostedDbContext>(sp =>
                    {
                        var options = sp.GetRequiredService<DbContextOptions<SelfHostedDbContext>>();
                        return new SelfHostedDbContext(options, tenantProvider);
                    });

                    // Factory registration
                    services.AddSingleton<IDbContextFactory<SelfHostedDbContext>>(sp =>
                    {
                        var options = sp.GetRequiredService<DbContextOptions<SelfHostedDbContext>>();
                        return new TestDbContextFactory(options, tenantProvider);
                    });

                    // Replace IFileStorageProvider with mock to verify blob delete calls
                    var storageDescriptors = services
                        .Where(d => d.ServiceType == typeof(IFileStorageProvider))
                        .ToList();
                    foreach (var d in storageDescriptors)
                        services.Remove(d);
                    services.AddSingleton(_mockStorage);
                });
            });

        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Api-Key", _apiKey);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _client.Dispose();
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
    /// Resolve a scoped DbContext from the test server's service provider to seed data.
    /// </summary>
    private SelfHostedDbContext GetDb()
    {
        var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<SelfHostedDbContext>();
    }

    private async Task<(Guid knowledgeId, Guid commentId, Guid fileRecordId)> SeedCommentWithAttachment(
        string fileName = "test-file.pdf")
    {
        using var db = GetDb();

        var knowledge = new Knowledge
        {
            TenantId = TestTenantId,
            Title = "Test Knowledge",
            Content = "Test content"
        };
        db.KnowledgeItems.Add(knowledge);

        var comment = new KnowledgeComment
        {
            TenantId = TestTenantId,
            KnowledgeId = knowledge.Id,
            AuthorName = "Test Author",
            Body = "Test comment body"
        };
        db.Comments.Add(comment);

        var fileRecord = new FileRecord
        {
            TenantId = TestTenantId,
            FileName = fileName,
            ContentType = "application/pdf"
        };
        db.FileRecords.Add(fileRecord);

        var attachment = new FileAttachment
        {
            FileRecordId = fileRecord.Id,
            CommentId = comment.Id,
            TenantId = TestTenantId
        };
        db.FileAttachments.Add(attachment);

        await db.SaveChangesAsync();

        return (knowledge.Id, comment.Id, fileRecord.Id);
    }

    // =============================================
    // VERIFY-16(a): deleteFiles=false (default) — HTTP endpoint binding
    // =============================================

    [Fact]
    public async Task DeleteComment_WithDeleteFilesFalse_Returns200_WithFilesPreservedCount()
    {
        var (_, commentId, fileRecordId) = await SeedCommentWithAttachment("preserve-me.pdf");

        // DELETE without deleteFiles query param — defaults to false
        var response = await _client.DeleteAsync($"/api/v1/comments/{commentId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("filesPreserved").GetInt32());
        Assert.Equal(0, body.GetProperty("filesDeleted").GetInt32());

        // Blob storage must NOT have been called for delete
        await _mockStorage.DidNotReceive().DeleteAsync(
            Arg.Any<Guid>(), fileRecordId, Arg.Any<CancellationToken>());
    }

    // =============================================
    // VERIFY-16(b): deleteFiles=true, sole-owner file — HTTP endpoint binding
    // =============================================

    [Fact]
    public async Task DeleteComment_WithDeleteFilesTrue_SoleOwnerFile_Returns200_WithFilesDeletedCount()
    {
        var (_, commentId, fileRecordId) = await SeedCommentWithAttachment("sole-owner.pdf");

        // DELETE with explicit deleteFiles=true
        var response = await _client.DeleteAsync($"/api/v1/comments/{commentId}?deleteFiles=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("filesPreserved").GetInt32());
        Assert.Equal(1, body.GetProperty("filesDeleted").GetInt32());
        Assert.Contains("sole-owner.pdf",
            body.GetProperty("deletedFileNames").EnumerateArray().Select(e => e.GetString()!));

        // Blob delete must have been called
        await _mockStorage.Received(1).DeleteAsync(
            TestTenantId, fileRecordId, Arg.Any<CancellationToken>());
    }

    // =============================================
    // VERIFY-16(c): deleteFiles=true, shared file — HTTP endpoint binding
    // =============================================

    [Fact]
    public async Task DeleteComment_WithDeleteFilesTrue_SharedFile_Returns200_WithFilesPreservedCount()
    {
        // Seed comment with attachment, then also attach the same file to a knowledge item
        var (knowledgeId, commentId, fileRecordId) = await SeedCommentWithAttachment("shared.pdf");

        // Add a second FileAttachment referencing the same FileRecord but attached to the knowledge item
        using (var db = GetDb())
        {
            var knowledgeAttachment = new FileAttachment
            {
                FileRecordId = fileRecordId,
                KnowledgeId = knowledgeId,
                TenantId = TestTenantId
            };
            db.FileAttachments.Add(knowledgeAttachment);
            await db.SaveChangesAsync();
        }

        // DELETE with deleteFiles=true — file is shared, so it must be preserved
        var response = await _client.DeleteAsync($"/api/v1/comments/{commentId}?deleteFiles=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("filesPreserved").GetInt32());
        Assert.Equal(0, body.GetProperty("filesDeleted").GetInt32());
        Assert.Contains("shared.pdf",
            body.GetProperty("preservedFileNames").EnumerateArray().Select(e => e.GetString()!));

        // Blob delete must NOT have been called — file is shared
        await _mockStorage.DidNotReceive().DeleteAsync(
            Arg.Any<Guid>(), fileRecordId, Arg.Any<CancellationToken>());
    }
}
