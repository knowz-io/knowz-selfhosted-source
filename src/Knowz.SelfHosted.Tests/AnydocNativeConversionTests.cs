using Knowz.Core.Entities;
using Knowz.Core.Enums;

namespace Knowz.SelfHosted.Tests;

public sealed class AnydocNativeTheoryAttribute : TheoryAttribute
{
    public AnydocNativeTheoryAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KNOWZ_ANYDOC_TEST_CLI")))
            Skip = "Run scripts/local/verify-anydoc.py IMAGE --dotnet for actual non-root container conversion";
    }
}

public sealed class AnydocNativeConversionTests
{
    [AnydocNativeTheory]
    [InlineData("fixture.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("fixture.odt", "application/vnd.oasis.opendocument.text")]
    [InlineData("fixture.pdf", "application/pdf")]
    [InlineData("fixture.rtf", "text/rtf")]
    [InlineData("scanned.pdf", "application/pdf")]
    public async Task ActualComposite_UsesNativeBundle_AndFailsTruthfullyWithoutOcr(string name, string contentType)
    {
        var cli = Environment.GetEnvironmentVariable("KNOWZ_ANYDOC_TEST_CLI")!;
        var directory = Environment.GetEnvironmentVariable("KNOWZ_ANYDOC_TEST_FIXTURES")!;
        Assert.True(File.Exists(cli));
        var composite = AnydocWiringTests.BuildComposite(new()
        {
            ["Anydoc:CliPath"] = cli, ["Anydoc:Ocr"] = "None"
        });
        var record = new FileRecord
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), FileName = name, ContentType = contentType,
            TextExtractionStatus = (int)TextExtractionStatus.NotStarted
        };
        await using var input = File.OpenRead(Path.Combine(directory, name));

        var result = await composite.ExtractAsync(record, input);

        if (name == "scanned.pdf")
        {
            Assert.False(result.Success);
            Assert.True(string.IsNullOrEmpty(result.ExtractedText));
            Assert.Contains("no extractable text", result.ErrorMessage);
            Assert.Equal((int)TextExtractionStatus.NotStarted, record.TextExtractionStatus);
        }
        else
        {
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Contains("Knowz native verification", result.ExtractedText);
            Assert.DoesNotContain(@"{\rtf", result.ExtractedText);
            // Native fallbacks do not populate this property: this proves anydoc won the real composite.
            Assert.Equal(result.ExtractedText, record.ExtractedText);
            Assert.Equal((int)TextExtractionStatus.Completed, record.TextExtractionStatus);
        }
    }
}
