using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.Services.Shared;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Application.Services.GitCommitHistory;

/// <summary>
/// Selfhosted commit-history ingestion orchestrator. Mirror of platform's
/// <c>Knowz.Application.Services.GitCommitHistoryService</c>, adapted for the in-process
/// (no Service Bus / no Azure Functions) execution model.
///
/// Responsibilities:
///   • Load or create the parent <c>KnowledgeType.CommitHistory</c> row.
///   • Create a per-commit <c>KnowledgeType.Commit</c> child for each new commit (dedup by
///     <c>Knowledge.Source = "{repoUrl}:{branch}:commit:{sha}"</c>).
///   • Enforce the sensitive-file deny list at ingestion time (CRIT-2 layer 1).
///   • Invoke the in-process LLM elaborator bounded by <see cref="SemaphoreSlim"/>
///     (CRIT-4 concurrency cap in lieu of platform quota service).
///   • Write <c>KnowledgeRelationship.PartOf</c> edges from child → parent (NODE-3 parity).
///   • Update the parent rolling-window <c>Content</c> with delimited markers.
///   • Advance / return a checkpoint SHA to the caller.
///
/// CRITICAL security mitigations (non-negotiable parity with Group A):
///   • CRIT-1: prompt injection defense via <see cref="CommitElaborationPromptBuilder"/>.
///   • CRIT-2: sensitive-file deny list (layer 1) + pre-LLM secret scan (layer 2).
///   • CRIT-3: tenant-scoped writes (<see cref="ITenantProvider"/>, no IgnoreQueryFilters on
///             the elaboration write path — we only use it for lookups).
///   • CRIT-4: <see cref="SemaphoreSlim"/>-bounded concurrency (default 2) + depth cap + NoOp
///             fallback when <see cref="ICommitElaborationLlmClient.IsAvailable"/> is false.
///   • CRIT-5: idempotency via <c>Knowledge.Source</c> uniqueness check.
///
/// Selfhosted deviation from platform: selfhosted has no <c>AiServiceQuotaService</c>.
/// The fallback is the concurrency semaphore + depth cap + NoOp short-circuit.
///
/// WorkGroupID: kc-feat-git-commit-knowledge-sync-20260410
/// NodeID: SelfHostedCommitHistoryParity (NODE-4)
/// </summary>
public sealed class GitCommitHistoryService : IGitCommitHistoryService
{
    /// <summary>Rolling-window size in the parent CommitHistory.Content.</summary>
    public const int RollingWindowSize = 50;

    /// <summary>Default commit-history depth when the repository has no explicit value set.</summary>
    public const int DefaultCommitHistoryDepth = 500;

    /// <summary>Hard ceiling on commit-history depth (enforced here even if stored value is larger).</summary>
    public const int MaxCommitHistoryDepth = 2000;

    /// <summary>
    /// Max concurrent in-process LLM elaborations per <see cref="ProcessCommitsAsync"/> invocation.
    /// Kept low for selfhosted since there is no external quota service.
    /// </summary>
    public const int DefaultElaborationConcurrency = 2;

    /// <summary>Sensitive-file deny list (CRIT-2 layer 1).</summary>
    private static readonly string[] SensitiveFileNames =
    {
        ".env", "secrets.yaml", "secrets.yml", "credentials.json"
    };

    private static readonly string[] SensitiveFileExtensions =
    {
        ".pem", ".key", ".pfx", ".p12"
    };

    private static readonly string[] SensitiveFileNamePrefixes =
    {
        "id_rsa"
    };

    private readonly SelfHostedDbContext _db;
    private readonly ITenantProvider _tenantProvider;
    private readonly ICommitSecretScanner _secretScanner;
    private readonly ICommitElaborationPromptBuilder _promptBuilder;
    private readonly ICommitElaborationLlmClient _llm;
    private readonly ILogger<GitCommitHistoryService> _logger;

