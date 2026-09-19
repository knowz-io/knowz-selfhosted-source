using System.Diagnostics;
using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// Explicit (never silent) skip for stub-executable tests on a host without a POSIX shell.
/// Mirrors the repo-native <c>PostgresConfigurationFactAttribute</c> pattern.
/// </summary>
public sealed class PosixShellFactAttribute : FactAttribute
{
    public PosixShellFactAttribute()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh"))
            Skip = "Requires a POSIX /bin/sh to host the anydoc stub executable";
    }
}

/// <inheritdoc cref="PosixShellFactAttribute"/>
public sealed class PosixShellTheoryAttribute : TheoryAttribute
{
    public PosixShellTheoryAttribute()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh"))
            Skip = "Requires a POSIX /bin/sh to host the anydoc stub executable";
    }
}

/// <summary>
/// Stub-executable tests for <see cref="AnydocContentExtractor"/>. A <c>#!/bin/sh</c> script stands in
/// for the real anydoc CLI and is injected via <c>Anydoc:CliPath</c>, so the real process invariants
/// (argv, stdin, exit codes, timeout kill) are exercised without any dependency on a bundled binary.
/// Host-incompatible cases are skipped EXPLICITLY (never silently).
///
/// WorkGroupID: kc-feat-anydoc-portable-tools-20260913-152433 — NodeID SH_AnydocContentExtractor.
/// </summary>
public sealed class AnydocContentExtractorTests : IDisposable
{
    private readonly string _tempDir;

    public AnydocContentExtractorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "anydoc-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private string WriteStub(string body)
    {
        var path = Path.Combine(_tempDir, "anydoc-stub-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(path, "#!/bin/sh\n" + body);
#pragma warning disable CA1416 // POSIX-only path — Windows hosts skip these tests via PosixShellFact
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416
        return path;
    }

    /// <summary>Stub that records argv + selected env + invocation count, then exits with a fixed code.</summary>
    private (string Path, string LogPath) WriteRecordingStub(int exitCode, string stdout = "")
    {
        var log = Path.Combine(_tempDir, "invocations-" + Guid.NewGuid().ToString("N") + ".log");
        var body =
            $"cat > /dev/null\n" +
            $"echo \"ARGV:$*\" >> {log}\n" +
            $"echo \"KEY:${{FIRECRAWL_API_KEY}}\" >> {log}\n" +
            (string.IsNullOrEmpty(stdout) ? string.Empty : $"printf '%s' '{stdout}'\n") +
            $"exit {exitCode}\n";
        return (WriteStub(body), log);
    }

    private static AnydocContentExtractor Build(string? cliPath, Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?> { ["Anydoc:CliPath"] = cliPath };
        if (extra is not null)
            foreach (var kv in extra) settings[kv.Key] = kv.Value;

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var options = new AnydocOptions();
        config.GetSection(AnydocOptions.SectionName).Bind(options);

        return new AnydocContentExtractor(
            Options.Create(options), config, NullLogger<AnydocContentExtractor>.Instance);
    }

    private static FileRecord MakeRecord(string contentType) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        FileName = "doc",
        ContentType = contentType,
        TextExtractionStatus = (int)TextExtractionStatus.NotStarted,
        TextExtractionError = "pre-existing"
    };

    // =============================================
    // CanExtract
    // =============================================

    [PosixShellFact]
    public void CanExtract_Odt_IsTrue_WhenStubResolves()
    {
        var extractor = Build(WriteRecordingStub(0).Path);
        Assert.True(extractor.CanExtract("application/vnd.oasis.opendocument.text"));
    }

    [Fact]
    public void CanExtract_Odt_IsFalse_WhenCliPathMissing()
    {
        var extractor = Build(Path.Combine(_tempDir, "does-not-exist"));
        Assert.False(extractor.CanExtract("application/vnd.oasis.opendocument.text"));
    }

    [PosixShellTheory]
    [InlineData("text/plain")]
    [InlineData("text/csv")]
    [InlineData("image/png")]
    [InlineData(null)]
    public void CanExtract_IsFalse_ForUnclaimedTypes(string? contentType)
    {
        var extractor = Build(WriteRecordingStub(0).Path);
        Assert.False(extractor.CanExtract(contentType));
    }

    [PosixShellFact]
    public void CanExtract_IsFalse_WhenModeOff()
    {
        var extractor = Build(WriteRecordingStub(0).Path,
            new Dictionary<string, string?> { ["Anydoc:Mode"] = "Off" });
        Assert.False(extractor.CanExtract("application/pdf"));
    }

    // =============================================
    // ExtractAsync
    // =============================================

