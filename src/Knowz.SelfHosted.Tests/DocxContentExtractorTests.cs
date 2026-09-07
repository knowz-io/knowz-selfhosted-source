using Knowz.Core.Entities;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Knowz.SelfHosted.Tests;

public class DocxContentExtractorTests
{
    private readonly DocxContentExtractor _extractor;
    private readonly ILogger<DocxContentExtractor> _logger;

    public DocxContentExtractorTests()
    {
        _logger = Substitute.For<ILogger<DocxContentExtractor>>();
        _extractor = new DocxContentExtractor(_logger);
    }

    private static FileRecord MakeRecord(string contentType, string fileName = "test.docx")
        => new() { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), FileName = fileName, ContentType = contentType };

    private static MemoryStream CreateDocxWithParagraphs(params string[] paragraphs)
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, true))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document();
            var body = mainPart.Document.AppendChild(new Body());

            foreach (var text in paragraphs)
            {
                var para = body.AppendChild(new Paragraph());
                var run = para.AppendChild(new Run());
                run.AppendChild(new Text(text));
            }

            mainPart.Document.Save();
        }

        ms.Position = 0;
        return ms;
    }

    private static MemoryStream CreateEmptyDocx()
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, true))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document();
            mainPart.Document.AppendChild(new Body());
            mainPart.Document.Save();
        }

        ms.Position = 0;
        return ms;
    }

    // =============================================
    // CanExtract — VERIFY_DOCX_01, VERIFY_DOCX_02
    // =============================================

    [Fact]
    public void CanExtract_ReturnsTrue_ForDocxContentType()
    {
        // VERIFY_DOCX_01
        Assert.True(_extractor.CanExtract(
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document"));
    }

    [Fact]
    public void CanExtract_ReturnsTrue_CaseInsensitive()
    {
        Assert.True(_extractor.CanExtract(
            "Application/VND.Openxmlformats-Officedocument.Wordprocessingml.Document"));
    }

    [Fact]
    public void CanExtract_ReturnsFalse_ForLegacyDoc()
    {
        // VERIFY_DOCX_02
        Assert.False(_extractor.CanExtract("application/msword"));
    }

    [Fact]
    public void CanExtract_ReturnsFalse_ForNull()
    {
        Assert.False(_extractor.CanExtract(null));
    }

    [Fact]
    public void CanExtract_ReturnsFalse_ForEmpty()
    {
        Assert.False(_extractor.CanExtract(""));
    }

    [Fact]
    public void CanExtract_ReturnsFalse_ForTextPlain()
    {
        Assert.False(_extractor.CanExtract("text/plain"));
    }

    [Fact]
    public void CanExtract_ReturnsFalse_ForPdf()
    {
        Assert.False(_extractor.CanExtract("application/pdf"));
    }

    // =============================================
    // ExtractAsync — Multiple paragraphs — VERIFY_DOCX_03
    // =============================================

    [Fact]
    public async Task ExtractAsync_MultipleParagraphs_ReturnsJoinedByNewlines()
    {
        // VERIFY_DOCX_03
        var record = MakeRecord("application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        using var stream = CreateDocxWithParagraphs("First paragraph", "Second paragraph", "Third paragraph");

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.NotNull(result.ExtractedText);
        Assert.Contains("First paragraph", result.ExtractedText);
        Assert.Contains("Second paragraph", result.ExtractedText);
        Assert.Contains("Third paragraph", result.ExtractedText);
        // Should be separated by newlines
        Assert.Equal("First paragraph\nSecond paragraph\nThird paragraph", result.ExtractedText);
    }

    // =============================================
    // ExtractAsync — Truncation — VERIFY_DOCX_04
    // =============================================

    [Fact]
    public async Task ExtractAsync_TruncatesWhenExceedingMaxChars()
    {
        // VERIFY_DOCX_04
        var record = MakeRecord("application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        // Create a DOCX with text content larger than the 2MB extraction cap
        var largeParagraph = new string('B', DocxContentExtractor.MaxExtractionChars + 100_000);
        using var stream = CreateDocxWithParagraphs(largeParagraph);

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.NotNull(result.ExtractedText);
        Assert.True(result.ExtractedText.Length <= DocxContentExtractor.MaxExtractionChars,
            $"Expected <= {DocxContentExtractor.MaxExtractionChars} chars but got {result.ExtractedText.Length}");
    }

    // =============================================
    // ExtractAsync — No body text — VERIFY_DOCX_05
    // =============================================

    [Fact]
    public async Task ExtractAsync_EmptyDocx_ReturnsNoExtractableText()
    {
        // VERIFY_DOCX_05
        var record = MakeRecord("application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        using var stream = CreateEmptyDocx();

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.False(result.Success);
        Assert.Contains("no extractable text", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    // =============================================
    // ExtractAsync — Corrupted DOCX — VERIFY_DOCX_06
    // =============================================

    [Fact]
    public async Task ExtractAsync_CorruptedDocx_ReturnsFailureWithoutThrowing()
    {
        // VERIFY_DOCX_06
        var record = MakeRecord("application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("this is not a DOCX file"));

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    // =============================================
    // ExtractAsync — Empty paragraphs skipped — VERIFY_DOCX_07
    // =============================================

    [Fact]
    public async Task ExtractAsync_SkipsEmptyParagraphs()
    {
        // VERIFY_DOCX_07
        var record = MakeRecord("application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        using var stream = CreateDocxWithParagraphs("First", "", "  ", "Second");

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.NotNull(result.ExtractedText);
        Assert.Equal("First\nSecond", result.ExtractedText);
    }

    // =============================================
    // ExtractAsync — Unsupported content type
    // =============================================

    [Fact]
    public async Task ExtractAsync_UnsupportedContentType_ReturnsFailure()
    {
        var record = MakeRecord("text/plain");
        using var stream = new MemoryStream(new byte[] { 1, 2, 3 });

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.False(result.Success);
        Assert.Equal("Unsupported content type", result.ErrorMessage);
    }

    // =============================================
    // ExtractAsync — Single paragraph
    // =============================================

    [Fact]
    public async Task ExtractAsync_SingleParagraph_ReturnsText()
    {
        var record = MakeRecord("application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        using var stream = CreateDocxWithParagraphs("Hello from DOCX");

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.Equal("Hello from DOCX", result.ExtractedText);
    }

    [Fact]
    public async Task ExtractAsync_SetsNativeFallbackMetadata_OnSuccess()
    {
        var record = MakeRecord("application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        using var stream = CreateDocxWithParagraphs("Hello from DOCX");

        await _extractor.ExtractAsync(record, stream);

        Assert.Equal(2, record.TextExtractionStatus);
        Assert.NotNull(record.TextExtractedAt);
        Assert.Equal("NativeFallback", record.AttachmentAIProvider);
    }

    // =============================================
    // ExtractAsync — Non-seekable stream
    // =============================================

    [Fact]
    public async Task ExtractAsync_NonSeekableStream_BuffersAndExtracts()
    {
        var record = MakeRecord("application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        using var seekableStream = CreateDocxWithParagraphs("Buffered content");
        var bytes = seekableStream.ToArray();
        using var nonSeekableStream = new NonSeekableStream(bytes);

        var result = await _extractor.ExtractAsync(record, nonSeekableStream);

        Assert.True(result.Success);
        Assert.Equal("Buffered content", result.ExtractedText);
    }

    /// <summary>
    /// Helper stream that wraps a byte array but reports CanSeek = false.
    /// </summary>
    private sealed class NonSeekableStream : Stream
    {
        private readonly MemoryStream _inner;

        public NonSeekableStream(byte[] data)
        {
            _inner = new MemoryStream(data);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _inner.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