    public GitCommitHistoryService(
        SelfHostedDbContext db,
        ITenantProvider tenantProvider,
        ICommitSecretScanner secretScanner,
        ICommitElaborationPromptBuilder promptBuilder,
        ICommitElaborationLlmClient llm,
        ILogger<GitCommitHistoryService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _tenantProvider = tenantProvider ?? throw new ArgumentNullException(nameof(tenantProvider));
        _secretScanner = secretScanner ?? throw new ArgumentNullException(nameof(secretScanner));
        _promptBuilder = promptBuilder ?? throw new ArgumentNullException(nameof(promptBuilder));
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string?> ProcessCommitsAsync(
        Guid repositoryId,
        IEnumerable<CommitDescriptor> commits,
        Guid vaultId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(commits);

        var tenantId = _tenantProvider.TenantId;

        // IgnoreQueryFilters is only safe here because we explicitly match TenantId AND
        // only for LOOKUPS, not writes — the write path flows through _db without filter bypass.
        var repo = await _db.GitRepositories
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(g => g.Id == repositoryId && g.TenantId == tenantId, ct);

        if (repo == null)
        {
            throw new InvalidOperationException($"GitRepository {repositoryId} not found");
        }

        if (!repo.TrackCommitHistory)
        {
            _logger.LogDebug(
                "TrackCommitHistory disabled for repository {RepositoryId} — skipping",
                repositoryId);
            return null;
        }

        var depthCap = Math.Min(
            repo.CommitHistoryDepth ?? DefaultCommitHistoryDepth,
            MaxCommitHistoryDepth);

        // Commits from the walker are newest-first. Clamp to depth cap then process
        // oldest-first so the parent rolling-window markers read in chronological order.
        var descriptors = commits.Take(depthCap).ToList();
        if (descriptors.Count == 0)
        {
            _logger.LogInformation(
                "No commits to process for repository {RepositoryId}",
                repositoryId);
            return null;
        }

        var newestShaInBatch = descriptors[0].Sha;
        var parent = await LoadOrCreateParentAsync(repo, vaultId, tenantId, ct);

        int createdCount = 0;
        int skippedCount = 0;
        var createdChildren = new List<(Knowledge Stub, CommitDescriptor Desc, bool HasSensitive)>();

        foreach (var desc in descriptors.AsEnumerable().Reverse())
        {
            ct.ThrowIfCancellationRequested();

            var childSource = BuildChildSource(repo, desc.Sha);
            var existing = await _db.KnowledgeItems
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    k => k.Source == childSource && k.TenantId == tenantId && !k.IsDeleted, ct);

            if (existing != null)
            {
                skippedCount++;
                continue;
            }

            var sensitivePaths = desc.ChangedFiles
                .Where(f => IsSensitiveFile(f.Path))
                .Select(f => f.Path)
                .ToList();
            var hasSensitive = sensitivePaths.Count > 0;

            var stub = new Knowledge
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Title = BuildChildTitle(desc),
                Content = hasSensitive
                    ? BuildSensitiveFileStubContent(sensitivePaths)
                    : BuildInitialStubContent(desc),
                Source = childSource,
                Type = KnowledgeType.Commit,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                CommittedAt = desc.CommittedAt.UtcDateTime,
                PlatformData = BuildInitialChildMetadataJson(desc, hasSensitive, sensitivePaths)
            };

            _db.KnowledgeItems.Add(stub);

            _db.KnowledgeVaults.Add(new KnowledgeVault
            {
                TenantId = tenantId,
                KnowledgeId = stub.Id,
                VaultId = vaultId,
                IsPrimary = true
            });

            _db.KnowledgeRelationships.Add(new KnowledgeRelationship
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                SourceKnowledgeId = stub.Id,
                TargetKnowledgeId = parent.Id,
                RelationshipType = KnowledgeRelationshipType.PartOf,
                Confidence = 1.0,
                Weight = 1.0,
                IsAutoDetected = true,
                IsBidirectional = false
            });

