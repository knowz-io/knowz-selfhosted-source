using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.Services.GitCommitHistory;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Knowz.SelfHosted.Infrastructure.Services;
using LibGit2Sharp;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Application.Services;

public class GitSyncService : IGitSyncService
{
    private readonly SelfHostedDbContext _db;
    private readonly ITenantProvider _tenantProvider;
    private readonly IDataProtector _dataProtector;
    private readonly Channel<GitSyncWorkItem> _gitSyncChannel;
    private readonly IEnrichmentOutboxWriter _enrichmentWriter;
    private readonly IGitCommitHistoryService _commitHistoryService;
    private readonly ILogger<GitSyncService> _logger;

    private static readonly string[] DefaultFilePatterns = new[]
    {
        "**/*.md", "**/*.txt", "**/*.json", "**/*.rst", "**/*.yaml", "**/*.yml"
    };

    public GitSyncService(
        SelfHostedDbContext db,
        ITenantProvider tenantProvider,
        IDataProtectionProvider dataProtectionProvider,
        Channel<GitSyncWorkItem> gitSyncChannel,
        IEnrichmentOutboxWriter enrichmentWriter,
        IGitCommitHistoryService commitHistoryService,
        ILogger<GitSyncService> logger)
    {
        _db = db;
        _tenantProvider = tenantProvider;
        _dataProtector = dataProtectionProvider.CreateProtector("Knowz.SelfHosted.GitSync");
        _gitSyncChannel = gitSyncChannel;
        _enrichmentWriter = enrichmentWriter;
        _commitHistoryService = commitHistoryService;
        _logger = logger;
    }

    /// <summary>
    /// Configure a git repository for a vault. Creates or updates the configuration.
    /// </summary>
    public async Task<GitRepository> ConfigureAsync(
        Guid vaultId, string repoUrl, string branch, string? pat, string? filePatterns,
        bool? trackCommitHistory = null, int? commitHistoryDepth = null,
        CancellationToken ct = default)
    {
        var tenantId = _tenantProvider.TenantId;

        // Defense-in-depth: enforce the platform ceiling at the service boundary as well.
        if (commitHistoryDepth.HasValue && (commitHistoryDepth.Value < 1 || commitHistoryDepth.Value > 2000))
        {
            throw new InvalidOperationException(
                $"CommitHistoryDepth must be between 1 and 2000 (got {commitHistoryDepth.Value}).");
        }

        // Verify vault exists
        var vault = await _db.Vaults.FirstOrDefaultAsync(v => v.Id == vaultId, ct);
        if (vault == null)
            throw new InvalidOperationException($"Vault {vaultId} not found.");

        var existing = await _db.GitRepositories
            .FirstOrDefaultAsync(g => g.VaultId == vaultId && !g.IsDeleted, ct);

        if (existing != null)
        {
            // Update existing config
            existing.RepositoryUrl = repoUrl;
            existing.Branch = branch;
            existing.FilePatterns = filePatterns;
            existing.UpdatedAt = DateTime.UtcNow;

            if (trackCommitHistory.HasValue)
            {
                existing.TrackCommitHistory = trackCommitHistory.Value;
            }
            if (commitHistoryDepth.HasValue)
            {
                existing.CommitHistoryDepth = commitHistoryDepth.Value;
            }

            if (!string.IsNullOrWhiteSpace(pat))
            {
                existing.PlatformData = _dataProtector.Protect(pat);
            }

            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Updated git sync config for VaultId {VaultId}", vaultId);
            return existing;
        }

        var gitRepo = new GitRepository
        {
            TenantId = tenantId,
            VaultId = vaultId,
            RepositoryUrl = repoUrl,
            Branch = branch,
            FilePatterns = filePatterns,
            Status = "NotSynced",
            PlatformData = !string.IsNullOrWhiteSpace(pat) ? _dataProtector.Protect(pat) : null,
            TrackCommitHistory = trackCommitHistory ?? false,
            CommitHistoryDepth = commitHistoryDepth
        };

        _db.GitRepositories.Add(gitRepo);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Created git sync config for VaultId {VaultId}", vaultId);
        return gitRepo;
    }

    /// <summary>
    /// Queue a sync request for background processing.
    /// </summary>
    public async Task TriggerSyncAsync(Guid vaultId, CancellationToken ct = default)
    {
        var tenantId = _tenantProvider.TenantId;

        var gitRepo = await _db.GitRepositories
            .FirstOrDefaultAsync(g => g.VaultId == vaultId && !g.IsDeleted, ct);

        if (gitRepo == null)
            throw new InvalidOperationException($"No git sync configuration found for vault {vaultId}.");

        if (gitRepo.Status == "Syncing")
            throw new InvalidOperationException("A sync is already in progress for this vault.");

        await _gitSyncChannel.Writer.WriteAsync(new GitSyncWorkItem(vaultId, tenantId), ct);
        _logger.LogInformation("Queued git sync for VaultId {VaultId}", vaultId);
    }

