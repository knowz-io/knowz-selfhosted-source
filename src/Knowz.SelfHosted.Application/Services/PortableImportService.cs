namespace Knowz.SelfHosted.Application.Services;

using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.Core.Schema;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

public class PortableImportService : IPortableImportService
{
    private readonly SelfHostedDbContext _db;
    private readonly ITenantProvider _tenantProvider;
    private readonly IFileStorageProvider _storageProvider;
    private readonly ILogger<PortableImportService> _logger;

    public PortableImportService(
        SelfHostedDbContext db,
        ITenantProvider tenantProvider,
        IFileStorageProvider storageProvider,
        ILogger<PortableImportService> logger)
    {
        _db = db;
        _tenantProvider = tenantProvider;
        _storageProvider = storageProvider;
        _logger = logger;
    }

    public async Task<ImportValidationResult> ValidateAsync(
        PortableExportPackage package,
        CancellationToken ct = default)
    {
        try { PortableZipReader.ValidatePackage(package); }
        catch (PortableZipSecurityException ex) { return new ImportValidationResult { Errors = new() { ex.Message } }; }
        var result = new ImportValidationResult
        {
            SchemaVersion = package.SchemaVersion,
            SourceEdition = package.SourceEdition,
            SchemaCompatible = CoreSchema.CanRead(package.SchemaVersion)
        };

        if (!result.SchemaCompatible)
        {
            result.SchemaError = $"Schema version {package.SchemaVersion} is not compatible. " +
                                 $"This build supports {CoreSchema.GetCompatibilityInfo()}.";
            result.Errors.Add(result.SchemaError);
            result.IsValid = false;
            return result;
        }

        // Metadata is advisory; previews reflect the data that will actually be imported.
        result.TotalVaults = package.Data.Vaults.Count;
        result.TotalKnowledgeItems = package.Data.KnowledgeItems.Count;
        result.TotalTopics = package.Data.Topics.Count;
        result.TotalTags = package.Data.Tags.Count;
        result.TotalPersons = package.Data.Persons.Count;
        result.TotalLocations = package.Data.Locations.Count;
        result.TotalEvents = package.Data.Events.Count;
        result.TotalInboxItems = package.Data.InboxItems.Count;
        result.TotalComments = package.Data.Comments.Count;
        result.TotalFileRecords = package.Data.FileRecords.Count;
        result.TotalArchiveTypes = package.Data.Archives.Count;

        // Detect conflicts against existing data
        var preserveIds = IsSameEdition(package.SourceEdition);
        int CountNamedConflicts(IEnumerable<(Guid Id, string Name)> imported,
            IEnumerable<(Guid Id, string Name)> existing, string kind)
        {
            var candidates = existing.ToList();
            var ids = candidates.Select(e => e.Id).ToHashSet();
            var names = candidates.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return imported.Count(e => ids.Contains(preserveIds ? e.Id :
                StableImportId(_tenantProvider.TenantId, package, kind, e.Id)) || (!preserveIds && names.Contains(e.Name)));
        }

        if (preserveIds)
        {
            // GUID-based conflict detection for same-edition
            var existingVaultIds = await _db.Vaults.Select(v => v.Id).ToListAsync(ct);
            var existingKnowledgeIds = await _db.KnowledgeItems.Select(k => k.Id).ToListAsync(ct);
            var existingPersonIds = await _db.Persons.Select(p => p.Id).ToListAsync(ct);
            var existingLocationIds = await _db.Locations.Select(l => l.Id).ToListAsync(ct);
            var existingEventIds = await _db.Events.Select(e => e.Id).ToListAsync(ct);

            var packageVaultIds = package.Data.Vaults.Select(v => v.Id).ToHashSet();
            var packageKnowledgeIds = package.Data.KnowledgeItems.Select(k => k.Id).ToHashSet();
            var packagePersonIds = package.Data.Persons.Select(p => p.Id).ToHashSet();
            var packageLocationIds = package.Data.Locations.Select(l => l.Id).ToHashSet();
            var packageEventIds = package.Data.Events.Select(e => e.Id).ToHashSet();

            result.ConflictingVaults = existingVaultIds.Count(id => packageVaultIds.Contains(id));
            result.ConflictingKnowledgeItems = existingKnowledgeIds.Count(id => packageKnowledgeIds.Contains(id));
            result.ConflictingPersons = existingPersonIds.Count(id => packagePersonIds.Contains(id));
            result.ConflictingLocations = existingLocationIds.Count(id => packageLocationIds.Contains(id));
            result.ConflictingEvents = existingEventIds.Count(id => packageEventIds.Contains(id));
        }
        else
        {
            // Match import's stable identity first, retaining its cross-edition name fallback.
            // Names can change in later snapshots without changing the destination identity.
            var existingVaults = await _db.Vaults.Select(v => new { v.Id, v.Name }).ToListAsync(ct);
            var existingPersons = await _db.Persons.Select(p => new { p.Id, p.Name }).ToListAsync(ct);
            var existingLocations = await _db.Locations.Select(l => new { l.Id, l.Name }).ToListAsync(ct);
            var existingEvents = await _db.Events.Select(e => new { e.Id, e.Name }).ToListAsync(ct);

            result.ConflictingVaults = CountNamedConflicts(package.Data.Vaults.Select(v => (v.Id, v.Name)),
                existingVaults.Select(v => (v.Id, v.Name)), "Vault");
            result.ConflictingPersons = CountNamedConflicts(package.Data.Persons.Select(p => (p.Id, p.Name)),
                existingPersons.Select(p => (p.Id, p.Name)), "Person");
            result.ConflictingLocations = CountNamedConflicts(package.Data.Locations.Select(l => (l.Id, l.Name)),
                existingLocations.Select(l => (l.Id, l.Name)), "Location");
            result.ConflictingEvents = CountNamedConflicts(package.Data.Events.Select(e => (e.Id, e.Name)),
                existingEvents.Select(e => (e.Id, e.Name)), "Event");

            // Knowledge has no name-based match; reuse the deterministic source namespace on retries.
            var knowledgeIds = (await _db.KnowledgeItems.Select(k => k.Id).ToListAsync(ct)).ToHashSet();
            result.ConflictingKnowledgeItems = package.Data.KnowledgeItems.Count(k =>
                knowledgeIds.Contains(StableImportId(_tenantProvider.TenantId, package, "Knowledge", k.Id)));
        }

        var topics = await _db.Topics.Select(t => new { t.Id, t.Name }).ToListAsync(ct);
        var tags = await _db.Tags.Select(t => new { t.Id, t.Name }).ToListAsync(ct);
        result.ConflictingTopics = CountNamedConflicts(package.Data.Topics.Select(t => (t.Id, t.Name)),
            topics.Select(t => (t.Id, t.Name)), "Topic");
        result.ConflictingTags = CountNamedConflicts(package.Data.Tags.Select(t => (t.Id, t.Name)),
            tags.Select(t => (t.Id, t.Name)), "Tag");
        {
            Guid ResolveId(string kind, Guid id) => preserveIds ? id : StableImportId(_tenantProvider.TenantId, package, kind, id);
            var comments = (await _db.Comments.Select(c => c.Id).ToListAsync(ct)).ToHashSet();
            var files = (await _db.FileRecords.Select(f => f.Id).ToListAsync(ct)).ToHashSet();
            var inbox = (await _db.InboxItems.Select(i => i.Id).ToListAsync(ct)).ToHashSet();
            result.ConflictingComments = package.Data.Comments.Count(c => comments.Contains(ResolveId("KnowledgeComment", c.Id)));
            result.ConflictingFileRecords = package.Data.FileRecords.Count(f => files.Contains(ResolveId("FileRecord", f.Id)));
            result.ConflictingInboxItems = package.Data.InboxItems.Count(i => inbox.Contains(ResolveId("InboxItem", i.Id)));
        }

        if (result.ConflictingVaults > 0 || result.ConflictingKnowledgeItems > 0)
            result.Warnings.Add($"Found conflicts: {result.ConflictingVaults} vaults, {result.ConflictingKnowledgeItems} knowledge items.");

        result.IsValid = result.Errors.Count == 0;
        return result;
    }