            // ─── NODE-3 parity: file-resolution pass (SelfHostedCommitKnowledgeLinkage) ──
            // For each touched file, attempt to resolve it to a file Knowledge row
            // in the repo's vault. Sensitive paths are silently skipped inside the
            // shared helper — CRIT-2 gates LLM elaboration AND graph linkage for
            // sensitive paths, while non-sensitive siblings still get their edges.
            //
            // NODE-3 (kc-feat-commit-history-polish): extracted into
            // ResolveAndLinkChangedFilesAsync so CommitRelinkService can reuse it
            // verbatim against stored paths. Single source of truth — zero drift
            // between ingestion-time and backfill-time linkage semantics.
            var (_, orphanPaths) = await ResolveAndLinkChangedFilesAsync(
                stub.Id,
                desc.ChangedFiles.Select(f => f.Path).ToList(),
                vaultId,
                tenantId,
                ct);

            if (orphanPaths.Count > 0)
            {
                stub.PlatformData = KnowledgeRelationshipHelpers.MergeUnlinkedFiles(
                    stub.PlatformData, orphanPaths);
            }

            createdCount++;
            createdChildren.Add((stub, desc, hasSensitive));
        }

        // Persist stubs + relationships before we elaborate. This gives us idempotency-on-crash:
        // a second call will see existing children and short-circuit.
        parent.Content = BuildParentRollingWindow(
            parent.Content,
            descriptors.AsEnumerable().Reverse().ToList());
        parent.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        // ─── In-process elaboration (CRIT-4: SemaphoreSlim-bounded, NoOp fallback) ──
        await ElaborateChildrenAsync(createdChildren, repo, vaultId, tenantId, ct);

        _logger.LogInformation(
            "CommitHistory ingested for repository {RepositoryId}: {Created} created, {Skipped} skipped (dedup)",
            repositoryId, createdCount, skippedCount);

        return createdCount > 0 ? newestShaInBatch : null;
    }

    // ─── Elaboration loop ────────────────────────────────────────────────────

    private async Task ElaborateChildrenAsync(
        List<(Knowledge Stub, CommitDescriptor Desc, bool HasSensitive)> toElaborate,
        GitRepository repo,
        Guid vaultId,
        Guid tenantId,
        CancellationToken ct)
    {
        if (toElaborate.Count == 0)
        {
            return;
        }

        if (!_llm.IsAvailable)
        {
            _logger.LogInformation(
                "Commit elaboration LLM unavailable (selfhosted platform AI not configured). " +
                "Marking {Count} commit children as elaborationSkipped=platform-ai-unavailable.",
                toElaborate.Count);

            foreach (var (stub, desc, hasSensitive) in toElaborate)
            {
                if (hasSensitive)
                {
                    continue; // already has sensitive-file skip marker
                }

                stub.PlatformData = MergeMetadata(stub.PlatformData, "elaborationSkipped", "platform-ai-unavailable");
                stub.UpdatedAt = DateTime.UtcNow;
            }
            await _db.SaveChangesAsync(ct);
            return;
        }

        var semaphore = new SemaphoreSlim(DefaultElaborationConcurrency);
        var tasks = new List<Task>();

        foreach (var (stub, desc, hasSensitive) in toElaborate)
        {
            if (hasSensitive)
            {
                continue; // CRIT-2: never elaborate commits touching sensitive files
            }

            await semaphore.WaitAsync(ct);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await ElaborateOneAsync(stub, desc, repo, vaultId, tenantId, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "In-process elaboration failed for commit {Sha} — leaving as stub",
                        desc.Sha);
                }
                finally
                {
                    semaphore.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks);

        // Persist elaboration results — saved on the OUTER db context. Child tasks mutate
        // the tracked entities directly (safe because SemaphoreSlim + awaited SaveChangesAsync).
        await _db.SaveChangesAsync(ct);
    }

    private async Task ElaborateOneAsync(
        Knowledge stub,
        CommitDescriptor desc,
        GitRepository repo,
        Guid vaultId,
        Guid tenantId,
        CancellationToken ct)
    {
        var request = new CommitElaborationRequest(
            TenantId: tenantId,
            KnowledgeId: stub.Id,
            ParentKnowledgeId: Guid.Empty, // not needed for prompt build
            RepositoryId: repo.Id,
            VaultId: vaultId,
            CommitSha: desc.Sha,
            CommitMessage: desc.Message ?? string.Empty,
            AuthorName: desc.AuthorName ?? string.Empty,
            AuthorEmail: desc.AuthorEmail ?? string.Empty,
            AuthoredAt: desc.AuthoredAt,
            ChangedFiles: desc.ChangedFiles);

        var prompt = _promptBuilder.Build(request);

        if (prompt.InjectionFlaggedFields.Count > 0)
        {
            _logger.LogWarning(
                "[SECURITY] GitCommitInjectionAttempt fields=[{Fields}] commit={Sha} repo={RepoId}",
                string.Join(",", prompt.InjectionFlaggedFields), desc.Sha, repo.Id);
        }

        if (prompt.SecretPatternIdsRedacted.Count > 0)
        {
            _logger.LogWarning(
                "[SECURITY] SecretScanHit patterns=[{Patterns}] commit={Sha} repo={RepoId}",
                string.Join(",", prompt.SecretPatternIdsRedacted), desc.Sha, repo.Id);
        }

        var response = await _llm.ElaborateAsync(prompt.SystemPrompt, prompt.UserPrompt, ct);

        if (string.IsNullOrWhiteSpace(response))
        {
            _logger.LogWarning(
                "LLM returned empty response for commit {Sha} — leaving as stub",
                desc.Sha);
            return;
        }

        // CRIT-2 Stage B: sweep LLM output for secrets before persist.
        var outputScan = _secretScanner.Scan(response);
        var finalContent = outputScan.HasMatches ? outputScan.RedactedText : response;

        if (outputScan.HasMatches)
        {
            _logger.LogWarning(
                "[SECURITY] GitCommitSecretInOutput patterns=[{Patterns}] commit={Sha} repo={RepoId}",
                string.Join(",", outputScan.Matches.Select(m => m.PatternId).Distinct()),
                desc.Sha, repo.Id);
        }

        stub.Content = finalContent;
        stub.UpdatedAt = DateTime.UtcNow;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Single source of truth for commit → file linkage. Resolves each changed-file path
    /// to a <see cref="Knowledge"/> row with matching <see cref="Knowledge.FilePath"/> in
    /// the target vault and writes a <see cref="KnowledgeRelationshipType.References"/>
    /// edge via <see cref="KnowledgeRelationshipHelpers.UpsertRelationshipAsync"/>
    /// (idempotent). Unresolvable paths are returned as orphans so the caller can merge
    /// them into the commit's <c>PlatformData.unlinkedFiles</c> array.
    ///
    /// <para>
    /// Both call sites — <see cref="ProcessCommitsAsync"/> during ingestion and
    /// <c>CommitRelinkService.RelinkRepositoryAsync</c> during backfill — route through
    /// this method, so the two paths produce byte-identical edges from the same inputs.
    /// </para>
    ///
    /// <para>
    /// CRIT-2 (read-time): the helper applies <see cref="IsSensitiveFile"/> to every
    /// input path BEFORE attempting the join, so sensitive paths never reach the
    /// relationship table — even if an older <c>PlatformData</c> blob contains a
    /// path that has since been added to the deny-list. This is the second gate on top
    /// of the write-time filter in <see cref="BuildInitialChildMetadataJson"/>.
    /// </para>
    ///
    /// WorkGroupID: kc-feat-commit-history-polish-20260411-051000
    /// NodeID: NODE-3 CommitBackfillEndpoint
    /// </summary>
    internal async Task<(int linked, List<string> orphans)> ResolveAndLinkChangedFilesAsync(
        Guid commitKnowledgeId,
        IReadOnlyList<string> changedFilePaths,
        Guid vaultId,
        Guid tenantId,
        CancellationToken ct)
    {
        var linked = 0;
        var orphans = new List<string>();

        foreach (var path in changedFilePaths)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            if (IsSensitiveFile(path))
            {
                // CRIT-2 layer 2: silently drop sensitive paths at read time. Not added
                // to orphans — sensitive paths are intentionally invisible to the graph.
                continue;
            }

            var targetFile = await (
                from k in _db.KnowledgeItems.IgnoreQueryFilters()
                join kv in _db.KnowledgeVaults.IgnoreQueryFilters()
                    on k.Id equals kv.KnowledgeId
                where k.TenantId == tenantId
                    && !k.IsDeleted
                    && k.FilePath == path
                    && kv.VaultId == vaultId
                select k).FirstOrDefaultAsync(ct);

            if (targetFile == null)
            {
                orphans.Add(path);
                continue;
            }

            // UpsertRelationshipAsync short-circuits on existing rows via AnyAsync,
            // so repeated calls with the same (source, target, type) tuple are no-ops.
            var before = _db.ChangeTracker.Entries<KnowledgeRelationship>().Count();
            await KnowledgeRelationshipHelpers.UpsertRelationshipAsync(
                _db, tenantId, commitKnowledgeId, targetFile.Id,
                KnowledgeRelationshipType.References, ct);
            var after = _db.ChangeTracker.Entries<KnowledgeRelationship>().Count();
            if (after > before)
            {
                linked++;
            }
        }

        return (linked, orphans);
    }

    private async Task<Knowledge> LoadOrCreateParentAsync(
        GitRepository repo,
        Guid vaultId,
        Guid tenantId,
        CancellationToken ct)
    {
        var parentSource = BuildParentSource(repo);
        var existing = await _db.KnowledgeItems
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                k => k.Source == parentSource && k.TenantId == tenantId && !k.IsDeleted, ct);

        if (existing != null)
        {
            return existing;
        }

        var parent = new Knowledge
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Title = $"Commit history: {repo.Branch}",
            Content = string.Empty,
            Source = parentSource,
            Type = KnowledgeType.CommitHistory,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            PlatformData = "{}"
        };

        _db.KnowledgeItems.Add(parent);

        _db.KnowledgeVaults.Add(new KnowledgeVault
        {
            TenantId = tenantId,
            KnowledgeId = parent.Id,
            VaultId = vaultId,
            IsPrimary = true
        });

        await _db.SaveChangesAsync(ct);
        return parent;
    }

    internal static string BuildParentSource(GitRepository repo)
        => $"{repo.RepositoryUrl}:{repo.Branch}:commit-history";

    internal static string BuildChildSource(GitRepository repo, string sha)
        => $"{repo.RepositoryUrl}:{repo.Branch}:commit:{sha}";

    /// <summary>
    /// Layer 1 sensitive-file deny list. Kept in sync with platform
    /// <c>Knowz.Application.Services.GitCommitHistoryService.IsSensitiveFile</c>.
    /// </summary>
    internal static bool IsSensitiveFile(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var fileName = Path.GetFileName(path);
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        foreach (var sensitive in SensitiveFileNames)
        {
            if (string.Equals(fileName, sensitive, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var ext in SensitiveFileExtensions)
        {
            if (fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var prefix in SensitiveFileNamePrefixes)
        {
            if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static string BuildSensitiveFileStubContent(IReadOnlyList<string> sensitivePaths)
    {
        if (sensitivePaths.Count == 1)
        {
            return $"File {sensitivePaths[0]} was touched but contents/stats were not elaborated (sensitive file pattern).";
        }
        return $"Files {string.Join(", ", sensitivePaths)} were touched but contents/stats were not elaborated (sensitive file pattern).";
    }

    internal static string BuildInitialStubContent(CommitDescriptor desc)
    {
        var shortSha = desc.Sha.Length >= 7 ? desc.Sha[..7] : desc.Sha;
        return $"Pending elaboration of commit {shortSha} ({desc.ChangedFiles.Count} files touched).";
    }

    internal static string BuildChildTitle(CommitDescriptor desc)
    {
        var shortSha = desc.Sha.Length >= 7 ? desc.Sha[..7] : desc.Sha;
        var firstLine = (desc.Message ?? string.Empty).Split('\n', 2)[0].Trim();
        if (string.IsNullOrEmpty(firstLine))
        {
            firstLine = "(no message)";
        }
        if (firstLine.Length > 120)
        {
            firstLine = firstLine[..120];
        }
        return $"Commit {shortSha}: {firstLine}";
    }

    internal static string BuildInitialChildMetadataJson(
        CommitDescriptor desc,
        bool sensitiveFilesPresent,
        IReadOnlyList<string> sensitivePaths)
    {
        var obj = new Dictionary<string, object?>
        {
            ["commitSha"] = desc.Sha,
            ["parentShas"] = desc.ParentShas,
            ["authorName"] = desc.AuthorName,
            ["authorEmail"] = desc.AuthorEmail,
            ["authoredAt"] = desc.AuthoredAt,
            ["committedAt"] = desc.CommittedAt,
            ["changedFileCount"] = desc.ChangedFiles.Count,
            ["linesAddedTotal"] = desc.ChangedFiles.Sum(c => c.LinesAdded),
            ["linesDeletedTotal"] = desc.ChangedFiles.Sum(c => c.LinesDeleted),
            ["unlinkedFiles"] = Array.Empty<string>(),
            // NODE-3: persist the non-sensitive changed-file path list so the
            // commit→file relink loop can rebuild edges later (e.g. when file rows
            // are created AFTER the commit, or when the edge was never written).
            // CRIT-2 write-time filter: sensitive paths are filtered here AND again
            // at read time inside ResolveAndLinkChangedFilesAsync — double gate.
            // WorkGroupID: kc-feat-commit-history-polish-20260411-051000
            ["changedFilePaths"] = desc.ChangedFiles
                .Where(f => !IsSensitiveFile(f.Path))
                .Select(f => f.Path)
                .ToArray()
        };

        if (sensitiveFilesPresent)
        {
            obj["elaborationSkipped"] = "sensitive-file";
            obj["sensitivePaths"] = sensitivePaths;
        }

        return JsonSerializer.Serialize(obj);
    }

    internal static string BuildParentRollingWindow(
        string existingContent,
        IReadOnlyList<CommitDescriptor> newCommitsOldestFirst)
    {
        var existingShaSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingBlocks = new List<string>();
        if (!string.IsNullOrEmpty(existingContent))
        {
            var regex = new System.Text.RegularExpressions.Regex(
                @"<!-- commit:(?<sha>[a-f0-9]{7,40}) -->(?<body>[\s\S]*?)<!-- /commit:\k<sha> -->",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (System.Text.RegularExpressions.Match m in regex.Matches(existingContent))
            {
                var sha = m.Groups["sha"].Value;
                if (existingShaSet.Add(sha))
                {
                    existingBlocks.Add(m.Value);
                }
            }
        }

        var newBlocks = new List<string>();
        foreach (var desc in newCommitsOldestFirst)
        {
            if (existingShaSet.Contains(desc.Sha))
            {
                continue;
            }
            var body = $"{BuildChildTitle(desc)}\n" +
                       $"Author: {desc.AuthorName}\n" +
                       $"Date: {desc.AuthoredAt:O}\n" +
                       $"Files: {desc.ChangedFiles.Count}\n";
            newBlocks.Add($"<!-- commit:{desc.Sha} -->\n{body}<!-- /commit:{desc.Sha} -->");
        }

        var combined = existingBlocks.Concat(newBlocks).ToList();
        if (combined.Count > RollingWindowSize)
        {
            combined = combined.Skip(combined.Count - RollingWindowSize).ToList();
        }

        return string.Join("\n\n", combined);
    }

    /// <summary>
    /// Merge a new key → value pair into a PlatformData JSON string. Used by the NoOp
    /// fallback path to tag <c>elaborationSkipped = "platform-ai-unavailable"</c> without
    /// clobbering the earlier metadata written at stub creation time.
    /// </summary>
    internal static string MergeMetadata(string? existing, string key, object value)
    {
        Dictionary<string, object?> dict;
        if (string.IsNullOrEmpty(existing))
        {
            dict = new Dictionary<string, object?>();
        }
        else
        {
            try
            {
                dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(existing)
                       ?? new Dictionary<string, object?>();
            }
            catch (JsonException)
            {
                dict = new Dictionary<string, object?>();
            }
        }

        dict[key] = value;
        return JsonSerializer.Serialize(dict);
    }
}