    /// <summary>
    /// Get the current git sync configuration and status for a vault.
    /// </summary>
    public async Task<GitSyncStatusDto?> GetStatusAsync(Guid vaultId, CancellationToken ct = default)
    {
        var gitRepo = await _db.GitRepositories
            .FirstOrDefaultAsync(g => g.VaultId == vaultId && !g.IsDeleted, ct);

        if (gitRepo == null)
            return null;

        return new GitSyncStatusDto
        {
            Id = gitRepo.Id,
            VaultId = gitRepo.VaultId,
            RepositoryUrl = gitRepo.RepositoryUrl,
            Branch = gitRepo.Branch,
            LastSyncCommitSha = gitRepo.LastSyncCommitSha,
            LastSyncAt = gitRepo.LastSyncAt,
            Status = gitRepo.Status,
            FilePatterns = gitRepo.FilePatterns,
            ErrorMessage = gitRepo.ErrorMessage,
            CreatedAt = gitRepo.CreatedAt,
            TrackCommitHistory = gitRepo.TrackCommitHistory,
            CommitHistoryDepth = gitRepo.CommitHistoryDepth
        };
    }

    /// <summary>
    /// Get sync history from audit logs.
    /// </summary>
    public async Task<List<GitSyncHistoryDto>> GetHistoryAsync(Guid vaultId, CancellationToken ct = default)
    {
        var gitRepo = await _db.GitRepositories
            .FirstOrDefaultAsync(g => g.VaultId == vaultId && !g.IsDeleted, ct);

        if (gitRepo == null)
            return new List<GitSyncHistoryDto>();

        var auditLogs = await _db.AuditLogs
            .Where(a => a.EntityType == "GitRepository" && a.EntityId == gitRepo.Id)
            .OrderByDescending(a => a.Timestamp)
            .Take(50)
            .ToListAsync(ct);

        return auditLogs.Select(a => new GitSyncHistoryDto
        {
            Timestamp = a.Timestamp,
            Action = a.Action,
            Details = a.Details
        }).ToList();
    }

    /// <summary>
    /// Soft-delete the git sync configuration for a vault.
    /// </summary>
    public async Task<bool> RemoveAsync(Guid vaultId, CancellationToken ct = default)
    {
        var gitRepo = await _db.GitRepositories
            .FirstOrDefaultAsync(g => g.VaultId == vaultId && !g.IsDeleted, ct);

        if (gitRepo == null)
            return false;

        gitRepo.IsDeleted = true;
        gitRepo.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Removed git sync config for VaultId {VaultId}", vaultId);
        return true;
    }