    public Task<PortableImportResult> ImportAsync(PortableExportPackage package,
        ImportConflictStrategy strategy = ImportConflictStrategy.Skip, CancellationToken ct = default)
        => ImportCoreAsync(package, strategy, ct, null);

    public Task<PortableImportResult> ImportZipAsync(ZipArchive archive,
        ImportConflictStrategy strategy = ImportConflictStrategy.Skip, CancellationToken ct = default)
        => ImportCoreAsync(PortableZipReader.ReadFromZip(archive), strategy, ct,
            file => string.IsNullOrEmpty(file.BinaryFilePath) ? null : archive.GetEntry(file.BinaryFilePath)?.Open());

    private async Task<PortableImportResult> ImportCoreAsync(
        PortableExportPackage package, ImportConflictStrategy strategy, CancellationToken ct,
        Func<PortableFileRecord, Stream?>? openBinary)
    {
        var sw = Stopwatch.StartNew();
        var result = new PortableImportResult { StrategyUsed = strategy };

        if (!Enum.IsDefined(strategy))
            return new PortableImportResult { Error = "Invalid import strategy; use Skip, Overwrite or Merge." };
        try { PortableZipReader.ValidatePackage(package); }
        catch (PortableZipSecurityException ex) { return new PortableImportResult { Error = ex.Message }; }

        // Schema gate
        if (!CoreSchema.CanRead(package.SchemaVersion))
        {
            result.Success = false;
            result.Error = $"Schema version {package.SchemaVersion} is not compatible. " +
                           $"This build supports {CoreSchema.GetCompatibilityInfo()}.";
            result.Duration = sw.Elapsed;
            return result;
        }

        var preserveIds = IsSameEdition(package.SourceEdition);
        var tenantId = _tenantProvider.TenantId;
        Guid ResolveId(string kind, Guid sourceId) => preserveIds ? sourceId :
            StableImportId(tenantId, package, kind, sourceId);

        // ID remap dictionaries (only used for cross-edition)
        var vaultIdMap = new Dictionary<Guid, Guid>();
        var topicIdMap = new Dictionary<Guid, Guid>();
        var tagIdMap = new Dictionary<Guid, Guid>();
        var personIdMap = new Dictionary<Guid, Guid>();
        var locationIdMap = new Dictionary<Guid, Guid>();
        var eventIdMap = new Dictionary<Guid, Guid>();
        var knowledgeIdMap = new Dictionary<Guid, Guid>();
        var commentIdMap = new Dictionary<Guid, Guid>();
        var fileRecordIdMap = new Dictionary<Guid, Guid>();
        var createdKnowledgeIds = new HashSet<Guid>();
        var createdCommentIds = new HashSet<Guid>();

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        try
        {
            _logger.LogInformation(
                "Starting import: {Edition} source, {Strategy} strategy, preserveIds={PreserveIds}",
                package.SourceEdition, strategy, preserveIds);

            var originalVaultIds = (await _db.Vaults.Select(v => v.Id).ToListAsync(ct)).ToHashSet();

            // 1. Vaults
            result.Vaults = await ImportVaultsAsync(
                package.Data.Vaults, vaultIdMap, preserveIds, ResolveId, strategy, tenantId, ct);

            // 2. Topics
            result.Topics = await ImportTopicsAsync(
                package.Data.Topics, topicIdMap, preserveIds, ResolveId, strategy, tenantId, ct);

            // 3. Tags
            result.Tags = await ImportTagsAsync(
                package.Data.Tags, tagIdMap, preserveIds, ResolveId, strategy, tenantId, ct);

            // 4. Persons
            result.Persons = await ImportPersonsAsync(
                package.Data.Persons, personIdMap, preserveIds, ResolveId, strategy, tenantId, ct);

            // 5. Locations
            result.Locations = await ImportLocationsAsync(
                package.Data.Locations, locationIdMap, preserveIds, ResolveId, strategy, tenantId, ct);

            // 6. Events
            result.Events = await ImportEventsAsync(
                package.Data.Events, eventIdMap, preserveIds, ResolveId, strategy, tenantId, ct);

            // 7. Knowledge (with junctions)
            var (knowledgeCounts, junctionCount) = await ImportKnowledgeAsync(
                package.Data.KnowledgeItems, knowledgeIdMap,
                vaultIdMap, topicIdMap, tagIdMap, personIdMap, locationIdMap, eventIdMap,
                preserveIds, ResolveId, strategy, tenantId, ct, createdKnowledgeIds);
            result.KnowledgeItems = knowledgeCounts;
            result.JunctionsRestored = junctionCount;

            // 8. InboxItems
            result.InboxItems = await ImportInboxItemsAsync(
                package.Data.InboxItems, preserveIds, ResolveId, strategy, tenantId, ct);

            // 9. VaultPerson junctions (from PortableVault.PersonIds)
            await ImportVaultPersonsAsync(
                package.Data.Vaults, vaultIdMap, personIdMap, preserveIds, tenantId, ct,
                strategy == ImportConflictStrategy.Skip ? originalVaultIds : null);

            // 10. Comments (v2+)
            if (package.Data.Comments.Count > 0)
            {
                result.Comments = await ImportCommentsAsync(
                    package.Data.Comments, commentIdMap, knowledgeIdMap,
                    preserveIds, ResolveId, strategy, tenantId, ct, createdCommentIds);
            }

            // 11. FileRecords (v2+)
            if (package.Data.FileRecords.Count > 0)
            {
                result.FileRecords = await ImportFileRecordsAsync(
                    package.Data.FileRecords, fileRecordIdMap, knowledgeIdMap, commentIdMap,
                    preserveIds, ResolveId, strategy, tenantId, ct, openBinary, result, createdKnowledgeIds, createdCommentIds);
            }

            // 12. Archives (v2+)
            if (package.Data.Archives.Count > 0)
            {
                result.ArchiveRecordsStored = await ImportArchivesAsync(
                    package.Data.Archives, tenantId, strategy, ct);
            }

            // 13. Recompute VaultAncestors
            await RecomputeVaultAncestorsAsync(tenantId, ct);

            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            result.Success = true;
            _logger.LogInformation("Import completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Import failed, rolling back transaction");
            await transaction.RollbackAsync(ct);
            result.Success = false;
            result.Error = ex.Message;
        }

        result.Duration = sw.Elapsed;
        return result;
    }