    [PosixShellFact]
    public async Task ExtractAsync_Exit0_WithMarkdown_Succeeds()
    {
        var markdown = new string('a', 80);
        var stub = WriteStub($"cat > /dev/null\nprintf '%s' '{markdown}'\nexit 0\n");
        var extractor = Build(stub);
        var record = MakeRecord("application/vnd.oasis.opendocument.text");

        var result = await extractor.ExtractAsync(record, new MemoryStream(new byte[] { 1, 2, 3 }));

        Assert.True(result.Success);
        Assert.False(result.Declined);
        Assert.Equal(markdown, result.ExtractedText);
        Assert.Equal((int)TextExtractionStatus.Completed, record.TextExtractionStatus);
        Assert.Null(record.TextExtractionError);
        Assert.Equal(markdown, record.ExtractedText);
    }

    [PosixShellFact]
    public async Task ExtractAsync_Exit3_Declines_AndLeavesRecordUntouched()
    {
        var extractor = Build(WriteRecordingStub(3).Path);
        var record = MakeRecord("application/pdf");

        var result = await extractor.ExtractAsync(record, new MemoryStream(new byte[] { 1, 2, 3 }));

        Assert.False(result.Success);
        Assert.True(result.Declined);
        Assert.Equal((int)TextExtractionStatus.NotStarted, record.TextExtractionStatus);
        Assert.Equal("pre-existing", record.TextExtractionError);
        Assert.Null(record.ExtractedText);
    }

    [PosixShellFact]
    public async Task ExtractAsync_BelowMinChars_Declines()
    {
        var stub = WriteStub("cat > /dev/null\nprintf '%s' 'ten charss'\nexit 0\n");
        var extractor = Build(stub, new Dictionary<string, string?> { ["Anydoc:MinChars"] = "50" });

        var result = await extractor.ExtractAsync(MakeRecord("application/pdf"), new MemoryStream(new byte[] { 1 }));

        Assert.True(result.Declined);
        Assert.False(result.Success);
    }

    [PosixShellFact]
    public async Task ExtractAsync_Timeout_Declines_AndKillsProcess()
    {
        var pidFile = Path.Combine(_tempDir, "pid-" + Guid.NewGuid().ToString("N"));
        var stub = WriteStub($"echo $$ > {pidFile}\ncat > /dev/null\nsleep 30\nexit 0\n");
        var extractor = Build(stub, new Dictionary<string, string?> { ["Anydoc:TimeoutSeconds"] = "1" });

        var sw = Stopwatch.StartNew();
        var result = await extractor.ExtractAsync(MakeRecord("application/pdf"), new MemoryStream(new byte[] { 1 }));
        sw.Stop();

        Assert.True(result.Declined);
        Assert.False(result.Success);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        Assert.Contains("timed out", result.ErrorMessage);

        // The stub sleeps 30s — if the tree had survived the kill its shell would still be alive.
        await Task.Delay(300);
        var pid = int.Parse(File.ReadAllText(pidFile).Trim());
        var alive = true;
        try
        {
            alive = !Process.GetProcessById(pid).HasExited;
        }
        catch (ArgumentException)
        {
            alive = false;  // process no longer exists
        }

        Assert.False(alive, $"anydoc stub pid {pid} survived the timeout kill");
    }

    [PosixShellFact]
    public async Task ExtractAsync_AboveMaxFileSize_Declines_WithoutInvokingStub()
    {
        var (stub, log) = WriteRecordingStub(0, new string('a', 80));
        var extractor = Build(stub, new Dictionary<string, string?> { ["Anydoc:MaxFileSizeMB"] = "1" });

        var oversized = new MemoryStream(new byte[2 * 1024 * 1024]);
        var result = await extractor.ExtractAsync(MakeRecord("application/pdf"), oversized);

        Assert.True(result.Declined);
        Assert.False(File.Exists(log));  // ZERO invocations
    }

    // =============================================
    // Hosted OCR (R10)
    // =============================================

    [PosixShellFact]
    public async Task ExtractAsync_HostedOcr_RetriesExactlyOnce_WithEnvOnlyCredentials()
    {
        var (stub, log) = WriteRecordingStub(3);
        var extractor = Build(stub, new Dictionary<string, string?>
        {
            ["Anydoc:Ocr"] = "Hosted",
            ["Firecrawl:ApiKey"] = "test-key"
        });

        var result = await extractor.ExtractAsync(MakeRecord("application/pdf"), new MemoryStream(new byte[] { 1 }));

        Assert.True(result.Declined);
        var lines = File.ReadAllLines(log);
        var argv = lines.Where(l => l.StartsWith("ARGV:")).ToList();
        Assert.Equal(2, argv.Count);                                  // exactly one retry
        Assert.DoesNotContain("--ocr", argv[0]);
        Assert.Contains("--ocr hosted", argv[1]);
        Assert.DoesNotContain("--api-key", argv[1]);
        Assert.DoesNotContain("--api-url", argv[1]);
        Assert.DoesNotContain("test-key", argv[1]);
        var keys = lines.Where(l => l.StartsWith("KEY:")).ToList();
        Assert.Equal("KEY:", keys[0]);                                // no key on the first attempt
        Assert.Equal("KEY:test-key", keys[1]);                        // env-only on the retry
    }