    /// <summary>
    /// Execute the actual sync operation. Called by GitSyncBackgroundService.
    /// </summary>
    public async Task ExecuteSyncAsync(Guid vaultId, CancellationToken ct)
    {
        var gitRepo = await _db.GitRepositories
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(g => g.VaultId == vaultId && !g.IsDeleted, ct);

        if (gitRepo == null)
        {
            _logger.LogWarning("Git sync config not found for VaultId {VaultId}", vaultId);
            return;
        }

        var tenantId = gitRepo.TenantId;

        try
        {
            // Set status to Syncing
            gitRepo.Status = "Syncing";
            gitRepo.ErrorMessage = null;
            gitRepo.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            // Decrypt PAT if available
            string? pat = null;
            if (!string.IsNullOrWhiteSpace(gitRepo.PlatformData))
            {
                try
                {
                    pat = _dataProtector.Unprotect(gitRepo.PlatformData);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to decrypt PAT for VaultId {VaultId}", vaultId);
                }
            }

            // Determine clone path
            var repoDir = Path.Combine(Path.GetTempPath(), "knowz-git-sync", gitRepo.Id.ToString());
            Directory.CreateDirectory(Path.GetDirectoryName(repoDir)!);

            // Clone or pull
            string commitSha;
            if (Directory.Exists(Path.Combine(repoDir, ".git")))
            {
                commitSha = PullRepository(repoDir, pat, gitRepo.Branch);
            }
            else
            {
                commitSha = CloneRepository(gitRepo.RepositoryUrl, repoDir, pat, gitRepo.Branch);
            }

            // Parse file patterns
            var patterns = ParseFilePatterns(gitRepo.FilePatterns);

            // Walk files matching patterns
            var matchedFiles = WalkFiles(repoDir, patterns);
            _logger.LogInformation("Git sync for VaultId {VaultId}: found {Count} matching files", vaultId, matchedFiles.Count);

            // Get existing knowledge items synced from this repo
            var existingItems = await _db.KnowledgeItems
                .IgnoreQueryFilters()
                .Where(k => k.TenantId == tenantId && k.Source == "git-sync" && !k.IsDeleted)
                .Join(
                    _db.KnowledgeVaults.IgnoreQueryFilters().Where(kv => kv.VaultId == vaultId),
                    k => k.Id,
                    kv => kv.KnowledgeId,
                    (k, kv) => k)
                .ToListAsync(ct);

            var existingByPath = existingItems.ToDictionary(k => k.FilePath ?? string.Empty, k => k);
            var processedPaths = new HashSet<string>();
            var changedIds = new List<Guid>();

            foreach (var (relativePath, fullPath) in matchedFiles)
            {
                ct.ThrowIfCancellationRequested();
                processedPaths.Add(relativePath);

                var fileContent = await File.ReadAllTextAsync(fullPath, ct);
                var contentHash = ComputeSha256(fileContent);

                if (existingByPath.TryGetValue(relativePath, out var existing))
                {
                    // Check if content changed by comparing hash
                    var existingHash = ComputeSha256(existing.Content);
                    if (existingHash != contentHash)
                    {
                        existing.Content = fileContent;
                        existing.Title = DeriveTitleFromPath(relativePath);
                        existing.UpdatedAt = DateTime.UtcNow;
                        changedIds.Add(existing.Id);
                        _logger.LogDebug("Updated git-synced item: {Path}", relativePath);
                    }
                }
                else
                {
                    // Create new knowledge item
                    var newItem = new Knowledge
                    {
                        TenantId = tenantId,
                        Title = DeriveTitleFromPath(relativePath),
                        Content = fileContent,
                        Type = KnowledgeType.Note,
                        Source = "git-sync",
                        FilePath = relativePath,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };

                    _db.KnowledgeItems.Add(newItem);

                    // Link to vault
                    _db.KnowledgeVaults.Add(new KnowledgeVault
                    {
                        TenantId = tenantId,
                        KnowledgeId = newItem.Id,
                        VaultId = vaultId,
                        IsPrimary = true
                    });

                    changedIds.Add(newItem.Id);
                    _logger.LogDebug("Created git-synced item: {Path}", relativePath);
                }
            }

            // Soft-delete items whose files were removed from the repo
            foreach (var kvp in existingByPath)
            {
                if (!processedPaths.Contains(kvp.Key))
                {
                    kvp.Value.IsDeleted = true;
                    kvp.Value.UpdatedAt = DateTime.UtcNow;
                    _logger.LogDebug("Soft-deleted git-synced item (file removed): {Path}", kvp.Key);
                }
            }

            await _db.SaveChangesAsync(ct);

            // ─── NODE-4: Commit-history ingestion ────────────────────────────
            // Gated on TrackCommitHistory flag (default OFF). Walks commits via LibGit2Sharp,
            // hands them to IGitCommitHistoryService which creates parent + per-commit
            // children, elaborates them in-process, and writes KnowledgeRelationship edges.
            // Errors are logged but do not fail the file-sync checkpoint — commit-history
            // is strictly additive.
            if (gitRepo.TrackCommitHistory)
            {
                try
                {
                    var descriptors = EnumerateCommitDescriptors(
                        repoDir,
                        sinceSha: gitRepo.LastCommitHistorySyncSha,
                        maxCommits: Math.Min(
                            gitRepo.CommitHistoryDepth ?? GitCommitHistoryService.DefaultCommitHistoryDepth,
                            GitCommitHistoryService.MaxCommitHistoryDepth));

                    var newestSha = await _commitHistoryService.ProcessCommitsAsync(
                        gitRepo.Id, descriptors, vaultId, ct);

                    if (!string.IsNullOrEmpty(newestSha))
                    {
                        gitRepo.LastCommitHistorySyncSha = newestSha;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Commit-history ingestion failed for VaultId {VaultId} — file sync continues",
                        vaultId);
                }
            }

            // Update sync status
            gitRepo.LastSyncCommitSha = commitSha;
            gitRepo.LastSyncAt = DateTime.UtcNow;
            gitRepo.Status = "Synced";
            gitRepo.ErrorMessage = null;
            gitRepo.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            // Queue changed items for enrichment
            foreach (var knowledgeId in changedIds)
            {
                try
                {
                    await _enrichmentWriter.EnqueueAsync(knowledgeId, tenantId, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to enqueue enrichment for knowledge {Id}", knowledgeId);
                }
            }

            // Write audit log entry
            _db.AuditLogs.Add(new AuditLog
            {
                TenantId = tenantId,
                EntityType = "GitRepository",
                EntityId = gitRepo.Id,
                Action = "Sync",
                Timestamp = DateTime.UtcNow,
                Details = JsonSerializer.Serialize(new
                {
                    CommitSha = commitSha,
                    FilesProcessed = matchedFiles.Count,
                    FilesChanged = changedIds.Count,
                    FilesRemoved = existingByPath.Count(kvp => !processedPaths.Contains(kvp.Key))
                })
            });
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Git sync completed for VaultId {VaultId}: {Changed} changed, {Total} total files",
                vaultId, changedIds.Count, matchedFiles.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Git sync failed for VaultId {VaultId}", vaultId);
            gitRepo.Status = "Failed";
            gitRepo.ErrorMessage = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
            gitRepo.UpdatedAt = DateTime.UtcNow;

            // Write failure audit log
            _db.AuditLogs.Add(new AuditLog
            {
                TenantId = tenantId,
                EntityType = "GitRepository",
                EntityId = gitRepo.Id,
                Action = "SyncFailed",
                Timestamp = DateTime.UtcNow,
                Details = ex.Message.Length > 4000 ? ex.Message[..4000] : ex.Message
            });

            await _db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Walk commits from HEAD backwards (newest-first), stopping at <paramref name="sinceSha"/>
    /// (exclusive) or after <paramref name="maxCommits"/> commits. Produces
    /// <see cref="CommitDescriptor"/> records with path + line-count stats only — NO diff body
    /// (CRIT-2 enforcement at the producer layer).
    ///
    /// NODE-4: selfhosted commit walker. Mirrors platform's
    /// <c>LibGit2SharpWrapper.EnumerateCommitsAsync</c> shape, inlined here because the
    /// selfhosted edition does not have a parallel IGitOperations abstraction.
    /// </summary>
    internal static List<CommitDescriptor> EnumerateCommitDescriptors(
        string repoDir,
        string? sinceSha,
        int maxCommits)
    {
        var result = new List<CommitDescriptor>();
        using var repo = new Repository(repoDir);

        var filter = new CommitFilter
        {
            SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time,
            IncludeReachableFrom = repo.Head.Tip
        };

        foreach (var commit in repo.Commits.QueryBy(filter))
        {
            if (!string.IsNullOrEmpty(sinceSha) &&
                string.Equals(commit.Sha, sinceSha, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (result.Count >= maxCommits)
            {
                break;
            }

            var changedFiles = new List<CommitChangedFile>();
            if (commit.Parents.Any())
            {
                var parentTree = commit.Parents.First().Tree;
                var patch = repo.Diff.Compare<Patch>(parentTree, commit.Tree);
                foreach (var fileChange in patch)
                {
                    var type = fileChange.Status switch
                    {
                        ChangeKind.Added => GitCommitChangeType.Added,
                        ChangeKind.Deleted => GitCommitChangeType.Deleted,
                        ChangeKind.Renamed => GitCommitChangeType.Renamed,
                        _ => GitCommitChangeType.Modified
                    };
                    changedFiles.Add(new CommitChangedFile(
                        fileChange.Path,
                        fileChange.LinesAdded,
                        fileChange.LinesDeleted,
                        type));
                }
            }

            result.Add(new CommitDescriptor(
                Sha: commit.Sha,
                ParentShas: commit.Parents.Select(p => p.Sha).ToList(),
                AuthorName: commit.Author?.Name ?? string.Empty,
                AuthorEmail: commit.Author?.Email ?? string.Empty,
                AuthoredAt: commit.Author?.When ?? DateTimeOffset.MinValue,
                CommittedAt: commit.Committer?.When ?? DateTimeOffset.MinValue,
                Message: commit.Message ?? string.Empty,
                ChangedFiles: changedFiles));
        }

        return result;
    }

    private static string CloneRepository(string url, string path, string? pat, string branch)
    {
        var cloneOptions = new CloneOptions
        {
            BranchName = branch,
            RecurseSubmodules = false
        };

        if (!string.IsNullOrWhiteSpace(pat))
        {
            cloneOptions.FetchOptions.CredentialsProvider = (_, _, _) =>
                new UsernamePasswordCredentials
                {
                    Username = pat,
                    Password = string.Empty
                };
        }

        var repoPath = Repository.Clone(url, path, cloneOptions);

        using var repo = new Repository(repoPath);
        return repo.Head.Tip.Sha;
    }

    private static string PullRepository(string path, string? pat, string branch)
    {
        using var repo = new Repository(path);

        var fetchOptions = new FetchOptions();
        if (!string.IsNullOrWhiteSpace(pat))
        {
            fetchOptions.CredentialsProvider = (_, _, _) =>
                new UsernamePasswordCredentials
                {
                    Username = pat,
                    Password = string.Empty
                };
        }

        // Fetch from origin
        var remote = repo.Network.Remotes["origin"];
        var refSpecs = remote.FetchRefSpecs.Select(rs => rs.Specification);
        Commands.Fetch(repo, remote.Name, refSpecs, fetchOptions, "git-sync fetch");

        // Checkout and reset to the remote branch tip
        var remoteBranch = repo.Branches[$"origin/{branch}"];
        if (remoteBranch != null)
        {
            repo.Reset(ResetMode.Hard, remoteBranch.Tip);
        }

        return repo.Head.Tip.Sha;
    }

    private static string[] ParseFilePatterns(string? filePatterns)
    {
        if (string.IsNullOrWhiteSpace(filePatterns))
            return DefaultFilePatterns;

        try
        {
            var parsed = JsonSerializer.Deserialize<string[]>(filePatterns);
            return parsed is { Length: > 0 } ? parsed : DefaultFilePatterns;
        }
        catch (JsonException)
        {
            // Try comma-separated fallback
            return filePatterns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }

    private static List<(string RelativePath, string FullPath)> WalkFiles(string repoDir, string[] patterns)
    {
        var results = new List<(string, string)>();
        var allFiles = Directory.GetFiles(repoDir, "*", SearchOption.AllDirectories);

        foreach (var fullPath in allFiles)
        {
            var relativePath = Path.GetRelativePath(repoDir, fullPath).Replace('\\', '/');

            // Skip .git directory
            if (relativePath.StartsWith(".git/") || relativePath == ".git")
                continue;

            // Match against patterns
            if (MatchesAnyPattern(relativePath, patterns))
            {
                results.Add((relativePath, fullPath));
            }
        }

        return results;
    }

    internal static bool MatchesAnyPattern(string relativePath, string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            if (MatchGlob(relativePath, pattern))
                return true;
        }
        return false;
    }

    internal static bool MatchGlob(string path, string pattern)
    {
        // Handle ** prefix (match any directory depth)
        if (pattern.StartsWith("**/"))
        {
            var suffix = pattern[3..]; // e.g. "*.md"
            var fileName = Path.GetFileName(path);
            return MatchSimpleGlob(fileName, suffix) || MatchSimpleGlob(path, pattern);
        }

        return MatchSimpleGlob(path, pattern);
    }

    private static bool MatchSimpleGlob(string input, string pattern)
    {
        // Simple glob: only supports * and **/ prefix
        // For **/*.ext patterns, match against filename
        if (pattern.StartsWith("**/"))
        {
            var ext = pattern[3..]; // e.g. "*.md"
            return MatchSimpleGlob(input, ext);
        }

        if (pattern.StartsWith("*."))
        {
            var extension = pattern[1..]; // e.g. ".md"
            return input.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(input, pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeSha256(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string DeriveTitleFromPath(string relativePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(relativePath);
        // Convert kebab-case/snake_case to readable title
        return fileName
            .Replace('-', ' ')
            .Replace('_', ' ')
            .Trim();
    }
}

// DTOs for git sync endpoints

public class GitSyncStatusDto
{
    public Guid Id { get; set; }
    public Guid VaultId { get; set; }
    public string RepositoryUrl { get; set; } = string.Empty;
    public string Branch { get; set; } = "main";
    public string? LastSyncCommitSha { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public string Status { get; set; } = "NotSynced";
    public string? FilePatterns { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool TrackCommitHistory { get; set; }
    public int? CommitHistoryDepth { get; set; }
}

public class GitSyncHistoryDto
{
    public DateTime Timestamp { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Details { get; set; }
}

public class ConfigureGitSyncRequest
{
    public string Url { get; set; } = string.Empty;
    public string Branch { get; set; } = "main";
    public string? Pat { get; set; }
    public string? FilePatterns { get; set; }

    /// <summary>
    /// When true, initial sync backfills commit history into AI-elaborated knowledge items.
    /// Nullable on the wire for backwards compatibility.
    /// </summary>
    public bool? TrackCommitHistory { get; set; }

    /// <summary>
    /// Number of historical commits to backfill on first enable. Range [1, 2000].
    /// Nullable: when null, the service uses its own default.
    /// </summary>
    public int? CommitHistoryDepth { get; set; }
}
