using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// Tests for FileStorageService attachment content extraction and re-enrichment triggers.
/// Covers VERIFY_FUNC_09-11, VERIFY_INT_01-05, VERIFY_ERR_01-04.
/// </summary>
public class FileStorageAttachmentExtractionTests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private readonly IFileStorageProvider _storageProvider;
    private readonly IFileContentExtractor _contentExtractor;
    private readonly IEnrichmentOutboxWriter _enrichmentWriter;
    private readonly FileStorageService _svc;
    private readonly ISelfHostedRepository<FileRecord> _fileRepo;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public FileStorageAttachmentExtractionTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);

        _db = new SelfHostedDbContext(options, tenantProvider);
        _fileRepo = new SelfHostedRepository<FileRecord>(_db);

        _storageProvider = Substitute.For<IFileStorageProvider>();
        _storageProvider.UploadAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => $"file:///fake/{ci.ArgAt<Guid>(0):N}/{ci.ArgAt<Guid>(1):N}.bin");

        _storageProvider.DownloadAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                Stream stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("extracted text content"));
                return (stream, "text/plain", "test.txt");
            });

        _storageProvider.DeleteAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _contentExtractor = Substitute.For<IFileContentExtractor>();
        _contentExtractor.CanExtract("text/plain").Returns(true);
        _contentExtractor.CanExtract("application/json").Returns(true);
        _contentExtractor.CanExtract("application/pdf").Returns(false);
        _contentExtractor.CanExtract("image/png").Returns(false);
        _contentExtractor.CanExtract(Arg.Is<string?>(s => s == null)).Returns(false);
        _contentExtractor.ExtractAsync(Arg.Any<FileRecord>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new FileExtractionResult(true, ExtractedText: "extracted text content"));

        _enrichmentWriter = Substitute.For<IEnrichmentOutboxWriter>();

        var logger = Substitute.For<ILogger<FileStorageService>>();

        _svc = new FileStorageService(
            _storageProvider, _fileRepo, _db, tenantProvider, logger,
            _contentExtractor, _enrichmentWriter);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    // --- Helpers ---

    private static MemoryStream CreateTestStream(string content = "test file content")
        => new(System.Text.Encoding.UTF8.GetBytes(content));

    private async Task<FileRecord> SeedFileRecord(
        string fileName = "test.txt", string contentType = "text/plain", string? extractedText = null)
    {
        var record = new FileRecord
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = 100,
            BlobUri = $"file:///fake/{TenantId:N}/{Guid.NewGuid():N}.txt",
            ExtractedText = extractedText,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.FileRecords.Add(record);
        await _db.SaveChangesAsync();
        return record;
    }

    private async Task<Knowledge> SeedKnowledge(string content = "Test knowledge")
    {
        var knowledge = new Knowledge
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            Content = content,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.KnowledgeItems.Add(knowledge);
        await _db.SaveChangesAsync();
        return knowledge;
    }

    private async Task<KnowledgeComment> SeedComment(Guid knowledgeId, string body = "Test comment")
    {
        var comment = new KnowledgeComment
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            KnowledgeId = knowledgeId,
            Body = body,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Comments.Add(comment);
        await _db.SaveChangesAsync();
        return comment;
    }

    // =============================================
    // Upload extraction (VERIFY_FUNC_09)
    // =============================================

    [Fact]
    public async Task Upload_ExtractsText_ForSupportedContentType()
    {
        // VERIFY_FUNC_09
        using var stream = CreateTestStream("hello world");

        var result = await _svc.UploadAsync(stream, "notes.txt", "text/plain");

        Assert.True(result.Success);

        // Verify ExtractedText was populated on the DB record
        var saved = await _db.FileRecords.FindAsync(result.FileRecordId);
        Assert.NotNull(saved);
        Assert.Equal("extracted text content", saved.ExtractedText);
    }

    [Fact]
    public async Task Upload_SkipsExtraction_ForUnsupportedContentType()
    {
        using var stream = CreateTestStream("fake pdf");

        var result = await _svc.UploadAsync(stream, "doc.pdf", "application/pdf");

        Assert.True(result.Success);
        var saved = await _db.FileRecords.FindAsync(result.FileRecordId);
        Assert.Null(saved!.ExtractedText);
    }

    [Fact]
    public async Task Upload_ContinuesOnExtractionFailure()
    {
        // VERIFY_ERR_01 (upload variant)
        _contentExtractor.ExtractAsync(Arg.Any<FileRecord>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<FileExtractionResult>(new IOException("disk error")));

        using var stream = CreateTestStream("content");

        var result = await _svc.UploadAsync(stream, "notes.txt", "text/plain");

        Assert.True(result.Success); // Upload still succeeds
        var saved = await _db.FileRecords.FindAsync(result.FileRecordId);
        Assert.Null(saved!.ExtractedText);
    }

    // =============================================
    // AttachToKnowledge extraction (VERIFY_FUNC_10, VERIFY_FUNC_11)
    // =============================================

    [Fact]
    public async Task AttachToKnowledge_ExtractsText_WhenNotAlreadyExtracted()
    {
        // VERIFY_FUNC_10
        var fileRecord = await SeedFileRecord(extractedText: null);
        var knowledge = await SeedKnowledge();

        await _svc.AttachToKnowledgeAsync(fileRecord.Id, knowledge.Id);

        // Should have downloaded + extracted
        await _storageProvider.Received(1).DownloadAsync(
            fileRecord.TenantId, fileRecord.Id, Arg.Any<CancellationToken>());
        await _contentExtractor.Received(1).ExtractAsync(
            Arg.Any<FileRecord>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AttachToKnowledge_SkipsExtraction_WhenAlreadyExtracted()
    {
        // VERIFY_FUNC_11 (idempotent)
        var fileRecord = await SeedFileRecord(extractedText: "already extracted");
        var knowledge = await SeedKnowledge();

        await _svc.AttachToKnowledgeAsync(fileRecord.Id, knowledge.Id);

        // Should NOT have attempted to download or extract
        await _storageProvider.DidNotReceive().DownloadAsync(
            fileRecord.TenantId, fileRecord.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AttachToKnowledge_CreatesAttachment_EvenWhenExtractionFails()
    {
        // VERIFY_ERR_01
        _contentExtractor.ExtractAsync(Arg.Any<FileRecord>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<FileExtractionResult>(new IOException("disk error")));

        var fileRecord = await SeedFileRecord(extractedText: null);
        var knowledge = await SeedKnowledge();

        var result = await _svc.AttachToKnowledgeAsync(fileRecord.Id, knowledge.Id);

        Assert.NotEqual(Guid.Empty, result.Id);
        var attachment = await _db.FileAttachments.FirstOrDefaultAsync(
            fa => fa.FileRecordId == fileRecord.Id && fa.KnowledgeId == knowledge.Id);
        Assert.NotNull(attachment);
    }

    // =============================================
    // Re-enrichment triggers (VERIFY_INT_01-04)
    // =============================================

    [Fact]
    public async Task AttachToKnowledge_EnqueuesReEnrichment()
    {
        // VERIFY_INT_01
        var fileRecord = await SeedFileRecord(extractedText: "preextracted");
        var knowledge = await SeedKnowledge();

        await _svc.AttachToKnowledgeAsync(fileRecord.Id, knowledge.Id);

        await _enrichmentWriter.Received(1).EnqueueAsync(
            knowledge.Id, TenantId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AttachToComment_EnqueuesReEnrichmentOfParentKnowledge()
    {
        // VERIFY_INT_02
        var knowledge = await SeedKnowledge();
        var comment = await SeedComment(knowledge.Id);
        var fileRecord = await SeedFileRecord(extractedText: "preextracted");

        await _svc.AttachToCommentAsync(fileRecord.Id, comment.Id);

        await _enrichmentWriter.Received(1).EnqueueAsync(
            knowledge.Id, TenantId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DetachFromKnowledge_EnqueuesReEnrichment()
    {
        // VERIFY_INT_03
        var fileRecord = await SeedFileRecord(extractedText: "preextracted");
        var knowledge = await SeedKnowledge();
        await _svc.AttachToKnowledgeAsync(fileRecord.Id, knowledge.Id);
        _enrichmentWriter.ClearReceivedCalls();

        await _svc.DetachFromKnowledgeAsync(fileRecord.Id, knowledge.Id, deleteFiles: null);

        await _enrichmentWriter.Received(1).EnqueueAsync(
            knowledge.Id, TenantId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DetachFromComment_EnqueuesReEnrichmentOfParentKnowledge()
    {
        // VERIFY_INT_04
        var knowledge = await SeedKnowledge();
        var comment = await SeedComment(knowledge.Id);
        var fileRecord = await SeedFileRecord(extractedText: "preextracted");
        await _svc.AttachToCommentAsync(fileRecord.Id, comment.Id);
        _enrichmentWriter.ClearReceivedCalls();

        await _svc.DetachFromCommentAsync(fileRecord.Id, comment.Id);

        await _enrichmentWriter.Received(1).EnqueueAsync(
            knowledge.Id, TenantId, Arg.Any<CancellationToken>());
    }

    // =============================================
    // Error handling (VERIFY_ERR_01-04)
    // =============================================

    [Fact]
    public async Task AttachToKnowledge_ContinuesOnDownloadFailure()
    {
        // VERIFY_ERR_03
        _storageProvider.DownloadAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<(Stream, string, string)>(_ => throw new FileNotFoundException("not found"));

        var fileRecord = await SeedFileRecord(extractedText: null);
        var knowledge = await SeedKnowledge();

        var result = await _svc.AttachToKnowledgeAsync(fileRecord.Id, knowledge.Id);

        // Attachment created even though extraction failed
        Assert.NotEqual(Guid.Empty, result.Id);
    }

    [Fact]
    public async Task AttachToKnowledge_ContinuesOnEnrichmentEnqueueFailure()
    {
        // VERIFY_ERR_04
        _enrichmentWriter.EnqueueAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new Exception("service bus error")));

        var fileRecord = await SeedFileRecord(extractedText: "preextracted");
        var knowledge = await SeedKnowledge();

        var result = await _svc.AttachToKnowledgeAsync(fileRecord.Id, knowledge.Id);

        // Attachment was still created
        Assert.NotEqual(Guid.Empty, result.Id);
    }

    // =============================================
    // Backward compatibility (VERIFY_INT_05)
    // =============================================

    [Fact]
    public void Constructor_AcceptsNullOptionalParams()
    {
        // VERIFY_INT_05 - FileStorageService works without extraction or enrichment
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var tp = Substitute.For<ITenantProvider>();
        tp.TenantId.Returns(Guid.NewGuid());
        var db = new SelfHostedDbContext(options, tp);
        var repo = new SelfHostedRepository<FileRecord>(db);
        var sp = Substitute.For<IFileStorageProvider>();
        var logger = Substitute.For<ILogger<FileStorageService>>();

        // Should not throw
        var svc = new FileStorageService(sp, repo, db, tp, logger);
        Assert.NotNull(svc);

        db.Dispose();
    }
}
