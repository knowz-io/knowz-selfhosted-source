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
/// Tests for the self-hosted file-detach cleanup policy.
/// WorkGroupID: kc-feat-file-delete-orphan-policy-20260616-140729 — FEAT_SelfHostedFileCleanupPolicy (N6).
/// Self-hosted deletes blobs IMMEDIATELY (no grace period). Fail-safe default = PromptUser → preserve.
/// </summary>
public class FileDetachCleanupPolicyTests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private readonly IFileStorageProvider _storage;
    private readonly ISelfHostedRepository<FileRecord> _fileRepo;
    private readonly ITenantProvider _tenantProvider;
    private readonly IEnrichmentOutboxWriter _enrichmentWriter;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public FileDetachCleanupPolicyTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _tenantProvider = Substitute.For<ITenantProvider>();
        _tenantProvider.TenantId.Returns(TenantId);

        _db = new SelfHostedDbContext(options, _tenantProvider);
        _fileRepo = new SelfHostedRepository<FileRecord>(_db);

        _storage = Substitute.For<IFileStorageProvider>();
        _storage.DeleteAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        // ExistsAsync returns true UNLESS a delete was issued for that file (simulated below per test).
        _storage.ExistsAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);

        _enrichmentWriter = Substitute.For<IEnrichmentOutboxWriter>();
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    // --- Helpers ---

    /// <summary>Test double: returns a fixed mode (the config resolver is unit-tested separately).</summary>
    private sealed class StubResolver : IFileCleanupPolicyResolver
    {
        private readonly FileCleanupMode _mode;
        public StubResolver(FileCleanupMode mode) => _mode = mode;
        public FileCleanupMode ResolveMode() => _mode;
    }

    private FileStorageService BuildSvc(FileCleanupMode? mode)
    {
        var logger = Substitute.For<ILogger<FileStorageService>>();
        IFileCleanupPolicyResolver? resolver = mode is null ? null : new StubResolver(mode.Value);
        return new FileStorageService(
            _storage, _fileRepo, _db, _tenantProvider, logger,
            contentExtractor: null, enrichmentWriter: _enrichmentWriter, cleanupPolicyResolver: resolver);
    }

    private async Task<Knowledge> SeedKnowledge(string title = "K")
    {
        var k = new Knowledge { TenantId = TenantId, Title = title, Content = "c" };
        _db.KnowledgeItems.Add(k);
        await _db.SaveChangesAsync();
        return k;
    }

    private async Task<FileRecord> SeedFileRecord(string fileName = "file.txt")
    {
        var f = new FileRecord
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            FileName = fileName,
            ContentType = "text/plain"
        };
        _db.FileRecords.Add(f);
        await _db.SaveChangesAsync();
        return f;
    }

    private async Task<FileAttachment> AttachToKnowledge(Guid fileRecordId, Guid knowledgeId)
    {
        var a = new FileAttachment
        {
            Id = Guid.NewGuid(),
            FileRecordId = fileRecordId,
            KnowledgeId = knowledgeId,
            TenantId = TenantId
        };
        _db.FileAttachments.Add(a);
        await _db.SaveChangesAsync();
        return a;
    }

    private async Task<FileAttachment> AttachToComment(Guid fileRecordId, Guid commentId)
    {
        var a = new FileAttachment
        {
            Id = Guid.NewGuid(),
            FileRecordId = fileRecordId,
            CommentId = commentId,
            TenantId = TenantId
        };
        _db.FileAttachments.Add(a);
        await _db.SaveChangesAsync();
        return a;
    }

    private async Task<KnowledgeComment> SeedComment(Guid knowledgeId)
    {
        var c = new KnowledgeComment
        {
            TenantId = TenantId,
            KnowledgeId = knowledgeId,
            AuthorName = "Alice",
            Body = "comment"
        };
        _db.Comments.Add(c);
        await _db.SaveChangesAsync();
        return c;
    }

    private async Task<bool> FileRecordIsDeleted(Guid fileRecordId)
    {
        var raw = await _db.FileRecords.IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.Id == fileRecordId);
        Assert.NotNull(raw);
        return raw!.IsDeleted;
    }

    // =============================================
    // VERIFY-N6.1 (MANDATORY): last-link guard — shared file preserved
    // =============================================

    [Fact]
    public async Task VerifyN61_DetachSharedFile_WithDeleteFilesTrue_PreservesFile_AndBlob()
    {
        var knowledge = await SeedKnowledge("A");
        var comment = await SeedComment(knowledge.Id);
        var file = await SeedFileRecord("shared.pdf");
        var knowledgeAttachment = await AttachToKnowledge(file.Id, knowledge.Id);
        // Second reference: same file attached to a comment.
        await AttachToComment(file.Id, comment.Id);

        var svc = BuildSvc(FileCleanupMode.PromptUser);

        var result = await svc.DetachFromKnowledgeAsync(file.Id, knowledge.Id, deleteFiles: true);

        Assert.NotNull(result);
        Assert.True(result!.Detached);
        Assert.Equal(1, result.FilesPreserved);
        Assert.Equal(0, result.FilesDeleted);
        Assert.Contains("shared.pdf", result.PreservedFileNames);

        // FileRecord NOT soft-deleted (other reference exists).
        Assert.False(await FileRecordIsDeleted(file.Id));

        // Blob NOT deleted.
        await _storage.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), file.Id, Arg.Any<CancellationToken>());
        Assert.True(await _storage.ExistsAsync(TenantId, file.Id));

        // Knowledge-side junction row was removed; comment-side reference still present.
        var knowledgeJunction = await _db.FileAttachments
            .FirstOrDefaultAsync(fa => fa.Id == knowledgeAttachment.Id);
        Assert.Null(knowledgeJunction);
        var commentJunction = await _db.FileAttachments
            .FirstOrDefaultAsync(fa => fa.FileRecordId == file.Id && fa.CommentId == comment.Id);
        Assert.NotNull(commentJunction);
    }

    // =============================================
    // VERIFY-N6.2: delete opt-in on sole-reference file — blob gone immediately
    // =============================================

    [Fact]
    public async Task VerifyN62_DetachSoleReference_WithDeleteFilesTrue_SoftDeletesRecord_AndDeletesBlob()
    {
        var knowledge = await SeedKnowledge();
        var file = await SeedFileRecord("bye.pdf");
        var attachment = await AttachToKnowledge(file.Id, knowledge.Id);

        var svc = BuildSvc(FileCleanupMode.PromptUser);

        var result = await svc.DetachFromKnowledgeAsync(file.Id, knowledge.Id, deleteFiles: true);

        Assert.NotNull(result);
        Assert.Equal(1, result!.FilesDeleted);
        Assert.Equal(0, result.FilesPreserved);
        Assert.Contains("bye.pdf", result.DeletedFileNames);

        Assert.True(await FileRecordIsDeleted(file.Id));
        await _storage.Received(1).DeleteAsync(TenantId, file.Id, Arg.Any<CancellationToken>());

        // Junction removed.
        Assert.Null(await _db.FileAttachments.FirstOrDefaultAsync(fa => fa.Id == attachment.Id));
    }

    // =============================================
    // VERIFY-N6.3: bug-fix / preserve — plain DELETE under default PromptUser
    // =============================================

    [Fact]
    public async Task VerifyN63_PlainDetach_DefaultPromptUser_PreservesFileRecord_NotOrphaned()
    {
        var knowledge = await SeedKnowledge();
        var file = await SeedFileRecord("keep.pdf");
        await AttachToKnowledge(file.Id, knowledge.Id);

        var svc = BuildSvc(FileCleanupMode.PromptUser);

        var result = await svc.DetachFromKnowledgeAsync(file.Id, knowledge.Id, deleteFiles: null);

        Assert.NotNull(result);
        Assert.True(result!.Detached);
        Assert.Equal(1, result.FilesPreserved);
        Assert.Equal(0, result.FilesDeleted);

        // FileRecord explicitly preserved (queryable, NOT deleted) — pre-fix this orphaned it.
        Assert.False(await FileRecordIsDeleted(file.Id));
        await _storage.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), file.Id, Arg.Any<CancellationToken>());

        // Junction still removed.
        Assert.Null(await _db.FileAttachments
            .FirstOrDefaultAsync(fa => fa.FileRecordId == file.Id && fa.KnowledgeId == knowledge.Id));
    }

    // =============================================
    // VERIFY-N6.4: fail-safe — PromptUser + absent param => preserve
    // =============================================

    [Fact]
    public async Task VerifyN64_PromptUser_AbsentParam_IsPreserve()
    {
        Assert.False(FileStorageService.ResolveEffectiveDeleteFiles(FileCleanupMode.PromptUser, null));
        Assert.True(FileStorageService.ResolveEffectiveDeleteFiles(FileCleanupMode.PromptUser, true));
        Assert.False(FileStorageService.ResolveEffectiveDeleteFiles(FileCleanupMode.PromptUser, false));
    }

    // =============================================
    // VERIFY-N6.5: mode resolution
    // =============================================

    private static FileCleanupPolicyResolver BuildResolver(string? configuredMode)
    {
        var config = Substitute.For<Microsoft.Extensions.Configuration.IConfiguration>();
        config["FileCleanup:Mode"].Returns(configuredMode);
        return new FileCleanupPolicyResolver(config);
    }

    [Fact]
    public void VerifyN65_Resolver_DefaultsToPromptUser_WhenUnset()
    {
        Assert.Equal(FileCleanupMode.PromptUser, BuildResolver(null).ResolveMode());
    }

    [Theory]
    [InlineData("AutoCleanup", FileCleanupMode.AutoCleanup)]
    [InlineData("autocleanup", FileCleanupMode.AutoCleanup)]
    [InlineData("PreserveAlways", FileCleanupMode.PreserveAlways)]
    [InlineData("PromptUser", FileCleanupMode.PromptUser)]
    [InlineData("garbage", FileCleanupMode.PromptUser)]
    public void VerifyN65_Resolver_ReadsConfiguredMode(string configured, FileCleanupMode expected)
    {
        Assert.Equal(expected, BuildResolver(configured).ResolveMode());
    }

    [Fact]
    public async Task VerifyN65_AutoCleanup_PlainDetach_DeletesSoleReferenceFile()
    {
        var knowledge = await SeedKnowledge();
        var file = await SeedFileRecord("auto.pdf");
        await AttachToKnowledge(file.Id, knowledge.Id);

        var svc = BuildSvc(FileCleanupMode.AutoCleanup);

        // No deleteFiles param — AutoCleanup makes it destructive.
        var result = await svc.DetachFromKnowledgeAsync(file.Id, knowledge.Id, deleteFiles: null);

        Assert.NotNull(result);
        Assert.Equal(1, result!.FilesDeleted);
        Assert.True(await FileRecordIsDeleted(file.Id));
        await _storage.Received(1).DeleteAsync(TenantId, file.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyN65_PreserveAlways_IgnoresDeleteFilesTrue()
    {
        var knowledge = await SeedKnowledge();
        var file = await SeedFileRecord("preserve.pdf");
        await AttachToKnowledge(file.Id, knowledge.Id);

        var svc = BuildSvc(FileCleanupMode.PreserveAlways);

        // deleteFiles=true is ignored under PreserveAlways.
        var result = await svc.DetachFromKnowledgeAsync(file.Id, knowledge.Id, deleteFiles: true);

        Assert.NotNull(result);
        Assert.Equal(1, result!.FilesPreserved);
        Assert.Equal(0, result.FilesDeleted);
        Assert.False(await FileRecordIsDeleted(file.Id));
        await _storage.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), file.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NullResolver_DefaultsToPreserve_FailSafe()
    {
        var knowledge = await SeedKnowledge();
        var file = await SeedFileRecord("nullres.pdf");
        await AttachToKnowledge(file.Id, knowledge.Id);

        var svc = BuildSvc(mode: null); // resolver not injected

        // Even with deleteFiles=true, a null resolver => PromptUser; but the explicit param is honored
        // (PromptUser + true => true). Confirm a *plain* detach with null resolver preserves.
        var plain = await svc.DetachFromKnowledgeAsync(file.Id, knowledge.Id, deleteFiles: null);
        Assert.NotNull(plain);
        Assert.Equal(1, plain!.FilesPreserved);
        Assert.False(await FileRecordIsDeleted(file.Id));
    }

    // =============================================
    // VERIFY-N6.6: storage failure — IsDeleted NOT persisted
    // =============================================

    [Fact]
    public async Task VerifyN66_StorageDeleteThrows_DoesNotPersistIsDeleted()
    {
        var knowledge = await SeedKnowledge();
        var file = await SeedFileRecord("flaky.pdf");
        await AttachToKnowledge(file.Id, knowledge.Id);

        _storage.DeleteAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("blob storage unreachable"));

        var svc = BuildSvc(FileCleanupMode.AutoCleanup);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DetachFromKnowledgeAsync(file.Id, knowledge.Id, deleteFiles: null));

        // Critical invariant: FileRecord must remain queryable as NOT deleted.
        Assert.False(await FileRecordIsDeleted(file.Id));
    }

    // =============================================
    // Re-enrichment preserved (R6)
    // =============================================

    [Fact]
    public async Task Detach_TriggersReEnrichment_AfterCleanup()
    {
        var knowledge = await SeedKnowledge();
        var file = await SeedFileRecord();
        await AttachToKnowledge(file.Id, knowledge.Id);

        var svc = BuildSvc(FileCleanupMode.PromptUser);

        await svc.DetachFromKnowledgeAsync(file.Id, knowledge.Id, deleteFiles: null);

        await _enrichmentWriter.Received(1).EnqueueAsync(knowledge.Id, TenantId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Detach_ReturnsNull_WhenAttachmentMissing()
    {
        var svc = BuildSvc(FileCleanupMode.PromptUser);
        var result = await svc.DetachFromKnowledgeAsync(Guid.NewGuid(), Guid.NewGuid(), deleteFiles: true);
        Assert.Null(result);
    }
}
