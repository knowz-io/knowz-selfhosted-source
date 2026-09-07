using Knowz.Core.Entities;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using D = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace Knowz.SelfHosted.Tests;

public class PowerPointContentExtractorTests
{
    private readonly PowerPointContentExtractor _extractor;
    private readonly ILogger<PowerPointContentExtractor> _logger;

    public PowerPointContentExtractorTests()
    {
        _logger = Substitute.For<ILogger<PowerPointContentExtractor>>();
        _extractor = new PowerPointContentExtractor(_logger);
    }

    private static FileRecord MakeRecord(string contentType, string fileName = "test.pptx")
        => new() { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), FileName = fileName, ContentType = contentType };

    // =============================================
    // Helper: Create in-memory PPTX
    // =============================================

    private static MemoryStream CreatePptx(params (string[] shapeTexts, string? speakerNotes)[] slides)
    {
        var ms = new MemoryStream();
        using (var doc = PresentationDocument.Create(ms, PresentationDocumentType.Presentation, true))
        {
            var presentationPart = doc.AddPresentationPart();
            presentationPart.Presentation = new P.Presentation();
            var slideIdList = presentationPart.Presentation.AppendChild(new SlideIdList());

            uint slideId = 256;
            foreach (var (shapeTexts, speakerNotes) in slides)
            {
                var slidePart = presentationPart.AddNewPart<SlidePart>();

                // Create slide with shapes
                var slide = new Slide(new CommonSlideData(new ShapeTree()));
                var shapeTree = slide.CommonSlideData!.ShapeTree!;

                uint shapeIdx = 1;
                foreach (var text in shapeTexts)
                {
                    var shape = new Shape();
                    shape.Append(new NonVisualShapeProperties(
                        new NonVisualDrawingProperties { Id = shapeIdx, Name = $"Shape{shapeIdx}" },
                        new NonVisualShapeDrawingProperties(),
                        new ApplicationNonVisualDrawingProperties()));
                    shape.Append(new ShapeProperties());

                    var textBody = new TextBody();
                    textBody.Append(new D.BodyProperties());
                    textBody.Append(new D.ListStyle());
                    var paragraph = new D.Paragraph();
                    paragraph.Append(new D.Run(new D.Text(text)));
                    textBody.Append(paragraph);
                    shape.Append(textBody);

                    shapeTree.Append(shape);
                    shapeIdx++;
                }

                slidePart.Slide = slide;
                slidePart.Slide.Save();

                // Add speaker notes if provided
                if (speakerNotes != null)
                {
                    var notesSlidePart = slidePart.AddNewPart<NotesSlidePart>();
                    var notesSlide = new NotesSlide(
                        new CommonSlideData(new ShapeTree()));
                    var notesShapeTree = notesSlide.CommonSlideData!.ShapeTree!;

                    var notesShape = new Shape();
                    notesShape.Append(new NonVisualShapeProperties(
                        new NonVisualDrawingProperties { Id = 1, Name = "Notes" },
                        new NonVisualShapeDrawingProperties(),
                        new ApplicationNonVisualDrawingProperties()));
                    notesShape.Append(new ShapeProperties());

                    var notesTextBody = new TextBody();
                    notesTextBody.Append(new D.BodyProperties());
                    notesTextBody.Append(new D.ListStyle());
                    var notesParagraph = new D.Paragraph();
                    notesParagraph.Append(new D.Run(new D.Text(speakerNotes)));
                    notesTextBody.Append(notesParagraph);
                    notesShape.Append(notesTextBody);

                    notesShapeTree.Append(notesShape);
                    notesSlidePart.NotesSlide = notesSlide;
                    notesSlidePart.NotesSlide.Save();
                }

                slideIdList.Append(new SlideId
                {
                    Id = slideId++,
                    RelationshipId = presentationPart.GetIdOfPart(slidePart)
                });
            }

            presentationPart.Presentation.Save();
        }

        ms.Position = 0;
        return ms;
    }

    private static MemoryStream CreateEmptyPptx()
    {
        var ms = new MemoryStream();
        using (var doc = PresentationDocument.Create(ms, PresentationDocumentType.Presentation, true))
        {
            var presentationPart = doc.AddPresentationPart();
            presentationPart.Presentation = new P.Presentation();
            presentationPart.Presentation.AppendChild(new SlideIdList());
            presentationPart.Presentation.Save();
        }

        ms.Position = 0;
        return ms;
    }

    // =============================================
    // CanExtract
    // =============================================

    [Fact]
    public void Should_CanExtract_ReturnTrue_WhenPptxContentType()
    {
        Assert.True(_extractor.CanExtract(
            "application/vnd.openxmlformats-officedocument.presentationml.presentation"));
    }

    [Fact]
    public void Should_CanExtract_ReturnTrue_WhenPptContentType()
    {
        Assert.True(_extractor.CanExtract("application/vnd.ms-powerpoint"));
    }

    [Fact]
    public void Should_CanExtract_ReturnTrue_WhenCaseInsensitive()
    {
        Assert.True(_extractor.CanExtract(
            "Application/VND.Openxmlformats-Officedocument.Presentationml.Presentation"));
    }

    [Fact]
    public void Should_CanExtract_ReturnFalse_WhenNull()
    {
        Assert.False(_extractor.CanExtract(null));
    }

    [Fact]
    public void Should_CanExtract_ReturnFalse_WhenEmpty()
    {
        Assert.False(_extractor.CanExtract(""));
    }

    [Fact]
    public void Should_CanExtract_ReturnFalse_WhenTextPlain()
    {
        Assert.False(_extractor.CanExtract("text/plain"));
    }

    [Fact]
    public void Should_CanExtract_ReturnFalse_WhenPdf()
    {
        Assert.False(_extractor.CanExtract("application/pdf"));
    }

    // =============================================
    // ExtractAsync — PPTX with slide text
    // =============================================

    [Fact]
    public async Task Should_ExtractText_WhenValidPptx()
    {
        var record = MakeRecord("application/vnd.openxmlformats-officedocument.presentationml.presentation");
        using var stream = CreatePptx(
            (new[] { "Welcome to the presentation" }, null));

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.NotNull(result.ExtractedText);
        Assert.Contains("Welcome to the presentation", result.ExtractedText);
    }

    [Fact]
    public async Task Should_IncludeSlideMarkers_WhenMultipleSlides()
    {
        var record = MakeRecord("application/vnd.openxmlformats-officedocument.presentationml.presentation");
        using var stream = CreatePptx(
            (new[] { "Title Slide" }, null),
            (new[] { "Content Slide" }, null));

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.NotNull(result.ExtractedText);
        Assert.Contains("--- Slide 1 ---", result.ExtractedText);
        Assert.Contains("--- Slide 2 ---", result.ExtractedText);
        Assert.Contains("Title Slide", result.ExtractedText);
        Assert.Contains("Content Slide", result.ExtractedText);
    }

    [Fact]
    public async Task Should_ExtractSpeakerNotes_WhenPresent()
    {
        var record = MakeRecord("application/vnd.openxmlformats-officedocument.presentationml.presentation");
        using var stream = CreatePptx(
            (new[] { "Slide content" }, "These are speaker notes"));

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.NotNull(result.ExtractedText);
        Assert.Contains("Slide content", result.ExtractedText);
        Assert.Contains("These are speaker notes", result.ExtractedText);
    }

    // =============================================
    // ExtractAsync — Empty PPTX
    // =============================================

    [Fact]
    public async Task Should_ReturnFailure_WhenEmptyPptx()
    {
        var record = MakeRecord("application/vnd.openxmlformats-officedocument.presentationml.presentation");
        using var stream = CreateEmptyPptx();

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.False(result.Success);
        Assert.Contains("no extractable text", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    // =============================================
    // ExtractAsync — Legacy PPT returns empty
    // =============================================

    [Fact]
    public async Task Should_ReturnFailure_WhenLegacyPpt()
    {
        var record = MakeRecord("application/vnd.ms-powerpoint", "legacy.ppt");
        using var stream = new MemoryStream(new byte[] { 0xD0, 0xCF, 0x11, 0xE0 }); // OLE header

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.False(result.Success);
    }

    // =============================================
    // ExtractAsync — Corrupted file
    // =============================================

    [Fact]
    public async Task Should_ReturnFailure_WhenCorruptedPptx()
    {
        var record = MakeRecord("application/vnd.openxmlformats-officedocument.presentationml.presentation");
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("not a valid pptx"));

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    // =============================================
    // ExtractAsync — Unsupported content type
    // =============================================

    [Fact]
    public async Task Should_ReturnFailure_WhenUnsupportedContentType()
    {
        var record = MakeRecord("text/plain");
        using var stream = new MemoryStream(new byte[] { 1, 2, 3 });

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.False(result.Success);
        Assert.Equal("Unsupported content type", result.ErrorMessage);
    }

    // =============================================
    // ExtractAsync — Truncation
    // =============================================

    [Fact]
    public async Task Should_TruncateText_WhenExceedingMaxChars()
    {
        var record = MakeRecord("application/vnd.openxmlformats-officedocument.presentationml.presentation");
        var largeText = new string('X', 600_000);
        using var stream = CreatePptx(
            (new[] { largeText }, null),
            (new[] { largeText }, null),
            (new[] { largeText }, null),
            (new[] { largeText }, null));

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.NotNull(result.ExtractedText);
        Assert.True(result.ExtractedText.Length <= PowerPointContentExtractor.MaxExtractionChars,
            $"Expected <= {PowerPointContentExtractor.MaxExtractionChars} chars but got {result.ExtractedText.Length}");
    }

    // =============================================
    // ExtractAsync — Non-seekable stream
    // =============================================

    [Fact]
    public async Task Should_HandleNonSeekableStream_WhenPptx()
    {
        var record = MakeRecord("application/vnd.openxmlformats-officedocument.presentationml.presentation");
        using var seekableStream = CreatePptx(
            (new[] { "buffered content" }, null));
        var bytes = seekableStream.ToArray();
        using var nonSeekableStream = new NonSeekableStream(bytes);

        var result = await _extractor.ExtractAsync(record, nonSeekableStream);

        Assert.True(result.Success);
        Assert.Contains("buffered content", result.ExtractedText!);
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
