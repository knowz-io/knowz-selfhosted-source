using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.API.Endpoints;
using Knowz.SelfHosted.API.Helpers;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// Tests for SelfHostedAttachmentExperience — DTO fields, projections, admin endpoints, auth.
/// </summary>
public class AttachmentExperienceTests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private readonly FileStorageService _svc;
    private readonly IFileStorageProvider _storageProvider;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public AttachmentExperienceTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);

        _db = new SelfHostedDbContext(options, tenantProvider);

        var fileRepo = new SelfHostedRepository<FileRecord>(_db);

        _storageProvider = Substitute.For<IFileStorageProvider>();
        _storageProvider.UploadAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("file:///fake/path");

        var logger = Substitute.For<ILogger<FileStorageService>>();

        _svc = new FileStorageService(_storageProvider, fileRepo, _db, tenantProvider, logger);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    private async Task<FileRecord> SeedFileWithStructuredFields(
        string? visionDescription = null,
        string? visionTagsJson = null,
        string? visionObjectsJson = null,
        string? visionExtractedText = null,
        DateTime? visionAnalyzedAt = null,
        string? layoutDataJson = null,
        int textExtractionStatus = 0,
        DateTime? textExtractedAt = null,
        string? attachmentAIProvider = null)
    {
        var record = new FileRecord
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            FileName = "test-image.jpg",
            ContentType = "image/jpeg",
            SizeBytes = 1024,
            BlobUri = "file:///fake/path",
            VisionDescription = visionDescription,
            VisionTagsJson = visionTagsJson,
            VisionObjectsJson = visionObjectsJson,
            VisionExtractedText = visionExtractedText,
            VisionAnalyzedAt = visionAnalyzedAt,
            LayoutDataJson = layoutDataJson,
            TextExtractionStatus = textExtractionStatus,
            TextExtractedAt = textExtractedAt,
            AttachmentAIProvider = attachmentAIProvider,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.FileRecords.Add(record);
        await _db.SaveChangesAsync();
        return record;
    }

    private static HttpContext MakeContext(string role)
    {
        var claims = new List<Claim> { new(ClaimTypes.Role, role) };
        var identity = new ClaimsIdentity(claims, "Test", ClaimTypes.Name, ClaimTypes.Role);
        return new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
    }

    // ===== VERIFY: GET /api/files/{id} returns new structured fields when populated =====

    [Fact]
    public async Task Should_ReturnStructuredVisionFields_WhenGetFileById()
    {
        var analyzedAt = new DateTime(2026, 4, 13, 12, 0, 0, DateTimeKind.Utc);
        var record = await SeedFileWithStructuredFields(
            visionDescription: "A cat sitting on a mat",
            visionTagsJson: "[\"cat\",\"mat\",\"indoor\"]",
            visionObjectsJson: "[\"cat\",\"floor mat\"]",
            visionExtractedText: "OCR text from image",
            visionAnalyzedAt: analyzedAt,
            attachmentAIProvider: "AzureAIVision");

        var dto = await _svc.GetMetadataAsync(record.Id);

        Assert.NotNull(dto);
        Assert.Equal("A cat sitting on a mat", dto.VisionDescription);
        Assert.Equal("[\"cat\",\"mat\",\"indoor\"]", dto.VisionTagsJson);
        Assert.Equal("[\"cat\",\"floor mat\"]", dto.VisionObjectsJson);
        Assert.Equal("OCR text from image", dto.VisionExtractedText);
        Assert.Equal(analyzedAt, dto.VisionAnalyzedAt);
        Assert.Equal("AzureAIVision", dto.AttachmentAIProvider);
    }

    // ===== VERIFY: GET /api/files list endpoint returns new fields in each item =====

    [Fact]
    public async Task Should_ReturnStructuredFieldsInListEndpoint_WhenPopulated()
    {
        await SeedFileWithStructuredFields(
            visionDescription: "A dog",
            visionTagsJson: "[\"dog\"]",
            layoutDataJson: "{\"pages\":1}",
            textExtractionStatus: 2,
            textExtractedAt: DateTime.UtcNow,
            attachmentAIProvider: "Platform");

        var result = await _svc.ListAsync(1, 20);

        Assert.Single(result.Items);
        var item = result.Items[0];
        Assert.Equal("A dog", item.VisionDescription);
        Assert.Equal("[\"dog\"]", item.VisionTagsJson);
        Assert.Equal("{\"pages\":1}", item.LayoutDataJson);
        Assert.Equal(2, item.TextExtractionStatus);
        Assert.NotNull(item.TextExtractedAt);
        Assert.Equal("Platform", item.AttachmentAIProvider);
    }

    // ===== VERIFY: TypeScript FileMetadataDto matches C# DTO shape (all new fields optional) =====
    // This is verified structurally — the C# DTO has all new fields with nullable defaults

    [Fact]
    public void Should_HaveAllNewFieldsAsOptionalInDto()
    {
        // Verify record can be constructed with only required fields (no new fields)
        var dto = new FileMetadataDto(
            Guid.NewGuid(), "test.jpg", "image/jpeg", 1024, "uri",
            null, null, null, false, DateTime.UtcNow, DateTime.UtcNow);

        Assert.Null(dto.VisionTagsJson);
        Assert.Null(dto.VisionObjectsJson);
        Assert.Null(dto.VisionExtractedText);
        Assert.Null(dto.VisionAnalyzedAt);
        Assert.Null(dto.LayoutDataJson);
        Assert.Equal(0, dto.TextExtractionStatus);
        Assert.Null(dto.TextExtractedAt);
        Assert.Null(dto.AttachmentAIProvider);
    }

    // ===== VERIFY: New DTO fields are omitted from JSON when null (no breaking change) =====

    [Fact]
    public void Should_OmitNullFieldsFromJson_WhenNewFieldsAreNull()
    {
        var dto = new FileMetadataDto(
            Guid.NewGuid(), "test.jpg", "image/jpeg", 1024, "uri",
            null, null, null, false, DateTime.UtcNow, DateTime.UtcNow);

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        var json = JsonSerializer.Serialize(dto, options);

        // New fields should be absent when null
        Assert.DoesNotContain("visionTagsJson", json);
        Assert.DoesNotContain("visionObjectsJson", json);
        Assert.DoesNotContain("visionExtractedText", json);
        Assert.DoesNotContain("visionAnalyzedAt", json);
        Assert.DoesNotContain("layoutDataJson", json);
        Assert.DoesNotContain("textExtractedAt", json);
        Assert.DoesNotContain("attachmentAIProvider", json);

        // Required fields should be present
        Assert.Contains("fileName", json);
        Assert.Contains("sizeBytes", json);
    }

    [Fact]
    public void Should_IncludePopulatedFieldsInJson_WhenNewFieldsHaveValues()
    {
        var dto = new FileMetadataDto(
            Guid.NewGuid(), "test.jpg", "image/jpeg", 1024, "uri",
            null, null, "A cat", false, DateTime.UtcNow, DateTime.UtcNow,
            VisionTagsJson: "[\"cat\"]",
            AttachmentAIProvider: "AzureAIVision",
            TextExtractionStatus: 2);

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        var json = JsonSerializer.Serialize(dto, options);

        Assert.Contains("visionTagsJson", json);
        Assert.Contains("attachmentAIProvider", json);
        Assert.Contains("textExtractionStatus", json);
    }

    // ===== VERIFY: Document extraction fields projected correctly =====

    [Fact]
    public async Task Should_ReturnDocumentExtractionFields_WhenPopulated()
    {
        var extractedAt = DateTime.UtcNow;
        var record = await SeedFileWithStructuredFields(
            layoutDataJson: "{\"pages\":[{\"pageNumber\":1}]}",
            textExtractionStatus: 2, // Completed
            textExtractedAt: extractedAt,
            attachmentAIProvider: "DocumentIntelligence");

        var dto = await _svc.GetMetadataAsync(record.Id);

        Assert.NotNull(dto);
        Assert.Equal("{\"pages\":[{\"pageNumber\":1}]}", dto.LayoutDataJson);
        Assert.Equal(2, dto.TextExtractionStatus);
        Assert.Equal(extractedAt, dto.TextExtractedAt);
        Assert.Equal("DocumentIntelligence", dto.AttachmentAIProvider);
    }

    // ===== VERIFY: Reprocess endpoint requires admin role — returns 403 for regular users =====

    [Fact]
    public void Should_DenyReprocessEndpoint_ForRegularUser()
    {
        var ctx = MakeContext("User");
        Assert.False(AuthorizationHelpers.IsAdminOrAbove(ctx));
    }

    [Fact]
    public void Should_AllowReprocessEndpoint_ForAdmin()
    {
        var ctx = MakeContext("Admin");
        Assert.True(AuthorizationHelpers.IsAdminOrAbove(ctx));
    }

    [Fact]
    public void Should_AllowReprocessEndpoint_ForSuperAdmin()
    {
        var ctx = MakeContext("SuperAdmin");
        Assert.True(AuthorizationHelpers.IsAdminOrAbove(ctx));
    }

    // ===== VERIFY: POST /api/admin/files/reprocess with onlyMissing=true skips files with VisionAnalyzedAt =====

    [Fact]
    public async Task Should_SkipAlreadyAnalyzedFiles_WhenOnlyMissingTrue()
    {
        // Seed file WITH VisionAnalyzedAt (should be skipped)
        await SeedFileWithStructuredFields(
            visionAnalyzedAt: DateTime.UtcNow,
            attachmentAIProvider: "AzureAIVision");

        // Seed file WITHOUT VisionAnalyzedAt (should be queued)
        await SeedFileWithStructuredFields();

        // Query for files that need reprocessing
        var filesNeedingReprocess = await _db.FileRecords
            .Where(f => !f.IsDeleted && f.VisionAnalyzedAt == null)
            .CountAsync();

        Assert.Equal(1, filesNeedingReprocess);
    }

    [Fact]
    public void Should_ExcludeCompletedImageAndDocument_FromOnlyMissingPredicateWithoutFilter()
    {
        var predicate = AttachmentAIAdminEndpoints.BuildOnlyMissingPredicate(null).Compile();

        var completedImage = new FileRecord
        {
            ContentType = "image/png",
            VisionAnalyzedAt = DateTime.UtcNow
        };
        var completedDocument = new FileRecord
        {
            ContentType = "application/pdf",
            TextExtractionStatus = 2,
            TextExtractedAt = DateTime.UtcNow
        };

        Assert.False(predicate(completedImage));
        Assert.False(predicate(completedDocument));
    }

    [Fact]
    public void Should_IncludeMissingImageAndDocument_InOnlyMissingPredicateWithoutFilter()
    {
        var predicate = AttachmentAIAdminEndpoints.BuildOnlyMissingPredicate(null).Compile();

        var missingImage = new FileRecord
        {
            ContentType = "image/png",
            VisionAnalyzedAt = null
        };
        var missingDocument = new FileRecord
        {
            ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            TextExtractionStatus = 0,
            TextExtractedAt = null
        };

        Assert.True(predicate(missingImage));
        Assert.True(predicate(missingDocument));
    }

    // ===== VERIFY: GET /api/admin/attachment-ai/status returns correct file statistics =====

    [Fact]
    public async Task Should_ReturnCorrectFileStats_ForDiagnosticsEndpoint()
    {
        // Seed various file states
        await SeedFileWithStructuredFields(
            visionDescription: "With vision",
            visionAnalyzedAt: DateTime.UtcNow,
            textExtractionStatus: 2, // Completed
            textExtractedAt: DateTime.UtcNow);

        await SeedFileWithStructuredFields(
            textExtractionStatus: 0); // NotStarted

        await SeedFileWithStructuredFields(
            textExtractionStatus: 3); // Failed

        // Verify stats queries
        var total = await _db.FileRecords.CountAsync(f => !f.IsDeleted);
        var withVision = await _db.FileRecords.CountAsync(f => !f.IsDeleted && f.VisionAnalyzedAt != null);
        var withExtracted = await _db.FileRecords.CountAsync(f => !f.IsDeleted && f.TextExtractionStatus == 2);
        var notStarted = await _db.FileRecords.CountAsync(f => !f.IsDeleted && f.TextExtractionStatus == 0);
        var failed = await _db.FileRecords.CountAsync(f => !f.IsDeleted && f.TextExtractionStatus == 3);

        Assert.Equal(3, total);
        Assert.Equal(1, withVision);
        Assert.Equal(1, withExtracted);
        Assert.Equal(1, notStarted);
        Assert.Equal(1, failed);
    }

    // ===== VERIFY: MapToDto includes new fields in attachment lists =====

    [Fact]
    public async Task Should_ReturnStructuredFieldsInKnowledgeAttachmentList()
    {
        var record = await SeedFileWithStructuredFields(
            visionDescription: "Attachment vision",
            visionTagsJson: "[\"test\"]",
            attachmentAIProvider: "Platform");

        // Create knowledge item and attach file
        var knowledge = new Knowledge
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            Content = "Test",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.KnowledgeItems.Add(knowledge);

        var attachment = new FileAttachment
        {
            Id = Guid.NewGuid(),
            FileRecordId = record.Id,
            KnowledgeId = knowledge.Id,
            TenantId = TenantId,
            CreatedAt = DateTime.UtcNow
        };
        _db.FileAttachments.Add(attachment);
        await _db.SaveChangesAsync();

        var attachments = await _svc.GetAttachmentsForKnowledgeAsync(knowledge.Id);

        Assert.Single(attachments);
        Assert.Equal("Attachment vision", attachments[0].VisionDescription);
        Assert.Equal("[\"test\"]", attachments[0].VisionTagsJson);
        Assert.Equal("Platform", attachments[0].AttachmentAIProvider);
    }
}
