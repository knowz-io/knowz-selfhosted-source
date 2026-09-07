using Knowz.Core.Entities;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class TextFileContentExtractorTests
{
    private readonly TextFileContentExtractor _extractor;

    public TextFileContentExtractorTests()
    {
        var logger = Substitute.For<ILogger<TextFileContentExtractor>>();
        _extractor = new TextFileContentExtractor(logger);
    }

    private static FileRecord MakeRecord(string contentType, string fileName = "test.txt")
        => new() { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), FileName = fileName, ContentType = contentType };

    private static MemoryStream MakeStream(string content)
        => new(System.Text.Encoding.UTF8.GetBytes(content));

    // =============================================
    // CanExtract
    // =============================================

    [Theory]
    [InlineData("text/plain", true)]
    [InlineData("text/markdown", true)]
    [InlineData("text/csv", true)]
    [InlineData("text/html", true)]
    [InlineData("text/xml", true)]
    [InlineData("application/json", true)]
    [InlineData("application/xml", true)]
    [InlineData("text/x-custom", true)]      // Wildcard text/* support
    [InlineData("application/pdf", false)]
    [InlineData("image/png", false)]
    [InlineData("audio/mp3", false)]
    [InlineData("video/mp4", false)]
    [InlineData("application/octet-stream", false)]
    public void CanExtract_ReturnsExpected(string contentType, bool expected)
    {
        // VERIFY_FUNC_01, VERIFY_FUNC_02, VERIFY_FUNC_03
        Assert.Equal(expected, _extractor.CanExtract(contentType));
    }

    [Fact]
    public void CanExtract_ReturnsFalse_ForNull()
    {
        // VERIFY_FUNC_04
        Assert.False(_extractor.CanExtract(null));
    }

    [Fact]
    public void CanExtract_ReturnsFalse_ForEmpty()
    {
        Assert.False(_extractor.CanExtract(""));
    }

    // =============================================
    // ExtractAsync
    // =============================================

    [Fact]
    public async Task ExtractAsync_ReturnsContent_ForUtf8TextFile()
    {
        // VERIFY_FUNC_05
        var record = MakeRecord("text/plain");
        using var stream = MakeStream("Hello, World!");

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.Equal("Hello, World!", result.ExtractedText);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task ExtractAsync_ReturnsFailure_ForEmptyFile()
    {
        // VERIFY_FUNC_07
        var record = MakeRecord("text/plain");
        using var stream = new MemoryStream();

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.False(result.Success);
        Assert.Equal("File is empty", result.ErrorMessage);
    }

    [Fact]
    public async Task ExtractAsync_TruncatesLargeFiles()
    {
        // VERIFY_FUNC_06
        var record = MakeRecord("text/plain");
        // Create content larger than 10M chars
        var largeContent = new string('x', 10_100_000);
        using var stream = MakeStream(largeContent);

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.NotNull(result.ExtractedText);
        // Should be truncated to MaxExtractionChars (10M)
        Assert.True(result.ExtractedText.Length <= 10_000_000,
            $"Expected <= 10000000 chars but got {result.ExtractedText.Length}");
    }

    [Fact]
    public async Task ExtractAsync_ReturnsFailure_ForUnsupportedType()
    {
        var record = MakeRecord("application/pdf");
        using var stream = MakeStream("fake pdf content");

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.False(result.Success);
        Assert.Equal("Unsupported content type", result.ErrorMessage);
    }

    [Fact]
    public async Task ExtractAsync_HandlesJsonContent()
    {
        var record = MakeRecord("application/json", "data.json");
        var json = "{\"name\": \"test\", \"value\": 42}";
        using var stream = MakeStream(json);

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.Equal(json, result.ExtractedText);
    }

    [Fact]
    public async Task ExtractAsync_HandlesCsvContent()
    {
        var record = MakeRecord("text/csv", "data.csv");
        var csv = "name,age\nAlice,30\nBob,25";
        using var stream = MakeStream(csv);

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.Equal(csv, result.ExtractedText);
    }

    [Fact]
    public async Task ExtractAsync_HandlesMarkdownContent()
    {
        var record = MakeRecord("text/markdown", "readme.md");
        var md = "# Title\n\nSome **bold** text.";
        using var stream = MakeStream(md);

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.Equal(md, result.ExtractedText);
    }

    [Fact]
    public async Task ExtractAsync_TrimsWhitespace()
    {
        var record = MakeRecord("text/plain");
        using var stream = MakeStream("  some content  \n\n");

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.Equal("some content", result.ExtractedText);
    }

    [Fact]
    public async Task ExtractAsync_DetectsBom()
    {
        // VERIFY_FUNC_08
        var record = MakeRecord("text/plain");
        var bom = new byte[] { 0xEF, 0xBB, 0xBF }; // UTF-8 BOM
        var content = System.Text.Encoding.UTF8.GetBytes("BOM content");
        var combined = new byte[bom.Length + content.Length];
        bom.CopyTo(combined, 0);
        content.CopyTo(combined, bom.Length);
        using var stream = new MemoryStream(combined);

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.Equal("BOM content", result.ExtractedText);
    }

    [Fact]
    public async Task ExtractAsync_HandlesHtmlContent()
    {
        var record = MakeRecord("text/html", "page.html");
        var html = "<html><body><p>Hello</p></body></html>";
        using var stream = MakeStream(html);

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.Equal(html, result.ExtractedText);
    }

    [Fact]
    public async Task ExtractAsync_HandlesXmlContent()
    {
        var record = MakeRecord("text/xml", "data.xml");
        var xml = "<root><item>value</item></root>";
        using var stream = MakeStream(xml);

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.Equal(xml, result.ExtractedText);
    }

    [Fact]
    public async Task ExtractAsync_HandlesApplicationXml()
    {
        var record = MakeRecord("application/xml", "data.xml");
        var xml = "<config><key>value</key></config>";
        using var stream = MakeStream(xml);

        var result = await _extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.Equal(xml, result.ExtractedText);
    }
}
