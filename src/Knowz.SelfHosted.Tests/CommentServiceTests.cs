using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class CommentServiceTests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private readonly CommentService _svc;
    private readonly IEnrichmentOutboxWriter _enrichmentWriter;
    private readonly IFileStorageProvider _storage;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherTenantId = Guid.Parse("00000000-0000-0000-0000-000000000099");

    public CommentServiceTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);

        _db = new SelfHostedDbContext(options, tenantProvider);
        _enrichmentWriter = Substitute.For<IEnrichmentOutboxWriter>();
        _storage = Substitute.For<IFileStorageProvider>();
        // Default: storage ops succeed
        _storage.DeleteAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _storage.ExistsAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var logger = Substitute.For<ILogger<CommentService>>();
        _svc = new CommentService(_db, tenantProvider, logger, _storage, _enrichmentWriter);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    // --- Helpers ---

    private async Task<Knowledge> SeedKnowledge(string title = "Test Knowledge", string content = "Test content")
    {
        var item = new Knowledge
        {
            TenantId = TenantId,
            Title = title,
            Content = content
        };
        _db.KnowledgeItems.Add(item);
        await _db.SaveChangesAsync();
        return item;
    }

    // =============================================
    // SVC_CommentService: AddCommentAsync
    // =============================================

    [Fact]
    public async Task AddComment_CreatesComment_WithCorrectFields()
    {
        var knowledge = await SeedKnowledge();

        var result = await _svc.AddCommentAsync(
            knowledge.Id, "Hello world", "Alice", null, "positive", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(knowledge.Id, result.KnowledgeId);
        Assert.Equal("Hello world", result.Body);
        Assert.Equal("Alice", result.AuthorName);
        Assert.Equal("positive", result.Sentiment);
        Assert.Null(result.ParentCommentId);
        Assert.False(result.IsAnswer);

        // Verify persisted in DB
        var saved = await _db.Comments.FindAsync(result.Id);
        Assert.NotNull(saved);
        Assert.Equal(TenantId, saved.TenantId);
    }

    [Fact]
    public async Task AddComment_ReturnsNull_WhenKnowledgeNotFound()
    {
        var result = await _svc.AddCommentAsync(
            Guid.NewGuid(), "Body", "Author", null, null, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task AddComment_CreatesReply_WithValidParentCommentId()
    {
        var knowledge = await SeedKnowledge();
        var parent = await _svc.AddCommentAsync(
            knowledge.Id, "Parent comment", "Alice", null, null, CancellationToken.None);

        var reply = await _svc.AddCommentAsync(
            knowledge.Id, "Reply to parent", "Bob", parent!.Id, null, CancellationToken.None);

        Assert.NotNull(reply);
        Assert.Equal(parent.Id, reply.ParentCommentId);
        Assert.Equal(knowledge.Id, reply.KnowledgeId);
    }

    [Fact]
    public async Task AddComment_ReturnsNull_WhenParentCommentNotFound()
    {
        var knowledge = await SeedKnowledge();

        var result = await _svc.AddCommentAsync(
            knowledge.Id, "Body", "Author", Guid.NewGuid(), null, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task AddComment_ReturnsNull_WhenParentBelongsToDifferentKnowledge()
    {
        var knowledge1 = await SeedKnowledge("K1");
        var knowledge2 = await SeedKnowledge("K2");

        var parentOnK1 = await _svc.AddCommentAsync(
            knowledge1.Id, "Parent on K1", "Alice", null, null, CancellationToken.None);

        var result = await _svc.AddCommentAsync(
            knowledge2.Id, "Reply on K2", "Bob", parentOnK1!.Id, null, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task AddComment_TriggersEnrichment()
    {
        var knowledge = await SeedKnowledge();

        await _svc.AddCommentAsync(
            knowledge.Id, "Body", "Author", null, null, CancellationToken.None);

        await _enrichmentWriter.Received(1).EnqueueAsync(
            knowledge.Id, TenantId, Arg.Any<CancellationToken>());
    }

    // =============================================
    // SVC_CommentService: ListCommentsAsync
    // =============================================

    [Fact]
    public async Task ListComments_ReturnsTopLevelWithNestedReplies()
    {
        var knowledge = await SeedKnowledge();
        var parent = await _svc.AddCommentAsync(
            knowledge.Id, "Top level", "Alice", null, null, CancellationToken.None);
        await _svc.AddCommentAsync(
            knowledge.Id, "Reply 1", "Bob", parent!.Id, null, CancellationToken.None);
        await _svc.AddCommentAsync(
            knowledge.Id, "Reply 2", "Charlie", parent.Id, null, CancellationToken.None);

        var result = await _svc.ListCommentsAsync(knowledge.Id, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Top level", result[0].Body);
        Assert.NotNull(result[0].Replies);
        Assert.Equal(2, result[0].Replies!.Count);
        Assert.Equal("Reply 1", result[0].Replies![0].Body);
        Assert.Equal("Reply 2", result[0].Replies![1].Body);
    }

    [Fact]
    public async Task ListComments_ReturnsEmptyList_WhenNoComments()
    {
        var knowledge = await SeedKnowledge();

        var result = await _svc.ListCommentsAsync(knowledge.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task ListComments_OrderedByCreatedAtAscending()
    {
        var knowledge = await SeedKnowledge();
        await _svc.AddCommentAsync(knowledge.Id, "First", "A", null, null, CancellationToken.None);
        await _svc.AddCommentAsync(knowledge.Id, "Second", "B", null, null, CancellationToken.None);
        await _svc.AddCommentAsync(knowledge.Id, "Third", "C", null, null, CancellationToken.None);

        var result = await _svc.ListCommentsAsync(knowledge.Id, CancellationToken.None);

        Assert.Equal(3, result.Count);
        Assert.Equal("First", result[0].Body);
        Assert.Equal("Second", result[1].Body);
        Assert.Equal("Third", result[2].Body);
    }

    [Fact]
    public async Task ListComments_ExcludesSoftDeletedComments()
    {
        var knowledge = await SeedKnowledge();
        var comment = await _svc.AddCommentAsync(
            knowledge.Id, "Will be deleted", "Alice", null, null, CancellationToken.None);
        await _svc.AddCommentAsync(
            knowledge.Id, "Stays", "Bob", null, null, CancellationToken.None);

        await _svc.DeleteCommentAsync(comment!.Id, deleteFiles: false, CancellationToken.None);

        var result = await _svc.ListCommentsAsync(knowledge.Id, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Stays", result[0].Body);
    }

    [Fact]
    public async Task ListComments_PopulatesAttachmentCount()
    {
        var knowledge = await SeedKnowledge();
        var comment = await _svc.AddCommentAsync(
            knowledge.Id, "With attachments", "Alice", null, null, CancellationToken.None);

        // Add file attachments
        var fileRecord = new FileRecord
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            FileName = "file.txt",
            ContentType = "text/plain"
        };
        _db.FileRecords.Add(fileRecord);
        _db.FileAttachments.Add(new FileAttachment
        {
            Id = Guid.NewGuid(),
            FileRecordId = fileRecord.Id,
            CommentId = comment!.Id,
            TenantId = TenantId
        });
        _db.FileAttachments.Add(new FileAttachment
        {
            Id = Guid.NewGuid(),
            FileRecordId = fileRecord.Id,
            CommentId = comment.Id,
            TenantId = TenantId
        });
        await _db.SaveChangesAsync();

        var result = await _svc.ListCommentsAsync(knowledge.Id, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(2, result[0].AttachmentCount);
    }

    // =============================================
    // SVC_CommentService: GetCommentAsync
    // =============================================

    [Fact]
    public async Task GetComment_ReturnsComment_WhenExists()
    {
        var knowledge = await SeedKnowledge();
        var created = await _svc.AddCommentAsync(
            knowledge.Id, "Get me", "Alice", null, null, CancellationToken.None);

        var result = await _svc.GetCommentAsync(created!.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(created.Id, result.Id);
        Assert.Equal("Get me", result.Body);
    }

    [Fact]
    public async Task GetComment_ReturnsNull_WhenNotFound()
    {
        var result = await _svc.GetCommentAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(result);
    }

    // =============================================
    // SVC_CommentService: UpdateCommentAsync
    // =============================================

    [Fact]
    public async Task UpdateComment_UpdatesBody()
    {
        var knowledge = await SeedKnowledge();
        var created = await _svc.AddCommentAsync(
            knowledge.Id, "Original", "Alice", null, null, CancellationToken.None);

        var result = await _svc.UpdateCommentAsync(
            created!.Id, "Updated body", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Updated body", result.Body);
    }

    [Fact]
    public async Task UpdateComment_UpdatesSentiment()
    {
        var knowledge = await SeedKnowledge();
        var created = await _svc.AddCommentAsync(
            knowledge.Id, "Body", "Alice", null, null, CancellationToken.None);

        var result = await _svc.UpdateCommentAsync(
            created!.Id, null, "negative", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("negative", result.Sentiment);
        Assert.Equal("Body", result.Body); // Body unchanged
    }

    [Fact]
    public async Task UpdateComment_SetsUpdatedAt()
    {
        var knowledge = await SeedKnowledge();
        var created = await _svc.AddCommentAsync(
            knowledge.Id, "Body", "Alice", null, null, CancellationToken.None);
        var beforeUpdate = DateTime.UtcNow;

        await Task.Delay(10);
        var result = await _svc.UpdateCommentAsync(
            created!.Id, "New body", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.UpdatedAt >= beforeUpdate);
    }

    [Fact]
    public async Task UpdateComment_TriggersEnrichment_WhenBodyChanged()
    {
        var knowledge = await SeedKnowledge();
        var created = await _svc.AddCommentAsync(
            knowledge.Id, "Original", "Alice", null, null, CancellationToken.None);

        _enrichmentWriter.ClearReceivedCalls();

        await _svc.UpdateCommentAsync(
            created!.Id, "Updated body", null, CancellationToken.None);

        await _enrichmentWriter.Received(1).EnqueueAsync(
            knowledge.Id, Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateComment_DoesNotTriggerEnrichment_WhenOnlySentimentChanged()
    {
        var knowledge = await SeedKnowledge();
        var created = await _svc.AddCommentAsync(
            knowledge.Id, "Body", "Alice", null, null, CancellationToken.None);

        _enrichmentWriter.ClearReceivedCalls();

        await _svc.UpdateCommentAsync(
            created!.Id, null, "positive", CancellationToken.None);

        await _enrichmentWriter.DidNotReceive().EnqueueAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateComment_ReturnsNull_WhenNotFound()
    {
        var result = await _svc.UpdateCommentAsync(
            Guid.NewGuid(), "Body", null, CancellationToken.None);

        Assert.Null(result);
    }

    // =============================================
    // SVC_CommentService: DeleteCommentAsync
    // WorkGroupID: kc-fix-attach-delete-transcript-20260411-080000 — FEAT_CommentDeleteAttachmentChoice
    // =============================================

    private async Task<FileRecord> SeedFileRecord(string fileName = "file.txt")
    {
        var fileRecord = new FileRecord
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            FileName = fileName,
            ContentType = "text/plain"
        };
        _db.FileRecords.Add(fileRecord);
        await _db.SaveChangesAsync();
        return fileRecord;
    }

    private async Task<FileAttachment> AttachFileToComment(Guid fileRecordId, Guid commentId)
    {
        var attachment = new FileAttachment
        {
            Id = Guid.NewGuid(),
            FileRecordId = fileRecordId,
            CommentId = commentId,
            TenantId = TenantId
        };
        _db.FileAttachments.Add(attachment);
        await _db.SaveChangesAsync();
        return attachment;
    }

    private async Task<FileAttachment> AttachFileToKnowledge(Guid fileRecordId, Guid knowledgeId)
    {
        var attachment = new FileAttachment
        {
            Id = Guid.NewGuid(),
            FileRecordId = fileRecordId,
            KnowledgeId = knowledgeId,
            TenantId = TenantId
        };
        _db.FileAttachments.Add(attachment);
        await _db.SaveChangesAsync();
        return attachment;
    }

    [Fact]
    public async Task DeleteComment_SoftDeletesComment()
    {
        var knowledge = await SeedKnowledge();
        var created = await _svc.AddCommentAsync(
            knowledge.Id, "Delete me", "Alice", null, null, CancellationToken.None);

        var result = await _svc.DeleteCommentAsync(created!.Id, deleteFiles: false, CancellationToken.None);

        Assert.NotNull(result);

        // Verify soft-deleted (query filter excludes it from queries)
        var found = await _db.Comments
            .FirstOrDefaultAsync(c => c.Id == created.Id);
        Assert.Null(found);

        // But exists when ignoring query filters
        var raw = await _db.Comments.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == created.Id);
        Assert.NotNull(raw);
        Assert.True(raw.IsDeleted);
    }

    [Fact]
    public async Task DeleteComment_CascadesSoftDeleteToReplies()
    {
        var knowledge = await SeedKnowledge();
        var parent = await _svc.AddCommentAsync(
            knowledge.Id, "Parent", "Alice", null, null, CancellationToken.None);
        var reply = await _svc.AddCommentAsync(
            knowledge.Id, "Reply", "Bob", parent!.Id, null, CancellationToken.None);

        await _svc.DeleteCommentAsync(parent.Id, deleteFiles: false, CancellationToken.None);

        // Both parent and reply should be soft-deleted
        var parentRaw = await _db.Comments.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == parent.Id);
        var replyRaw = await _db.Comments.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == reply!.Id);
        Assert.True(parentRaw!.IsDeleted);
        Assert.True(replyRaw!.IsDeleted);
    }

    [Fact]
    public async Task DeleteComment_RemovesFileAttachments()
    {
        var knowledge = await SeedKnowledge();
        var created = await _svc.AddCommentAsync(
            knowledge.Id, "With files", "Alice", null, null, CancellationToken.None);

        var fileRecord = await SeedFileRecord();
        await AttachFileToComment(fileRecord.Id, created!.Id);

        await _svc.DeleteCommentAsync(created.Id, deleteFiles: false, CancellationToken.None);

        // FileAttachment junction row should be hard-deleted
        var remaining = await _db.FileAttachments
            .Where(fa => fa.CommentId == created.Id)
            .CountAsync();
        Assert.Equal(0, remaining);
    }

    [Fact]
    public async Task DeleteComment_TriggersEnrichment()
    {
        var knowledge = await SeedKnowledge();
        var created = await _svc.AddCommentAsync(
            knowledge.Id, "Body", "Alice", null, null, CancellationToken.None);

        _enrichmentWriter.ClearReceivedCalls();

        await _svc.DeleteCommentAsync(created!.Id, deleteFiles: false, CancellationToken.None);

        await _enrichmentWriter.Received(1).EnqueueAsync(
            knowledge.Id, Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteComment_ReturnsNull_WhenNotFound()
    {
        var result = await _svc.DeleteCommentAsync(Guid.NewGuid(), deleteFiles: false, CancellationToken.None);

        Assert.Null(result);
    }

    // --- FEAT_CommentDeleteAttachmentChoice tests ---

    [Fact]
    public async Task DeleteComment_WithDeleteFilesFalse_PreservesFileRecord_AndDoesNotDeleteBlob()
    {
        // VERIFY-6, VERIFY-9: default (preserve) keeps FileRecord + blob intact
        var knowledge = await SeedKnowledge();
        var created = await _svc.AddCommentAsync(
            knowledge.Id, "Keep files", "Alice", null, null, CancellationToken.None);

        var fileRecord = await SeedFileRecord("keep-me.pdf");
        await AttachFileToComment(fileRecord.Id, created!.Id);

        var result = await _svc.DeleteCommentAsync(created.Id, deleteFiles: false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, result!.FilesPreserved);
        Assert.Equal(0, result.FilesDeleted);
        Assert.Contains("keep-me.pdf", result.PreservedFileNames);

        // FileRecord still present and not soft-deleted
        var rawFile = await _db.FileRecords.IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.Id == fileRecord.Id);
        Assert.NotNull(rawFile);
        Assert.False(rawFile!.IsDeleted);

        // Blob storage must NOT have been called for delete
        await _storage.DidNotReceive().DeleteAsync(
            Arg.Any<Guid>(), fileRecord.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteComment_WithDeleteFilesTrue_SoftDeletesFileRecord_AndDeletesBlob()
    {
        // VERIFY-7: explicit opt-in: FileRecord soft-deleted, blob deleted immediately
        var knowledge = await SeedKnowledge();
        var created = await _svc.AddCommentAsync(
            knowledge.Id, "Bye files", "Alice", null, null, CancellationToken.None);

        var fileRecord = await SeedFileRecord("bye.pdf");
        await AttachFileToComment(fileRecord.Id, created!.Id);

        var result = await _svc.DeleteCommentAsync(created.Id, deleteFiles: true, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, result!.FilesDeleted);
        Assert.Equal(0, result.FilesPreserved);
        Assert.Contains("bye.pdf", result.DeletedFileNames);

        // FileRecord soft-deleted
        var rawFile = await _db.FileRecords.IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.Id == fileRecord.Id);
        Assert.NotNull(rawFile);
        Assert.True(rawFile!.IsDeleted);

        // Blob deletion called with correct ids
        await _storage.Received(1).DeleteAsync(
            TenantId, fileRecord.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteComment_WithDeleteFilesTrue_PreservesSharedFile_AcrossKnowledge()
    {
        // VERIFY-8 (MANDATORY): cross-reference check - file also attached to knowledge is preserved
        var knowledge = await SeedKnowledge();
        var created = await _svc.AddCommentAsync(
            knowledge.Id, "Has shared file", "Alice", null, null, CancellationToken.None);

        var fileRecord = await SeedFileRecord("shared.pdf");
        // Attach the same file to both the comment AND the parent knowledge item
        await AttachFileToComment(fileRecord.Id, created!.Id);
        await AttachFileToKnowledge(fileRecord.Id, knowledge.Id);

        var result = await _svc.DeleteCommentAsync(created.Id, deleteFiles: true, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, result!.FilesPreserved);
        Assert.Equal(0, result.FilesDeleted);
        Assert.Contains("shared.pdf", result.PreservedFileNames);

        // FileRecord must NOT be soft-deleted
        var rawFile = await _db.FileRecords.IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.Id == fileRecord.Id);
        Assert.NotNull(rawFile);
        Assert.False(rawFile!.IsDeleted);

        // Blob delete must NOT have been called
        await _storage.DidNotReceive().DeleteAsync(
            Arg.Any<Guid>(), fileRecord.Id, Arg.Any<CancellationToken>());

        // The knowledge-side attachment must remain
        var knowledgeAttachment = await _db.FileAttachments
            .FirstOrDefaultAsync(fa => fa.FileRecordId == fileRecord.Id && fa.KnowledgeId == knowledge.Id);
        Assert.NotNull(knowledgeAttachment);
    }

    [Fact]
    public async Task DeleteComment_WithDeleteFilesTrue_CascadesCleanupToReplies()
    {
        // VERIFY-10: cascade - files attached to child replies also evaluated
        var knowledge = await SeedKnowledge();
        var parent = await _svc.AddCommentAsync(
            knowledge.Id, "Parent", "Alice", null, null, CancellationToken.None);
        var reply = await _svc.AddCommentAsync(
            knowledge.Id, "Reply", "Bob", parent!.Id, null, CancellationToken.None);

        var parentFile = await SeedFileRecord("parent.pdf");
        var replyFile = await SeedFileRecord("reply.pdf");
        await AttachFileToComment(parentFile.Id, parent.Id);
        await AttachFileToComment(replyFile.Id, reply!.Id);

        var result = await _svc.DeleteCommentAsync(parent.Id, deleteFiles: true, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, result!.FilesDeleted);
        Assert.Contains("parent.pdf", result.DeletedFileNames);
        Assert.Contains("reply.pdf", result.DeletedFileNames);

        // Both FileRecords soft-deleted
        var parentRaw = await _db.FileRecords.IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.Id == parentFile.Id);
        var replyRaw = await _db.FileRecords.IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.Id == replyFile.Id);
        Assert.True(parentRaw!.IsDeleted);
        Assert.True(replyRaw!.IsDeleted);

        // Both blob deletes called
        await _storage.Received(1).DeleteAsync(TenantId, parentFile.Id, Arg.Any<CancellationToken>());
        await _storage.Received(1).DeleteAsync(TenantId, replyFile.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteComment_WithDeleteFilesTrue_WhenStorageDeleteFails_DoesNotSoftDeleteFileRecord()
    {
        // VERIFY-11: storage failure - FileRecord.IsDeleted must NOT be persisted
        var knowledge = await SeedKnowledge();
        var created = await _svc.AddCommentAsync(
            knowledge.Id, "Flaky", "Alice", null, null, CancellationToken.None);

        var fileRecord = await SeedFileRecord("flaky.pdf");
        await AttachFileToComment(fileRecord.Id, created!.Id);

        _storage.DeleteAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("blob storage unreachable"));

        // Act — spec allows endpoint to return 500 OR 200-with-zero-counts; the service may throw or swallow.
        // Regardless of outcome, FileRecord.IsDeleted must NOT be persisted.
        try
        {
            var result = await _svc.DeleteCommentAsync(created.Id, deleteFiles: true, CancellationToken.None);
            // If swallowed, must report zero files deleted
            if (result != null)
            {
                Assert.Equal(0, result.FilesDeleted);
            }
        }
        catch (InvalidOperationException)
        {
            // Service chose to propagate — also acceptable per spec
        }

        // Critical invariant: FileRecord must still be queryable as NOT deleted
        var rawFile = await _db.FileRecords.IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.Id == fileRecord.Id);
        Assert.NotNull(rawFile);
        Assert.False(rawFile!.IsDeleted);
    }

    // =============================================
    // Tenant Isolation
    // =============================================

    [Fact]
    public async Task TenantIsolation_CannotAccessCommentsFromOtherTenant()
    {
        var knowledge = await SeedKnowledge();

        // Directly insert a comment for a different tenant
        _db.Comments.Add(new KnowledgeComment
        {
            TenantId = OtherTenantId,
            KnowledgeId = knowledge.Id,
            AuthorName = "OtherTenant",
            Body = "Other tenant comment"
        });
        await _db.SaveChangesAsync();

        var result = await _svc.ListCommentsAsync(knowledge.Id, CancellationToken.None);

        Assert.DoesNotContain(result, c => c.AuthorName == "OtherTenant");
    }

    // =============================================
    // MOD_SearchWithComments: Re-enrichment triggers
    // =============================================

    [Fact]
    public async Task EnrichmentTriggered_OnAddComment()
    {
        var knowledge = await SeedKnowledge();
        _enrichmentWriter.ClearReceivedCalls();

        await _svc.AddCommentAsync(
            knowledge.Id, "New comment", "Alice", null, null, CancellationToken.None);

        await _enrichmentWriter.Received(1).EnqueueAsync(
            knowledge.Id, TenantId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnrichmentTriggered_OnUpdateCommentBody()
    {
        var knowledge = await SeedKnowledge();
        var comment = await _svc.AddCommentAsync(
            knowledge.Id, "Original", "Alice", null, null, CancellationToken.None);
        _enrichmentWriter.ClearReceivedCalls();

        await _svc.UpdateCommentAsync(comment!.Id, "Updated", null, CancellationToken.None);

        await _enrichmentWriter.Received(1).EnqueueAsync(
            knowledge.Id, Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnrichmentNotTriggered_OnSentimentOnlyUpdate()
    {
        var knowledge = await SeedKnowledge();
        var comment = await _svc.AddCommentAsync(
            knowledge.Id, "Body", "Alice", null, null, CancellationToken.None);
        _enrichmentWriter.ClearReceivedCalls();

        await _svc.UpdateCommentAsync(comment!.Id, null, "neutral", CancellationToken.None);

        await _enrichmentWriter.DidNotReceive().EnqueueAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnrichmentTriggered_OnDeleteComment()
    {
        var knowledge = await SeedKnowledge();
        var comment = await _svc.AddCommentAsync(
            knowledge.Id, "Delete me", "Alice", null, null, CancellationToken.None);
        _enrichmentWriter.ClearReceivedCalls();

        await _svc.DeleteCommentAsync(comment!.Id, deleteFiles: false, CancellationToken.None);

        await _enrichmentWriter.Received(1).EnqueueAsync(
            knowledge.Id, Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // =============================================
    // MOD_SearchWithComments: Enrichment null safety
    // =============================================

    [Fact]
    public async Task AddComment_WorksWithoutEnrichmentWriter()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        using var db = new SelfHostedDbContext(options, tenantProvider);
        var logger = Substitute.For<ILogger<CommentService>>();
        var storage = Substitute.For<IFileStorageProvider>();
        var svcNoEnrichment = new CommentService(db, tenantProvider, logger, storage); // no enrichmentWriter

        var knowledge = new Knowledge { TenantId = TenantId, Title = "Test", Content = "C" };
        db.KnowledgeItems.Add(knowledge);
        await db.SaveChangesAsync();

        var result = await svcNoEnrichment.AddCommentAsync(
            knowledge.Id, "Body", "Author", null, null, CancellationToken.None);

        Assert.NotNull(result);
        db.Database.EnsureDeleted();
    }
}
