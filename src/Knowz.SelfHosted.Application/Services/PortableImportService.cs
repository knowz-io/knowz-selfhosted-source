namespace Knowz.SelfHosted.Application.Services;

using System.Diagnostics;
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

        // Copy counts from metadata
        result.TotalVaults = package.Metadata.TotalVaults;
        result.TotalKnowledgeItems = package.Metadata.TotalKnowledgeItems;
        result.TotalTopics = package.Metadata.TotalTopics;
        result.TotalTags = package.Metadata.TotalTags;
        result.TotalPersons = package.Metadata.TotalPersons;
        result.TotalLocations = package.Metadata.TotalLocations;
        result.TotalEvents = package.Metadata.TotalEvents;
        result.TotalInboxItems = package.Metadata.TotalInboxItems;
        result.TotalComments = package.Metadata.TotalComments;
        result.TotalFileRecords = package.Metadata.TotalFileRecords;
        result.TotalArchiveTypes = package.Metadata.TotalArchiveTypes;

        // Detect conflicts against existing data
        var preserveIds = IsSameEdition(package.SourceEdition);

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
            // Name-based conflict detection for cross-edition
            var existingVaultNames = await _db.Vaults.Select(v => v.Name).ToListAsync(ct);
            var existingPersonNames = await _db.Persons.Select(p => p.Name).ToListAsync(ct);
            var existingLocationNames = await _db.Locations.Select(l => l.Name).ToListAsync(ct);
            var existingEventNames = await _db.Events.Select(e => e.Name).ToListAsync(ct);

            var packageVaultNames = package.Data.Vaults.Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var packagePersonNames = package.Data.Persons.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var packageLocationNames = package.Data.Locations.Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var packageEventNames = package.Data.Events.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            result.ConflictingVaults = existingVaultNames.Count(n => packageVaultNames.Contains(n));
            result.ConflictingPersons = existingPersonNames.Count(n => packagePersonNames.Contains(n));
            result.ConflictingLocations = existingLocationNames.Count(n => packageLocationNames.Contains(n));
            result.ConflictingEvents = existingEventNames.Count(n => packageEventNames.Contains(n));

            // Knowledge conflicts are GUID-only per spec
            result.ConflictingKnowledgeItems = 0;
        }

        if (result.ConflictingVaults > 0 || result.ConflictingKnowledgeItems > 0)
            result.Warnings.Add($"Found conflicts: {result.ConflictingVaults} vaults, {result.ConflictingKnowledgeItems} knowledge items.");

        result.IsValid = result.Errors.Count == 0;
        return result;
    }

    public async Task<PortableImportResult> ImportAsync(
        PortableExportPackage package,
        ImportConflictStrategy strategy = ImportConflictStrategy.Skip,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new PortableImportResult { StrategyUsed = strategy };

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

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        try
        {
            _logger.LogInformation(
                "Starting import: {Edition} source, {Strategy} strategy, preserveIds={PreserveIds}",
                package.SourceEdition, strategy, preserveIds);

            // 1. Vaults
            result.Vaults = await ImportVaultsAsync(
                package.Data.Vaults, vaultIdMap, preserveIds, strategy, tenantId, ct);

            // 2. Topics
            result.Topics = await ImportTopicsAsync(
                package.Data.Topics, topicIdMap, preserveIds, strategy, tenantId, ct);

            // 3. Tags
            result.Tags = await ImportTagsAsync(
                package.Data.Tags, tagIdMap, preserveIds, strategy, tenantId, ct);

            // 4. Persons
            result.Persons = await ImportPersonsAsync(
                package.Data.Persons, personIdMap, preserveIds, strategy, tenantId, ct);

            // 5. Locations
            result.Locations = await ImportLocationsAsync(
                package.Data.Locations, locationIdMap, preserveIds, strategy, tenantId, ct);

            // 6. Events
            result.Events = await ImportEventsAsync(
                package.Data.Events, eventIdMap, preserveIds, strategy, tenantId, ct);

            // 7. Knowledge (with junctions)
            var (knowledgeCounts, junctionCount) = await ImportKnowledgeAsync(
                package.Data.KnowledgeItems, knowledgeIdMap,
                vaultIdMap, topicIdMap, tagIdMap, personIdMap, locationIdMap, eventIdMap,
                preserveIds, strategy, tenantId, ct);
            result.KnowledgeItems = knowledgeCounts;
            result.JunctionsRestored = junctionCount;

            // 8. InboxItems
            result.InboxItems = await ImportInboxItemsAsync(
                package.Data.InboxItems, preserveIds, strategy, tenantId, ct);

            // 9. VaultPerson junctions (from PortableVault.PersonIds)
            await ImportVaultPersonsAsync(
                package.Data.Vaults, vaultIdMap, personIdMap, preserveIds, tenantId, ct);

            // 10. Comments (v2+)
            if (package.Data.Comments.Count > 0)
            {
                result.Comments = await ImportCommentsAsync(
                    package.Data.Comments, commentIdMap, knowledgeIdMap,
                    preserveIds, strategy, tenantId, ct);
            }

            // 11. FileRecords (v2+)
            if (package.Data.FileRecords.Count > 0)
            {
                result.FileRecords = await ImportFileRecordsAsync(
                    package.Data.FileRecords, fileRecordIdMap, knowledgeIdMap, commentIdMap,
                    preserveIds, strategy, tenantId, ct);
            }

            // 12. Archives (v2+)
            if (package.Data.Archives.Count > 0)
            {
                result.ArchiveRecordsStored = await ImportArchivesAsync(
                    package.Data.Archives, tenantId, ct);
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
        bool preserveIds,
        ImportConflictStrategy strategy,
        Guid tenantId,
        CancellationToken ct)
    {
        var counts = new EntityImportCounts();

        // Load existing vaults for conflict detection
        var existingById = await _db.Vaults.ToDictionaryAsync(v => v.Id, ct);
        var existingByName = existingById.Values
            .GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var pv in portableVaults)
        {
            var existing = FindExisting(pv.Id, pv.Name, preserveIds, existingById, existingByName);

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
                        existing.ParentVaultId = preserveIds ? pv.ParentVaultId : RemapNullableId(pv.ParentVaultId, idMap);
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
                var newId = preserveIds ? pv.Id : Guid.NewGuid();
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

        // Second pass: set ParentVaultId using remapped IDs
        foreach (var pv in portableVaults.Where(v => v.ParentVaultId.HasValue))
        {
            var mappedId = idMap[pv.Id];
            var mappedParentId = idMap.TryGetValue(pv.ParentVaultId!.Value, out var parentId) ? parentId : (Guid?)null;

            if (mappedParentId.HasValue && existingById.TryGetValue(mappedId, out var vault))
            {
                vault.ParentVaultId = mappedParentId;
            }
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
        CancellationToken ct)
    {
        foreach (var pv in portableVaults)
        {
            if (pv.PersonIds.Count == 0) continue;

            var mappedVaultId = vaultIdMap.TryGetValue(pv.Id, out var vid) ? vid : (preserveIds ? pv.Id : (Guid?)null);
            if (!mappedVaultId.HasValue) continue;

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
                if (existingPersonIds.Contains(mappedPersonId.Value)) continue;

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
        bool preserveIds,
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
            var existing = FindExisting(pt.Id, pt.Name, preserveIds, existingById, existingByName);

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
                var newId = preserveIds ? pt.Id : Guid.NewGuid();
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
        bool preserveIds,
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
            var existing = FindExisting(pt.Id, pt.Name, preserveIds, existingById, existingByName);

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
                var newId = preserveIds ? pt.Id : Guid.NewGuid();
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
        bool preserveIds,
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
            var existing = FindExisting(pp.Id, pp.Name, preserveIds, existingById, existingByName);

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
                var newId = preserveIds ? pp.Id : Guid.NewGuid();
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
        bool preserveIds,
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
            var existing = FindExisting(pl.Id, pl.Name, preserveIds, existingById, existingByName);

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
                var newId = preserveIds ? pl.Id : Guid.NewGuid();
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
        bool preserveIds,
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
            var existing = FindExisting(pe.Id, pe.Name, preserveIds, existingById, existingByName);

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
                var newId = preserveIds ? pe.Id : Guid.NewGuid();
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
        bool preserveIds,
        ImportConflictStrategy strategy,
        Guid tenantId,
        CancellationToken ct)
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
            var lookupId = preserveIds ? pk.Id : Guid.Empty; // Cross-edition won't match by ID
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
                        existing.IsIndexed = false;
                        existing.UpdatedAt = DateTime.UtcNow;
                        MergePlatformData(existing, pk.ExtensionData);
                        counts.Merged++;
                        break;
                }
            }
            else
            {
                var newId = preserveIds ? pk.Id : Guid.NewGuid();
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
        bool preserveIds,
        ImportConflictStrategy strategy,
        Guid tenantId,
        CancellationToken ct)
    {
        var counts = new EntityImportCounts();
        var existingById = await _db.Comments.ToDictionaryAsync(c => c.Id, ct);

        // Two-pass: first create all comments with null ParentCommentId, then set parents
        foreach (var pc in portableComments)
        {
            var lookupId = preserveIds ? pc.Id : Guid.Empty;
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
                var newId = preserveIds ? pc.Id : Guid.NewGuid();
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
                counts.Created++;
            }
        }

        await _db.SaveChangesAsync(ct);

        // Second pass: set ParentCommentId
        foreach (var pc in portableComments.Where(c => c.ParentCommentId.HasValue))
        {
            if (!commentIdMap.TryGetValue(pc.Id, out var mappedId)) continue;
            if (!commentIdMap.TryGetValue(pc.ParentCommentId!.Value, out var mappedParentId)) continue;

            if (existingById.TryGetValue(mappedId, out var comment))
            {
                comment.ParentCommentId = mappedParentId;
            }
        }

        await _db.SaveChangesAsync(ct);
        return counts;
    }

    private async Task<EntityImportCounts> ImportFileRecordsAsync(
        List<PortableFileRecord> portableFiles,
        Dictionary<Guid, Guid> fileRecordIdMap,
        Dictionary<Guid, Guid> knowledgeIdMap,
        Dictionary<Guid, Guid> commentIdMap,
        bool preserveIds,
        ImportConflictStrategy strategy,
        Guid tenantId,
        CancellationToken ct)
    {
        var counts = new EntityImportCounts();
        var existingById = await _db.FileRecords.ToDictionaryAsync(f => f.Id, ct);

        foreach (var pf in portableFiles)
        {
            var lookupId = preserveIds ? pf.Id : Guid.Empty;
            existingById.TryGetValue(lookupId, out var existing);

            if (existing != null)
            {
                fileRecordIdMap[pf.Id] = existing.Id;

                switch (strategy)
                {
                    case ImportConflictStrategy.Skip:
                        counts.Skipped++;
                        break;
                    case ImportConflictStrategy.Overwrite:
                        existing.FileName = pf.FileName;
                        existing.ContentType = pf.ContentType;
                        existing.SizeBytes = pf.SizeBytes;
                        existing.BlobUri = pf.BlobUri;
                        existing.TranscriptionText = pf.TranscriptionText;
                        existing.ExtractedText = pf.ExtractedText;
                        existing.VisionDescription = pf.VisionDescription;
                        existing.BlobMigrationPending = true;
                        existing.UpdatedAt = DateTime.UtcNow;
                        existing.PlatformData = SerializeExtensionData(pf.ExtensionData);
                        counts.Overwritten++;
                        break;
                    case ImportConflictStrategy.Merge:
                        existing.TranscriptionText ??= pf.TranscriptionText;
                        existing.ExtractedText ??= pf.ExtractedText;
                        existing.VisionDescription ??= pf.VisionDescription;
                        existing.UpdatedAt = DateTime.UtcNow;
                        MergePlatformData(existing, pf.ExtensionData);
                        counts.Merged++;
                        break;
                }
            }
            else
            {
                var newId = preserveIds ? pf.Id : Guid.NewGuid();
                fileRecordIdMap[pf.Id] = newId;

                // Determine if binary content is available for upload
                string? uploadedBlobUri = pf.BlobUri;
                bool blobMigrationPending = true;

                if (!string.IsNullOrEmpty(pf.BinaryContentBase64))
                {
                    try
                    {
                        var binaryData = Convert.FromBase64String(pf.BinaryContentBase64);
                        using var stream = new MemoryStream(binaryData);

                        uploadedBlobUri = await _storageProvider.UploadAsync(
                            tenantId, newId, stream,
                            pf.ContentType ?? "application/octet-stream", ct);

                        blobMigrationPending = false;

                        _logger.LogInformation(
                            "Uploaded binary content for file {FileRecordId} ({FileName}, {SizeBytes} bytes)",
                            newId, pf.FileName, pf.SizeBytes);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Failed to upload binary for file {FileRecordId} ({FileName}). Setting BlobMigrationPending=true.",
                            newId, pf.FileName);
                        uploadedBlobUri = pf.BlobUri;
                        blobMigrationPending = true;
                    }
                }

                var file = new FileRecord
                {
                    Id = newId,
                    TenantId = tenantId,
                    FileName = pf.FileName,
                    ContentType = pf.ContentType,
                    SizeBytes = pf.SizeBytes,
                    BlobUri = uploadedBlobUri,
                    TranscriptionText = pf.TranscriptionText,
                    ExtractedText = pf.ExtractedText,
                    VisionDescription = pf.VisionDescription,
                    BlobMigrationPending = blobMigrationPending,
                    CreatedAt = pf.CreatedAt,
                    UpdatedAt = pf.UpdatedAt,
                    PlatformData = SerializeExtensionData(pf.ExtensionData)
                };
                _db.FileRecords.Add(file);
                existingById[newId] = file;

                // Create attachment junctions
                foreach (var attachment in pf.Attachments)
                {
                    var mappedKnowledgeId = attachment.KnowledgeId.HasValue
                        ? (knowledgeIdMap.TryGetValue(attachment.KnowledgeId.Value, out var kid) ? kid : (preserveIds ? attachment.KnowledgeId : null))
                        : null;
                    var mappedCommentId = attachment.CommentId.HasValue
                        ? (commentIdMap.TryGetValue(attachment.CommentId.Value, out var cid) ? cid : (preserveIds ? attachment.CommentId : null))
                        : null;

                    _db.FileAttachments.Add(new FileAttachment
                    {
                        FileRecordId = newId,
                        KnowledgeId = mappedKnowledgeId,
                        CommentId = mappedCommentId,
                        TenantId = tenantId
                    });
                }

                counts.Created++;
            }
        }

        await _db.SaveChangesAsync(ct);
        return counts;
    }

    private async Task<int> ImportArchivesAsync(
        Dictionary<string, List<JsonElement>> archives,
        Guid tenantId,
        CancellationToken ct)
    {
        var count = 0;

        // Remove existing archives for this tenant to avoid duplicates on re-import
        var existingArchives = await _db.PortableArchives
            .Where(a => a.TenantId == tenantId)
            .ToListAsync(ct);
        _db.PortableArchives.RemoveRange(existingArchives);

        foreach (var (entityType, entries) in archives)
        {
            foreach (var entry in entries)
            {
                var originalId = Guid.Empty;
                if (entry.TryGetProperty("Id", out var idProp) || entry.TryGetProperty("id", out idProp))
                {
                    Guid.TryParse(idProp.GetString(), out originalId);
                }

                _db.PortableArchives.Add(new PortableArchive
                {
                    TenantId = tenantId,
                    EntityType = entityType,
                    OriginalId = originalId,
                    JsonData = entry.GetRawText(),
                    CreatedAt = DateTime.UtcNow
                });
                count++;
            }
        }

        await _db.SaveChangesAsync(ct);
        return count;
    }

    private async Task<EntityImportCounts> ImportInboxItemsAsync(
        List<PortableInboxItem> portableItems,
        bool preserveIds,
        ImportConflictStrategy strategy,
        Guid tenantId,
        CancellationToken ct)
    {
        var counts = new EntityImportCounts();
        var existingById = await _db.InboxItems.ToDictionaryAsync(i => i.Id, ct);

        foreach (var pi in portableItems)
        {
            var lookupId = preserveIds ? pi.Id : Guid.Empty;
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
                var newId = preserveIds ? pi.Id : Guid.NewGuid();
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

    // --- Helper methods ---

    private static bool IsSameEdition(string sourceEdition)
        => string.Equals(sourceEdition, "selfhosted", StringComparison.OrdinalIgnoreCase);

    private static T? FindExisting<T>(
        Guid id, string name, bool preserveIds,
        Dictionary<Guid, T> byId, Dictionary<string, T> byName)
    {
        if (preserveIds && byId.TryGetValue(id, out var byIdResult))
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
