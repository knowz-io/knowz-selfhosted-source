using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.Specifications;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class SpecificationTests : IDisposable
{
    private readonly SelfHostedDbContext _db;
    private readonly ISelfHostedRepository<Knowledge> _knowledgeRepo;
    private readonly ISelfHostedRepository<Tag> _tagRepo;
    private readonly ISelfHostedRepository<Person> _personRepo;
    private readonly ISelfHostedRepository<Topic> _topicRepo;
    private readonly ISelfHostedRepository<Vault> _vaultRepo;
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public SpecificationTests()
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var tenantProvider = NSubstitute.Substitute.For<ITenantProvider>();
        tenantProvider.TenantId.Returns(TenantId);
        _db = new SelfHostedDbContext(options, tenantProvider);

        _knowledgeRepo = new SelfHostedRepository<Knowledge>(_db);
        _tagRepo = new SelfHostedRepository<Tag>(_db);
        _personRepo = new SelfHostedRepository<Person>(_db);
        _topicRepo = new SelfHostedRepository<Topic>(_db);
        _vaultRepo = new SelfHostedRepository<Vault>(_db);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    [Fact]
    public async Task KnowledgeByIdWithRelationsSpec_IncludesRelations()
    {
        var tag = new Tag { TenantId = TenantId, Name = "test-tag" };
        _db.Tags.Add(tag);

        var vault = new Vault { TenantId = TenantId, Name = "test-vault" };
        _db.Vaults.Add(vault);

        var topic = new Topic { TenantId = TenantId, Name = "test-topic" };
        _db.Topics.Add(topic);

        var item = new Knowledge
        {
            TenantId = TenantId,
            Title = "With Relations",
            Content = "Content",
            TopicId = topic.Id
        };
        item.Tags.Add(tag);
        _db.KnowledgeItems.Add(item);

        _db.KnowledgeVaults.Add(new KnowledgeVault
        {
            TenantId = TenantId,
            KnowledgeId = item.Id,
            VaultId = vault.Id,
            IsPrimary = true
        });

        await _db.SaveChangesAsync();

        var spec = new KnowledgeByIdWithRelationsSpec(item.Id);
        var result = await _knowledgeRepo.FirstOrDefaultAsync(spec);

        Assert.NotNull(result);
        Assert.Single(result.Tags);
        Assert.Equal("test-tag", result.Tags.First().Name);
        Assert.Single(result.KnowledgeVaults);
        Assert.Equal("test-vault", result.KnowledgeVaults.First().Vault.Name);
        Assert.NotNull(result.Topic);
        Assert.Equal("test-topic", result.Topic.Name);
    }

    [Fact]
    public async Task TagsByNamesSpec_FiltersByNames()
    {
        _db.Tags.AddRange(
            new Tag { TenantId = TenantId, Name = "alpha" },
            new Tag { TenantId = TenantId, Name = "beta" },
            new Tag { TenantId = TenantId, Name = "gamma" },
            new Tag { TenantId = TenantId, Name = "delta" });
        await _db.SaveChangesAsync();

        var spec = new TagsByNamesSpec(new[] { "beta", "delta" });
        var result = await _tagRepo.ListAsync(spec);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, t => t.Name == "beta");
        Assert.Contains(result, t => t.Name == "delta");
    }

    [Fact]
    public async Task PersonSearchSpec_FiltersByName()
    {
        _db.Persons.AddRange(
            new Person { TenantId = TenantId, Name = "Alice Johnson" },
            new Person { TenantId = TenantId, Name = "Bob Smith" },
            new Person { TenantId = TenantId, Name = "Alice Williams" });
        await _db.SaveChangesAsync();

        var spec = new PersonSearchSpec("Alice", 100);
        var result = await _personRepo.ListAsync(spec);

        Assert.Equal(2, result.Count);
        Assert.All(result, p => Assert.Contains("Alice", p.Name));
    }

    [Fact]
    public async Task TopicListSpec_OrdersAndLimits()
    {
        _db.Topics.AddRange(
            new Topic { TenantId = TenantId, Name = "Zebra" },
            new Topic { TenantId = TenantId, Name = "Apple" },
            new Topic { TenantId = TenantId, Name = "Mango" },
            new Topic { TenantId = TenantId, Name = "Banana" });
        await _db.SaveChangesAsync();

        var spec = new TopicListSpec(2);
        var result = await _topicRepo.ListAsync(spec);

        Assert.Equal(2, result.Count);
        Assert.Equal("Apple", result[0].Name);
        Assert.Equal("Banana", result[1].Name);
    }

    [Fact]
    public async Task KnowledgeByIdsSpec_FiltersByIds()
    {
        var k1 = new Knowledge { TenantId = TenantId, Title = "First", Content = "C1" };
        var k2 = new Knowledge { TenantId = TenantId, Title = "Second", Content = "C2" };
        var k3 = new Knowledge { TenantId = TenantId, Title = "Third", Content = "C3" };
        _db.KnowledgeItems.AddRange(k1, k2, k3);
        await _db.SaveChangesAsync();

        var spec = new KnowledgeByIdsSpec(new[] { k1.Id, k3.Id });
        var result = await _knowledgeRepo.ListAsync(spec);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, k => k.Title == "First");
        Assert.Contains(result, k => k.Title == "Third");
        Assert.DoesNotContain(result, k => k.Title == "Second");
    }

    [Fact]
    public async Task VaultListSpec_OrdersByName()
    {
        _db.Vaults.AddRange(
            new Vault { TenantId = TenantId, Name = "Zeta" },
            new Vault { TenantId = TenantId, Name = "Alpha" },
            new Vault { TenantId = TenantId, Name = "Kappa" });
        await _db.SaveChangesAsync();

        var spec = new VaultListSpec();
        var result = await _vaultRepo.ListAsync(spec);

        Assert.Equal(3, result.Count);
        Assert.Equal("Alpha", result[0].Name);
        Assert.Equal("Kappa", result[1].Name);
        Assert.Equal("Zeta", result[2].Name);
    }
}
