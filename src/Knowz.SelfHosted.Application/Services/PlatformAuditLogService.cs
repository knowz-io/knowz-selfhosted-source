namespace Knowz.SelfHosted.Application.Services;

using System.Text.RegularExpressions;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// SQL-backed <see cref="IPlatformAuditLog"/>. Writes rows to <see cref="SelfHostedDbContext.PlatformSyncRuns"/>
/// with sanitized error messages and tenant scoping at every read.
/// </summary>
public class PlatformAuditLogService : IPlatformAuditLog
{
    private const int MaxErrorMessageLength = 500;
    private const int MaxPageSize = 500;

    // Matches ukz_ API keys (self-hosted + platform format): ukz_ followed by 8+ alnum chars.
    private static readonly Regex ApiKeyPattern = new(
        @"\bukz_[A-Za-z0-9]{8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Matches Authorization: Bearer / X-Api-Key: ... headers leaked into exception messages.
    // Captures the entire value up to end-of-line so multi-word values (e.g. "Bearer <jwt>")
    // are fully redacted, not just the first token.
    private static readonly Regex AuthHeaderPattern = new(
        @"(?i)\b(authorization|x-api-key|x-service-key)\s*[:=]\s*[^\r\n]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Matches basic-auth embedded in URLs (https://user:pass@host/...).
    private static readonly Regex BasicAuthUrlPattern = new(
        @"(?i)(https?://)[^/@\s]+:[^/@\s]+@",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly SelfHostedDbContext _db;
    private readonly ITenantProvider _tenantProvider;
    private readonly ILogger<PlatformAuditLogService> _logger;

    public PlatformAuditLogService(
        SelfHostedDbContext db,
        ITenantProvider tenantProvider,
        ILogger<PlatformAuditLogService> logger)
    {
        _db = db;
        _tenantProvider = tenantProvider;
        _logger = logger;
    }

    public async Task<Guid> StartAsync(PlatformSyncRunStart start, CancellationToken ct = default)
    {
        var run = new PlatformSyncRun
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantProvider.TenantId,
            VaultSyncLinkId = start.VaultSyncLinkId,
            UserId = start.UserId,
            UserEmail = Truncate(start.UserEmail, 255),
            Operation = start.Operation,
            Direction = start.Direction,
            KnowledgeId = start.KnowledgeId,
            Status = PlatformSyncRunStatus.InProgress,
            StartedAt = DateTime.UtcNow,
        };

        _db.PlatformSyncRuns.Add(run);
        await _db.SaveChangesAsync(ct);
        return run.Id;
    }

    public async Task CompleteAsync(Guid runId, PlatformSyncRunResult result, CancellationToken ct = default)
    {
        var run = await _db.PlatformSyncRuns
            .FirstOrDefaultAsync(r => r.Id == runId, ct);

        if (run is null)
        {
            _logger.LogWarning("PlatformAuditLog.CompleteAsync: run {RunId} not found", runId);
            return;
        }

        // Reject attempts to reuse FailAsync contract via CompleteAsync — spec: callers must use FailAsync.
        var status = result.Status == PlatformSyncRunStatus.Failed
            ? PlatformSyncRunStatus.Partial
            : result.Status;

        run.Status = status;
        run.ItemCount = result.ItemCount;
        run.BytesTransferred = result.BytesTransferred;
        run.CompletedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
    }

    public async Task FailAsync(Guid runId, string errorMessage, CancellationToken ct = default)
    {
        var run = await _db.PlatformSyncRuns
            .FirstOrDefaultAsync(r => r.Id == runId, ct);

        if (run is null)
        {
            _logger.LogWarning("PlatformAuditLog.FailAsync: run {RunId} not found", runId);
            return;
        }

        run.Status = PlatformSyncRunStatus.Failed;
        run.ErrorMessage = SanitizeErrorMessage(errorMessage);
        run.CompletedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<PlatformSyncRunDto>> GetHistoryAsync(
        Guid tenantId,
        int page,
        int pageSize,
        CancellationToken ct = default,
        Guid? vaultSyncLinkId = null)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > MaxPageSize) pageSize = MaxPageSize;

        var skip = (page - 1) * pageSize;

        var query = _db.PlatformSyncRuns
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId);

        if (vaultSyncLinkId.HasValue)
            query = query.Where(r => r.VaultSyncLinkId == vaultSyncLinkId.Value);

        var rows = await query
            .OrderByDescending(r => r.StartedAt)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync(ct);

        return rows.Select(ToDto).ToList();
    }

    public async Task<int> CountRecentAsync(Guid tenantId, TimeSpan window, CancellationToken ct = default)
    {
        var threshold = DateTime.UtcNow - window;
        return await _db.PlatformSyncRuns
            .Where(r => r.TenantId == tenantId && r.StartedAt >= threshold)
            .CountAsync(ct);
    }

    public async Task<int> CountInProgressAsync(Guid tenantId, CancellationToken ct = default)
    {
        return await _db.PlatformSyncRuns
            .Where(r => r.TenantId == tenantId && r.Status == PlatformSyncRunStatus.InProgress)
            .CountAsync(ct);
    }

    public async Task LogAsync(
        PlatformSyncRunStart start,
        PlatformSyncRunStatus status,
        string? errorMessage,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var run = new PlatformSyncRun
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantProvider.TenantId,
            VaultSyncLinkId = start.VaultSyncLinkId,
            UserId = start.UserId,
            UserEmail = Truncate(start.UserEmail, 255),
            Operation = start.Operation,
            Direction = start.Direction,
            KnowledgeId = start.KnowledgeId,
            Status = status,
            ErrorMessage = status == PlatformSyncRunStatus.Failed
                ? SanitizeErrorMessage(errorMessage)
                : null,
            StartedAt = now,
            CompletedAt = now,
        };

        _db.PlatformSyncRuns.Add(run);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Redact API keys / auth headers / basic-auth URLs from an error string and truncate to
    /// <see cref="MaxErrorMessageLength"/> characters. Exposed as internal for test coverage.
    /// </summary>
    internal static string SanitizeErrorMessage(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var cleaned = raw;
        cleaned = ApiKeyPattern.Replace(cleaned, "ukz_****REDACTED");
        cleaned = AuthHeaderPattern.Replace(cleaned, "$1: [redacted]");
        cleaned = BasicAuthUrlPattern.Replace(cleaned, "$1****REDACTED@");

        if (cleaned.Length > MaxErrorMessageLength)
            cleaned = cleaned.Substring(0, MaxErrorMessageLength);

        return cleaned;
    }

    private static PlatformSyncRunDto ToDto(PlatformSyncRun r) => new(
        r.Id,
        r.VaultSyncLinkId,
        r.UserId,
        r.UserEmail,
        r.Operation,
        r.Direction,
        r.KnowledgeId,
        r.ItemCount,
        r.BytesTransferred,
        r.Status,
        r.ErrorMessage,
        r.StartedAt,
        r.CompletedAt);

    private static string? Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Length <= max ? s : s.Substring(0, max);
    }
}
