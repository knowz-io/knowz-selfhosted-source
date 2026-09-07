using FluentAssertions;
using Knowz.Core.Configuration;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Knowz.MCP.Tests.Data;

public class McpDbContextTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherTenantId = Guid.Parse("00000000-0000-0000-0000-000000000099");

    private readonly SqliteConnection _connection;
    private readonly SelfHostedDbContext _context;

    public McpDbContextTests()
    {
        // Keep connection open so SQLite in-memory DB persists for the test
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = CreateContext(TestTenantId, _connection);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Close();
        _connection.Dispose();
    }

    private static SelfHostedDbContext CreateContext(Guid tenantId, SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseSqlite(connection)
            .Options;

        var tenantProvider = new TestTenantProvider(tenantId);
        return new SelfHostedDbContext(options, tenantProvider);
    }

    #region Query Filter Tests

    [Fact]
    public async Task QueryFilter_ScopesToTenant()
    {
        _context.KnowledgeItems.Add(new Knowledge { TenantId = TestTenantId, Title = "Mine" });
        _context.KnowledgeItems.Add(new Knowledge { TenantId = OtherTenantId, Title = "Other" });
        await _context.SaveChangesAsync();

        var items = await _context.KnowledgeItems.ToListAsync();

        items.Should().HaveCount(1);
        items[0].Title.Should().Be("Mine");
    }

    [Fact]
    public async Task QueryFilter_ExcludesDeleted()
    {
        _context.KnowledgeItems.Add(new Knowledge { TenantId = TestTenantId, Title = "Active" });
        _context.KnowledgeItems.Add(new Knowledge { TenantId = TestTenantId, Title = "Deleted", IsDeleted = true });
        await _context.SaveChangesAsync();

        var items = await _context.KnowledgeItems.ToListAsync();

        items.Should().HaveCount(1);
        items[0].Title.Should().Be("Active");
    }

    [Fact]
    public async Task QueryFilter_IgnoreQueryFilters_ReturnsAll()
    {
        _context.KnowledgeItems.Add(new Knowledge { TenantId = TestTenantId, Title = "Active" });
        _context.KnowledgeItems.Add(new Knowledge { TenantId = TestTenantId, Title = "Deleted", IsDeleted = true });
        _context.KnowledgeItems.Add(new Knowledge { TenantId = OtherTenantId, Title = "Other" });
        await _context.SaveChangesAsync();

        var items = await _context.KnowledgeItems.IgnoreQueryFilters().ToListAsync();

        items.Should().HaveCount(3);
    }

    [Fact]
    public async Task QueryFilter_AppliesToVaults()
    {
        _context.Vaults.Add(new Vault { TenantId = TestTenantId, Name = "My Vault" });
        _context.Vaults.Add(new Vault { TenantId = OtherTenantId, Name = "Other Vault" });
        _context.Vaults.Add(new Vault { TenantId = TestTenantId, Name = "Deleted Vault", IsDeleted = true });
        await _context.SaveChangesAsync();

        var vaults = await _context.Vaults.ToListAsync();

        vaults.Should().HaveCount(1);
        vaults[0].Name.Should().Be("My Vault");
    }

    [Fact]
    public async Task QueryFilter_AppliesToTopics()
    {
        _context.Topics.Add(new Topic { TenantId = TestTenantId, Name = "My Topic" });
        _context.Topics.Add(new Topic { TenantId = OtherTenantId, Name = "Other Topic" });
        await _context.SaveChangesAsync();

        var topics = await _context.Topics.ToListAsync();

        topics.Should().HaveCount(1);
        topics[0].Name.Should().Be("My Topic");
    }

    [Fact]
    public async Task QueryFilter_AppliesToTags()
    {
        _context.Tags.Add(new Tag { TenantId = TestTenantId, Name = "my-tag" });
        _context.Tags.Add(new Tag { TenantId = OtherTenantId, Name = "other-tag" });
        await _context.SaveChangesAsync();

        var tags = await _context.Tags.ToListAsync();

        tags.Should().HaveCount(1);
        tags[0].Name.Should().Be("my-tag");
    }

    [Fact]
    public async Task QueryFilter_AppliesToPersons()
    {
        _context.Persons.Add(new Person { TenantId = TestTenantId, Name = "Alice" });
        _context.Persons.Add(new Person { TenantId = OtherTenantId, Name = "Bob" });
        await _context.SaveChangesAsync();

        var persons = await _context.Persons.ToListAsync();

        persons.Should().HaveCount(1);
        persons[0].Name.Should().Be("Alice");
    }

    [Fact]
    public async Task QueryFilter_AppliesToLocations()
    {
        _context.Locations.Add(new Location { TenantId = TestTenantId, Name = "New York" });
        _context.Locations.Add(new Location { TenantId = OtherTenantId, Name = "London" });
        await _context.SaveChangesAsync();

        var locations = await _context.Locations.ToListAsync();

        locations.Should().HaveCount(1);
        locations[0].Name.Should().Be("New York");
    }

    [Fact]
    public async Task QueryFilter_AppliesToEvents()
    {
        _context.Events.Add(new Event { TenantId = TestTenantId, Name = "Conference" });
        _context.Events.Add(new Event { TenantId = OtherTenantId, Name = "Workshop" });
        await _context.SaveChangesAsync();

        var events = await _context.Events.ToListAsync();

        events.Should().HaveCount(1);
        events[0].Name.Should().Be("Conference");
    }

    [Fact]
    public async Task QueryFilter_AppliesToInboxItems()
    {
        _context.InboxItems.Add(new InboxItem { TenantId = TestTenantId, Body = "My item" });
        _context.InboxItems.Add(new InboxItem { TenantId = OtherTenantId, Body = "Other item" });
        await _context.SaveChangesAsync();

        var items = await _context.InboxItems.ToListAsync();

        items.Should().HaveCount(1);
        items[0].Body.Should().Be("My item");
    }

    #endregion

    #region Default Values Tests

    [Fact]
    public void Knowledge_DefaultValues_AreCorrect()
    {
        var entity = new Knowledge();
        entity.Id.Should().NotBe(Guid.Empty);
        entity.Title.Should().BeEmpty();
        entity.Content.Should().BeEmpty();
        entity.Type.Should().Be(KnowledgeType.Note);
        entity.IsDeleted.Should().BeFalse();
        entity.IsIndexed.Should().BeFalse();
        entity.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        entity.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        entity.Tags.Should().BeEmpty();
        entity.KnowledgeVaults.Should().BeEmpty();
        entity.KnowledgePersons.Should().BeEmpty();
        entity.KnowledgeLocations.Should().BeEmpty();
        entity.KnowledgeEvents.Should().BeEmpty();
    }

    [Fact]
    public void Vault_DefaultValues_AreCorrect()
    {
        var entity = new Vault();
        entity.Id.Should().NotBe(Guid.Empty);
        entity.Name.Should().BeEmpty();
        entity.IsDefault.Should().BeFalse();
        entity.IsDeleted.Should().BeFalse();
        entity.Children.Should().BeEmpty();
        entity.KnowledgeVaults.Should().BeEmpty();
        entity.Ancestors.Should().BeEmpty();
        entity.Descendants.Should().BeEmpty();
    }

    [Fact]
    public void InboxItem_DefaultValues_AreCorrect()
    {
        var entity = new InboxItem();
        entity.Id.Should().NotBe(Guid.Empty);
        entity.Body.Should().BeEmpty();
        entity.IsDeleted.Should().BeFalse();
        entity.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    #endregion

    #region Composite Key Tests

    [Fact]
    public async Task KnowledgeVault_CompositeKey_PreventsDuplicates()
    {
        var knowledge = new Knowledge { TenantId = TestTenantId, Title = "Test" };
        var vault = new Vault { TenantId = TestTenantId, Name = "Vault" };
        _context.KnowledgeItems.Add(knowledge);
        _context.Vaults.Add(vault);
        await _context.SaveChangesAsync();

        _context.KnowledgeVaults.Add(new KnowledgeVault
        {
            TenantId = TestTenantId,
            KnowledgeId = knowledge.Id,
            VaultId = vault.Id,
            IsPrimary = true
        });
        await _context.SaveChangesAsync();

        // Adding duplicate should throw - the change tracker detects the conflict
        // before it even gets to the database
        var act = () =>
        {
            _context.KnowledgeVaults.Add(new KnowledgeVault
            {
                TenantId = TestTenantId,
                KnowledgeId = knowledge.Id,
                VaultId = vault.Id,
                IsPrimary = false
            });
            return _context.SaveChangesAsync();
        };

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task KnowledgePerson_CompositeKey_Works()
    {
        var knowledge = new Knowledge { TenantId = TestTenantId, Title = "Test" };
        var person = new Person { TenantId = TestTenantId, Name = "Alice" };
        _context.KnowledgeItems.Add(knowledge);
        _context.Persons.Add(person);
        await _context.SaveChangesAsync();

        _context.KnowledgePersons.Add(new KnowledgePerson
        {
            KnowledgeId = knowledge.Id,
            PersonId = person.Id
        });
        await _context.SaveChangesAsync();

        var link = await _context.KnowledgePersons.FirstOrDefaultAsync();
        link.Should().NotBeNull();
        link!.KnowledgeId.Should().Be(knowledge.Id);
        link.PersonId.Should().Be(person.Id);
    }

    [Fact]
    public async Task VaultAncestor_CompositeKey_Works()
    {
        var parent = new Vault { TenantId = TestTenantId, Name = "Parent" };
        var child = new Vault { TenantId = TestTenantId, Name = "Child", ParentVaultId = parent.Id };
        _context.Vaults.Add(parent);
        _context.Vaults.Add(child);
        await _context.SaveChangesAsync();

        _context.VaultAncestors.Add(new VaultAncestor
        {
            AncestorVaultId = parent.Id,
            DescendantVaultId = child.Id,
            Depth = 1
        });
        await _context.SaveChangesAsync();

        var ancestor = await _context.VaultAncestors.FirstOrDefaultAsync();
        ancestor.Should().NotBeNull();
        ancestor!.Depth.Should().Be(1);
    }

    #endregion

    #region Vault Hierarchy Tests

    [Fact]
    public async Task VaultHierarchy_SelfReference_Works()
    {
        var parent = new Vault { TenantId = TestTenantId, Name = "Parent" };
        _context.Vaults.Add(parent);
        await _context.SaveChangesAsync();

        var child = new Vault { TenantId = TestTenantId, Name = "Child", ParentVaultId = parent.Id };
        _context.Vaults.Add(child);
        await _context.SaveChangesAsync();

        var parentWithChildren = await _context.Vaults
            .Include(v => v.Children)
            .FirstAsync(v => v.Id == parent.Id);

        parentWithChildren.Children.Should().HaveCount(1);
        parentWithChildren.Children.First().Name.Should().Be("Child");
    }

    [Fact]
    public async Task VaultAncestor_ClosureTable_QueriesDescendants()
    {
        var root = new Vault { TenantId = TestTenantId, Name = "Root" };
        var a = new Vault { TenantId = TestTenantId, Name = "A" };
        var b = new Vault { TenantId = TestTenantId, Name = "B" };
        _context.Vaults.AddRange(root, a, b);
        await _context.SaveChangesAsync();

        _context.VaultAncestors.AddRange(
            new VaultAncestor { AncestorVaultId = root.Id, DescendantVaultId = a.Id, Depth = 1 },
            new VaultAncestor { AncestorVaultId = root.Id, DescendantVaultId = b.Id, Depth = 2 },
            new VaultAncestor { AncestorVaultId = a.Id, DescendantVaultId = b.Id, Depth = 1 }
        );
        await _context.SaveChangesAsync();

        var descendantIds = await _context.VaultAncestors
            .Where(va => va.AncestorVaultId == root.Id)
            .Select(va => va.DescendantVaultId)
            .ToListAsync();

        descendantIds.Should().HaveCount(2);
        descendantIds.Should().Contain(a.Id);
        descendantIds.Should().Contain(b.Id);
    }

    #endregion

    #region Tag Many-to-Many Tests

    [Fact]
    public async Task Tag_ManyToMany_Works()
    {
        var tag = new Tag { TenantId = TestTenantId, Name = "important" };
        var knowledge = new Knowledge { TenantId = TestTenantId, Title = "Test" };
        knowledge.Tags.Add(tag);
        _context.KnowledgeItems.Add(knowledge);
        await _context.SaveChangesAsync();

        var result = await _context.KnowledgeItems
            .Include(k => k.Tags)
            .FirstAsync(k => k.Id == knowledge.Id);

        result.Tags.Should().HaveCount(1);
        result.Tags.First().Name.Should().Be("important");
    }

    [Fact]
    public async Task Tag_ManyToMany_MultipleTagsOnKnowledge()
    {
        var tag1 = new Tag { TenantId = TestTenantId, Name = "tag-a" };
        var tag2 = new Tag { TenantId = TestTenantId, Name = "tag-b" };
        var knowledge = new Knowledge { TenantId = TestTenantId, Title = "Multi-tagged" };
        knowledge.Tags.Add(tag1);
        knowledge.Tags.Add(tag2);
        _context.KnowledgeItems.Add(knowledge);
        await _context.SaveChangesAsync();

        var result = await _context.KnowledgeItems
            .Include(k => k.Tags)
            .FirstAsync(k => k.Id == knowledge.Id);

        result.Tags.Should().HaveCount(2);
        result.Tags.Select(t => t.Name).Should().Contain("tag-a");
        result.Tags.Select(t => t.Name).Should().Contain("tag-b");
    }

    #endregion

    #region CRUD Tests

    [Fact]
    public async Task CanCreateAndQueryKnowledge()
    {
        var knowledge = new Knowledge
        {
            TenantId = TestTenantId,
            Title = "Test Knowledge",
            Content = "Some content",
            Type = KnowledgeType.Document,
            Source = "test"
        };
        _context.KnowledgeItems.Add(knowledge);
        await _context.SaveChangesAsync();

        var result = await _context.KnowledgeItems.FirstOrDefaultAsync(k => k.Title == "Test Knowledge");

        result.Should().NotBeNull();
        result!.Content.Should().Be("Some content");
        result.Type.Should().Be(KnowledgeType.Document);
        result.Source.Should().Be("test");
    }

    [Fact]
    public async Task CanCreateVaultWithKnowledge()
    {
        var vault = new Vault { TenantId = TestTenantId, Name = "Test Vault", IsDefault = true };
        var knowledge = new Knowledge { TenantId = TestTenantId, Title = "In Vault" };
        _context.Vaults.Add(vault);
        _context.KnowledgeItems.Add(knowledge);
        await _context.SaveChangesAsync();

        _context.KnowledgeVaults.Add(new KnowledgeVault
        {
            TenantId = TestTenantId,
            KnowledgeId = knowledge.Id,
            VaultId = vault.Id,
            IsPrimary = true
        });
        await _context.SaveChangesAsync();

        var result = await _context.Vaults
            .Include(v => v.KnowledgeVaults)
            .ThenInclude(kv => kv.Knowledge)
            .FirstAsync(v => v.Id == vault.Id);

        result.KnowledgeVaults.Should().HaveCount(1);
        result.KnowledgeVaults.First().Knowledge.Title.Should().Be("In Vault");
        result.KnowledgeVaults.First().IsPrimary.Should().BeTrue();
    }

    [Fact]
    public async Task CanCreateAndQueryTopicWithKnowledge()
    {
        var topic = new Topic { TenantId = TestTenantId, Name = "AI" };
        _context.Topics.Add(topic);
        await _context.SaveChangesAsync();

        var knowledge = new Knowledge { TenantId = TestTenantId, Title = "AI Article", TopicId = topic.Id };
        _context.KnowledgeItems.Add(knowledge);
        await _context.SaveChangesAsync();

        var result = await _context.Topics
            .Include(t => t.KnowledgeItems)
            .FirstAsync(t => t.Id == topic.Id);

        result.KnowledgeItems.Should().HaveCount(1);
        result.KnowledgeItems.First().Title.Should().Be("AI Article");
    }

    [Fact]
    public async Task CanCreateInboxItem()
    {
        var item = new InboxItem { TenantId = TestTenantId, Body = "Quick note from MCP" };
        _context.InboxItems.Add(item);
        await _context.SaveChangesAsync();

        var result = await _context.InboxItems.FirstOrDefaultAsync();

        result.Should().NotBeNull();
        result!.Body.Should().Be("Quick note from MCP");
    }

    [Fact]
    public async Task CanCreateEntityLinks()
    {
        var knowledge = new Knowledge { TenantId = TestTenantId, Title = "Test" };
        var person = new Person { TenantId = TestTenantId, Name = "Alice" };
        var location = new Location { TenantId = TestTenantId, Name = "NYC" };
        var evt = new Event { TenantId = TestTenantId, Name = "Launch" };
        _context.KnowledgeItems.Add(knowledge);
        _context.Persons.Add(person);
        _context.Locations.Add(location);
        _context.Events.Add(evt);
        await _context.SaveChangesAsync();

        _context.KnowledgePersons.Add(new KnowledgePerson { KnowledgeId = knowledge.Id, PersonId = person.Id });
        _context.KnowledgeLocations.Add(new KnowledgeLocation { KnowledgeId = knowledge.Id, LocationId = location.Id });
        _context.KnowledgeEvents.Add(new KnowledgeEvent { KnowledgeId = knowledge.Id, EventId = evt.Id });
        await _context.SaveChangesAsync();

        var result = await _context.KnowledgeItems
            .Include(k => k.KnowledgePersons).ThenInclude(kp => kp.Person)
            .Include(k => k.KnowledgeLocations).ThenInclude(kl => kl.Location)
            .Include(k => k.KnowledgeEvents).ThenInclude(ke => ke.Event)
            .FirstAsync(k => k.Id == knowledge.Id);

        result.KnowledgePersons.Should().HaveCount(1);
        result.KnowledgePersons.First().Person.Name.Should().Be("Alice");
        result.KnowledgeLocations.Should().HaveCount(1);
        result.KnowledgeLocations.First().Location.Name.Should().Be("NYC");
        result.KnowledgeEvents.Should().HaveCount(1);
        result.KnowledgeEvents.First().Event.Name.Should().Be("Launch");
    }

    #endregion

    #region Design-Time Factory Test

    [Fact]
    public void DesignTimeFactory_CreateDbContext_ReturnsValidContext()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();

        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseSqlite(conn)
            .Options;

        var tenantProvider = new TestTenantProvider(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        using var context = new SelfHostedDbContext(options, tenantProvider);

        context.Should().NotBeNull();
        context.Database.EnsureCreated().Should().BeTrue();
    }

    #endregion

    #region DbSet Availability Tests

    [Fact]
    public void AllDbSets_AreAccessible()
    {
        _context.KnowledgeItems.Should().NotBeNull();
        _context.Vaults.Should().NotBeNull();
        _context.Topics.Should().NotBeNull();
        _context.Tags.Should().NotBeNull();
        _context.Persons.Should().NotBeNull();
        _context.Locations.Should().NotBeNull();
        _context.Events.Should().NotBeNull();
        _context.KnowledgeVaults.Should().NotBeNull();
        _context.KnowledgePersons.Should().NotBeNull();
        _context.KnowledgeLocations.Should().NotBeNull();
        _context.KnowledgeEvents.Should().NotBeNull();
        _context.VaultAncestors.Should().NotBeNull();
        _context.InboxItems.Should().NotBeNull();
    }

    #endregion

    private class TestTenantProvider(Guid tenantId) : ITenantProvider
    {
        public Guid TenantId => tenantId;
    }
}
