using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class EntityServiceTests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private readonly EntityService _svc;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public EntityServiceTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(options, tenantProvider);

        var personRepo = new SelfHostedRepository<Person>(_db);
        var locationRepo = new SelfHostedRepository<Location>(_db);
        var eventRepo = new SelfHostedRepository<Event>(_db);
        var logger = Substitute.For<ILogger<EntityService>>();

        _svc = new EntityService(personRepo, locationRepo, eventRepo, tenantProvider, logger);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    [Fact]
    public async Task FindEntitiesAsync_Person_ReturnsResults()
    {
        _db.Persons.AddRange(
            new Person { TenantId = TenantId, Name = "Alice" },
            new Person { TenantId = TenantId, Name = "Bob" });
        await _db.SaveChangesAsync();

        var result = await _svc.FindEntitiesAsync("person", null, 100, CancellationToken.None);

        Assert.IsType<EntitySearchResponse>(result);
        Assert.Equal("person", result.EntityType);
        Assert.Equal(2, result.Entities.Count);
    }

    [Fact]
    public async Task FindEntitiesAsync_Location_ReturnsResults()
    {
        _db.Locations.AddRange(
            new Location { TenantId = TenantId, Name = "London" },
            new Location { TenantId = TenantId, Name = "Paris" });
        await _db.SaveChangesAsync();

        var result = await _svc.FindEntitiesAsync("location", null, 100, CancellationToken.None);

        Assert.Equal("location", result.EntityType);
        Assert.Equal(2, result.Entities.Count);
    }

    [Fact]
    public async Task FindEntitiesAsync_Event_ReturnsResults()
    {
        _db.Events.Add(new Event { TenantId = TenantId, Name = "Conference 2026" });
        await _db.SaveChangesAsync();

        var result = await _svc.FindEntitiesAsync("event", null, 100, CancellationToken.None);

        Assert.Equal("event", result.EntityType);
        Assert.Single(result.Entities);
        Assert.Equal("Conference 2026", result.Entities[0].Name);
    }

    [Fact]
    public async Task FindEntitiesAsync_UnknownType_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _svc.FindEntitiesAsync("unknown", null, 100, CancellationToken.None));
    }

    [Fact]
    public async Task FindEntitiesAsync_WithQuery_FiltersResults()
    {
        _db.Persons.AddRange(
            new Person { TenantId = TenantId, Name = "Alice Johnson" },
            new Person { TenantId = TenantId, Name = "Bob Smith" },
            new Person { TenantId = TenantId, Name = "Alice Williams" });
        await _db.SaveChangesAsync();

        var result = await _svc.FindEntitiesAsync("person", "Alice", 100, CancellationToken.None);

        Assert.Equal(2, result.Entities.Count);
        Assert.All(result.Entities, e => Assert.Contains("Alice", e.Name));
    }
}
