using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class PlatformDataPropertyTests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public PlatformDataPropertyTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var tenantProvider = Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(options, tenantProvider);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    #region ISelfHostedEntity Interface

    [Fact]
    public void ISelfHostedEntity_HasPlatformDataProperty()
    {
        var property = typeof(ISelfHostedEntity).GetProperty("PlatformData");
        Assert.NotNull(property);
        Assert.Equal(typeof(string), property.PropertyType);
    }

    #endregion

    #region All 8 entities implement PlatformData

    [Fact]
    public void Knowledge_HasPlatformData()
    {
        var entity = new Knowledge { TenantId = TenantId, Title = "Test", Content = "Content" };
        Assert.Null(entity.PlatformData);
        entity.PlatformData = """{"ContentHash":"abc"}""";
        Assert.Equal("""{"ContentHash":"abc"}""", entity.PlatformData);
    }

    [Fact]
    public void Vault_HasPlatformData()
    {
        var entity = new Vault { TenantId = TenantId, Name = "Test" };
        Assert.Null(entity.PlatformData);
        entity.PlatformData = """{"Settings":"{}"}""";
        Assert.Equal("""{"Settings":"{}"}""", entity.PlatformData);
    }

    [Fact]
    public void Person_HasPlatformData()
    {
        var entity = new Person { TenantId = TenantId, Name = "Test" };
        Assert.Null(entity.PlatformData);
        entity.PlatformData = """{"Email":"test@test.com"}""";
        Assert.Equal("""{"Email":"test@test.com"}""", entity.PlatformData);
    }

    [Fact]
    public void Location_HasPlatformData()
    {
        var entity = new Location { TenantId = TenantId, Name = "Test" };
        Assert.Null(entity.PlatformData);
        entity.PlatformData = """{"Latitude":51.5}""";
        Assert.Equal("""{"Latitude":51.5}""", entity.PlatformData);
    }

    [Fact]
    public void Event_HasPlatformData()
    {
        var entity = new Event { TenantId = TenantId, Name = "Test" };
        Assert.Null(entity.PlatformData);
        entity.PlatformData = """{"StartDate":"2026-01-01"}""";
        Assert.Equal("""{"StartDate":"2026-01-01"}""", entity.PlatformData);
    }

    [Fact]
    public void Topic_HasPlatformData()
    {
        var entity = new Topic { TenantId = TenantId, Name = "Test" };
        Assert.Null(entity.PlatformData);
        entity.PlatformData = """{"Custom":"data"}""";
        Assert.Equal("""{"Custom":"data"}""", entity.PlatformData);
    }

    [Fact]
    public void Tag_HasPlatformData()
    {
        var entity = new Tag { TenantId = TenantId, Name = "Test" };
        Assert.Null(entity.PlatformData);
        entity.PlatformData = """{"Color":"blue"}""";
        Assert.Equal("""{"Color":"blue"}""", entity.PlatformData);
    }

    [Fact]
    public void InboxItem_HasPlatformData()
    {
        var entity = new InboxItem { TenantId = TenantId, Body = "Test" };
        Assert.Null(entity.PlatformData);
        entity.PlatformData = """{"Priority":"High"}""";
        Assert.Equal("""{"Priority":"High"}""", entity.PlatformData);
    }

    #endregion

    #region PlatformData persists through DbContext

    [Fact]
    public async Task Knowledge_PlatformData_PersistsToDatabase()
    {
        var entity = new Knowledge
        {
            TenantId = TenantId,
            Title = "Test",
            Content = "Content",
            PlatformData = """{"ContentHash":"abc123","SensitivityLevel":3}"""
        };
        _db.KnowledgeItems.Add(entity);
        await _db.SaveChangesAsync();

        var saved = await _db.KnowledgeItems.FindAsync(entity.Id);
        Assert.NotNull(saved);
        Assert.Equal("""{"ContentHash":"abc123","SensitivityLevel":3}""", saved.PlatformData);
    }

    [Fact]
    public async Task Vault_PlatformData_PersistsToDatabase()
    {
        var entity = new Vault
        {
            TenantId = TenantId,
            Name = "Test Vault",
            PlatformData = """{"Settings":{"autoSync":true}}"""
        };
        _db.Vaults.Add(entity);
        await _db.SaveChangesAsync();

        var saved = await _db.Vaults.FindAsync(entity.Id);
        Assert.NotNull(saved);
        Assert.Equal("""{"Settings":{"autoSync":true}}""", saved.PlatformData);
    }

    [Fact]
    public async Task PlatformData_CanBeNull()
    {
        var entity = new Knowledge
        {
            TenantId = TenantId,
            Title = "No Platform Data",
            Content = "Content",
            PlatformData = null
        };
        _db.KnowledgeItems.Add(entity);
        await _db.SaveChangesAsync();

        var saved = await _db.KnowledgeItems.FindAsync(entity.Id);
        Assert.NotNull(saved);
        Assert.Null(saved.PlatformData);
    }

    #endregion

    #region All entities implement ISelfHostedEntity (including PlatformData)

    [Fact]
    public void AllCoreEntities_ImplementISelfHostedEntity_WithPlatformData()
    {
        // Verify all 8 ISelfHostedEntity implementations have PlatformData
        var entityTypes = new Type[]
        {
            typeof(Knowledge),
            typeof(Vault),
            typeof(Person),
            typeof(Location),
            typeof(Event),
            typeof(Topic),
            typeof(Tag),
            typeof(InboxItem)
        };

        foreach (var type in entityTypes)
        {
            Assert.True(typeof(ISelfHostedEntity).IsAssignableFrom(type),
                $"{type.Name} should implement ISelfHostedEntity");

            var platformDataProp = type.GetProperty("PlatformData");
            Assert.NotNull(platformDataProp);
            Assert.Equal(typeof(string), platformDataProp.PropertyType);
        }
    }

    #endregion
}
