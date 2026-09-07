using Azure.Core;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class DocumentIntelligenceContentExtractorTests
{
    private readonly ILogger<DocumentIntelligenceContentExtractor> _logger;
    private readonly IAttachmentAIProvider _mockProvider;

    public DocumentIntelligenceContentExtractorTests()
    {
        _logger = Substitute.For<ILogger<DocumentIntelligenceContentExtractor>>();
        _mockProvider = Substitute.For<IAttachmentAIProvider>();
    }

    private DocumentIntelligenceContentExtractor CreateExtractor(IAttachmentAIProvider? provider = null)
        => new(provider ?? _mockProvider, _logger);

    private static FileRecord MakeRecord(string contentType, string fileName = "test.pdf")
        => new() { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), FileName = fileName, ContentType = contentType };

    // =============================================
    // CanExtract
    // =============================================

    [Theory]
    [InlineData("application/pdf", true)]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", true)]
    [InlineData("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", true)]
    [InlineData("application/vnd.openxmlformats-officedocument.presentationml.presentation", true)]
    [InlineData("image/jpeg", false)]   // images route to ImageContentExtractor for vision
    [InlineData("image/png", false)]    // images route to ImageContentExtractor for vision
    [InlineData("image/tiff", false)]   // images route to ImageContentExtractor for vision
    [InlineData("image/bmp", false)]    // images route to ImageContentExtractor for vision
    [InlineData("text/plain", false)]
    [InlineData("application/json", false)]
    [InlineData("audio/mp3", false)]
    [InlineData("application/octet-stream", false)]
    public void CanExtract_ReturnsExpected(string contentType, bool expected)
    {
        var extractor = CreateExtractor();
        Assert.Equal(expected, extractor.CanExtract(contentType));
    }

    [Fact]
    public void CanExtract_ReturnsFalse_ForNull()
    {
        var extractor = CreateExtractor();
        Assert.False(extractor.CanExtract(null));
    }

    [Fact]
    public void CanExtract_ReturnsFalse_ForEmpty()
    {
        var extractor = CreateExtractor();
        Assert.False(extractor.CanExtract(""));
    }

    // =============================================
    // VERIFY: CanExtract returns false when provider is NoOp
    // (so CompositeContentExtractor falls through to native extractors)
    // =============================================

    [Fact]
    public void Should_CanExtract_ReturnFalse_WhenProviderIsNoOp()
    {
        var noOp = new NoOpAttachmentAIProvider();
        var extractor = CreateExtractor(noOp);

        // Even for supported types, should return false when NoOp
        Assert.False(extractor.CanExtract("application/pdf"));
        Assert.False(extractor.CanExtract("application/vnd.openxmlformats-officedocument.wordprocessingml.document"));
    }

    [Fact]
    public void Should_CanExtract_ReturnFalse_WhenAzureProviderLacksDocumentIntelligenceCapability()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureAIVision:Endpoint"] = "https://vision.test",
                ["AzureAIVision:ApiKey"] = "vision-key"
            })
            .Build();

        var provider = new AzureAttachmentAIProvider(
            configuration,
            Substitute.For<ILogger<AzureAttachmentAIProvider>>(),
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<TokenCredential>());
        var extractor = CreateExtractor(provider);

        Assert.False(extractor.CanExtract("application/pdf"));
        Assert.False(extractor.CanExtract("application/vnd.openxmlformats-officedocument.wordprocessingml.document"));
    }

    // =============================================
    // ExtractAsync — unsupported type
    // =============================================

    [Fact]
    public async Task ExtractAsync_ReturnsFailure_ForUnsupportedType()
    {
        _mockProvider.ProviderName.Returns("Platform");
        var extractor = CreateExtractor();

        var record = MakeRecord("text/plain", "test.txt");
        using var stream = new MemoryStream(new byte[] { 1, 2, 3 });

        var result = await extractor.ExtractAsync(record, stream);

        Assert.False(result.Success);
        Assert.Equal("Unsupported content type", result.ErrorMessage);
    }

    // =============================================
    // VERIFY: PDF in platform-proxy mode populates ExtractedText + LayoutDataJson
    // =============================================

    [Fact]
    public async Task Should_PopulateExtractedTextAndLayoutDataJson_WhenPlatformProxySucceeds()
    {
        _mockProvider.ProviderName.Returns("Platform");
        _mockProvider.ExtractDocumentAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentExtractionResult(
                Success: true,
                ExtractedText: "Extracted content from platform",
                LayoutDataJson: "{\"pages\":[{\"pageNumber\":1}]}"));

        var extractor = CreateExtractor();
        var record = MakeRecord("application/pdf", "doc.pdf");
        using var stream = new MemoryStream(new byte[] { 0x25, 0x50, 0x44, 0x46 });

        var result = await extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.Equal("Extracted content from platform", result.ExtractedText);
        // Verify provider was called
        await _mockProvider.Received(1).ExtractDocumentAsync(
            Arg.Any<byte[]>(), Arg.Is("application/pdf"), Arg.Any<CancellationToken>());
    }

    // =============================================
    // VERIFY: PDF in direct Azure Document Intelligence mode populates ExtractedText
    // =============================================

    [Fact]
    public async Task Should_PopulateExtractedText_WhenAzureDocIntelligenceSucceeds()
    {
        _mockProvider.ProviderName.Returns("AzureDocumentIntelligence");
        _mockProvider.ExtractDocumentAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentExtractionResult(
                Success: true,
                ExtractedText: "OCR text from Azure"));

        var extractor = CreateExtractor();
        var record = MakeRecord("application/pdf", "scanned.pdf");
        using var stream = new MemoryStream(new byte[] { 0x25, 0x50 });

        var result = await extractor.ExtractAsync(record, stream);

        Assert.True(result.Success);
        Assert.Equal("OCR text from Azure", result.ExtractedText);
    }

    // =============================================
    // VERIFY: PDF with no AI falls back (NotAvailable from provider)
    // =============================================

    [Fact]
    public async Task Should_ReturnNotAvailable_WhenProviderReturnsNotAvailable()
    {
        _mockProvider.ProviderName.Returns("NoOp");
        _mockProvider.ExtractDocumentAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentExtractionResult(
                Success: false,
                NotAvailable: true,
                ErrorMessage: "Attachment AI is not configured"));

        var extractor = CreateExtractor();
        var record = MakeRecord("application/pdf", "doc.pdf");
        using var stream = new MemoryStream(new byte[] { 0x25 });

        var result = await extractor.ExtractAsync(record, stream);

        Assert.False(result.Success);
        Assert.Contains("not configured", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    // =============================================
    // VERIFY: AttachmentAIProvider records which provider performed extraction
    // =============================================

    [Fact]
    public async Task Should_RecordProviderName_OnFileRecord_WhenExtractionSucceeds()
    {
        _mockProvider.ProviderName.Returns("Platform");
        _mockProvider.ExtractDocumentAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentExtractionResult(
                Success: true,
                ExtractedText: "text",
                LayoutDataJson: "{}"));

        var extractor = CreateExtractor();
        var record = MakeRecord("application/pdf");
        using var stream = new MemoryStream(new byte[] { 0x25, 0x50 });

        await extractor.ExtractAsync(record, stream);

        Assert.Equal("Platform", record.AttachmentAIProvider);
    }

    // =============================================
    // VERIFY: TextExtractionStatus lifecycle — Processing → Completed on success
    // =============================================

    [Fact]
    public async Task Should_SetTextExtractionStatus_ToCompleted_OnSuccess()
    {
        _mockProvider.ProviderName.Returns("Platform");
        _mockProvider.ExtractDocumentAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentExtractionResult(
                Success: true,
                ExtractedText: "text"));

        var extractor = CreateExtractor();
        var record = MakeRecord("application/pdf");
        Assert.Equal((int)TextExtractionStatus.NotStarted, record.TextExtractionStatus);

        using var stream = new MemoryStream(new byte[] { 0x25 });
        await extractor.ExtractAsync(record, stream);

        Assert.Equal((int)TextExtractionStatus.Completed, record.TextExtractionStatus);
    }

    // =============================================
    // VERIFY: TextExtractionStatus lifecycle — Processing → Failed on failure
    // =============================================

    [Fact]
    public async Task Should_SetTextExtractionStatus_ToFailed_OnProviderFailure()
    {
        _mockProvider.ProviderName.Returns("AzureDocumentIntelligence");
        _mockProvider.ExtractDocumentAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentExtractionResult(
                Success: false,
                ErrorMessage: "Document too large"));

        var extractor = CreateExtractor();
        var record = MakeRecord("application/pdf");
        using var stream = new MemoryStream(new byte[] { 0x25 });

        await extractor.ExtractAsync(record, stream);

        Assert.Equal((int)TextExtractionStatus.Failed, record.TextExtractionStatus);
    }

    // =============================================
    // VERIFY: TextExtractedAt is set on successful extraction
    // =============================================

    [Fact]
    public async Task Should_SetTextExtractedAt_OnSuccess()
    {
        _mockProvider.ProviderName.Returns("Platform");
        _mockProvider.ExtractDocumentAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentExtractionResult(
                Success: true,
                ExtractedText: "text"));

        var extractor = CreateExtractor();
        var record = MakeRecord("application/pdf");
        Assert.Null(record.TextExtractedAt);

        var before = DateTime.UtcNow;
        using var stream = new MemoryStream(new byte[] { 0x25 });
        await extractor.ExtractAsync(record, stream);
        var after = DateTime.UtcNow;

        Assert.NotNull(record.TextExtractedAt);
        Assert.InRange(record.TextExtractedAt!.Value, before, after);
    }

    [Fact]
    public async Task Should_NotSetTextExtractedAt_OnFailure()
    {
        _mockProvider.ProviderName.Returns("Platform");
        _mockProvider.ExtractDocumentAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentExtractionResult(
                Success: false,
                ErrorMessage: "Failed"));

        var extractor = CreateExtractor();
        var record = MakeRecord("application/pdf");
        using var stream = new MemoryStream(new byte[] { 0x25 });

        await extractor.ExtractAsync(record, stream);

        Assert.Null(record.TextExtractedAt);
    }

    // =============================================
    // VERIFY: TextExtractionError captures error message on failure
    // =============================================

    [Fact]
    public async Task Should_SetTextExtractionError_OnProviderFailure()
    {
        _mockProvider.ProviderName.Returns("Platform");
        _mockProvider.ExtractDocumentAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentExtractionResult(
                Success: false,
                ErrorMessage: "Document Intelligence failed: timeout"));

        var extractor = CreateExtractor();
        var record = MakeRecord("application/pdf");
        using var stream = new MemoryStream(new byte[] { 0x25 });

        await extractor.ExtractAsync(record, stream);

        Assert.Equal("Document Intelligence failed: timeout", record.TextExtractionError);
    }

    [Fact]
    public async Task Should_ClearTextExtractionError_OnSuccess()
    {
        _mockProvider.ProviderName.Returns("Platform");
        _mockProvider.ExtractDocumentAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentExtractionResult(
                Success: true,
                ExtractedText: "text"));

        var extractor = CreateExtractor();
        var record = MakeRecord("application/pdf");
        record.TextExtractionError = "previous error"; // simulate prior failure
        using var stream = new MemoryStream(new byte[] { 0x25 });

        await extractor.ExtractAsync(record, stream);

        Assert.Null(record.TextExtractionError);
    }

    // =============================================
    // VERIFY: LayoutDataJson populated from platform response
    // =============================================

    [Fact]
    public async Task Should_PopulateLayoutDataJson_OnFileRecord_WhenProviderReturnsIt()
    {
        var layoutJson = "{\"pages\":[{\"pageNumber\":1,\"lines\":[]}]}";
        _mockProvider.ProviderName.Returns("Platform");
        _mockProvider.ExtractDocumentAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentExtractionResult(
                Success: true,
                ExtractedText: "text",
                LayoutDataJson: layoutJson));

        var extractor = CreateExtractor();
        var record = MakeRecord("application/pdf");
        using var stream = new MemoryStream(new byte[] { 0x25, 0x50 });

        await extractor.ExtractAsync(record, stream);

        Assert.Equal(layoutJson, record.LayoutDataJson);
    }

    // =============================================
    // Exception handling
    // =============================================

    [Fact]
    public async Task Should_SetFailedStatus_WhenProviderThrowsException()
    {
        _mockProvider.ProviderName.Returns("Platform");
        _mockProvider.ExtractDocumentAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<DocumentExtractionResult>(_ => throw new InvalidOperationException("Connection refused"));

        var extractor = CreateExtractor();
        var record = MakeRecord("application/pdf");
        using var stream = new MemoryStream(new byte[] { 0x25 });

        var result = await extractor.ExtractAsync(record, stream);

        Assert.False(result.Success);
        Assert.Equal((int)TextExtractionStatus.Failed, record.TextExtractionStatus);
        Assert.Equal("Connection refused", record.TextExtractionError);
    }

    [Fact]
    public async Task Should_PropagateOperationCancelledException()
    {
        _mockProvider.ProviderName.Returns("Platform");
        _mockProvider.ExtractDocumentAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<DocumentExtractionResult>(_ => throw new OperationCanceledException());

        var extractor = CreateExtractor();
        var record = MakeRecord("application/pdf");
        using var stream = new MemoryStream(new byte[] { 0x25 });

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => extractor.ExtractAsync(record, stream));
    }

    // =============================================
    // Empty document
    // =============================================

    [Fact]
    public async Task Should_ReturnFailure_WhenDocumentStreamIsEmpty()
    {
        _mockProvider.ProviderName.Returns("Platform");
        var extractor = CreateExtractor();
        var record = MakeRecord("application/pdf");
        using var stream = new MemoryStream(Array.Empty<byte>());

        var result = await extractor.ExtractAsync(record, stream);

        Assert.False(result.Success);
        Assert.Equal("Document file is empty", result.ErrorMessage);
    }

    // =============================================
    // VERIFY: ExtractedText populated on FileRecord
    // =============================================

    [Fact]
    public async Task Should_PopulateExtractedText_OnFileRecord_WhenSuccessful()
    {
        _mockProvider.ProviderName.Returns("Platform");
        _mockProvider.ExtractDocumentAsync(
            Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentExtractionResult(
                Success: true,
                ExtractedText: "Extracted doc text"));

        var extractor = CreateExtractor();
        var record = MakeRecord("application/pdf");
        using var stream = new MemoryStream(new byte[] { 0x25, 0x50 });

        await extractor.ExtractAsync(record, stream);

        Assert.Equal("Extracted doc text", record.ExtractedText);
    }
}
