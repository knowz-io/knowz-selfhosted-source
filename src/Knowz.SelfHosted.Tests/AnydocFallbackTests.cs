using System.Text;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public sealed class AnydocFallbackTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "anydoc-fallback-" + Guid.NewGuid().ToString("N"));
    private const string Document = "# Complete document\n\nCloud failure must preserve every byte for the local conversion tier.";

    public AnydocFallbackTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [PosixShellTheory]
    [InlineData("unavailable", false)]
    [InlineData("unavailable", true)]
    [InlineData("failure", false)]
    [InlineData("failure", true)]
    [InlineData("exception", false)]
    [InlineData("exception", true)]
    public async Task FallbackOnly_CloudFailure_ConvertsCompleteStreamLocally(string failure, bool nonSeekable)
    {
        var provider = FailingProvider(failure);
        var composite = AnydocWiringTests.BuildComposite(Settings("FallbackOnly"), provider);
        var record = Record();
        using var input = Input(nonSeekable);

        var result = await composite.ExtractAsync(record, input);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(Document, result.ExtractedText);
        Assert.Equal(Document, record.ExtractedText);
        Assert.Equal((int)TextExtractionStatus.Completed, record.TextExtractionStatus);
        Assert.Null(record.TextExtractionError);
        Assert.Equal(Document, await File.ReadAllTextAsync(Path.Combine(_directory, "input")));
        await provider.Received(1).ExtractDocumentAsync(
            Arg.Is<byte[]>(bytes => Encoding.UTF8.GetString(bytes) == Document), "application/pdf", Arg.Any<CancellationToken>());
    }

    [PosixShellFact]
    public async Task FallbackOnly_CloudSuccess_DoesNotInvokeLocalConverter()
    {
        var provider = FailingProvider("failure");
        provider.ExtractDocumentAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentExtractionResult(true, ExtractedText: "cloud text", LayoutDataJson: "{}"));
        var composite = AnydocWiringTests.BuildComposite(Settings("FallbackOnly"), provider);
        var record = Record();
        using var input = Input(false);

        var result = await composite.ExtractAsync(record, input);

        Assert.True(result.Success);
        Assert.Equal("cloud text", record.ExtractedText);
        Assert.Equal("{}", record.LayoutDataJson);
        Assert.False(File.Exists(Path.Combine(_directory, "input")));
    }

    [PosixShellFact]
    public async Task FallbackOnly_CloudDecline_LeavesRecordUntouchedForNextTier()
    {
        var composite = AnydocWiringTests.BuildComposite(Settings("FallbackOnly"), FailingProvider("failure"));
        var providerExtractor = Assert.Single(composite.Extractors.OfType<DocumentIntelligenceContentExtractor>());
        var record = Record();
        record.TextExtractionStatus = (int)TextExtractionStatus.NotStarted;
        record.TextExtractionError = "existing error";
        record.ExtractedText = "existing content";
        record.LayoutDataJson = "existing layout";
        record.AttachmentAIProvider = "existing provider";
        using var input = Input(false);

        var result = await providerExtractor.ExtractAsync(record, input);

        Assert.True(result.Declined);
        Assert.Equal((int)TextExtractionStatus.NotStarted, record.TextExtractionStatus);
        Assert.Equal("existing error", record.TextExtractionError);
        Assert.Equal("existing content", record.ExtractedText);
        Assert.Equal("existing layout", record.LayoutDataJson);
        Assert.Equal("existing provider", record.AttachmentAIProvider);
    }

    [PosixShellFact]
    public async Task FallbackOnly_NoCloudProvider_StillConvertsLocally()
    {
        var composite = AnydocWiringTests.BuildComposite(Settings("FallbackOnly"));
        using var input = Input(true);

        var result = await composite.ExtractAsync(Record(), input);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(Document, result.ExtractedText);
    }

    [PosixShellFact]
    public async Task FallbackOnly_DisabledAnydoc_PreservesTerminalProviderFailure()
    {
        var settings = Settings("FallbackOnly");
        settings["Anydoc:Enabled"] = "false";
        var composite = AnydocWiringTests.BuildComposite(settings, FailingProvider("failure"));
        using var input = Input(false);

        var result = await composite.ExtractAsync(Record(), input);

        Assert.False(result.Success);
        Assert.False(result.Declined);
        Assert.Equal("cloud unavailable", result.ErrorMessage);
        Assert.False(File.Exists(Path.Combine(_directory, "input")));
    }

    [PosixShellFact]
    public async Task FallbackOnly_ProviderCancellation_DoesNotInvokeLocalConverter()
    {
        var provider = FailingProvider("failure");
        provider.ExtractDocumentAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<DocumentExtractionResult>>(_ => throw new OperationCanceledException());
        var composite = AnydocWiringTests.BuildComposite(Settings("FallbackOnly"), provider);
        using var input = Input(false);

        await Assert.ThrowsAsync<OperationCanceledException>(() => composite.ExtractAsync(Record(), input));

        Assert.False(File.Exists(Path.Combine(_directory, "input")));
    }

    [PosixShellFact]
    public async Task FallbackOnly_EmptyDocument_IsTerminalWithoutProviderOrLocalInvocation()
    {
        var provider = FailingProvider("failure");
        var composite = AnydocWiringTests.BuildComposite(Settings("FallbackOnly"), provider);
        using var input = new MemoryStream();

        var result = await composite.ExtractAsync(Record(), input);

        Assert.False(result.Success);
        Assert.False(result.Declined);
        Assert.Equal("Document file is empty", result.ErrorMessage);
        Assert.False(File.Exists(Path.Combine(_directory, "input")));
        await provider.DidNotReceive().ExtractDocumentAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [PosixShellTheory]
    [InlineData("Off", false, 1)]
    [InlineData("PreferLocal", true, 0)]
    public async Task LegacyModes_PreserveOrderAndFailureSemantics(string mode, bool success, int providerCalls)
    {
        var provider = FailingProvider("failure");
        var composite = AnydocWiringTests.BuildComposite(Settings(mode), provider);
        var record = Record();
        using var input = Input(false);

        var result = await composite.ExtractAsync(record, input);

        Assert.Equal(success, result.Success);
        Assert.Equal(success, File.Exists(Path.Combine(_directory, "input")));
        await provider.Received(providerCalls).ExtractDocumentAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        if (!success)
        {
            Assert.False(result.Declined);
            Assert.Equal((int)TextExtractionStatus.Failed, record.TextExtractionStatus);
            Assert.Equal("cloud unavailable", record.TextExtractionError);
        }
    }

    private Dictionary<string, string?> Settings(string mode)
    {
        var script = Path.Combine(_directory, "anydoc");
        File.WriteAllText(script, $"#!/bin/sh\ncat > '{_directory}/input'\ncat '{_directory}/input'\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new() { ["Anydoc:Mode"] = mode, ["Anydoc:CliPath"] = script };
    }

    private static IAttachmentAIProvider FailingProvider(string failure)
    {
        var provider = Substitute.For<IAttachmentAIProvider>();
        provider.ProviderName.Returns("ConfiguredCloud");
        provider.ExtractDocumentAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<DocumentExtractionResult>>(_ => failure == "exception"
                ? throw new HttpRequestException("provider transport failure")
                : Task.FromResult(new DocumentExtractionResult(false, ErrorMessage: "cloud unavailable", NotAvailable: failure == "unavailable")));
        return provider;
    }

    private static FileRecord Record() => new()
    {
        Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), FileName = "document.pdf", ContentType = "application/pdf"
    };

    private static Stream Input(bool nonSeekable) => nonSeekable
        ? new NonSeekableStream(Encoding.UTF8.GetBytes(Document))
        : new MemoryStream(Encoding.UTF8.GetBytes(Document));

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    }
}
