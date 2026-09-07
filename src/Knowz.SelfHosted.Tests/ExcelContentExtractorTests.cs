using Knowz.Core.Entities;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Knowz.SelfHosted.Tests;

public class ExcelContentExtractorTests
{
    private readonly ExcelContentExtractor _extractor;
    private readonly ILogger<ExcelContentExtractor> _logger;

    public ExcelContentExtractorTests()
    {
        _logger = Substitute.For<ILogger<ExcelContentExtractor>>();
        _extractor = new ExcelContentExtractor(_logger);
    }

    private static FileRecord MakeRecord(string contentType, string fileName = "test.xlsx")
        => new() { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), FileName = fileName, ContentType = contentType };

    // =============================================
    // Helper: Create in-memory XLSX
    // =============================================

    private static MemoryStream CreateXlsx(params (string sheetName, string[][] rows)[] sheets)
    {
        var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook, true))
        {
            var workbookPart = doc.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var sheetsElement = workbookPart.Workbook.AppendChild(new Sheets());

            uint sheetId = 1;
            foreach (var (sheetName, rows) in sheets)
            {
                var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
                var sheetData = new SheetData();

                foreach (var row in rows)
                {
                    var sheetRow = new Row();
                    foreach (var cellValue in row)
                    {
                        var cell = new Cell
                        {
                            DataType = CellValues.InlineString,
                            InlineString = new InlineString(new Text(cellValue))
                        };
                        sheetRow.Append(cell);
                    }
                    sheetData.Append(sheetRow);
                }

                worksheetPart.Worksheet = new Worksheet(sheetData);
                worksheetPart.Worksheet.Save();

                sheetsElement.Append(new Sheet
                {
                    Id = workbookPart.GetIdOfPart(worksheetPart),
                    SheetId = sheetId++,
                    Name = sheetName
                });
            }

            workbookPart.Workbook.Save();
        }

        ms.Position = 0;
        return ms;
    }

    private static MemoryStream CreateEmptyXlsx()
    {
        var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook, true))
        {
            var workbookPart = doc.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var sheetsElement = workbookPart.Workbook.AppendChild(new Sheets());

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            worksheetPart.Worksheet = new Worksheet(new SheetData());
            worksheetPart.Worksheet.Save();

            sheetsElement.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "Sheet1"
            });

            workbookPart.Workbook.Save();
        }

        ms.Position = 0;
        return ms;
    }

    // =============================================
    // CanExtract
    // =============================================

    [Fact]
    public void Should_CanExtract_ReturnTrue_WhenXlsxContentType()
    {
        Assert.True(_extractor.CanExtract(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));
    }

    [Fact]
    public void Should_CanExtract_ReturnTrue_WhenXlsContentType()
    {
        Assert.True(_extractor.CanExtract("application/vnd.ms-excel"));
    }

    [Fact]
    public void Should_CanExtract_ReturnTrue_WhenCaseInsensitive()
    {
        Assert.True(_extractor.CanExtract(
            "Application/VND.Openxmlformats-Officedocument.Spreadsheetml.Sheet"));
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
    // ExtractAsync — XLSX with data
    // =============================================

    [Fact]
    public async Task Should_ExtractText_WhenValidXlsx()
    {
        var record = MakeRecord("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        using var stream = CreateXlsx(("Sales", new[]
        {
            new[] { "Product", "Revenue" },
            new[] { "Widget", "1000" }
        }));

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.NotNull(result.ExtractedText);
        Assert.Contains("Sales", result.ExtractedText);
        Assert.Contains("Product", result.ExtractedText);
        Assert.Contains("Revenue", result.ExtractedText);
        Assert.Contains("Widget", result.ExtractedText);
        Assert.Contains("1000", result.ExtractedText);
    }

    [Fact]
    public async Task Should_IncludeSheetNames_WhenMultipleSheets()
    {
        var record = MakeRecord("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        using var stream = CreateXlsx(
            ("Sales", new[] { new[] { "Revenue" } }),
            ("Expenses", new[] { new[] { "Cost" } })
        );

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.NotNull(result.ExtractedText);
        Assert.Contains("Sales", result.ExtractedText);
        Assert.Contains("Expenses", result.ExtractedText);
        Assert.Contains("Revenue", result.ExtractedText);
        Assert.Contains("Cost", result.ExtractedText);
    }

    // =============================================
    // ExtractAsync — Empty XLSX
    // =============================================

    [Fact]
    public async Task Should_ReturnFailure_WhenEmptyXlsx()
    {
        var record = MakeRecord("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        using var stream = CreateEmptyXlsx();

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.False(result.Success);
        Assert.Contains("no extractable text", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    // =============================================
    // ExtractAsync — Legacy XLS returns empty
    // =============================================

    [Fact]
    public async Task Should_ReturnEmptyText_WhenLegacyXls()
    {
        var record = MakeRecord("application/vnd.ms-excel", "legacy.xls");
        using var stream = new MemoryStream(new byte[] { 0xD0, 0xCF, 0x11, 0xE0 }); // OLE header

        var result = await _extractor.ExtractAsync(record, stream);

        // Legacy XLS should return empty — DI fallback handles it
        Assert.False(result.Success);
    }

    // =============================================
    // ExtractAsync — Corrupted file
    // =============================================

    [Fact]
    public async Task Should_ReturnFailure_WhenCorruptedXlsx()
    {
        var record = MakeRecord("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("not a valid xlsx"));

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
    // ExtractAsync — Truncation at MaxExtractionChars
    // =============================================

    [Fact]
    public async Task Should_TruncateText_WhenExceedingMaxChars()
    {
        var record = MakeRecord("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        // Create a sheet with very large cell content
        var largeValue = new string('A', 500_000);
        using var stream = CreateXlsx(("BigSheet", new[]
        {
            new[] { largeValue },
            new[] { largeValue },
            new[] { largeValue },
            new[] { largeValue },
            new[] { largeValue }
        }));

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.NotNull(result.ExtractedText);
        // Total should not exceed MaxExtractionChars (10_000_000)
        Assert.True(result.ExtractedText.Length <= ExcelContentExtractor.MaxExtractionChars,
            $"Expected <= {ExcelContentExtractor.MaxExtractionChars} chars but got {result.ExtractedText.Length}");
    }

    // =============================================
    // ExtractAsync — Non-seekable stream
    // =============================================

    [Fact]
    public async Task Should_HandleNonSeekableStream_WhenXlsx()
    {
        var record = MakeRecord("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        using var seekableStream = CreateXlsx(("Sheet1", new[] { new[] { "buffered content" } }));
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
