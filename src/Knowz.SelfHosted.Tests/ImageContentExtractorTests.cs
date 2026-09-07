using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class ImageContentExtractorTests
{
    private readonly ILogger<ImageContentExtractor> _logger;
    private readonly IAttachmentAIProvider _mockProvider;

    public ImageContentExtractorTests()
    {
        _logger = Substitute.For<ILogger<ImageContentExtractor>>();
        _mockProvider = Substitute.For<IAttachmentAIProvider>();
    }

    private ImageContentExtractor CreateExtractor(IAttachmentAIProvider? provider = null)
        => new(provider ?? _mockProvider, _logger);

    private static FileRecord MakeRecord(string contentType, string fileName = "test.png")
        => new() { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), FileName = fileName, ContentType = contentType };

    // =============================================
    // CanExtract
    // =============================================

    [Theory]
    [InlineData("image/png", true)]
    [InlineData("image/jpeg", true)]
    [InlineData("image/gif", true)]
    [InlineData("image/webp", true)]
    [InlineData("image/bmp", true)]
    [InlineData("image/tiff", true)]
    [InlineData("IMAGE/PNG", true)]
    [InlineData("Image/Jpeg", true)]
    [InlineData("text/plain", false)]
    [InlineData("application/pdf", false)]
    [InlineData("audio/mp3", false)]
    [InlineData("video/mp4", false)]
    public void Should_CanExtract_ReturnExpected(string contentType, bool expected)
    {
        var extractor = CreateExtractor();
        Assert.Equal(expected, extractor.CanExtract(contentType));
    }

    [Fact]
    public void Should_CanExtract_ReturnFalse_WhenNull()
    {
        var extractor = CreateExtractor();
        Assert.False(extractor.CanExtract(null));
    }

    [Fact]
    public void Should_CanExtract_ReturnFalse_WhenEmpty()
    {
        var extractor = CreateExtractor();
        Assert.False(extractor.CanExtract(""));
    }

    // =============================================
    // ExtractAsync — delegates to IAttachmentAIProvider
    // =============================================

    [Fact]
    public async Task Should_DelegateToProvider_WhenExtractingImage()
    {
        _mockProvider.AnalyzeImageAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new VisionAnalysisResult(
                Success: true,
                ExtractedText: "Analysis result from provider",
                Caption: "A test image"));

        var extractor = CreateExtractor();
        var record = MakeRecord("image/png");
        using var stream = new MemoryStream(new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var result = await extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.Contains("Analysis result from provider", result.ExtractedText!);

        // Verify provider was called with correct args
        await _mockProvider.Received(1).AnalyzeImageAsync(
            Arg.Any<byte[]>(), Arg.Is("image/png"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_ReturnNotConfigured_WhenProviderReturnsNotAvailable()
    {
        _mockProvider.AnalyzeImageAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new VisionAnalysisResult(
                Success: false,
                NotAvailable: true,
                ErrorMessage: "Attachment AI is not configured"));

        var extractor = CreateExtractor();
        var record = MakeRecord("image/png");
        using var stream = new MemoryStream(new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var result = await extractor.ExtractAsync(record, stream);

        Assert.False(result.Success);
        Assert.Contains("not configured", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    // =============================================
    // ExtractAsync — Unsupported content type
    // =============================================

    [Fact]
    public async Task Should_ReturnFailure_WhenUnsupportedContentType()
    {
        var extractor = CreateExtractor();
        var record = MakeRecord("text/plain", "test.txt");
        using var stream = new MemoryStream(new byte[] { 1, 2, 3 });

        var result = await extractor.ExtractAsync(record, stream);

        Assert.False(result.Success);
        Assert.Equal("Unsupported content type", result.ErrorMessage);
    }

    [Fact]
    public async Task Should_ReturnFailure_WhenEmptyImageFile()
    {
        var extractor = CreateExtractor();
        var record = MakeRecord("image/png");
        using var stream = new MemoryStream(Array.Empty<byte>());

        var result = await extractor.ExtractAsync(record, stream);

        Assert.False(result.Success);
        Assert.Equal("Image file is empty", result.ErrorMessage);
    }

    [Fact]
    public async Task Should_ReturnFailure_WhenProviderFails()
    {
        _mockProvider.AnalyzeImageAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new VisionAnalysisResult(
                Success: false,
                ErrorMessage: "Something went wrong"));

        var extractor = CreateExtractor();
        var record = MakeRecord("image/png");
        using var stream = new MemoryStream(new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var result = await extractor.ExtractAsync(record, stream);

        Assert.False(result.Success);
        Assert.Equal("Something went wrong", result.ErrorMessage);
    }

    // =============================================
    // VERIFY: Structured vision fields on FileRecord
    // =============================================

    // VERIFY: Image upload in platform-proxy mode populates all structured fields
    [Fact]
    public async Task Should_PopulateAllStructuredFields_WhenPlatformProxySucceeds()
    {
        _mockProvider.ProviderName.Returns("Platform");
        _mockProvider.AnalyzeImageAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new VisionAnalysisResult(
                Success: true,
                Caption: "A cat sitting on a mat",
                ExtractedText: "Hello World",
                Tags: new List<string> { "cat", "mat", "indoor" },
                Objects: new List<string> { "cat", "mat" }));

        var extractor = CreateExtractor();
        var record = MakeRecord("image/png");
        using var stream = new MemoryStream(new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var result = await extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);

        // FileRecord fields populated
        Assert.Equal("A cat sitting on a mat", record.VisionDescription);
        Assert.Equal("Hello World", record.VisionExtractedText);
        Assert.NotNull(record.VisionTagsJson);
        var tags = JsonSerializer.Deserialize<List<string>>(record.VisionTagsJson!);
        Assert.Contains("cat", tags!);
        Assert.Contains("mat", tags!);
        Assert.Contains("indoor", tags!);
        Assert.NotNull(record.VisionObjectsJson);
        var objects = JsonSerializer.Deserialize<List<string>>(record.VisionObjectsJson!);
        Assert.Contains("cat", objects!);
        Assert.NotNull(record.VisionAnalyzedAt);
        Assert.Equal("Platform", record.AttachmentAIProvider);
    }

    // VERIFY: Image upload in direct Azure AI Vision mode populates same structured fields
    [Fact]
    public async Task Should_PopulateAllStructuredFields_WhenAzureAIVisionSucceeds()
    {
        _mockProvider.ProviderName.Returns("AzureAIVision");
        _mockProvider.AnalyzeImageAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new VisionAnalysisResult(
                Success: true,
                Caption: "A landscape photo",
                ExtractedText: "Sign: Welcome",
                Tags: new List<string> { "landscape", "nature" },
                Objects: new List<string> { "sign", "tree" }));

        var extractor = CreateExtractor();
        var record = MakeRecord("image/jpeg");
        using var stream = new MemoryStream(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });

        var result = await extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.Equal("A landscape photo", record.VisionDescription);
        Assert.Equal("Sign: Welcome", record.VisionExtractedText);
        Assert.NotNull(record.VisionTagsJson);
        Assert.NotNull(record.VisionObjectsJson);
        Assert.NotNull(record.VisionAnalyzedAt);
        Assert.Equal("AzureAIVision", record.AttachmentAIProvider);
    }

    // VERIFY: Image upload in Azure OpenAI (GPT-4V) mode populates VisionDescription + VisionExtractedText, leaves Tags/Objects null
    [Fact]
    public async Task Should_PopulateDescriptionAndText_WhenAzureOpenAIGPT4VMode()
    {
        _mockProvider.ProviderName.Returns("AzureOpenAI");
        _mockProvider.AnalyzeImageAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new VisionAnalysisResult(
                Success: true,
                Caption: "The image shows a document with text content about project planning.",
                ExtractedText: "Project Plan Q1 2026",
                Tags: null,
                Objects: null));

        var extractor = CreateExtractor();
        var record = MakeRecord("image/png");
        using var stream = new MemoryStream(new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var result = await extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.Equal("The image shows a document with text content about project planning.", record.VisionDescription);
        Assert.Equal("Project Plan Q1 2026", record.VisionExtractedText);
        Assert.Null(record.VisionTagsJson);
        Assert.Null(record.VisionObjectsJson);
        Assert.NotNull(record.VisionAnalyzedAt);
        Assert.Equal("AzureOpenAI", record.AttachmentAIProvider);
    }

    // VERIFY: Image upload in NoOp mode returns NotAvailable and does not populate any vision fields
    [Fact]
    public async Task Should_NotPopulateAnyFields_WhenNoOpProviderReturnsNotAvailable()
    {
        _mockProvider.ProviderName.Returns("NoOp");
        _mockProvider.AnalyzeImageAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new VisionAnalysisResult(
                Success: false,
                NotAvailable: true,
                ErrorMessage: "Attachment AI is not configured"));

        var extractor = CreateExtractor();
        var record = MakeRecord("image/png");
        using var stream = new MemoryStream(new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var result = await extractor.ExtractAsync(record, stream);

        Assert.False(result.Success);
        Assert.Null(record.VisionDescription);
        Assert.Null(record.VisionTagsJson);
        Assert.Null(record.VisionObjectsJson);
        Assert.Null(record.VisionExtractedText);
        Assert.Null(record.VisionAnalyzedAt);
        Assert.Null(record.AttachmentAIProvider);
    }

    // VERIFY: VisionDescription always contains the primary caption or description when any vision provider succeeds
    [Fact]
    public async Task Should_AlwaysPopulateVisionDescription_WhenProviderSucceeds()
    {
        _mockProvider.ProviderName.Returns("Platform");
        _mockProvider.AnalyzeImageAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new VisionAnalysisResult(
                Success: true,
                Caption: "Primary caption text"));

        var extractor = CreateExtractor();
        var record = MakeRecord("image/png");
        using var stream = new MemoryStream(new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var result = await extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.Equal("Primary caption text", record.VisionDescription);
        Assert.Equal("Primary caption text", result.VisionDescription);
    }

    // VERIFY: FileExtractionResult.ExtractedText contains combined human-readable summary
    [Fact]
    public async Task Should_ReturnCombinedText_ForEnrichmentPipelineCompatibility()
    {
        _mockProvider.ProviderName.Returns("Platform");
        _mockProvider.AnalyzeImageAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new VisionAnalysisResult(
                Success: true,
                Caption: "A photo of a cat",
                ExtractedText: "OCR text here",
                Tags: new List<string> { "cat", "animal" },
                Objects: new List<string> { "cat" }));

        var extractor = CreateExtractor();
        var record = MakeRecord("image/png");
        using var stream = new MemoryStream(new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var result = await extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.NotNull(result.ExtractedText);
        Assert.Contains("A photo of a cat", result.ExtractedText!);
        Assert.Contains("OCR text here", result.ExtractedText!);
        Assert.Contains("Tags: cat, animal", result.ExtractedText!);
        Assert.Contains("Objects: cat", result.ExtractedText!);
    }

    // VERIFY: Vision analysis failure does not block file upload or attachment creation
    [Fact]
    public async Task Should_ReturnFailureGracefully_WhenVisionAnalysisThrows()
    {
        _mockProvider.AnalyzeImageAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<VisionAnalysisResult>(new HttpRequestException("Connection refused")));

        var extractor = CreateExtractor();
        var record = MakeRecord("image/png");
        using var stream = new MemoryStream(new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var result = await extractor.ExtractAsync(record, stream);

        // Should return failure, not throw
        Assert.False(result.Success);
        Assert.Contains("Connection refused", result.ErrorMessage!);
        // FileRecord fields should NOT be populated on failure
        Assert.Null(record.VisionDescription);
        Assert.Null(record.VisionAnalyzedAt);
    }

    // VERIFY: AttachmentAIProvider field records which provider generated the results
    [Fact]
    public async Task Should_RecordProviderName_OnFileRecord()
    {
        _mockProvider.ProviderName.Returns("AzureAIVision");
        _mockProvider.AnalyzeImageAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new VisionAnalysisResult(
                Success: true,
                Caption: "Test caption"));

        var extractor = CreateExtractor();
        var record = MakeRecord("image/png");
        using var stream = new MemoryStream(new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        await extractor.ExtractAsync(record, stream);

        Assert.Equal("AzureAIVision", record.AttachmentAIProvider);
    }

    // VERIFY: VisionAnalyzedAt is set to approximately current time
    [Fact]
    public async Task Should_SetVisionAnalyzedAt_ToCurrentUtcTime()
    {
        _mockProvider.ProviderName.Returns("Platform");
        _mockProvider.AnalyzeImageAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new VisionAnalysisResult(
                Success: true,
                Caption: "Test"));

        var extractor = CreateExtractor();
        var record = MakeRecord("image/png");
        using var stream = new MemoryStream(new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var before = DateTime.UtcNow;
        await extractor.ExtractAsync(record, stream);
        var after = DateTime.UtcNow;

        Assert.NotNull(record.VisionAnalyzedAt);
        Assert.InRange(record.VisionAnalyzedAt!.Value, before, after);
    }

    // VERIFY: Structured fields returned in FileExtractionResult match FileRecord
    [Fact]
    public async Task Should_ReturnStructuredFieldsInResult_MatchingFileRecord()
    {
        _mockProvider.ProviderName.Returns("Platform");
        _mockProvider.AnalyzeImageAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new VisionAnalysisResult(
                Success: true,
                Caption: "A test image",
                ExtractedText: "OCR text",
                Tags: new List<string> { "tag1", "tag2" },
                Objects: new List<string> { "obj1" }));

        var extractor = CreateExtractor();
        var record = MakeRecord("image/png");
        using var stream = new MemoryStream(new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var result = await extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.Equal("A test image", result.VisionDescription);
        Assert.Equal(record.VisionTagsJson, result.VisionTagsJson);
        Assert.Equal(record.VisionObjectsJson, result.VisionObjectsJson);
        Assert.Equal("OCR text", result.VisionExtractedText);
    }

    // VERIFY: Existing text/PDF/DOCX extraction is NOT affected — FileExtractionResult backward compatible
    [Fact]
    public void Should_FileExtractionResult_BeBackwardCompatible()
    {
        // Existing callers create FileExtractionResult with just Success + ExtractedText or ErrorMessage
        var success = new FileExtractionResult(true, ExtractedText: "some text");
        Assert.True(success.Success);
        Assert.Equal("some text", success.ExtractedText);
        Assert.Null(success.ErrorMessage);
        Assert.Null(success.VisionDescription);
        Assert.Null(success.VisionTagsJson);
        Assert.Null(success.VisionObjectsJson);
        Assert.Null(success.VisionExtractedText);

        var failure = new FileExtractionResult(false, ErrorMessage: "bad format");
        Assert.False(failure.Success);
        Assert.Null(failure.ExtractedText);
        Assert.Equal("bad format", failure.ErrorMessage);
    }

    // VERIFY: FileRecord has all new fields as nullable
    [Fact]
    public void Should_FileRecord_HaveNullableVisionFields()
    {
        var record = new FileRecord();

        // All new fields default to null
        Assert.Null(record.VisionTagsJson);
        Assert.Null(record.VisionObjectsJson);
        Assert.Null(record.VisionExtractedText);
        Assert.Null(record.VisionAnalyzedAt);
        Assert.Null(record.AttachmentAIProvider);
        // Pre-existing field also null by default
        Assert.Null(record.VisionDescription);
    }

    // VERIFY: When provider returns failure (not NotAvailable), FileRecord fields not populated
    [Fact]
    public async Task Should_NotPopulateFields_WhenProviderReturnsFailure()
    {
        _mockProvider.ProviderName.Returns("Platform");
        _mockProvider.AnalyzeImageAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new VisionAnalysisResult(
                Success: false,
                ErrorMessage: "Vision service returned 500"));

        var extractor = CreateExtractor();
        var record = MakeRecord("image/png");
        using var stream = new MemoryStream(new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var result = await extractor.ExtractAsync(record, stream);

        Assert.False(result.Success);
        Assert.Null(record.VisionDescription);
        Assert.Null(record.VisionTagsJson);
        Assert.Null(record.VisionObjectsJson);
        Assert.Null(record.VisionExtractedText);
        Assert.Null(record.VisionAnalyzedAt);
        Assert.Null(record.AttachmentAIProvider);
    }
}
