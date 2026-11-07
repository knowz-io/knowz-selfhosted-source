using System.Text.Json;
using Knowz.Core.Entities;
using Knowz.Core.Portability;

namespace Knowz.SelfHosted.Application.Services;

// The public junction model stores identity only for locations/events. Keep their
// typed portable metadata beside the item, and never use it to establish ownership.
internal static class PortableKnowledgeLinks
{
    private const string LocationKey = "$knowz.portable.locationLinks";
    private const string EventKey = "$knowz.portable.eventLinks";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Dictionary<string, JsonElement>? PublicExtensions(Dictionary<string, JsonElement>? values)
        => values?.Where(p => p.Key != LocationKey && p.Key != EventKey).ToDictionary(p => p.Key, p => p.Value);

    public static void Store(Knowledge row, PortableKnowledge source, IReadOnlyDictionary<Guid, Guid> locations,
        IReadOnlyDictionary<Guid, Guid> events, bool merge)
    {
        var values = Read(row.PlatformData);
        Save(LocationKey, source.LocationLinks, source.LocationIds, locations);
        Save(EventKey, source.EventLinks, source.EventIds, events);
        row.PlatformData = values.Count == 0 ? null : JsonSerializer.Serialize(values, Json);

        void Save(string key, List<PortableEntityLink>? links, List<Guid> ids, IReadOnlyDictionary<Guid, Guid> mapping)
        {
            var retained = merge ? ReadLinks(values, key) : [];
            var seen = retained.Select(l => l.EntityId).ToHashSet();
            var sourceLinks = (links ?? []).Concat(ids.Where(id => !(links ?? []).Any(l => l.EntityId == id))
                .Select(id => new PortableEntityLink { EntityId = id }));
            foreach (var link in sourceLinks)
            {
                if (!mapping.TryGetValue(link.EntityId, out var destination))
                    throw new InvalidDataException("A portable relationship target is unavailable.");
                if (!seen.Add(destination)) continue;
                retained.Add(new() { EntityId = destination, RelationshipContext = link.RelationshipContext,
                    Role = link.Role, Mentions = link.Mentions, ConfidenceScore = link.ConfidenceScore });
            }
            if (retained.Count == 0) values.Remove(key);
            else values[key] = JsonSerializer.SerializeToElement(retained, Json);
        }
    }

    public static void Project(Knowledge row, PortableKnowledge dto, IReadOnlySet<Guid> availableLocations,
        IReadOnlySet<Guid> availableEvents)
    {
        var values = Read(row.PlatformData);
        dto.LocationLinks = ProjectLinks(LocationKey, dto.LocationIds, availableLocations);
        dto.EventLinks = ProjectLinks(EventKey, dto.EventIds, availableEvents);
        dto.ExtensionData = PublicExtensions(dto.ExtensionData);
        dto.Relationships ??= [];

        List<PortableEntityLink> ProjectLinks(string key, List<Guid> liveIds, IReadOnlySet<Guid> available)
        {
            var live = liveIds.ToHashSet(); var metadata = ReadLinks(values, key);
            if (live.Any(id => !available.Contains(id)) || metadata.Any(l => !live.Contains(l.EntityId))
                || metadata.Select(l => l.EntityId).Distinct().Count() != metadata.Count)
                throw new InvalidDataException("Portable relationship metadata no longer matches available tenant relationships. Refresh or repair the item before exporting.");
            var byId = metadata.ToDictionary(l => l.EntityId);
            return liveIds.Select(id => byId.GetValueOrDefault(id) ?? new PortableEntityLink { EntityId = id }).ToList();
        }
    }

    private static Dictionary<string, JsonElement> Read(string? json)
        => string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, Json)
            ?? throw new InvalidDataException("Portable relationship metadata is invalid.");

    private static List<PortableEntityLink> ReadLinks(Dictionary<string, JsonElement> values, string key)
    {
        if (!values.TryGetValue(key, out var value)) return [];
        try
        {
            var links = value.Deserialize<List<PortableEntityLink>>(Json);
            if (links == null || links.Any(l => l == null || l.EntityId == Guid.Empty))
                throw new InvalidDataException("Portable relationship metadata is invalid.");
            return links;
        }
        catch (JsonException ex) { throw new InvalidDataException("Portable relationship metadata is invalid.", ex); }
    }
}
