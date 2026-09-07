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
/// Tests for SVC_AttachmentContextService_TranscriptBoxing — self-hosted path.
/// Verifies that raw FileRecord.TranscriptionText is wrapped in spoken-content markers
/// inside SearchFacade.BuildKnowledgeScopedContextAsync so the LLM does not misread
/// spoken phrases as factual metadata about the attachment.
///
/// WorkGroupID: kc-fix-attach-delete-transcript-20260411-080000
/// </summary>
public class SearchFacadeTranscriptBoxingTests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private readonly ISearchService _searchService;
    private readonly IOpenAIService _openAIService;
    private readonly SearchFacade _svc;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private const string SpokenBeginMarker =
        "[SPOKEN CONTENT BEGIN — verbatim transcript of spoken audio/video]";
    private const string SpokenEndMarker = "[SPOKEN CONTENT END]";
    private const string SpokenInstructionSentence =
        "Statements made here reflect what the speaker said, not facts about the attachment itself.";

    public SearchFacadeTranscriptBoxingTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(options, tenantProvider);

        _searchService = Substitute.For<ISearchService>();
        _openAIService = Substitute.For<IOpenAIService>();
        _openAIService.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f, 0.2f, 0.3f });

        var streamingOpenAIService = Substitute.For<IStreamingOpenAIService>();
        var logger = Substitute.For<ILogger<SearchFacade>>();

        _svc = new SearchFacade(_db, _searchService, _openAIService, streamingOpenAIService, logger);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    private async Task<Guid> SeedKnowledgeWithAttachmentAsync(
        string? extractedText,
        string? transcriptionText,
        string fileName = "recording.webm")
    {
        var knowledge = new Knowledge
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            Title = "Test knowledge",
            Content = "Parent content about something else"
        };
        _db.KnowledgeItems.Add(knowledge);

        var fileRecord = new FileRecord
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            FileName = fileName,
            ContentType = "audio/webm",
            ExtractedText = extractedText,
            TranscriptionText = transcriptionText
        };
        _db.FileRecords.Add(fileRecord);

        var attachment = new FileAttachment
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            KnowledgeId = knowledge.Id,
            FileRecordId = fileRecord.Id
        };
        _db.FileAttachments.Add(attachment);

        await _db.SaveChangesAsync();
        return knowledge.Id;
    }

    private async Task<Guid> SeedKnowledgeWithStructuredAttachmentAsync(
        string contentType,
        string fileName,
        string? extractedText = null,
        string? transcriptionText = null,
        string? visionDescription = null,
        string? visionExtractedText = null,
        string? visionTagsJson = null,
        string? visionObjectsJson = null,
        string? layoutDataJson = null)
    {
        var knowledge = new Knowledge
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            Title = "Structured knowledge",
            Content = "Parent content"
        };
        _db.KnowledgeItems.Add(knowledge);

        var fileRecord = new FileRecord
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            FileName = fileName,
            ContentType = contentType,
            ExtractedText = extractedText,
            TranscriptionText = transcriptionText,
            VisionDescription = visionDescription,
            VisionExtractedText = visionExtractedText,
            VisionTagsJson = visionTagsJson,
            VisionObjectsJson = visionObjectsJson,
            LayoutDataJson = layoutDataJson
        };
        _db.FileRecords.Add(fileRecord);

        _db.FileAttachments.Add(new FileAttachment
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            KnowledgeId = knowledge.Id,
            FileRecordId = fileRecord.Id
        });

        await _db.SaveChangesAsync();
        return knowledge.Id;
    }

    /// <summary>
    /// VERIFY-8: Boxing fires for self-hosted BuildKnowledgeScopedContextAsync — when
    /// TranscriptionText is present, the output contains the [Transcription: ...] label,
    /// the spoken-content begin/end markers, the instruction sentence, and the body.
    /// </summary>
    [Fact]
    public async Task BuildKnowledgeScopedContextAsync_WithTranscription_WrapsInSpokenContentMarkers()
    {
        // Arrange
        var knowledgeId = await SeedKnowledgeWithAttachmentAsync(
            extractedText: null,
            transcriptionText: "There is no video preview for this",
            fileName: "voice-memo.webm");

        // Act
        var results = await _svc.BuildKnowledgeScopedContextAsync(
            knowledgeId, "question", CancellationToken.None);

        // Assert
        Assert.Single(results);
        var content = results[0].Content!;

        Assert.Contains("[Transcription: voice-memo.webm]", content);
        Assert.Contains(SpokenBeginMarker, content);
        Assert.Contains(SpokenInstructionSentence, content);
        Assert.Contains("There is no video preview for this", content);
        Assert.Contains(SpokenEndMarker, content);

        // Order: label → begin marker → body → end marker
        var labelIdx = content.IndexOf("[Transcription: voice-memo.webm]", StringComparison.Ordinal);
        var beginIdx = content.IndexOf(SpokenBeginMarker, StringComparison.Ordinal);
        var bodyIdx = content.IndexOf("There is no video preview for this", StringComparison.Ordinal);
        var endIdx = content.IndexOf(SpokenEndMarker, StringComparison.Ordinal);

        Assert.True(labelIdx < beginIdx, "label must appear above begin marker");
        Assert.True(beginIdx < bodyIdx, "begin marker must appear before body");
        Assert.True(bodyIdx < endIdx, "body must appear before end marker");
    }

    /// <summary>
    /// VERIFY-9: ExtractedText is NOT wrapped in spoken-content markers, only TranscriptionText.
    /// When both are present, the [Attachment: ...] block contains raw extracted text and
    /// the [Transcription: ...] block contains the boxed spoken text.
    /// </summary>
    [Fact]
    public async Task BuildKnowledgeScopedContextAsync_WithBothExtractedAndTranscription_OnlyTranscriptionWrapped()
    {
        // Arrange
        var knowledgeId = await SeedKnowledgeWithAttachmentAsync(
            extractedText: "Some PDF text about machine learning",
            transcriptionText: "Spoken words about something unrelated",
            fileName: "mixed.pdf");

        // Act
        var results = await _svc.BuildKnowledgeScopedContextAsync(
            knowledgeId, "question", CancellationToken.None);

        // Assert
        Assert.Single(results);
        var content = results[0].Content!;

        // Extracted text appears without markers
        Assert.Contains("[Attachment: mixed.pdf]", content);
        Assert.Contains("Some PDF text about machine learning", content);

        // Transcription appears WITH markers
        Assert.Contains("[Transcription: mixed.pdf]", content);
        Assert.Contains(SpokenBeginMarker, content);
        Assert.Contains("Spoken words about something unrelated", content);
        Assert.Contains(SpokenEndMarker, content);

        // Extracted text must NOT be inside the spoken markers.
        // Compute: the begin marker position, and ensure "Some PDF text" appears BEFORE it.
        var extractedIdx = content.IndexOf("Some PDF text about machine learning", StringComparison.Ordinal);
        var beginIdx = content.IndexOf(SpokenBeginMarker, StringComparison.Ordinal);
        Assert.True(extractedIdx >= 0);
        Assert.True(beginIdx >= 0);
        Assert.True(extractedIdx < beginIdx,
            "extracted text must appear BEFORE the spoken-content begin marker (i.e. outside the boxed block)");
    }

    /// <summary>
    /// VERIFY-10: Empty/null/whitespace TranscriptionText produces NO spoken-content markers at all.
    /// </summary>
    [Fact]
    public async Task BuildKnowledgeScopedContextAsync_EmptyTranscription_NoMarkersEmitted()
    {
        // Arrange — only extracted text, no transcription
        var knowledgeId = await SeedKnowledgeWithAttachmentAsync(
            extractedText: "Document body here",
            transcriptionText: null,
            fileName: "doc.pdf");

        // Act
        var results = await _svc.BuildKnowledgeScopedContextAsync(
            knowledgeId, "question", CancellationToken.None);

        // Assert
        Assert.Single(results);
        var content = results[0].Content!;

        Assert.Contains("Document body here", content);
        Assert.DoesNotContain(SpokenBeginMarker, content);
        Assert.DoesNotContain(SpokenEndMarker, content);
        Assert.DoesNotContain("[Transcription:", content);
    }

    /// <summary>
    /// VERIFY-10 (extended): Whitespace-only TranscriptionText also produces no markers.
    /// </summary>
    [Fact]
    public async Task BuildKnowledgeScopedContextAsync_WhitespaceTranscription_NoMarkersEmitted()
    {
        // Arrange
        var knowledgeId = await SeedKnowledgeWithAttachmentAsync(
            extractedText: null,
            transcriptionText: "   \t  ",
            fileName: "silent.webm");

        // Act
        var results = await _svc.BuildKnowledgeScopedContextAsync(
            knowledgeId, "question", CancellationToken.None);

        // Assert
        Assert.Single(results);
        var content = results[0].Content!;

        Assert.DoesNotContain(SpokenBeginMarker, content);
        Assert.DoesNotContain(SpokenEndMarker, content);
    }

    [Fact]
    public async Task BuildKnowledgeScopedContextAsync_WithStructuredImageFields_IncludesImageAnalysisBlock()
    {
        var knowledgeId = await SeedKnowledgeWithStructuredAttachmentAsync(
            contentType: "image/png",
            fileName: "architecture.png",
            visionDescription: "Architecture diagram showing API, worker, and SQL database",
            visionExtractedText: "API -> Queue -> Worker -> SQL",
            visionTagsJson: "[\"diagram\",\"architecture\",\"api\"]",
            visionObjectsJson: "[\"api\",\"worker\",\"database\"]");

        var results = await _svc.BuildKnowledgeScopedContextAsync(
            knowledgeId, "question", CancellationToken.None);

        Assert.Single(results);
        var content = results[0].Content!;

        Assert.Contains("[Image Analysis: architecture.png]", content);
        Assert.Contains("Architecture diagram showing API, worker, and SQL database", content);
        Assert.Contains("Tags: diagram, architecture, api", content);
        Assert.Contains("Objects detected: api, worker, database", content);
        Assert.Contains("Text from image:", content);
        Assert.Contains("API -> Queue -> Worker -> SQL", content);
    }

    [Fact]
    public async Task BuildKnowledgeScopedContextAsync_WithDocumentLayoutData_IncludesLayoutMarker()
    {
        var knowledgeId = await SeedKnowledgeWithStructuredAttachmentAsync(
            contentType: "application/pdf",
            fileName: "design.pdf",
            extractedText: "Section 1: System overview",
            layoutDataJson: "{\"pages\":[{\"pageNumber\":1}],\"tables\":[]}");

        var results = await _svc.BuildKnowledgeScopedContextAsync(
            knowledgeId, "question", CancellationToken.None);

        Assert.Single(results);
        var content = results[0].Content!;

        Assert.Contains("[Attachment: design.pdf]", content);
        Assert.Contains("Section 1: System overview", content);
        Assert.Contains("Structured layout data available", content);
    }

    [Fact]
    public async Task BuildKnowledgeScopedContextAsync_WithImageMissingAnalysis_IncludesExplicitUnavailableMarker()
    {
        var knowledgeId = await SeedKnowledgeWithStructuredAttachmentAsync(
            contentType: "image/png",
            fileName: "pending-diagram.png");

        var results = await _svc.BuildKnowledgeScopedContextAsync(
            knowledgeId, "question", CancellationToken.None);

        Assert.Single(results);
        var content = results[0].Content!;

        Assert.Contains("[Image: pending-diagram.png", content);
        Assert.Contains("analysis unavailable", content, StringComparison.OrdinalIgnoreCase);
    }
}
