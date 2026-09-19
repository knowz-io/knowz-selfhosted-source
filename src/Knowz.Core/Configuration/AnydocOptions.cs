namespace Knowz.Core.Configuration;

/// <summary>
/// Options for the local <c>anydoc</c> CLI used by <c>AnydocContentExtractor</c> to convert
/// documents to markdown entirely on-box. Option names mirror the main platform verbatim
/// (SVC_AnydocDocumentConverter) so a single deployment can configure both editions identically.
///
/// WorkGroupID: kc-feat-anydoc-portable-tools-20260913-152433 — NodeID SH_AnydocContentExtractor (R9).
/// </summary>
public class AnydocOptions
{
    public const string SectionName = "Anydoc";

    /// <summary>Master switch. When false the extractor never claims a content type.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// <c>PreferLocal</c> (default) places the extractor before Document Intelligence,
    /// <c>FallbackOnly</c> places it after, <c>Off</c> omits it entirely.
    /// Ordering is resolved at container-build time and is not hot-reloadable.
    /// </summary>
    public string Mode { get; set; } = "PreferLocal";

    /// <summary>Explicit path to the anydoc binary/wrapper. Highest-priority locator entry.</summary>
    public string? CliPath { get; set; }

    /// <summary>Per-conversion wall-clock budget. On expiry the process tree is killed and the extractor declines.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Static process-wide gate on concurrent anydoc invocations.</summary>
    public int MaxConcurrency { get; set; } = 8;

    /// <summary>Inputs larger than this are declined without invoking the CLI.</summary>
    public int MaxFileSizeMB { get; set; } = 200;

    /// <summary>Markdown shorter than this is treated as "no usable text" and declined.</summary>
    public int MinChars { get; set; } = 50;

    /// <summary>
    /// <c>None</c> (default) or <c>Hosted</c>. <c>Hosted</c> is THIRD-PARTY EGRESS: the document is
    /// sent to Firecrawl for OCR. Off unless explicitly enabled.
    /// </summary>
    public string Ocr { get; set; } = "None";
}
