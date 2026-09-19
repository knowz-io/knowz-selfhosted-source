using Knowz.Core.Entities;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class CompositeContentExtractorTests
{
    private readonly TextFileContentExtractor _textExtractor;
    private readonly PdfContentExtractor _pdfExtractor;
    private readonly DocxContentExtractor _docxExtractor;
    private readonly CompositeContentExtractor _composite;

    public CompositeContentExtractorTests()
    {
        _textExtractor = new TextFileContentExtractor(
            Substitute.For<ILogger<TextFileContentExtractor>>());
        _pdfExtractor = new PdfContentExtractor(
            Substitute.For<ILogger<PdfContentExtractor>>());
        _docxExtractor = new DocxContentExtractor(
            Substitute.For<ILogger<DocxContentExtractor>>());

        // Basic composite with native extractors only (no AI)
        _composite = new CompositeContentExtractor(new IFileContentExtractor[]
        {
            _textExtractor,
            _pdfExtractor,
            _docxExtractor
        });
    }

    private static FileRecord MakeRecord(string contentType, string fileName = "test")
        => new() { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), FileName = fileName, ContentType = contentType };

    // =============================================
    // CanExtract — VERIFY_COMP_01, VERIFY_COMP_02, VERIFY_COMP_03
    // =============================================

    [Fact]
    public void CanExtract_ReturnsTrue_ForPdf()
    {
        // VERIFY_COMP_01
        Assert.True(_composite.CanExtract("application/pdf"));
    }

    [Fact]
    public void CanExtract_ReturnsTrue_ForTextPlain()
    {
        // VERIFY_COMP_02
        Assert.True(_composite.CanExtract("text/plain"));
    }

    [Fact]
    public void CanExtract_ReturnsTrue_ForDocx()
    {
        Assert.True(_composite.CanExtract(
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document"));
    }

    [Fact]
    public void CanExtract_ReturnsFalse_ForImagePng()
    {
        // VERIFY_COMP_03
        Assert.False(_composite.CanExtract("image/png"));
    }

    [Fact]
    public void CanExtract_ReturnsFalse_ForNull()
    {
        Assert.False(_composite.CanExtract(null));
    }

    [Fact]
    public void CanExtract_ReturnsFalse_ForAudioMp3()
    {
        Assert.False(_composite.CanExtract("audio/mp3"));
    }

    // =============================================
    // ExtractAsync — Delegation — VERIFY_COMP_04
    // =============================================

    [Fact]
    public async Task ExtractAsync_TextPlain_DelegatesToTextExtractor()
    {
        var record = MakeRecord("text/plain", "test.txt");
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("Hello text"));

        var result = await _composite.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.Equal("Hello text", result.ExtractedText);
    }

    [Fact]
    public async Task ExtractAsync_Json_DelegatesToTextExtractor()
    {
        var record = MakeRecord("application/json", "data.json");
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"key\":\"value\"}"));

        var result = await _composite.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.Equal("{\"key\":\"value\"}", result.ExtractedText);
    }

    // =============================================
    // ExtractAsync — Unknown type — VERIFY_COMP_05
    // =============================================

    [Fact]
    public async Task ExtractAsync_UnknownType_ReturnsNoExtractorAvailable()
    {
        // VERIFY_COMP_05
        var record = MakeRecord("image/png", "photo.png");
        using var stream = new MemoryStream(new byte[] { 1, 2, 3 });

        var result = await _composite.ExtractAsync(record, stream);

        Assert.False(result.Success);
        Assert.Equal("No extractor available for this content type", result.ErrorMessage);
    }

    // =============================================
    // Empty extractors list
    // =============================================

    [Fact]
    public void CanExtract_WithNoExtractors_ReturnsFalse()
    {
        var empty = new CompositeContentExtractor(Array.Empty<IFileContentExtractor>());
        Assert.False(empty.CanExtract("text/plain"));
    }

    [Fact]
    public async Task ExtractAsync_WithNoExtractors_ReturnsNoExtractorAvailable()
    {
        var empty = new CompositeContentExtractor(Array.Empty<IFileContentExtractor>());
        var record = MakeRecord("text/plain");
        using var stream = new MemoryStream(new byte[] { 1 });

        var result = await empty.ExtractAsync(record, stream);

        Assert.False(result.Success);
        Assert.Equal("No extractor available for this content type", result.ErrorMessage);
    }

    // =============================================
    // VERIFY: CompositeContentExtractor tries AI-powered extractors before native fallbacks
    // =============================================

    [Fact]
    public async Task Should_TryAIPoweredExtractor_BeforeNativeFallback_ForPdf()
    {
        // Simulates the production ordering: DocIntelligence (AI) before PdfPig (native)
        var mockProvider = Substitute.For<IAttachmentAIProvider>();
        mockProvider.ProviderName.Returns("Platform");
        mockProvider.ExtractDocumentAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentExtractionResult(
                Success: true,
                ExtractedText: "AI-extracted text"));

        var diExtractor = new DocumentIntelligenceContentExtractor(
            mockProvider,
            Substitute.For<ILogger<DocumentIntelligenceContentExtractor>>());

        var pdfExtractor = new PdfContentExtractor(
            Substitute.For<ILogger<PdfContentExtractor>>());

        // AI-powered extractor BEFORE native fallback (matches production ordering)
        var composite = new CompositeContentExtractor(new IFileContentExtractor[]
        {
            diExtractor,   // AI-powered — should be tried first
            pdfExtractor   // Native fallback — should not be reached
        });

        var record = MakeRecord("application/pdf", "doc.pdf");
        using var stream = new MemoryStream(new byte[] { 0x25, 0x50, 0x44, 0x46 });

        var result = await composite.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.Equal("AI-extracted text", result.ExtractedText);

        // Verify the AI provider was called (proving AI extractor was tried first)
        await mockProvider.Received(1).ExtractDocumentAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // =============================================
    // VERIFY: PDF with NoOp provider falls through to PdfPig native fallback
    // =============================================

    [Fact]
    public void Should_FallThroughToNativeExtractor_WhenProviderIsNoOp()
    {
        var noOp = new NoOpAttachmentAIProvider();
        var diExtractor = new DocumentIntelligenceContentExtractor(
            noOp,
            Substitute.For<ILogger<DocumentIntelligenceContentExtractor>>());

        var pdfExtractor = new PdfContentExtractor(
            Substitute.For<ILogger<PdfContentExtractor>>());

        var composite = new CompositeContentExtractor(new IFileContentExtractor[]
        {
            diExtractor,   // NoOp — CanExtract returns false for all types
            pdfExtractor   // Native fallback — should handle PDF
        });

        // DocIntelligence with NoOp should NOT claim PDF
        Assert.False(diExtractor.CanExtract("application/pdf"));
        // But the composite should still handle PDF via PdfPig fallback
        Assert.True(composite.CanExtract("application/pdf"));
    }

    // =============================================
    // VERIFY: DOCX prefers Document Intelligence when the provider can extract documents
    // =============================================

    [Fact]
    public void Should_DocxPreferDocumentIntelligence_WhenProviderHasDocumentCapability()
    {
        var mockProvider = Substitute.For<IAttachmentAIProvider>();
        mockProvider.ProviderName.Returns("AzureDocumentIntelligence");

        var diExtractor = new DocumentIntelligenceContentExtractor(
            mockProvider,
            Substitute.For<ILogger<DocumentIntelligenceContentExtractor>>());
        var docxExtractor = new DocxContentExtractor(
            Substitute.For<ILogger<DocxContentExtractor>>());

        var composite = new CompositeContentExtractor(new IFileContentExtractor[]
        {
            diExtractor,
            docxExtractor
        });

        var docxType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

        Assert.True(diExtractor.CanExtract(docxType));
        Assert.True(docxExtractor.CanExtract(docxType));
        Assert.True(composite.CanExtract(docxType));
    }

    // =============================================
    // R5 fall-through (NodeID SH_AnydocContentExtractor) — declining extractors
    // =============================================

    /// <summary>Test double standing in for AnydocContentExtractor: claims a type and declines.</summary>
    private sealed class FakeDecliningExtractor : IFileContentExtractor
    {
        private readonly string _contentType;
        private readonly bool _decline;
        private readonly bool _consumeStream;
        public int Calls { get; private set; }
        public string? SeenContent { get; private set; }

        public FakeDecliningExtractor(string contentType, bool decline, bool consumeStream = false)
        {
            _contentType = contentType;
            _decline = decline;
            _consumeStream = consumeStream;
        }

        public bool MayDecline => true;

        public bool CanExtract(string? contentType) =>
            string.Equals(contentType, _contentType, StringComparison.OrdinalIgnoreCase);

        public async Task<FileExtractionResult> ExtractAsync(
            FileRecord fileRecord, Stream fileStream, CancellationToken ct = default)
        {
            Calls++;
            if (_consumeStream)
            {
                using var reader = new StreamReader(fileStream, leaveOpen: true);
                SeenContent = await reader.ReadToEndAsync(ct);
            }

            return _decline
                ? new FileExtractionResult(false, ErrorMessage: "needs-ocr", Declined: true)
                : new FileExtractionResult(true, ExtractedText: "anydoc markdown");
        }
    }

    /// <summary>Legacy-semantics double: fails WITHOUT declining, so the chain must stop.</summary>
    private sealed class FakeFailingExtractor : IFileContentExtractor
    {
        private readonly string _contentType;
        public FakeFailingExtractor(string contentType) => _contentType = contentType;

        public bool CanExtract(string? contentType) =>
            string.Equals(contentType, _contentType, StringComparison.OrdinalIgnoreCase);

        public Task<FileExtractionResult> ExtractAsync(
            FileRecord fileRecord, Stream fileStream, CancellationToken ct = default)
            => Task.FromResult(new FileExtractionResult(false, ErrorMessage: "hard failure"));
    }

    /// <summary>Reads the stream and echoes what it saw — proves the rewind between attempts.</summary>
    private sealed class EchoExtractor : IFileContentExtractor
    {
        private readonly string _contentType;
        public int Calls { get; private set; }
        public EchoExtractor(string contentType) => _contentType = contentType;

        public bool CanExtract(string? contentType) =>
            string.Equals(contentType, _contentType, StringComparison.OrdinalIgnoreCase);

        public async Task<FileExtractionResult> ExtractAsync(
            FileRecord fileRecord, Stream fileStream, CancellationToken ct = default)
        {
            Calls++;
            using var reader = new StreamReader(fileStream, leaveOpen: true);
            var text = await reader.ReadToEndAsync(ct);
            return new FileExtractionResult(true, ExtractedText: text);
        }
    }

    [Fact]
    public async Task Should_FallThroughToDocumentIntelligence_WhenAnydocDeclines()
    {
        // DISCRIMINATING TEST for R5 — fails against the pre-change composite, which returned the
        // first matching extractor's result verbatim (a decline would have KILLED extraction).
        var mockProvider = Substitute.For<IAttachmentAIProvider>();
        mockProvider.ProviderName.Returns("Platform");
        mockProvider.ExtractDocumentAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentExtractionResult(Success: true, ExtractedText: "AI-extracted text"));

        var anydoc = new FakeDecliningExtractor("application/pdf", decline: true);
        var diExtractor = new DocumentIntelligenceContentExtractor(
            mockProvider, Substitute.For<ILogger<DocumentIntelligenceContentExtractor>>());

        var composite = new CompositeContentExtractor(new IFileContentExtractor[] { anydoc, diExtractor });

        var record = MakeRecord("application/pdf", "doc.pdf");
        using var stream = new MemoryStream(new byte[] { 0x25, 0x50, 0x44, 0x46 });

        var result = await composite.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.Equal("AI-extracted text", result.ExtractedText);
        Assert.False(result.Declined);
        Assert.Equal(1, anydoc.Calls);
        await mockProvider.Received(1).ExtractDocumentAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_ShortCircuit_WhenAnydocSucceeds()
    {
        var mockProvider = Substitute.For<IAttachmentAIProvider>();
        mockProvider.ProviderName.Returns("Platform");
        mockProvider.ExtractDocumentAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentExtractionResult(Success: true, ExtractedText: "AI-extracted text"));

        var anydoc = new FakeDecliningExtractor("application/pdf", decline: false);
        var diExtractor = new DocumentIntelligenceContentExtractor(
            mockProvider, Substitute.For<ILogger<DocumentIntelligenceContentExtractor>>());

        var composite = new CompositeContentExtractor(new IFileContentExtractor[] { anydoc, diExtractor });

        var result = await composite.ExtractAsync(
            MakeRecord("application/pdf", "doc.pdf"), new MemoryStream(new byte[] { 1, 2, 3 }));

        Assert.True(result.Success);
        Assert.Equal("anydoc markdown", result.ExtractedText);
        await mockProvider.DidNotReceive().ExtractDocumentAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_StopChain_WhenExtractorFailsWithoutDeclining()
    {
        // Legacy semantics preserved for PdfContentExtractor et al.
        var failing = new FakeFailingExtractor("application/pdf");
        var echo = new EchoExtractor("application/pdf");
        var composite = new CompositeContentExtractor(new IFileContentExtractor[] { failing, echo });

        var result = await composite.ExtractAsync(
            MakeRecord("application/pdf"), new MemoryStream(new byte[] { 1 }));

        Assert.False(result.Success);
        Assert.Equal("hard failure", result.ErrorMessage);
        Assert.Equal(0, echo.Calls);
    }

    [Fact]
    public async Task Should_RewindStream_BetweenAttempts()
    {
        var anydoc = new FakeDecliningExtractor("application/pdf", decline: true, consumeStream: true);
        var echo = new EchoExtractor("application/pdf");
        var composite = new CompositeContentExtractor(new IFileContentExtractor[] { anydoc, echo });

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("full document body"));
        var result = await composite.ExtractAsync(MakeRecord("application/pdf"), stream);

        Assert.Equal("full document body", anydoc.SeenContent);
        Assert.True(result.Success);
        Assert.Equal("full document body", result.ExtractedText);
    }

    [Fact]
    public async Task Should_ReturnLastResult_WhenEveryCandidateDeclines()
    {
        var first = new FakeDecliningExtractor("application/pdf", decline: true);
        var second = new FakeDecliningExtractor("application/pdf", decline: true);
        var composite = new CompositeContentExtractor(new IFileContentExtractor[] { first, second });

        var result = await composite.ExtractAsync(
            MakeRecord("application/pdf"), new MemoryStream(new byte[] { 1 }));

        Assert.False(result.Success);
        Assert.True(result.Declined);
        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);
    }
}
