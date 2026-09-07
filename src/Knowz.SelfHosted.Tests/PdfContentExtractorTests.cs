using Knowz.Core.Entities;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;

namespace Knowz.SelfHosted.Tests;

public class PdfContentExtractorTests
{
    private readonly PdfContentExtractor _extractor;
    private readonly ILogger<PdfContentExtractor> _logger;

    public PdfContentExtractorTests()
    {
        _logger = Substitute.For<ILogger<PdfContentExtractor>>();
        _extractor = new PdfContentExtractor(_logger);
    }

    private static FileRecord MakeRecord(string contentType, string fileName = "test.pdf")
        => new() { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), FileName = fileName, ContentType = contentType };

    private static MemoryStream CreatePdfWithText(params string[] pageTexts)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);

        foreach (var text in pageTexts)
        {
            var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            page.AddText(text, 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), font);
        }

        var bytes = builder.Build();
        return new MemoryStream(bytes);
    }

    // =============================================
    // CanExtract — VERIFY_PDF_01, VERIFY_PDF_02
    // =============================================

    [Fact]
    public void CanExtract_ReturnsTrue_ForApplicationPdf()
    {
        // VERIFY_PDF_01
        Assert.True(_extractor.CanExtract("application/pdf"));
    }

    [Fact]
    public void CanExtract_ReturnsTrue_ForApplicationPdf_CaseInsensitive()
    {
        Assert.True(_extractor.CanExtract("Application/PDF"));
    }

    [Fact]
    public void CanExtract_ReturnsFalse_ForTextPlain()
    {
        // VERIFY_PDF_02
        Assert.False(_extractor.CanExtract("text/plain"));
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
    public void CanExtract_ReturnsFalse_ForImagePng()
    {
        Assert.False(_extractor.CanExtract("image/png"));
    }

    // =============================================
    // ExtractAsync — Multi-page — VERIFY_PDF_03
    // =============================================

    [Fact]
    public async Task ExtractAsync_MultiPagePdf_ReturnsAllPagesWithDoubleNewlineSeparator()
    {
        // VERIFY_PDF_03
        var record = MakeRecord("application/pdf");
        using var stream = CreatePdfWithText("Page one text", "Page two text", "Page three text");

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.NotNull(result.ExtractedText);
        Assert.Contains("Page one text", result.ExtractedText);
        Assert.Contains("Page two text", result.ExtractedText);
        Assert.Contains("Page three text", result.ExtractedText);
        // Pages should be separated by double newlines
        Assert.Contains("\n\n", result.ExtractedText);
    }

    // =============================================
    // ExtractAsync — Truncation — VERIFY_PDF_04
    // =============================================

    [Fact]
    public async Task ExtractAsync_TruncatesWhenExceedingMaxChars()
    {
        // VERIFY_PDF_04
        // Create a PDF with a huge text page exceeding the 2MB extraction cap
        var record = MakeRecord("application/pdf");
        var largeText = new string('A', PdfContentExtractor.MaxExtractionChars + 100_000);
        using var stream = CreatePdfWithText(largeText);

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.NotNull(result.ExtractedText);
        Assert.True(result.ExtractedText.Length <= PdfContentExtractor.MaxExtractionChars,
            $"Expected <= {PdfContentExtractor.MaxExtractionChars} chars but got {result.ExtractedText.Length}");
    }

    // =============================================
    // ExtractAsync — Password-protected — VERIFY_PDF_05
    // =============================================

    [Fact]
    public async Task ExtractAsync_PasswordProtectedPdf_ReturnsFailure()
    {
        // VERIFY_PDF_05
        // PdfPig throws on encrypted PDFs. We create a minimal invalid encrypted-looking PDF.
        // The simplest way is to provide bytes that PdfPig will reject with a password/encrypt message.
        var record = MakeRecord("application/pdf");
        // A real encrypted PDF would have /Encrypt in its trailer
        // We simulate by using bytes PdfPig can't open - let's just create garbage with PDF header
        // that has encryption markers
        var encryptedPdfBytes = System.Text.Encoding.ASCII.GetBytes(
            "%PDF-1.4\n1 0 obj<</Type/Catalog/Encrypt 2 0 R>>endobj\n" +
            "2 0 obj<</Filter/Standard/V 1/R 2/O(oooooooooooooooo)/U(uuuuuuuuuuuuuuuu)/P -3904>>endobj\n" +
            "xref\n0 3\n0000000000 65535 f \n0000000009 00000 n \n0000000058 00000 n \n" +
            "trailer<</Size 3/Root 1 0 R/Encrypt 2 0 R>>\nstartxref\n178\n%%EOF");
        using var stream = new MemoryStream(encryptedPdfBytes);

        var result = await _extractor.ExtractAsync(record, stream);

        // Should not throw, should return failure
        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    // =============================================
    // ExtractAsync — Image-only PDF — VERIFY_PDF_06
    // =============================================

    [Fact]
    public async Task ExtractAsync_ImageOnlyPdf_ReturnsNoExtractableText()
    {
        // VERIFY_PDF_06
        // Create a PDF with no text content - just empty pages
        var builder = new PdfDocumentBuilder();
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4); // empty page, no text
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4); // another empty page
        var bytes = builder.Build();
        using var stream = new MemoryStream(bytes);

        var record = MakeRecord("application/pdf");
        var result = await _extractor.ExtractAsync(record, stream);

        Assert.False(result.Success);
        Assert.Contains("no extractable text", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    // =============================================
    // ExtractAsync — Corrupted PDF — VERIFY_PDF_07
    // =============================================

    [Fact]
    public async Task ExtractAsync_CorruptedPdf_ReturnsFailureWithoutThrowing()
    {
        // VERIFY_PDF_07
        var record = MakeRecord("application/pdf");
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("this is not a PDF file at all"));

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    // =============================================
    // ExtractAsync — Non-seekable stream — VERIFY_PDF_08
    // =============================================

    [Fact]
    public async Task ExtractAsync_NonSeekableStream_BuffersAndExtracts()
    {
        // VERIFY_PDF_08
        var record = MakeRecord("application/pdf");
        using var seekableStream = CreatePdfWithText("Non-seekable test content");
        var bytes = seekableStream.ToArray();
        using var nonSeekableStream = new NonSeekableStream(bytes);

        var result = await _extractor.ExtractAsync(record, nonSeekableStream);

        Assert.True(result.Success);
        Assert.NotNull(result.ExtractedText);
        Assert.Contains("Non-seekable test content", result.ExtractedText);
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
    // Single page extraction
    // =============================================

    [Fact]
    public async Task ExtractAsync_SinglePagePdf_ReturnsText()
    {
        var record = MakeRecord("application/pdf");
        using var stream = CreatePdfWithText("Hello from PDF");

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.NotNull(result.ExtractedText);
        Assert.Contains("Hello from PDF", result.ExtractedText);
    }

    [Fact]
    public async Task ExtractAsync_SetsNativeFallbackMetadata_OnSuccess()
    {
        var record = MakeRecord("application/pdf");
        using var stream = CreatePdfWithText("Hello from PDF");

        await _extractor.ExtractAsync(record, stream);

        Assert.Equal(2, record.TextExtractionStatus);
        Assert.NotNull(record.TextExtractedAt);
        Assert.Equal("NativeFallback", record.AttachmentAIProvider);
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