    [PosixShellFact]
    public async Task ExtractAsync_WithoutOcrConfigured_InvokesStubExactlyOnce()
    {
        var (stub, log) = WriteRecordingStub(3);
        var extractor = Build(stub);

        await extractor.ExtractAsync(MakeRecord("application/pdf"), new MemoryStream(new byte[] { 1 }));

        Assert.Single(File.ReadAllLines(log), l => l.StartsWith("ARGV:"));
    }

    // =============================================
    // ProcessStartInfo seam
    // =============================================

    [Fact]
    public void BuildStartInfo_HasHardenedShape()
    {
        var cli = new AnydocContentExtractor.ResolvedCli("/usr/local/bin/anydoc", []);

        var psi = AnydocContentExtractor.BuildStartInfo(cli, "odt", hostedOcr: false);

        Assert.False(psi.UseShellExecute);
        Assert.True(psi.CreateNoWindow);
        Assert.True(psi.RedirectStandardInput);
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
        Assert.Equal(string.Empty, psi.Arguments);                     // per-arg only, never a string
        Assert.Equal(["--format", "odt", "-"], psi.ArgumentList);
        Assert.Single(psi.ArgumentList, a => a == "--format");
        Assert.Equal("-", psi.ArgumentList[^1]);                       // payload travels on stdin
    }

    [Fact]
    public void BuildStartInfo_FormatTokenIsWhitelisted()
    {
        foreach (var format in AnydocContentExtractor.FormatMap.Values.Distinct())
        {
            var psi = AnydocContentExtractor.BuildStartInfo(
                new AnydocContentExtractor.ResolvedCli("anydoc", []), format, hostedOcr: true);
            Assert.Equal(["--format", format, "--ocr", "hosted", "-"], psi.ArgumentList);
        }
    }

    [Fact]
    public void BuildStartInfo_NodeInterpreterPrefixComesFirst()
    {
        var cli = new AnydocContentExtractor.ResolvedCli("node", ["/app/tools/anydoc/cli.js"]);

        var psi = AnydocContentExtractor.BuildStartInfo(cli, "pdf", hostedOcr: false);

        Assert.Equal("node", psi.FileName);
        Assert.Equal(["/app/tools/anydoc/cli.js", "--format", "pdf", "-"], psi.ArgumentList);
    }

    [Fact]
    public void FormatMap_DoesNotClaimTextCsvOrImages()
    {
        Assert.DoesNotContain("text/plain", AnydocContentExtractor.FormatMap.Keys);
        Assert.DoesNotContain("text/csv", AnydocContentExtractor.FormatMap.Keys);
        Assert.DoesNotContain(AnydocContentExtractor.FormatMap.Keys, k => k.StartsWith("image/"));
        Assert.Contains("text/rtf", AnydocContentExtractor.FormatMap.Keys);
        Assert.Contains("application/epub+zip", AnydocContentExtractor.FormatMap.Keys);
    }
    [PosixShellFact]
    public async Task ExtractAsync_HostedOcrWithoutKey_RetriesExactlyOnce()
    {
        var (stub, log) = WriteRecordingStub(3);
        var extractor = Build(stub, new Dictionary<string, string?> { ["Anydoc:Ocr"] = "Hosted" });

        var result = await extractor.ExtractAsync(MakeRecord("application/pdf"), new MemoryStream([1]));

        Assert.True(result.Declined);
        var argv = File.ReadAllLines(log).Where(l => l.StartsWith("ARGV:")).ToArray();
        Assert.Equal(2, argv.Length);
        Assert.DoesNotContain("--ocr hosted", argv[0]);
        Assert.Contains("--ocr hosted", argv[1]);
        Assert.DoesNotContain("--api-key", argv[1]);
    }

    [Fact]
    public void ResolveCli_ConfiguredJavaScript_UsesNodeWithoutExecutablePermission()
    {
        var script = Path.Combine(_tempDir, "cli with spaces.js");
        File.WriteAllText(script, "process.stdout.write('0.2.4');");

        var resolved = Build(script).ResolveCli();

        Assert.NotNull(resolved);
        Assert.Equal("node", resolved.FileName);
        Assert.Equal([script], resolved.PrefixArguments);
    }
}
