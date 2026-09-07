using Knowz.Core.Entities;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.Specifications;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Application.Services;

/// <summary>
/// Service for finding and managing entities (persons, locations, events).
/// Uses ISelfHostedRepository with specs for each entity type.
/// </summary>
public class EntityService
{
    private readonly ISelfHostedRepository<Person> _personRepo;
    private readonly ISelfHostedRepository<Location> _locationRepo;
    private readonly ISelfHostedRepository<Event> _eventRepo;
    private readonly ITenantProvider _tenantProvider;
    private readonly ILogger<EntityService> _logger;

    public EntityService(
        ISelfHostedRepository<Person> personRepo,
        ISelfHostedRepository<Location> locationRepo,
        ISelfHostedRepository<Event> eventRepo,
        ITenantProvider tenantProvider,
        ILogger<EntityService> logger)
    {
        _personRepo = personRepo;
        _locationRepo = locationRepo;
        _eventRepo = eventRepo;
        _tenantProvider = tenantProvider;
        _logger = logger;
    }

    public async Task<EntitySearchResponse> FindEntitiesAsync(string entityType, string? query, int limit, CancellationToken ct)
    {
        var items = entityType.ToLowerInvariant() switch
        {
            "person" => await FindPersonsAsync(query, limit, ct),
            "location" => await FindLocationsAsync(query, limit, ct),
            "event" => await FindEventsAsync(query, limit, ct),
            _ => throw new ArgumentException($"Unknown entity type: {entityType}. Use 'person', 'location', or 'event'.")
        };

        return new EntitySearchResponse(entityType, items);
    }

    public async Task<EntityItem> CreateEntityAsync(string entityType, string name, CancellationToken ct)
    {
        var tenantId = _tenantProvider.TenantId;

        return entityType.ToLowerInvariant() switch
        {
            "person" => await CreatePersonAsync(name, tenantId, ct),
            "location" => await CreateLocationAsync(name, tenantId, ct),
            "event" => await CreateEventAsync(name, tenantId, ct),
            _ => throw new ArgumentException($"Unknown entity type: {entityType}. Use 'person', 'location', or 'event'.")
        };
    }

    public async Task<EntityItem?> UpdateEntityAsync(string entityType, Guid id, string name, CancellationToken ct)
    {
        return entityType.ToLowerInvariant() switch
        {
            "person" => await UpdatePersonAsync(id, name, ct),
            "location" => await UpdateLocationAsync(id, name, ct),
            "event" => await UpdateEventAsync(id, name, ct),
            _ => throw new ArgumentException($"Unknown entity type: {entityType}. Use 'person', 'location', or 'event'.")
        };
    }

    public async Task<bool> DeleteEntityAsync(string entityType, Guid id, CancellationToken ct)
    {
        return entityType.ToLowerInvariant() switch
        {
            "person" => await DeletePersonAsync(id, ct),
            "location" => await DeleteLocationAsync(id, ct),
            "event" => await DeleteEventAsync(id, ct),
            _ => throw new ArgumentException($"Unknown entity type: {entityType}. Use 'person', 'location', or 'event'.")
        };
    }

    // --- Find ---

    private async Task<List<EntityItem>> FindPersonsAsync(string? query, int limit, CancellationToken ct)
    {
        var persons = await _personRepo.ListAsync(new PersonSearchSpec(query, limit), ct);
        return persons.Select(p => new EntityItem(p.Id, p.Name, p.CreatedAt)).ToList();
    }

    private async Task<List<EntityItem>> FindLocationsAsync(string? query, int limit, CancellationToken ct)
    {
        var locations = await _locationRepo.ListAsync(new LocationSearchSpec(query, limit), ct);
        return locations.Select(l => new EntityItem(l.Id, l.Name, l.CreatedAt)).ToList();
    }

    private async Task<List<EntityItem>> FindEventsAsync(string? query, int limit, CancellationToken ct)
    {
        var events = await _eventRepo.ListAsync(new EventSearchSpec(query, limit), ct);
        return events.Select(e => new EntityItem(e.Id, e.Name, e.CreatedAt)).ToList();
    }

    // --- Create ---

