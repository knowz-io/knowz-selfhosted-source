using System.Diagnostics;
using System.Text;
using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>
/// Converts documents to markdown entirely on-box by shelling out to the local <c>anydoc</c> CLI.
/// Adds ODF / RTF / EPUB / legacy-Office coverage to the self-hosted edition without any cloud call.
///
/// Edition isolation (R1) is policy: this class is SELF-CONTAINED — it deliberately duplicates the
/// locator + process invariants of the main platform's <c>AnydocDocumentConverter</c> rather than
/// referencing <c>Knowz.Infrastructure</c>. Process pattern mirrors <c>LegacyOfficeConverter</c>:
/// per-arg <see cref="ProcessStartInfo.ArgumentList"/>, <c>UseShellExecute=false</c>, redirected
/// stdin/stdout/stderr with async drain, linked timeout CTS + <c>Kill(entireProcessTree: true)</c>,
/// a static config-seeded <see cref="SemaphoreSlim"/> gate, and truthful non-throwing failure.
///
/// When anydoc cannot produce usable markdown the extractor DECLINES
/// (<c>FileExtractionResult.Declined = true</c>) so <see cref="CompositeContentExtractor"/> hands the
/// file to the next extractor (Document Intelligence / native) instead of ending extraction (R5/R6).
///
/// WorkGroupID: kc-feat-anydoc-portable-tools-20260913-152433 — NodeID SH_AnydocContentExtractor.
/// </summary>
public class AnydocContentExtractor : IFileContentExtractor
{
    // MIME → anydoc --format token. Server-controlled WHITELIST: the value on the argv can only ever
    // be one of these constants, never a user-supplied string. text/*, text/csv and image/* are
    // deliberately absent — TextFileContentExtractor and ImageContentExtractor keep them (R4).
    internal static readonly IReadOnlyDictionary<string, string> FormatMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["application/pdf"] = "pdf",
            ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = "docx",
            ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = "xlsx",
            ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = "pptx",
            ["application/vnd.ms-word.document.macroenabled.12"] = "docx",
            ["application/vnd.ms-excel.sheet.macroenabled.12"] = "xlsx",
            ["application/vnd.ms-powerpoint.presentation.macroenabled.12"] = "pptx",
            ["application/msword"] = "doc",
            ["application/vnd.ms-excel"] = "xls",
            ["application/vnd.ms-powerpoint"] = "ppt",
            ["application/vnd.oasis.opendocument.text"] = "odt",
            ["application/vnd.oasis.opendocument.spreadsheet"] = "ods",
            ["application/vnd.oasis.opendocument.presentation"] = "odp",
            ["application/rtf"] = "rtf",
            ["text/rtf"] = "rtf",
            ["application/epub+zip"] = "epub"
        };

    private const int ExitOk = 0;
    private const int ExitNeedsOcr = 3;

    // Static, config-seeded gate (LegacyOfficeConverter pattern). Process-lifetime, never disposed.
    private static readonly object GateLock = new();
    private static SemaphoreSlim? _gate;
    private static int _gateLimit;

    private readonly AnydocOptions _options;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AnydocContentExtractor> _logger;
    private readonly Lazy<ResolvedCli?> _cli;

    public AnydocContentExtractor(
        IOptions<AnydocOptions> options,
        IConfiguration configuration,
        ILogger<AnydocContentExtractor> logger)
    {
        _options = options.Value;
        _configuration = configuration;
        _logger = logger;
        _cli = new Lazy<ResolvedCli?>(ResolveCli, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// The resolved anydoc launch target, or null when no binary could be located. Surfaced publicly
    /// for <c>ConfigurationProbe</c>, which reports availability WITHOUT performing any network call.
    /// </summary>
    public string? ResolvedCliPath => _cli.Value?.FileName;

    /// <summary>This extractor may hand the file on to the next candidate (R5).</summary>
    public bool MayDecline => true;

    /// <summary>
    /// True ONLY when the type is supported AND a usable binary resolves (R4). When anydoc is absent
    /// nothing is claimed and the composite chain behaves exactly as it did before this extractor existed.
    /// </summary>
    public bool CanExtract(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return false;
        if (!_options.Enabled) return false;
        if (string.Equals(_options.Mode, "Off", StringComparison.OrdinalIgnoreCase)) return false;
        if (!FormatMap.ContainsKey(contentType)) return false;
        return _cli.Value is not null;
    }

    public async Task<FileExtractionResult> ExtractAsync(
        FileRecord fileRecord, Stream fileStream, CancellationToken ct = default)
    {
        var contentType = fileRecord.ContentType;
        if (string.IsNullOrWhiteSpace(contentType) || !FormatMap.TryGetValue(contentType, out var format))
            return Decline("Unsupported content type", fileRecord);

        var cli = _cli.Value;
        if (cli is null)
            return Decline("anydoc CLI is not available", fileRecord);

        var maxBytes = (long)Math.Max(1, _options.MaxFileSizeMB) * 1024 * 1024;
        if (fileStream.CanSeek && fileStream.Length - fileStream.Position > maxBytes)
            return Decline($"file exceeds Anydoc:MaxFileSizeMB ({_options.MaxFileSizeMB} MB)", fileRecord);

        using var buffer = new MemoryStream();
        await fileStream.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();

        if (bytes.Length == 0)
            return Decline("file is empty", fileRecord);
        if (bytes.Length > maxBytes)
            return Decline($"file exceeds Anydoc:MaxFileSizeMB ({_options.MaxFileSizeMB} MB)", fileRecord);

        var gate = GetGate();
        await gate.WaitAsync(ct);
        try
        {
            var run = await RunAsync(cli, format, bytes, hostedOcr: false, ct);

            // Hosted OCR is opt-in third-party egress and is retried EXACTLY ONCE (R10).
            if (run.ExitCode == ExitNeedsOcr && HostedOcrEnabled(out var apiKey))
            {
                _logger.LogInformation(
                    "anydoc needs OCR for FileRecord {Id} — retrying once via hosted Firecrawl OCR ({Bytes} bytes sent off-box)",
                    fileRecord.Id, bytes.Length);
                run = await RunAsync(cli, format, bytes, hostedOcr: true, ct, apiKey);
            }

            if (run.TimedOut)
                return Decline($"anydoc timed out after {_options.TimeoutSeconds}s", fileRecord);
            if (run.LaunchFailed)
                return Decline("anydoc could not be launched", fileRecord);
            if (run.ExitCode == ExitNeedsOcr)
                return Decline("anydoc reports the document needs OCR", fileRecord);
            if (run.ExitCode != ExitOk)
                return Decline($"anydoc conversion failed (exit {run.ExitCode})", fileRecord);

            var markdown = run.StandardOutput;
            if (string.IsNullOrWhiteSpace(markdown) || markdown.Trim().Length < Math.Max(0, _options.MinChars))
                return Decline(
                    $"anydoc produced fewer than Anydoc:MinChars ({_options.MinChars}) characters", fileRecord);

            // Success — and ONLY on success is the FileRecord mutated (R8).
            fileRecord.ExtractedText = markdown;
            fileRecord.TextExtractedAt = DateTime.UtcNow;
            fileRecord.TextExtractionStatus = (int)TextExtractionStatus.Completed;
            fileRecord.TextExtractionError = null;

            return new FileExtractionResult(true, ExtractedText: markdown);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "anydoc extraction declined for FileRecord {Id}", fileRecord.Id);
            return Decline("anydoc extraction failed", fileRecord);
        }
        finally
        {
            gate.Release();
        }
    }

    private FileExtractionResult Decline(string reason, FileRecord fileRecord)
    {
        // R8: no FileRecord mutation on a decline — a later extractor must find the record untouched.
        _logger.LogInformation(
            "anydoc declined FileRecord {Id} ({ContentType}): {Reason}",
            fileRecord.Id, fileRecord.ContentType, reason);
        return new FileExtractionResult(false, ErrorMessage: reason, Declined: true);
    }

    private bool HostedOcrEnabled(out string? apiKey)
    {
        apiKey = _configuration["Firecrawl:ApiKey"];
        // Explicit Hosted mode authorizes the retry; Firecrawl also supports keyless requests.
        return string.Equals(_options.Ocr, "Hosted", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ProcessRun> RunAsync(
        ResolvedCli cli, string format, byte[] input, bool hostedOcr, CancellationToken ct, string? apiKey = null)
    {
        var startInfo = BuildStartInfo(cli, format, hostedOcr);
        if (hostedOcr)
        {
            // Key/URL travel via the environment ONLY — never argv (R10).
            if (!string.IsNullOrWhiteSpace(apiKey))
                startInfo.Environment["FIRECRAWL_API_KEY"] = apiKey;
            var apiUrl = _configuration["Firecrawl:ApiUrl"];
            if (!string.IsNullOrWhiteSpace(apiUrl))
                startInfo.Environment["FIRECRAWL_API_URL"] = apiUrl;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            if (!process.Start())
                return ProcessRun.Launch();

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await using (var stdin = process.StandardInput.BaseStream)
            {
                await stdin.WriteAsync(input, timeoutCts.Token);
                await stdin.FlushAsync(timeoutCts.Token);
            }

            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            KillTree(process);
            return ProcessRun.Timeout();
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "anydoc could not be launched from {Path}", cli.FileName);
            return ProcessRun.Launch();
        }

        if (process.ExitCode != ExitOk && stderr.Length > 0)
            _logger.LogInformation("anydoc exit {Code}: {Error}", process.ExitCode, stderr.ToString().Trim());

        return new ProcessRun(process.ExitCode, stdout.ToString().TrimEnd('\r', '\n'), false, false);
    }

    /// <summary>
    /// Visible-for-testing argv construction. Every argument is a server constant: the format token is
    /// whitelist-derived and the payload travels on stdin via the <c>-</c> positional, so no
    /// user-controlled string ever reaches argv.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(ResolvedCli cli, string format, bool hostedOcr)
    {
        var startInfo = new ProcessStartInfo(cli.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var prefix in cli.PrefixArguments)
            startInfo.ArgumentList.Add(prefix);

        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add(format);
        if (hostedOcr)
        {
            startInfo.ArgumentList.Add("--ocr");
            startInfo.ArgumentList.Add("hosted");
        }

        startInfo.ArgumentList.Add("-");
        return startInfo;
    }

    /// <summary>
    /// Locator order (R3): <c>Anydoc:CliPath</c> → <c>/usr/local/bin/anydoc</c> (the wrapper installed
    /// into the self-hosted image) → <c>&lt;cwd&gt;/tools/anydoc/node_modules/@firecrawl/anydoc/cli.js</c>
    /// via <c>node</c> → bare <c>anydoc</c> on PATH. Cached for the lifetime of the instance.
    /// </summary>
    internal ResolvedCli? ResolveCli()
    {
        var configured = _options.CliPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            configured = configured.Trim();
            if (!File.Exists(configured)) return null;
            return configured.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                ? new ResolvedCli("node", [configured])
                : new ResolvedCli(configured, []);
        }

        const string wrapper = "/usr/local/bin/anydoc";
        if (File.Exists(wrapper))
            return new ResolvedCli(wrapper, []);

        var bundled = Path.Combine(
            Directory.GetCurrentDirectory(), "tools", "anydoc", "node_modules", "@firecrawl", "anydoc", "cli.js");
        if (File.Exists(bundled))
            return new ResolvedCli("node", [bundled]);

        var onPath = FindOnPath("anydoc");
        return onPath is null ? null : new ResolvedCli(onPath, []);
    }

    private static string? FindOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), executable);
                if (File.Exists(candidate)) return candidate;
                if (OperatingSystem.IsWindows() && File.Exists(candidate + ".cmd")) return candidate + ".cmd";
            }
            catch
            {
                // Malformed PATH entry — skip.
            }
        }

        return null;
    }

    private SemaphoreSlim GetGate()
    {
        var limit = Math.Clamp(_options.MaxConcurrency, 1, 50);
        lock (GateLock)
        {
            if (_gate is null || _gateLimit != limit)
            {
                _gate = new SemaphoreSlim(limit, limit);
                _gateLimit = limit;
            }

            return _gate;
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort only.
        }
    }

    /// <summary>Resolved launch target: an executable plus any interpreter prefix arguments.</summary>
    internal sealed record ResolvedCli(string FileName, IReadOnlyList<string> PrefixArguments);

    private sealed record ProcessRun(int ExitCode, string StandardOutput, bool TimedOut, bool LaunchFailed)
    {
        public static ProcessRun Timeout() => new(-1, string.Empty, true, false);
        public static ProcessRun Launch() => new(-1, string.Empty, false, true);
    }
}
