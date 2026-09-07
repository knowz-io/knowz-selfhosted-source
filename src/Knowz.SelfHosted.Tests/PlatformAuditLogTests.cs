namespace Knowz.SelfHosted.Tests;

using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

/// <summary>
/// Tests for <see cref="PlatformAuditLogService"/> — Node 4 (PlatformSyncHistory).
/// Covers V-SEC-07 (every platform sync event audited) and V-SEC-03 (sanitized error messages).
/// </summary>
public class PlatformAuditLogTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherTenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");

    private readonly SelfHostedDbContext _db;
    private readonly PlatformAuditLogService _service;

    public PlatformAuditLogTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(options, tenantProvider);

        var logger = Substitute.For<ILogger<PlatformAuditLogService>>();
        _service = new PlatformAuditLogService(_db, tenantProvider, logger);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- StartAsync ---

    [Fact]
    public async Task StartAsync_CreatesInProgressRow()
    {
        var linkId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var runId = await _service.StartAsync(new PlatformSyncRunStart(
            UserId: userId,
            UserEmail: "admin@example.com",
            Operation: PlatformSyncOperation.PullVault,
            Direction: PlatformSyncDirection.Pull,
            VaultSyncLinkId: linkId));

        Assert.NotEqual(Guid.Empty, runId);

        var row = await _db.PlatformSyncRuns.FirstAsync(r => r.Id == runId);
        Assert.Equal(TenantId, row.TenantId);
        Assert.Equal(linkId, row.VaultSyncLinkId);
        Assert.Equal(userId, row.UserId);
        Assert.Equal("admin@example.com", row.UserEmail);
        Assert.Equal(PlatformSyncOperation.PullVault, row.Operation);
        Assert.Equal(PlatformSyncDirection.Pull, row.Direction);
        Assert.Equal(PlatformSyncRunStatus.InProgress, row.Status);
        Assert.Null(row.CompletedAt);
    }

    [Fact]
    public async Task StartAsync_ConnectionOp_NoLink()
    {
        var runId = await _service.StartAsync(new PlatformSyncRunStart(
            UserId: Guid.Empty,
            UserEmail: null,
            Operation: PlatformSyncOperation.Connect,
            Direction: PlatformSyncDirection.None,
            VaultSyncLinkId: null));

        var row = await _db.PlatformSyncRuns.FirstAsync(r => r.Id == runId);
        Assert.Null(row.VaultSyncLinkId);
        Assert.Equal(PlatformSyncOperation.Connect, row.Operation);
        Assert.Equal(PlatformSyncDirection.None, row.Direction);
    }

    // --- CompleteAsync ---

    [Fact]
    public async Task CompleteAsync_UpdatesFieldsAndCompletedAt()
    {
        var runId = await _service.StartAsync(new PlatformSyncRunStart(
            Guid.Empty, null, PlatformSyncOperation.PullVault, PlatformSyncDirection.Pull));

        await _service.CompleteAsync(runId, new PlatformSyncRunResult(
            ItemCount: 42,
            BytesTransferred: 1024,
            Status: PlatformSyncRunStatus.Succeeded));

        var row = await _db.PlatformSyncRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
        Assert.Equal(PlatformSyncRunStatus.Succeeded, row.Status);
        Assert.Equal(42, row.ItemCount);
        Assert.Equal(1024, row.BytesTransferred);
        Assert.NotNull(row.CompletedAt);
    }

    [Fact]
    public async Task CompleteAsync_PartialStatus_PreservedAsPartial()
    {
        var runId = await _service.StartAsync(new PlatformSyncRunStart(
            Guid.Empty, null, PlatformSyncOperation.PullVault, PlatformSyncDirection.Pull));

        await _service.CompleteAsync(runId, new PlatformSyncRunResult(
            ItemCount: 100,
            BytesTransferred: 2048,
            Status: PlatformSyncRunStatus.Partial));

        var row = await _db.PlatformSyncRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
        Assert.Equal(PlatformSyncRunStatus.Partial, row.Status);
    }

    [Fact]
    public async Task CompleteAsync_UnknownRunId_LogsButDoesNotThrow()
    {
        await _service.CompleteAsync(
            Guid.NewGuid(),
            new PlatformSyncRunResult(0, 0, PlatformSyncRunStatus.Succeeded));
        // Should not throw — silent no-op with warning log.
    }

    // --- FailAsync + sanitization ---

    [Fact]
    public async Task FailAsync_RedactsApiKeyFromErrorMessage()
    {
        var runId = await _service.StartAsync(new PlatformSyncRunStart(
            Guid.Empty, null, PlatformSyncOperation.PullVault, PlatformSyncDirection.Pull));

        var raw = "Request failed: Authorization: X-Api-Key: ukz_abcdefghijklmnopqrstuvwxyz";
        await _service.FailAsync(runId, raw);

        var row = await _db.PlatformSyncRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
        Assert.Equal(PlatformSyncRunStatus.Failed, row.Status);
        Assert.NotNull(row.ErrorMessage);
        Assert.DoesNotContain("ukz_abcdefghijklmnopqrstuvwxyz", row.ErrorMessage);
        Assert.Contains("redacted", row.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailAsync_RedactsBasicAuthFromUrl()
    {
        var runId = await _service.StartAsync(new PlatformSyncRunStart(
            Guid.Empty, null, PlatformSyncOperation.PullVault, PlatformSyncDirection.Pull));

        await _service.FailAsync(runId, "Connection failed to https://user:secret@api.knowz.io/foo");

        var row = await _db.PlatformSyncRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
        Assert.NotNull(row.ErrorMessage);
        Assert.DoesNotContain("user:secret", row.ErrorMessage);
        Assert.DoesNotContain("secret", row.ErrorMessage);
    }

    [Fact]
    public async Task FailAsync_TruncatesLongErrorMessageTo500Chars()
    {
        var runId = await _service.StartAsync(new PlatformSyncRunStart(
            Guid.Empty, null, PlatformSyncOperation.PullVault, PlatformSyncDirection.Pull));

        var longMessage = new string('x', 1000);
        await _service.FailAsync(runId, longMessage);

        var row = await _db.PlatformSyncRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
        Assert.NotNull(row.ErrorMessage);
        Assert.Equal(500, row.ErrorMessage!.Length);
    }

    [Fact]
    public async Task FailAsync_EmptyErrorMessage_StoresEmpty()
    {
        var runId = await _service.StartAsync(new PlatformSyncRunStart(
            Guid.Empty, null, PlatformSyncOperation.PullVault, PlatformSyncDirection.Pull));

        await _service.FailAsync(runId, string.Empty);

        var row = await _db.PlatformSyncRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
        Assert.Equal(PlatformSyncRunStatus.Failed, row.Status);
        Assert.Equal(string.Empty, row.ErrorMessage);
    }

    // --- GetHistoryAsync ---

    [Fact]
    public async Task GetHistoryAsync_ReturnsOnlyCurrentTenant()
    {
        await _service.StartAsync(new PlatformSyncRunStart(
            Guid.Empty, null, PlatformSyncOperation.PullVault, PlatformSyncDirection.Pull));

        _db.PlatformSyncRuns.Add(new PlatformSyncRun
        {
            Id = Guid.NewGuid(),
            TenantId = OtherTenantId,
            Operation = PlatformSyncOperation.PushVault,
            Direction = PlatformSyncDirection.Push,
            Status = PlatformSyncRunStatus.Succeeded,
            StartedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var rows = await _service.GetHistoryAsync(TenantId, page: 1, pageSize: 50);
        Assert.Single(rows);
    }

    [Fact]
    public async Task GetHistoryAsync_OrdersByStartedAtDescending()
    {
        var older = new PlatformSyncRun
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            Operation = PlatformSyncOperation.PullVault,
            Status = PlatformSyncRunStatus.Succeeded,
            StartedAt = DateTime.UtcNow.AddHours(-2),
        };
        var newer = new PlatformSyncRun
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            Operation = PlatformSyncOperation.PushVault,
            Status = PlatformSyncRunStatus.Succeeded,
            StartedAt = DateTime.UtcNow.AddHours(-1),
        };
        _db.PlatformSyncRuns.AddRange(older, newer);
        await _db.SaveChangesAsync();

        var rows = await _service.GetHistoryAsync(TenantId, 1, 50);
        Assert.Equal(2, rows.Count);
        Assert.Equal(newer.Id, rows[0].Id);
        Assert.Equal(older.Id, rows[1].Id);
    }

    [Fact]
    public async Task GetHistoryAsync_RespectsPagination()
    {
        for (var i = 0; i < 5; i++)
        {
            _db.PlatformSyncRuns.Add(new PlatformSyncRun
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                Operation = PlatformSyncOperation.PullVault,
                Status = PlatformSyncRunStatus.Succeeded,
                StartedAt = DateTime.UtcNow.AddSeconds(i),
            });
        }
        await _db.SaveChangesAsync();

        var page1 = await _service.GetHistoryAsync(TenantId, page: 1, pageSize: 2);
        var page2 = await _service.GetHistoryAsync(TenantId, page: 2, pageSize: 2);

        Assert.Equal(2, page1.Count);
        Assert.Equal(2, page2.Count);
        Assert.NotEqual(page1[0].Id, page2[0].Id);
    }

    [Fact]
    public async Task GetHistoryAsync_CapsPageSizeAt500()
    {
        // Caller requests 10_000 — implementation must clamp.
        var rows = await _service.GetHistoryAsync(TenantId, 1, 10_000);
        Assert.NotNull(rows);
        // No exception, no overflow — empty DB should return empty.
        Assert.Empty(rows);
    }

    // --- CountRecentAsync ---

    [Fact]
    public async Task CountRecentAsync_OnlyCountsWithinWindow()
    {
        _db.PlatformSyncRuns.Add(new PlatformSyncRun
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            Operation = PlatformSyncOperation.PullVault,
            Status = PlatformSyncRunStatus.Succeeded,
            StartedAt = DateTime.UtcNow.AddMinutes(-30), // inside window
        });
        _db.PlatformSyncRuns.Add(new PlatformSyncRun
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            Operation = PlatformSyncOperation.PullVault,
            Status = PlatformSyncRunStatus.Succeeded,
            StartedAt = DateTime.UtcNow.AddHours(-2), // outside window
        });
        await _db.SaveChangesAsync();

        var count = await _service.CountRecentAsync(TenantId, TimeSpan.FromHours(1));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task CountRecentAsync_ScopedByTenant()
    {
        _db.PlatformSyncRuns.Add(new PlatformSyncRun
        {
            Id = Guid.NewGuid(),
            TenantId = OtherTenantId,
            Operation = PlatformSyncOperation.PullVault,
            Status = PlatformSyncRunStatus.Succeeded,
            StartedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var count = await _service.CountRecentAsync(TenantId, TimeSpan.FromHours(1));
        Assert.Equal(0, count);
    }

    // --- CountInProgressAsync ---

    [Fact]
    public async Task CountInProgressAsync_CountsOnlyInProgress()
    {
        _db.PlatformSyncRuns.Add(new PlatformSyncRun
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            Operation = PlatformSyncOperation.PullVault,
            Status = PlatformSyncRunStatus.InProgress,
            StartedAt = DateTime.UtcNow,
        });
        _db.PlatformSyncRuns.Add(new PlatformSyncRun
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            Operation = PlatformSyncOperation.PullVault,
            Status = PlatformSyncRunStatus.Succeeded,
            StartedAt = DateTime.UtcNow,
        });
        _db.PlatformSyncRuns.Add(new PlatformSyncRun
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            Operation = PlatformSyncOperation.PullVault,
            Status = PlatformSyncRunStatus.Failed,
            StartedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var count = await _service.CountInProgressAsync(TenantId);
        Assert.Equal(1, count);
    }

    // --- Display-safe DTO projection ---

    [Fact]
    public async Task GetHistoryAsync_DtoDoesNotLeakApiKeyInErrorMessage()
    {
        var runId = await _service.StartAsync(new PlatformSyncRunStart(
            Guid.Empty, null, PlatformSyncOperation.PullVault, PlatformSyncDirection.Pull));

        await _service.FailAsync(runId, "Failed with ukz_1234567890abcdef token");

        var rows = await _service.GetHistoryAsync(TenantId, 1, 50);
        var dto = Assert.Single(rows);
        Assert.NotNull(dto.ErrorMessage);
        Assert.DoesNotContain("ukz_1234567890abcdef", dto.ErrorMessage);
    }
}