    private async Task<EntityImportCounts> ImportVaultsAsync(
        List<PortableVault> portableVaults,
        Dictionary<Guid, Guid> idMap,
        bool preserveIds, Func<string, Guid, Guid> resolveId,
        ImportConflictStrategy strategy,
        Guid tenantId,
        CancellationToken ct)
    {
        var counts = new EntityImportCounts();

        // Load existing vaults for conflict detection
        var existingById = await _db.Vaults.ToDictionaryAsync(v => v.Id, ct);
        var originalIds = existingById.Keys.ToHashSet();
        var existingByName = existingById.Values
            .GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var pv in portableVaults)
        {
            var existing = FindExisting(resolveId("Vault", pv.Id), pv.Name, preserveIds, existingById, existingByName);

            if (existing != null)
            {
                idMap[pv.Id] = existing.Id;

                switch (strategy)
                {
                    case ImportConflictStrategy.Skip:
                        counts.Skipped++;
                        break;
                    case ImportConflictStrategy.Overwrite:
                        existing.Name = pv.Name;
                        existing.Description = pv.Description;
                        existing.VaultType = pv.VaultType;
                        existing.IsDefault = pv.IsDefault;
                        existing.UpdatedAt = DateTime.UtcNow;
                        existing.PlatformData = SerializeExtensionData(pv.ExtensionData);
                        counts.Overwritten++;
                        break;
                    case ImportConflictStrategy.Merge:
                        existing.Description ??= pv.Description;
                        existing.VaultType ??= pv.VaultType;
                        existing.UpdatedAt = DateTime.UtcNow;
                        MergePlatformData(existing, pv.ExtensionData);
                        counts.Merged++;
                        break;
                }
            }
            else
            {
                var newId = resolveId("Vault", pv.Id);
                idMap[pv.Id] = newId;

                var vault = new Vault
                {
                    Id = newId,
                    TenantId = tenantId,
                    Name = pv.Name,
                    Description = pv.Description,
                    VaultType = pv.VaultType,
                    IsDefault = pv.IsDefault,
                    ParentVaultId = null, // Set in second pass after all vaults exist
                    CreatedAt = pv.CreatedAt,
                    UpdatedAt = pv.UpdatedAt,
                    PlatformData = SerializeExtensionData(pv.ExtensionData)
                };
                _db.Vaults.Add(vault);
                existingById[newId] = vault;
                if (!existingByName.ContainsKey(vault.Name))
                    existingByName[vault.Name] = vault;
                counts.Created++;
            }
        }

        await _db.SaveChangesAsync(ct);

        // Resolve parents only after every ID is known, honoring the selected conflict strategy.
        foreach (var pv in portableVaults)
        {
            var mappedId = idMap[pv.Id];
            if (!existingById.TryGetValue(mappedId, out var vault)) continue;
            if (originalIds.Contains(mappedId) && (strategy == ImportConflictStrategy.Skip ||
                strategy == ImportConflictStrategy.Merge && vault.ParentVaultId.HasValue)) continue;
            vault.ParentVaultId = pv.ParentVaultId.HasValue && idMap.TryGetValue(pv.ParentVaultId.Value, out var parentId)
                ? parentId : null;
        }

