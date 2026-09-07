using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class EnrichmentTranscriptBoxingTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private readonly SelfHostedDbContext _db;

    public EnrichmentTranscriptBoxingTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(options, tenantProvider);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    [Fact]
    public async Task GetAllAttachmentTextAsync_WithTranscription_WrapsSpokenContentMarkers()
    {
        var knowledgeId = Guid.NewGuid();
        _db.KnowledgeItems.Add(new Knowledge
        {
            Id = knowledgeId,
            TenantId = TenantId,
            Title = "Transcript knowledge",
            Content = "Parent content"
        });

        var fileRecord = new FileRecord
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            FileName = "recording.webm",
            ContentType = "audio/webm",
            TranscriptionText = "This is what the speaker said"
        };
        _db.FileRecords.Add(fileRecord);
        _db.FileAttachments.Add(new FileAttachment
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            KnowledgeId = knowledgeId,
            FileRecordId = fileRecord.Id
        });

        await _db.SaveChangesAsync();

        var result = await EnrichmentBackgroundService.GetAllAttachmentTextAsync(
            _db, knowledgeId, CancellationToken.None);

        Assert.Contains("[Transcription: recording.webm]", result);
        Assert.Contains("[SPOKEN CONTENT BEGIN", result);
        Assert.Contains("This is what the speaker said", result);
        Assert.Contains("[SPOKEN CONTENT END]", result);
    }
}
