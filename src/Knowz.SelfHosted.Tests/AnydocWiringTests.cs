using Knowz.Core.Configuration;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// DI ordering, ConfigurationProbe surfacing and optional-list wiring for the anydoc extractor.
/// WorkGroupID: kc-feat-anydoc-portable-tools-20260913-152433 — NodeID SH_AnydocContentExtractor (R7/R11/R12).
/// </summary>
public class AnydocWiringTests
{
    [PosixShellFact]
    public async Task Composite_TextRtf_UsesAnydocInsteadOfReturningRawRtf()
    {
        var stub = Path.Combine(Path.GetTempPath(), "anydoc-rtf-" + Guid.NewGuid().ToString("N"));
        const string markdown = "# Converted document\n\nThe structured RTF document was converted locally into markdown.";
        await File.WriteAllTextAsync(stub, "#!/bin/sh\ncat >/dev/null\nprintf '%s' '" + markdown + "'\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var composite = BuildComposite(new() { ["Anydoc:CliPath"] = stub });
            var record = new Knowz.Core.Entities.FileRecord
            {
                Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), FileName = "fixture.rtf", ContentType = "text/rtf"
            };
            using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(@"{\rtf1\ansi raw RTF}"));

            var result = await composite.ExtractAsync(record, input);

            Assert.True(result.Success);
            Assert.Equal(markdown, result.ExtractedText);
            Assert.Equal(markdown, record.ExtractedText);
        }
        finally { File.Delete(stub); }
    }

    internal static CompositeContentExtractor BuildComposite(Dictionary<string, string?> settings, IAttachmentAIProvider? attachmentProvider = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IAttachmentAIProvider>(attachmentProvider ?? new NoOpAttachmentAIProvider());
        services.AddScoped<AnydocContentExtractor>();
        services.AddScoped<TextFileContentExtractor>();
        services.AddScoped<PdfContentExtractor>();
        services.AddScoped<DocxContentExtractor>();
        services.AddScoped<ExcelContentExtractor>();
        services.AddScoped<PowerPointContentExtractor>();
        services.AddScoped<ImageContentExtractor>();
        services.AddScoped<DocumentIntelligenceContentExtractor>();
        // Mirrors ApplicationServiceExtensions' composite factory registration.
        Knowz.SelfHosted.Application.Extensions.ApplicationServiceExtensions
            .AddContentExtractionComposite(services);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        return Assert.IsType<CompositeContentExtractor>(
            scope.ServiceProvider.GetRequiredService<IFileContentExtractor>());
    }

    [Fact]
    public void CompositeOrder_IsTextThenAnydocThenDocIntelligenceThenNatives()
    {
        var composite = BuildComposite(new Dictionary<string, string?>());

        var order = composite.Extractors.Select(e => e.GetType().Name).ToArray();

        Assert.Equal(
        [
            nameof(TextFileContentExtractor),
            nameof(AnydocContentExtractor),
            nameof(DocumentIntelligenceContentExtractor),
            nameof(ImageContentExtractor),
            nameof(PdfContentExtractor),
            nameof(DocxContentExtractor),
            nameof(ExcelContentExtractor),
            nameof(PowerPointContentExtractor)
        ], order);
    }

    [Fact]
    public void CompositeOrder_FallbackOnly_PlacesAnydocAfterDocumentIntelligence()
    {
        var composite = BuildComposite(new Dictionary<string, string?> { ["Anydoc:Mode"] = "FallbackOnly" });

        var order = composite.Extractors.Select(e => e.GetType().Name).ToList();

        Assert.True(order.IndexOf(nameof(AnydocContentExtractor))
                    > order.IndexOf(nameof(DocumentIntelligenceContentExtractor)));
    }

    [Fact]
    public void CompositeOrder_Off_OmitsAnydocEntirely()
    {
        var composite = BuildComposite(new Dictionary<string, string?> { ["Anydoc:Mode"] = "Off" });

        Assert.DoesNotContain(composite.Extractors, e => e is AnydocContentExtractor);
    }

    [Fact]
    public void OptionalList_ContainsAnydocContentExtractor()
    {
        Assert.Contains("AnydocContentExtractor", SelfHostedOptionalList.Default);
    }

    // =============================================
    // ConfigurationProbe (R11) — never performs a network call
    // =============================================

    [Fact]
    public void Probe_Kind_Anydoc_IsConfiguration()
    {
        Assert.Equal("configuration", ConfigurationProbe.Kind("Anydoc"));
    }

    [Fact]
    public async Task Probe_Anydoc_ReportsUnconfigured_WhenBinaryMissing()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Anydoc:CliPath"] = Path.Combine(Path.GetTempPath(), "anydoc-missing-" + Guid.NewGuid().ToString("N"))
        }).Build();
        using var http = new HttpClient(new ThrowingHandler());

        var result = await ConfigurationProbe.RunAsync("Anydoc", config, http);

        Assert.Equal("unconfigured", result.ProbeStatus);
        Assert.Equal("Not Configured", result.Status);
        Assert.False(result.IsHealthy);
    }

    [Fact]
    public async Task Probe_Anydoc_ReportsConfigurationValid_WhenBinaryResolves()
    {
        var stub = Path.Combine(Path.GetTempPath(), "anydoc-probe-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(stub, "#!/bin/sh\nexit 0\n");
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Anydoc:CliPath"] = stub
            }).Build();
            using var http = new HttpClient(new ThrowingHandler());

            var result = await ConfigurationProbe.RunAsync("Anydoc", config, http);

            Assert.Equal("configuration-valid", result.ProbeStatus);
            Assert.Contains(stub, result.Status);
            Assert.DoesNotContain("Connected", result.Status);   // never claims connectivity
        }
        finally
        {
            File.Delete(stub);
        }
    }

    /// <summary>Fails the test loudly if the Anydoc probe ever issues an HTTP request.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The Anydoc probe must not perform any network call");
    }
}