        await _db.SaveChangesAsync(ct);
        return counts;
    }

    private async Task ImportVaultPersonsAsync(
        List<PortableVault> portableVaults,
        Dictionary<Guid, Guid> vaultIdMap,
        Dictionary<Guid, Guid> personIdMap,
        bool preserveIds,
        Guid tenantId,
        CancellationToken ct,
        HashSet<Guid>? skippedVaultIds)
    {
        foreach (var pv in portableVaults)
        {
            if (pv.PersonIds.Count == 0) continue;

            var mappedVaultId = vaultIdMap.TryGetValue(pv.Id, out var vid) ? vid : (preserveIds ? pv.Id : (Guid?)null);
            if (!mappedVaultId.HasValue || skippedVaultIds?.Contains(mappedVaultId.Value) == true) continue;

            // Load existing VaultPerson links for this vault
            var existingPersonIdList = await _db.VaultPersons
                .Where(vp => vp.VaultId == mappedVaultId.Value)
                .Select(vp => vp.PersonId)
                .ToListAsync(ct);
            var existingPersonIds = new HashSet<Guid>(existingPersonIdList);

            foreach (var personId in pv.PersonIds)
            {
                var mappedPersonId = personIdMap.TryGetValue(personId, out var pid) ? pid : (preserveIds ? personId : (Guid?)null);
                if (!mappedPersonId.HasValue) continue;
                if (!existingPersonIds.Add(mappedPersonId.Value)) continue;

                _db.VaultPersons.Add(new VaultPerson
                {
                    VaultId = mappedVaultId.Value,
                    PersonId = mappedPersonId.Value,
                    LinkedAt = DateTime.UtcNow
                });
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task<EntityImportCounts> ImportTopicsAsync(
        List<PortableTopic> portableTopics,
        Dictionary<Guid, Guid> idMap,
        bool preserveIds, Func<string, Guid, Guid> resolveId,
        ImportConflictStrategy strategy,
        Guid tenantId,
        CancellationToken ct)
    {
        var counts = new EntityImportCounts();
        var existingById = await _db.Topics.ToDictionaryAsync(t => t.Id, ct);
        var existingByName = existingById.Values
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var pt in portableTopics)
        {
            var existing = FindExisting(resolveId("Topic", pt.Id), pt.Name, preserveIds, existingById, existingByName);

            if (existing != null)
            {
                idMap[pt.Id] = existing.Id;

                switch (strategy)
                {
                    case ImportConflictStrategy.Skip:
                        counts.Skipped++;
                        break;
                    case ImportConflictStrategy.Overwrite:
                        existing.Name = pt.Name;
                        existing.Description = pt.Description;
                        existing.UpdatedAt = DateTime.UtcNow;
                        existing.PlatformData = SerializeExtensionData(pt.ExtensionData);
                        counts.Overwritten++;
                        break;
                    case ImportConflictStrategy.Merge:
                        existing.Description ??= pt.Description;
                        existing.UpdatedAt = DateTime.UtcNow;
                        MergePlatformData(existing, pt.ExtensionData);
                        counts.Merged++;
                        break;
                }
            }
            else
            {
                var newId = resolveId("Topic", pt.Id);
                idMap[pt.Id] = newId;

                var topic = new Topic
                {
                    Id = newId,
                    TenantId = tenantId,
                    Name = pt.Name,
                    Description = pt.Description,
                    CreatedAt = pt.CreatedAt,
                    UpdatedAt = pt.UpdatedAt,
                    PlatformData = SerializeExtensionData(pt.ExtensionData)
                };
                _db.Topics.Add(topic);
                existingById[newId] = topic;
                if (!existingByName.ContainsKey(topic.Name))
                    existingByName[topic.Name] = topic;
                counts.Created++;
            }
        }

        await _db.SaveChangesAsync(ct);
        return counts;
    }

    private async Task<EntityImportCounts> ImportTagsAsync(
        List<PortableTag> portableTags,
        Dictionary<Guid, Guid> idMap,
        bool preserveIds, Func<string, Guid, Guid> resolveId,
        ImportConflictStrategy strategy,
        Guid tenantId,
        CancellationToken ct)
    {
        var counts = new EntityImportCounts();
        var existingById = await _db.Tags.ToDictionaryAsync(t => t.Id, ct);
        var existingByName = existingById.Values
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var pt in portableTags)
        {
            var existing = FindExisting(resolveId("Tag", pt.Id), pt.Name, preserveIds, existingById, existingByName);

            if (existing != null)
            {
                idMap[pt.Id] = existing.Id;

                switch (strategy)
                {
                    case ImportConflictStrategy.Skip:
                        counts.Skipped++;
                        break;
                    case ImportConflictStrategy.Overwrite:
                        existing.Name = pt.Name;
                        existing.UpdatedAt = DateTime.UtcNow;
                        existing.PlatformData = SerializeExtensionData(pt.ExtensionData);
                        counts.Overwritten++;
                        break;
                    case ImportConflictStrategy.Merge:
                        // Tag only has Name, nothing to merge
                        MergePlatformData(existing, pt.ExtensionData);
                        counts.Merged++;
                        break;
                }
            }
            else
            {
                var newId = resolveId("Tag", pt.Id);
                idMap[pt.Id] = newId;

                var tag = new Tag
                {
                    Id = newId,
                    TenantId = tenantId,
                    Name = pt.Name,
                    CreatedAt = pt.CreatedAt,
                    UpdatedAt = pt.UpdatedAt,
                    PlatformData = SerializeExtensionData(pt.ExtensionData)
                };
                _db.Tags.Add(tag);
                existingById[newId] = tag;
                if (!existingByName.ContainsKey(tag.Name))
                    existingByName[tag.Name] = tag;
                counts.Created++;
            }
        }

        await _db.SaveChangesAsync(ct);
        return counts;
    }

    private async Task<EntityImportCounts> ImportPersonsAsync(
        List<PortablePerson> portablePersons,
        Dictionary<Guid, Guid> idMap,
        bool preserveIds, Func<string, Guid, Guid> resolveId,
        ImportConflictStrategy strategy,
        Guid tenantId,
        CancellationToken ct)
    {
        var counts = new EntityImportCounts();
        var existingById = await _db.Persons.ToDictionaryAsync(p => p.Id, ct);
        var existingByName = existingById.Values
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var pp in portablePersons)
        {
            var existing = FindExisting(resolveId("Person", pp.Id), pp.Name, preserveIds, existingById, existingByName);

            if (existing != null)
            {
                idMap[pp.Id] = existing.Id;

                switch (strategy)
                {
                    case ImportConflictStrategy.Skip:
                        counts.Skipped++;
                        break;
                    case ImportConflictStrategy.Overwrite:
                        existing.Name = pp.Name;
                        existing.UpdatedAt = DateTime.UtcNow;
                        existing.PlatformData = SerializeExtensionData(pp.ExtensionData);
                        counts.Overwritten++;
                        break;
                    case ImportConflictStrategy.Merge:
                        MergePlatformData(existing, pp.ExtensionData);
                        counts.Merged++;
                        break;
                }
            }
            else
            {
                var newId = resolveId("Person", pp.Id);
                idMap[pp.Id] = newId;

                var person = new Person
                {
                    Id = newId,
                    TenantId = tenantId,
                    Name = pp.Name,
                    CreatedAt = pp.CreatedAt,
                    UpdatedAt = pp.UpdatedAt,
                    PlatformData = SerializeExtensionData(pp.ExtensionData)
                };
                _db.Persons.Add(person);
                existingById[newId] = person;
                if (!existingByName.ContainsKey(person.Name))
                    existingByName[person.Name] = person;
                counts.Created++;
            }
        }

        await _db.SaveChangesAsync(ct);
        return counts;
    }

    private async Task<EntityImportCounts> ImportLocationsAsync(
        List<PortableLocation> portableLocations,
        Dictionary<Guid, Guid> idMap,
        bool preserveIds, Func<string, Guid, Guid> resolveId,
        ImportConflictStrategy strategy,
        Guid tenantId,
        CancellationToken ct)
    {
        var counts = new EntityImportCounts();
        var existingById = await _db.Locations.ToDictionaryAsync(l => l.Id, ct);
        var existingByName = existingById.Values
            .GroupBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var pl in portableLocations)
        {
            var existing = FindExisting(resolveId("Location", pl.Id), pl.Name, preserveIds, existingById, existingByName);

            if (existing != null)
            {
                idMap[pl.Id] = existing.Id;

                switch (strategy)
                {
                    case ImportConflictStrategy.Skip:
                        counts.Skipped++;
                        break;
                    case ImportConflictStrategy.Overwrite:
                        existing.Name = pl.Name;
                        existing.UpdatedAt = DateTime.UtcNow;
                        existing.PlatformData = SerializeExtensionData(pl.ExtensionData);
                        counts.Overwritten++;
                        break;
                    case ImportConflictStrategy.Merge:
                        MergePlatformData(existing, pl.ExtensionData);
                        counts.Merged++;
                        break;
                }
            }
            else
            {
                var newId = resolveId("Location", pl.Id);
                idMap[pl.Id] = newId;

                var location = new Location
                {
                    Id = newId,
                    TenantId = tenantId,
                    Name = pl.Name,
                    CreatedAt = pl.CreatedAt,
                    UpdatedAt = pl.UpdatedAt,
                    PlatformData = SerializeExtensionData(pl.ExtensionData)
                };
                _db.Locations.Add(location);
                existingById[newId] = location;
                if (!existingByName.ContainsKey(location.Name))
                    existingByName[location.Name] = location;
                counts.Created++;
            }
        }

        await _db.SaveChangesAsync(ct);
        return counts;
    }

    private async Task<EntityImportCounts> ImportEventsAsync(
        List<PortableEvent> portableEvents,
        Dictionary<Guid, Guid> idMap,
        bool preserveIds, Func<string, Guid, Guid> resolveId,
        ImportConflictStrategy strategy,
        Guid tenantId,
        CancellationToken ct)
    {
        var counts = new EntityImportCounts();
        var existingById = await _db.Events.ToDictionaryAsync(e => e.Id, ct);
        var existingByName = existingById.Values
            .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var pe in portableEvents)
        {
            var existing = FindExisting(resolveId("Event", pe.Id), pe.Name, preserveIds, existingById, existingByName);

            if (existing != null)
            {
                idMap[pe.Id] = existing.Id;

                switch (strategy)
                {
                    case ImportConflictStrategy.Skip:
                        counts.Skipped++;
                        break;
                    case ImportConflictStrategy.Overwrite:
                        existing.Name = pe.Name;
                        existing.UpdatedAt = DateTime.UtcNow;
                        existing.PlatformData = SerializeExtensionData(pe.ExtensionData);
                        counts.Overwritten++;
                        break;
                    case ImportConflictStrategy.Merge:
                        MergePlatformData(existing, pe.ExtensionData);
                        counts.Merged++;
                        break;
                }
            }
            else
            {
                var newId = resolveId("Event", pe.Id);
                idMap[pe.Id] = newId;

                var evt = new Event
                {
                    Id = newId,
                    TenantId = tenantId,
                    Name = pe.Name,
                    CreatedAt = pe.CreatedAt,
                    UpdatedAt = pe.UpdatedAt,
                    PlatformData = SerializeExtensionData(pe.ExtensionData)
                };
                _db.Events.Add(evt);
                existingById[newId] = evt;
                if (!existingByName.ContainsKey(evt.Name))
                    existingByName[evt.Name] = evt;
                counts.Created++;
            }
        }

        await _db.SaveChangesAsync(ct);
        return counts;
    }

    private async Task<(EntityImportCounts counts, int junctionCount)> ImportKnowledgeAsync(
        List<PortableKnowledge> portableKnowledge,
        Dictionary<Guid, Guid> knowledgeIdMap,
        Dictionary<Guid, Guid> vaultIdMap,
        Dictionary<Guid, Guid> topicIdMap,
        Dictionary<Guid, Guid> tagIdMap,
        Dictionary<Guid, Guid> personIdMap,
        Dictionary<Guid, Guid> locationIdMap,
        Dictionary<Guid, Guid> eventIdMap,
        bool preserveIds, Func<string, Guid, Guid> resolveId,
        ImportConflictStrategy strategy,
        Guid tenantId,
        CancellationToken ct, HashSet<Guid> createdIds)
    {
        var counts = new EntityImportCounts();
        var junctionCount = 0;

        // Knowledge conflicts are GUID-only per spec
        var existingById = await _db.KnowledgeItems
            .Include(k => k.KnowledgeVaults)
            .Include(k => k.KnowledgePersons)
            .Include(k => k.KnowledgeLocations)
            .Include(k => k.KnowledgeEvents)
            .Include(k => k.Tags)
            .ToDictionaryAsync(k => k.Id, ct);

        // Pre-load tags for many-to-many linking
        var allTags = await _db.Tags.ToDictionaryAsync(t => t.Id, ct);

        foreach (var pk in portableKnowledge)
        {
            var lookupId = resolveId("Knowledge", pk.Id); // Stable source namespace makes retries idempotent.
            existingById.TryGetValue(lookupId, out var existing);

            if (existing != null)
            {
                knowledgeIdMap[pk.Id] = existing.Id;

                switch (strategy)
                {
                    case ImportConflictStrategy.Skip:
                        counts.Skipped++;
                        break;
                    case ImportConflictStrategy.Overwrite:
                        existing.Title = pk.Title;
                        existing.Content = pk.Content;
                        existing.Summary = pk.Summary;
                        existing.BriefSummary = pk.BriefSummary;
                        existing.Type = pk.Type;
                        existing.Source = pk.Source;
                        existing.FilePath = pk.FilePath;
                        existing.IsIndexed = false;
                        existing.IndexedAt = null;
                        existing.TopicId = RemapNullableId(pk.TopicId, topicIdMap);
                        existing.UpdatedAt = DateTime.UtcNow;
                        existing.PlatformData = SerializeExtensionData(pk.ExtensionData);

                        // Rebuild junctions
                        junctionCount += RebuildKnowledgeJunctions(
                            existing, pk, vaultIdMap, tagIdMap, personIdMap, locationIdMap, eventIdMap, allTags, tenantId);
                        counts.Overwritten++;
                        break;
                    case ImportConflictStrategy.Merge:
                        existing.Summary ??= pk.Summary;
                        existing.BriefSummary ??= pk.BriefSummary;
                        if (string.IsNullOrEmpty(existing.Content))
                            existing.Content = pk.Content;
                        existing.TopicId ??= RemapNullableId(pk.TopicId, topicIdMap);
                        junctionCount += MergeKnowledgeJunctions(
                            existing, pk, vaultIdMap, tagIdMap, personIdMap, locationIdMap, eventIdMap, allTags, tenantId);
                        existing.IsIndexed = false;
                        existing.UpdatedAt = DateTime.UtcNow;
                        MergePlatformData(existing, pk.ExtensionData);
                        counts.Merged++;
                        break;
                }
            }
            else
            {
                var newId = resolveId("Knowledge", pk.Id);
                knowledgeIdMap[pk.Id] = newId;

                var knowledge = new Knowledge
                {
                    Id = newId,
                    TenantId = tenantId,
                    Title = pk.Title,
                    Content = pk.Content,
                    Summary = pk.Summary,
                    BriefSummary = pk.BriefSummary,
                    Type = pk.Type,
                    Source = pk.Source,
                    FilePath = pk.FilePath,
                    IsIndexed = false,
                    IndexedAt = null,
                    TopicId = RemapNullableId(pk.TopicId, topicIdMap),
                    CreatedAt = pk.CreatedAt,
                    UpdatedAt = pk.UpdatedAt,
                    PlatformData = SerializeExtensionData(pk.ExtensionData)
                };
                _db.KnowledgeItems.Add(knowledge);
                existingById[newId] = knowledge;

                // Create junctions
                junctionCount += CreateKnowledgeJunctions(
                    knowledge, pk, vaultIdMap, tagIdMap, personIdMap, locationIdMap, eventIdMap, allTags, tenantId);

                createdIds.Add(newId);
                counts.Created++;
            }
        }

        await _db.SaveChangesAsync(ct);
        return (counts, junctionCount);
    }

    private int CreateKnowledgeJunctions(
        Knowledge knowledge,
        PortableKnowledge pk,
        Dictionary<Guid, Guid> vaultIdMap,
        Dictionary<Guid, Guid> tagIdMap,
        Dictionary<Guid, Guid> personIdMap,
        Dictionary<Guid, Guid> locationIdMap,
        Dictionary<Guid, Guid> eventIdMap,
        Dictionary<Guid, Tag> allTags,
        Guid tenantId)
    {
        var count = 0;

        // KnowledgeVault junctions
        foreach (var vaultId in pk.VaultIds)
        {
            if (vaultIdMap.TryGetValue(vaultId, out var mappedVaultId))
            {
                _db.KnowledgeVaults.Add(new KnowledgeVault
                {
                    KnowledgeId = knowledge.Id,
                    VaultId = mappedVaultId,
                    TenantId = tenantId,
                    IsPrimary = pk.PrimaryVaultId.HasValue && pk.PrimaryVaultId.Value == vaultId,
                    CreatedAt = DateTime.UtcNow
                });
                count++;
            }
        }

        // Tag many-to-many
        foreach (var tagId in pk.TagIds)
        {
            if (tagIdMap.TryGetValue(tagId, out var mappedTagId) && allTags.TryGetValue(mappedTagId, out var tag))
            {
                knowledge.Tags.Add(tag);
                count++;
            }
        }

        // KnowledgePerson junctions — use PersonLinks (v2) if available, else flat PersonIds
        if (pk.PersonLinks is { Count: > 0 })
        {
            foreach (var link in pk.PersonLinks)
            {
                if (personIdMap.TryGetValue(link.EntityId, out var mappedPersonId))
                {
                    _db.KnowledgePersons.Add(new KnowledgePerson
                    {
                        KnowledgeId = knowledge.Id,
                        PersonId = mappedPersonId,
                        RelationshipContext = link.RelationshipContext,
                        Role = link.Role,
                        Mentions = link.Mentions,
                        ConfidenceScore = link.ConfidenceScore
                    });
                    count++;
                }
            }
        }
        else
        {
            foreach (var personId in pk.PersonIds)
            {
                if (personIdMap.TryGetValue(personId, out var mappedPersonId))
                {
                    _db.KnowledgePersons.Add(new KnowledgePerson
                    {
                        KnowledgeId = knowledge.Id,
                        PersonId = mappedPersonId
                    });
                    count++;
                }
            }
        }

        // KnowledgeLocation junctions
        foreach (var locationId in pk.LocationIds)
        {
            if (locationIdMap.TryGetValue(locationId, out var mappedLocationId))
            {
                _db.KnowledgeLocations.Add(new KnowledgeLocation
                {
                    KnowledgeId = knowledge.Id,
                    LocationId = mappedLocationId
                });
                count++;
            }
        }

        // KnowledgeEvent junctions
        foreach (var eventId in pk.EventIds)
        {
            if (eventIdMap.TryGetValue(eventId, out var mappedEventId))
            {
                _db.KnowledgeEvents.Add(new KnowledgeEvent
                {
                    KnowledgeId = knowledge.Id,
                    EventId = mappedEventId
                });
                count++;
            }
        }

        return count;
    }

    private int MergeKnowledgeJunctions(
        Knowledge existing, PortableKnowledge source,
        Dictionary<Guid, Guid> vaultIdMap, Dictionary<Guid, Guid> tagIdMap,
        Dictionary<Guid, Guid> personIdMap, Dictionary<Guid, Guid> locationIdMap,
        Dictionary<Guid, Guid> eventIdMap, Dictionary<Guid, Tag> allTags, Guid tenantId)
    {
        // Deduplicate by destination identity: distinct source IDs can resolve to the same
        // named entity across editions. Existing junctions and their metadata stay intact.
        static List<Guid> MissingIds(IEnumerable<Guid> sourceIds, Dictionary<Guid, Guid> map, IEnumerable<Guid> existingIds)
        {
            var seen = existingIds.ToHashSet();
            return sourceIds.Where(id => map.TryGetValue(id, out var mappedId) && seen.Add(mappedId)).ToList();
        }
        var personLinks = source.PersonLinks?.ToList() ?? new();
        var richPersonIds = personLinks.Select(link => link.EntityId).ToHashSet();
        personLinks.AddRange(source.PersonIds.Where(id => !richPersonIds.Contains(id))
            .Select(id => new PortableEntityLink { EntityId = id }));
        var missingPersonIds = MissingIds(personLinks.Select(link => link.EntityId), personIdMap,
            existing.KnowledgePersons.Select(p => p.PersonId)).ToHashSet();
        var additions = new PortableKnowledge
        {
            VaultIds = MissingIds(source.VaultIds, vaultIdMap, existing.KnowledgeVaults.Select(v => v.VaultId)),
            PrimaryVaultId = existing.KnowledgeVaults.Any(v => v.IsPrimary) ? null : source.PrimaryVaultId,
            TagIds = MissingIds(source.TagIds, tagIdMap, existing.Tags.Select(t => t.Id)),
            PersonLinks = personLinks.Where(link => missingPersonIds.Contains(link.EntityId)).DistinctBy(link => link.EntityId).ToList(),
            LocationIds = MissingIds(source.LocationIds.Concat(source.LocationLinks?.Select(link => link.EntityId) ?? []),
                locationIdMap, existing.KnowledgeLocations.Select(l => l.LocationId)),
            EventIds = MissingIds(source.EventIds.Concat(source.EventLinks?.Select(link => link.EntityId) ?? []),
                eventIdMap, existing.KnowledgeEvents.Select(e => e.EventId))
        };
        return CreateKnowledgeJunctions(existing, additions, vaultIdMap, tagIdMap,
            personIdMap, locationIdMap, eventIdMap, allTags, tenantId);
    }

    private int RebuildKnowledgeJunctions(
        Knowledge existing,
        PortableKnowledge pk,
        Dictionary<Guid, Guid> vaultIdMap,
        Dictionary<Guid, Guid> tagIdMap,
        Dictionary<Guid, Guid> personIdMap,
        Dictionary<Guid, Guid> locationIdMap,
        Dictionary<Guid, Guid> eventIdMap,
        Dictionary<Guid, Tag> allTags,
        Guid tenantId)
    {
        // Remove existing junctions
        _db.KnowledgeVaults.RemoveRange(existing.KnowledgeVaults);
        _db.KnowledgePersons.RemoveRange(existing.KnowledgePersons);
        _db.KnowledgeLocations.RemoveRange(existing.KnowledgeLocations);
        _db.KnowledgeEvents.RemoveRange(existing.KnowledgeEvents);
        existing.Tags.Clear();

        // Recreate from package
        return CreateKnowledgeJunctions(existing, pk, vaultIdMap, tagIdMap, personIdMap, locationIdMap, eventIdMap, allTags, tenantId);
    }

    private async Task<EntityImportCounts> ImportCommentsAsync(
        List<PortableKnowledgeComment> portableComments,
        Dictionary<Guid, Guid> commentIdMap,
        Dictionary<Guid, Guid> knowledgeIdMap,
        bool preserveIds, Func<string, Guid, Guid> resolveId,
        ImportConflictStrategy strategy,
        Guid tenantId,
        CancellationToken ct, HashSet<Guid> createdIds)
    {
        var counts = new EntityImportCounts();
        var existingById = await _db.Comments.ToDictionaryAsync(c => c.Id, ct);
        var originalIds = existingById.Keys.ToHashSet();

        // Two-pass: first create all comments with null ParentCommentId, then set parents
        foreach (var pc in portableComments)
        {
            var lookupId = resolveId("KnowledgeComment", pc.Id);
            existingById.TryGetValue(lookupId, out var existing);

            if (existing != null)
            {
                commentIdMap[pc.Id] = existing.Id;

                switch (strategy)
                {
                    case ImportConflictStrategy.Skip:
                        counts.Skipped++;
                        break;
                    case ImportConflictStrategy.Overwrite:
                        existing.AuthorName = pc.AuthorName;
                        existing.Body = pc.Body;
                        existing.IsAnswer = pc.IsAnswer;
                        existing.Sentiment = pc.Sentiment;
                        existing.UpdatedAt = DateTime.UtcNow;
                        existing.PlatformData = SerializeExtensionData(pc.ExtensionData);
                        counts.Overwritten++;
                        break;
                    case ImportConflictStrategy.Merge:
                        if (string.IsNullOrEmpty(existing.Body))
                            existing.Body = pc.Body;
                        existing.Sentiment ??= pc.Sentiment;
                        existing.UpdatedAt = DateTime.UtcNow;
                        MergePlatformData(existing, pc.ExtensionData);
                        counts.Merged++;
                        break;
                }
            }
            else
            {
                var newId = resolveId("KnowledgeComment", pc.Id);
                commentIdMap[pc.Id] = newId;

                var mappedKnowledgeId = knowledgeIdMap.TryGetValue(pc.KnowledgeId, out var kid)
                    ? kid : (preserveIds ? pc.KnowledgeId : Guid.Empty);

                if (mappedKnowledgeId == Guid.Empty) continue;

                var comment = new KnowledgeComment
                {
                    Id = newId,
                    TenantId = tenantId,
                    KnowledgeId = mappedKnowledgeId,
                    ParentCommentId = null, // Set in second pass
                    AuthorName = pc.AuthorName,
                    Body = pc.Body,
                    IsAnswer = pc.IsAnswer,
                    Sentiment = pc.Sentiment,
                    CreatedAt = pc.CreatedAt,
                    UpdatedAt = pc.UpdatedAt,
                    PlatformData = SerializeExtensionData(pc.ExtensionData)
                };
                _db.Comments.Add(comment);
                existingById[newId] = comment;
                createdIds.Add(newId);
                counts.Created++;
            }
        }

        await _db.SaveChangesAsync(ct);

        // Existing parent values are retained by Skip/Merge; Overwrite can also clear a parent.
        foreach (var pc in portableComments)
        {
            if (!commentIdMap.TryGetValue(pc.Id, out var mappedId) || !existingById.TryGetValue(mappedId, out var comment)) continue;
            if (originalIds.Contains(mappedId) && (strategy == ImportConflictStrategy.Skip ||
                strategy == ImportConflictStrategy.Merge && comment.ParentCommentId.HasValue)) continue;
            comment.ParentCommentId = pc.ParentCommentId.HasValue && commentIdMap.TryGetValue(pc.ParentCommentId.Value, out var parent)
                ? parent : null;
        }

        await _db.SaveChangesAsync(ct);
        return counts;
    }

    internal const string AttachmentMetadataKey = "__knowzPortableAttachmentMetadata";

    private async Task<EntityImportCounts> ImportFileRecordsAsync(
        List<PortableFileRecord> portableFiles, Dictionary<Guid, Guid> fileRecordIdMap,
        Dictionary<Guid, Guid> knowledgeIdMap, Dictionary<Guid, Guid> commentIdMap,
        bool preserveIds, Func<string, Guid, Guid> resolveId, ImportConflictStrategy strategy, Guid tenantId, CancellationToken ct,
        Func<PortableFileRecord, Stream?>? openBinary, PortableImportResult result,
        HashSet<Guid> createdKnowledgeIds, HashSet<Guid> createdCommentIds)
    {
        var counts = new EntityImportCounts();
        var existingById = await _db.FileRecords.Include(f => f.Attachments).ToDictionaryAsync(f => f.Id, ct);
        foreach (var pf in portableFiles)
        {
            existingById.TryGetValue(resolveId("FileRecord", pf.Id), out var file);
            var existing = file != null;
            if (existing && strategy == ImportConflictStrategy.Skip)
            {
                fileRecordIdMap[pf.Id] = file!.Id;
                counts.Skipped++;
                // Shared files recur in independently importable parts. Only attach to owners
                // created by this part; keep existing owners, metadata and bytes untouched.
                var linksForNewOwners = pf.Attachments.Where(a =>
                    (a.KnowledgeId.HasValue && knowledgeIdMap.TryGetValue(a.KnowledgeId.Value, out var kid) && createdKnowledgeIds.Contains(kid)) ||
                    (a.CommentId.HasValue && commentIdMap.TryGetValue(a.CommentId.Value, out var cid) && createdCommentIds.Contains(cid))).ToList();
                if (linksForNewOwners.Count > 0)
                    RestoreFileAttachmentLinks(file!, linksForNewOwners, knowledgeIdMap, commentIdMap, replace: false);
                continue;
            }
            if (file == null)
            {
                file = new FileRecord { Id = resolveId("FileRecord", pf.Id), TenantId = tenantId,
                    CreatedAt = pf.CreatedAt, UpdatedAt = pf.UpdatedAt, BlobUri = pf.BlobUri, BlobMigrationPending = true };
                _db.FileRecords.Add(file);
                existingById[file.Id] = file;
                counts.Created++;
            }
            else if (strategy == ImportConflictStrategy.Overwrite) counts.Overwritten++;
            else counts.Merged++;
            fileRecordIdMap[pf.Id] = file.Id;

            if (!existing || strategy == ImportConflictStrategy.Overwrite)
            {
                file.FileName = pf.FileName; file.ContentType = pf.ContentType; file.SizeBytes = pf.SizeBytes;
                file.TranscriptionText = pf.TranscriptionText; file.ExtractedText = pf.ExtractedText; file.VisionDescription = pf.VisionDescription;
                var extensionData = pf.ExtensionData?.Where(p => p.Key != AttachmentMetadataKey)
                    .ToDictionary(p => p.Key, p => p.Value) ?? new();
                // Attachment metadata belonging to other independently imported parts survives
                // file overwrite; the link restore below replaces only owners in this package.
                if (existing && !string.IsNullOrEmpty(file.PlatformData)
                    && JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(file.PlatformData) is { } previous
                    && previous.TryGetValue(AttachmentMetadataKey, out var previousLinks))
                    extensionData[AttachmentMetadataKey] = previousLinks;
                file.PlatformData = SerializeExtensionData(extensionData);
            }
            else
            {
                file.TranscriptionText ??= pf.TranscriptionText; file.ExtractedText ??= pf.ExtractedText; file.VisionDescription ??= pf.VisionDescription;
                MergePlatformData(file, pf.ExtensionData?.Where(p => p.Key != AttachmentMetadataKey)
                    .ToDictionary(p => p.Key, p => p.Value));
            }
            if (existing) file.UpdatedAt = DateTime.UtcNow;

            if (!existing || strategy == ImportConflictStrategy.Overwrite || file.BlobMigrationPending || string.IsNullOrEmpty(file.BlobUri))
                await RestoreBinaryAsync(pf, file, openBinary, result, ct);

            RestoreFileAttachmentLinks(file, pf.Attachments, knowledgeIdMap, commentIdMap,
                replace: existing && strategy == ImportConflictStrategy.Overwrite);
        }
        await _db.SaveChangesAsync(ct);
        return counts;
    }

    private void RestoreFileAttachmentLinks(FileRecord file, IEnumerable<PortableFileAttachmentLink> attachments,
        Dictionary<Guid, Guid> knowledgeIdMap, Dictionary<Guid, Guid> commentIdMap, bool replace)
    {
        var metadata = string.IsNullOrEmpty(file.PlatformData) ? new Dictionary<string, JsonElement>()
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(file.PlatformData)!;
        var links = metadata.TryGetValue(AttachmentMetadataKey, out var oldLinks)
            ? oldLinks.Deserialize<List<PortableFileAttachmentLink>>() ?? new() : new List<PortableFileAttachmentLink>();
        if (replace)
        {
            var knowledgeIds = knowledgeIdMap.Values.ToHashSet();
            var commentIds = commentIdMap.Values.ToHashSet();
            bool IsCurrentOwner(Guid? knowledgeId, Guid? commentId) =>
                (knowledgeId.HasValue && knowledgeIds.Contains(knowledgeId.Value)) ||
                (commentId.HasValue && commentIds.Contains(commentId.Value));
            foreach (var existing in file.Attachments.Where(a => IsCurrentOwner(a.KnowledgeId, a.CommentId)).ToList())
            {
                _db.FileAttachments.Remove(existing);
                file.Attachments.Remove(existing);
            }
            links.RemoveAll(a => IsCurrentOwner(a.KnowledgeId, a.CommentId));
        }
        foreach (var attachment in attachments)
        {
            var kid = RemapNullableId(attachment.KnowledgeId, knowledgeIdMap);
            var cid = RemapNullableId(attachment.CommentId, commentIdMap);
            if (!kid.HasValue && !cid.HasValue) continue;
            if (!file.Attachments.Any(a => a.KnowledgeId == kid && a.CommentId == cid))
            {
                var junction = new FileAttachment { FileRecordId = file.Id, KnowledgeId = kid, CommentId = cid, TenantId = file.TenantId };
                _db.FileAttachments.Add(junction);
            }
            if (!links.Any(a => a.KnowledgeId == kid && a.CommentId == cid))
            {
                var link = JsonSerializer.Deserialize<PortableFileAttachmentLink>(JsonSerializer.Serialize(attachment))!;
                link.KnowledgeId = kid; link.CommentId = cid; links.Add(link);
            }
        }
        if (links.Count > 0) metadata[AttachmentMetadataKey] = JsonSerializer.SerializeToElement(links);
        else metadata.Remove(AttachmentMetadataKey);
        file.PlatformData = metadata.Count > 0 ? JsonSerializer.Serialize(metadata) : null;
    }

    private async Task RestoreBinaryAsync(PortableFileRecord source, FileRecord destination,
        Func<PortableFileRecord, Stream?>? openBinary, PortableImportResult result, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(source.BinaryFilePath) && string.IsNullOrEmpty(source.BinaryContentBase64)) return;
        try
        {
            using var stream = !string.IsNullOrEmpty(source.BinaryContentBase64)
                ? new MemoryStream(Convert.FromBase64String(source.BinaryContentBase64))
                : openBinary?.Invoke(source) ?? throw new FileNotFoundException($"ZIP binary '{source.BinaryFilePath}' is missing.");
            destination.BlobUri = await _storageProvider.UploadAsync(destination.TenantId, destination.Id, stream,
                source.ContentType ?? "application/octet-stream", ct);
            destination.BlobMigrationPending = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.FilesBlobFailed++;
            result.Warnings.Add($"Could not restore file '{source.FileName}' ({destination.Id}): {ex.Message}");
            _logger.LogWarning(ex, "Failed to restore portable file {FileId}", destination.Id);
        }
    }

    private async Task<int> ImportArchivesAsync(
        Dictionary<string, List<JsonElement>> archives,
        Guid tenantId,
        ImportConflictStrategy strategy,
        CancellationToken ct)
    {
        var count = 0;

        // Preserve unrelated archives; conflicts are local to entity type and original ID.
        var existingArchives = await _db.PortableArchives
            .Where(a => a.TenantId == tenantId)
            .ToListAsync(ct);

        foreach (var (entityType, entries) in archives)
        {
            foreach (var entry in entries)
            {
                var originalId = Guid.Empty;
                if (entry.TryGetProperty("Id", out var idProp) || entry.TryGetProperty("id", out idProp))
                {
                    Guid.TryParse(idProp.GetString(), out originalId);
                }

                var existing = existingArchives.FirstOrDefault(a => a.EntityType == entityType &&
                    (originalId != Guid.Empty ? a.OriginalId == originalId : a.JsonData == entry.GetRawText()));
                if (existing != null)
                {
                    if (strategy == ImportConflictStrategy.Overwrite) { existing.JsonData = entry.GetRawText(); count++; }
                    else if (strategy == ImportConflictStrategy.Merge)
                    {
                        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existing.JsonData)!;
                        foreach (var field in entry.EnumerateObject())
                            if (!fields.TryGetValue(field.Name, out var value) || value.ValueKind == JsonValueKind.Null)
                                fields[field.Name] = field.Value.Clone();
                        existing.JsonData = JsonSerializer.Serialize(fields); count++;
                    }
                    continue;
                }
                var added = new PortableArchive
                {
                    TenantId = tenantId,
                    EntityType = entityType,
                    OriginalId = originalId,
                    JsonData = entry.GetRawText(),
                    CreatedAt = DateTime.UtcNow
                };
                _db.PortableArchives.Add(added);
                existingArchives.Add(added);
                count++;
            }
        }

        await _db.SaveChangesAsync(ct);
        return count;
    }

    private async Task<EntityImportCounts> ImportInboxItemsAsync(
        List<PortableInboxItem> portableItems,
        bool preserveIds, Func<string, Guid, Guid> resolveId,
        ImportConflictStrategy strategy,
        Guid tenantId,
        CancellationToken ct)
    {
        var counts = new EntityImportCounts();
        var existingById = await _db.InboxItems.ToDictionaryAsync(i => i.Id, ct);

        foreach (var pi in portableItems)
        {
            var lookupId = resolveId("InboxItem", pi.Id);
            existingById.TryGetValue(lookupId, out var existing);

            if (existing != null)
            {
                switch (strategy)
                {
                    case ImportConflictStrategy.Skip:
                        counts.Skipped++;
                        break;
                    case ImportConflictStrategy.Overwrite:
                        existing.Body = pi.Body;
                        existing.Type = pi.Type;
                        existing.UpdatedAt = DateTime.UtcNow;
                        existing.PlatformData = SerializeExtensionData(pi.ExtensionData);
                        counts.Overwritten++;
                        break;
                    case ImportConflictStrategy.Merge:
                        if (string.IsNullOrEmpty(existing.Body))
                            existing.Body = pi.Body;
                        existing.UpdatedAt = DateTime.UtcNow;
                        MergePlatformData(existing, pi.ExtensionData);
                        counts.Merged++;
                        break;
                }
            }
            else
            {
                var newId = resolveId("InboxItem", pi.Id);
                var item = new InboxItem
                {
                    Id = newId,
                    TenantId = tenantId,
                    Body = pi.Body,
                    Type = pi.Type,
                    CreatedAt = pi.CreatedAt,
                    UpdatedAt = pi.UpdatedAt,
                    PlatformData = SerializeExtensionData(pi.ExtensionData)
                };
                _db.InboxItems.Add(item);
                existingById[newId] = item;
                counts.Created++;
            }
        }

        await _db.SaveChangesAsync(ct);
        return counts;
    }

    private async Task RecomputeVaultAncestorsAsync(Guid tenantId, CancellationToken ct)
    {
        // Remove all existing ancestors for this tenant
        var existingAncestors = await _db.VaultAncestors
            .Where(va => _db.Vaults.Any(v => v.Id == va.AncestorVaultId))
            .ToListAsync(ct);
        _db.VaultAncestors.RemoveRange(existingAncestors);
        await _db.SaveChangesAsync(ct);

        // Load all vaults and build closure table from ParentVaultId
        var vaults = await _db.Vaults.AsNoTracking().ToListAsync(ct);
        var vaultById = vaults.ToDictionary(v => v.Id);

        foreach (var vault in vaults)
        {
            // Walk up the parent chain
            var current = vault;
            var depth = 0;
            while (current.ParentVaultId.HasValue && vaultById.TryGetValue(current.ParentVaultId.Value, out var parent))
            {
                depth++;
                _db.VaultAncestors.Add(new VaultAncestor
                {
                    DescendantVaultId = vault.Id,
                    AncestorVaultId = parent.Id,
                    Depth = depth
                });
                current = parent;

                // Safety valve to prevent infinite loops from circular references
                if (depth > 100) break;
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    private static Guid StableImportId(Guid destinationTenantId, PortableExportPackage source, string kind, Guid sourceId)
    {
        var name = $"knowz-portability-v1|{destinationTenantId:D}|{(source.SourceEdition ?? string.Empty).ToLowerInvariant()}|{source.SourceTenantId:D}|{kind}|{sourceId:D}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    // --- Helper methods ---

    private static bool IsSameEdition(string sourceEdition)
        => string.Equals(sourceEdition, "selfhosted", StringComparison.OrdinalIgnoreCase);

    private static T? FindExisting<T>(
        Guid id, string name, bool preserveIds,
        Dictionary<Guid, T> byId, Dictionary<string, T> byName)
    {
        if (byId.TryGetValue(id, out var byIdResult))
            return byIdResult;

        if (!preserveIds && byName.TryGetValue(name, out var byNameResult))
            return byNameResult;

        // For same-edition, also try name as fallback
        if (preserveIds && byName.TryGetValue(name, out var fallback))
            return default; // Don't match by name for same-edition (GUID is authoritative)

        return default;
    }

    private static Guid? RemapNullableId(Guid? id, Dictionary<Guid, Guid> idMap)
    {
        if (!id.HasValue) return null;
        return idMap.TryGetValue(id.Value, out var mapped) ? mapped : null;
    }

    private static string? SerializeExtensionData(Dictionary<string, JsonElement>? extensionData)
    {
        if (extensionData == null || extensionData.Count == 0)
            return null;
        return JsonSerializer.Serialize(extensionData);
    }

    private static void MergePlatformData(Knowz.Core.Interfaces.ISelfHostedEntity entity, Dictionary<string, JsonElement>? importedExtensionData)
    {
        if (importedExtensionData == null || importedExtensionData.Count == 0)
            return;

        if (string.IsNullOrEmpty(entity.PlatformData))
        {
            entity.PlatformData = JsonSerializer.Serialize(importedExtensionData);
            return;
        }

        // Merge: import wins on key conflicts
        var existing = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(entity.PlatformData)
                       ?? new Dictionary<string, JsonElement>();
        foreach (var kvp in importedExtensionData)
            existing[kvp.Key] = kvp.Value;

        entity.PlatformData = JsonSerializer.Serialize(existing);
    }
}