    private async Task<EntityItem> CreatePersonAsync(string name, Guid tenantId, CancellationToken ct)
    {
        var entity = new Person { TenantId = tenantId, Name = name };
        await _personRepo.AddAsync(entity, ct);
        await _personRepo.SaveChangesAsync(ct);
        _logger.LogInformation("Created person: {Name} ({Id})", name, entity.Id);
        return new EntityItem(entity.Id, entity.Name, entity.CreatedAt);
    }

    private async Task<EntityItem> CreateLocationAsync(string name, Guid tenantId, CancellationToken ct)
    {
        var entity = new Location { TenantId = tenantId, Name = name };
        await _locationRepo.AddAsync(entity, ct);
        await _locationRepo.SaveChangesAsync(ct);
        _logger.LogInformation("Created location: {Name} ({Id})", name, entity.Id);
        return new EntityItem(entity.Id, entity.Name, entity.CreatedAt);
    }

    private async Task<EntityItem> CreateEventAsync(string name, Guid tenantId, CancellationToken ct)
    {
        var entity = new Event { TenantId = tenantId, Name = name };
        await _eventRepo.AddAsync(entity, ct);
        await _eventRepo.SaveChangesAsync(ct);
        _logger.LogInformation("Created event: {Name} ({Id})", name, entity.Id);
        return new EntityItem(entity.Id, entity.Name, entity.CreatedAt);
    }

    // --- Update ---

    private async Task<EntityItem?> UpdatePersonAsync(Guid id, string name, CancellationToken ct)
    {
        var entity = await _personRepo.GetByIdAsync(id, ct);
        if (entity is null) return null;
        entity.Name = name;
        entity.UpdatedAt = DateTime.UtcNow;
        await _personRepo.UpdateAsync(entity, ct);
        await _personRepo.SaveChangesAsync(ct);
        _logger.LogInformation("Updated person: {Id} -> {Name}", id, name);
        return new EntityItem(entity.Id, entity.Name, entity.CreatedAt);
    }

    private async Task<EntityItem?> UpdateLocationAsync(Guid id, string name, CancellationToken ct)
    {
        var entity = await _locationRepo.GetByIdAsync(id, ct);
        if (entity is null) return null;
        entity.Name = name;
        entity.UpdatedAt = DateTime.UtcNow;
        await _locationRepo.UpdateAsync(entity, ct);
        await _locationRepo.SaveChangesAsync(ct);
        _logger.LogInformation("Updated location: {Id} -> {Name}", id, name);
        return new EntityItem(entity.Id, entity.Name, entity.CreatedAt);
    }

    private async Task<EntityItem?> UpdateEventAsync(Guid id, string name, CancellationToken ct)
    {
        var entity = await _eventRepo.GetByIdAsync(id, ct);
        if (entity is null) return null;
        entity.Name = name;
        entity.UpdatedAt = DateTime.UtcNow;
        await _eventRepo.UpdateAsync(entity, ct);
        await _eventRepo.SaveChangesAsync(ct);
        _logger.LogInformation("Updated event: {Id} -> {Name}", id, name);
        return new EntityItem(entity.Id, entity.Name, entity.CreatedAt);
    }

    // --- Delete ---

    private async Task<bool> DeletePersonAsync(Guid id, CancellationToken ct)
    {
        var entity = await _personRepo.GetByIdAsync(id, ct);
        if (entity is null) return false;
        await _personRepo.SoftDeleteAsync(entity, ct);
        await _personRepo.SaveChangesAsync(ct);
        _logger.LogInformation("Deleted person: {Id}", id);
        return true;
    }

    private async Task<bool> DeleteLocationAsync(Guid id, CancellationToken ct)
    {
        var entity = await _locationRepo.GetByIdAsync(id, ct);
        if (entity is null) return false;
        await _locationRepo.SoftDeleteAsync(entity, ct);
        await _locationRepo.SaveChangesAsync(ct);
        _logger.LogInformation("Deleted location: {Id}", id);
        return true;
    }

    private async Task<bool> DeleteEventAsync(Guid id, CancellationToken ct)
    {
        var entity = await _eventRepo.GetByIdAsync(id, ct);
        if (entity is null) return false;
        await _eventRepo.SoftDeleteAsync(entity, ct);
        await _eventRepo.SaveChangesAsync(ct);
        _logger.LogInformation("Deleted event: {Id}", id);
        return true;
    }
}
