using FluentAssertions;
using Knowz.Core.Entities;
using Knowz.Core.Enums;
using Xunit;

namespace Knowz.MCP.Tests.Data;

public class McpEntityDefaultsTests
{
    [Fact]
    public void Knowledge_NewInstance_HasExpectedDefaults()
    {
        var entity = new Knowledge();

        entity.Id.Should().NotBe(Guid.Empty);
        entity.Title.Should().Be(string.Empty);
        entity.Content.Should().Be(string.Empty);
        entity.Summary.Should().BeNull();
        entity.Type.Should().Be(KnowledgeType.Note);
        entity.Source.Should().BeNull();
        entity.FilePath.Should().BeNull();
        entity.IsIndexed.Should().BeFalse();
        entity.IndexedAt.Should().BeNull();
        entity.IsDeleted.Should().BeFalse();
        entity.TopicId.Should().BeNull();
        entity.Topic.Should().BeNull();
    }

    [Fact]
    public void Vault_NewInstance_HasExpectedDefaults()
    {
        var entity = new Vault();

        entity.Id.Should().NotBe(Guid.Empty);
        entity.Name.Should().Be(string.Empty);
        entity.Description.Should().BeNull();
        entity.VaultType.Should().BeNull();
        entity.IsDefault.Should().BeFalse();
        entity.ParentVaultId.Should().BeNull();
        entity.ParentVault.Should().BeNull();
        entity.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public void Topic_NewInstance_HasExpectedDefaults()
    {
        var entity = new Topic();

        entity.Id.Should().NotBe(Guid.Empty);
        entity.Name.Should().Be(string.Empty);
        entity.Description.Should().BeNull();
        entity.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public void Tag_NewInstance_HasExpectedDefaults()
    {
        var entity = new Tag();

        entity.Id.Should().NotBe(Guid.Empty);
        entity.Name.Should().Be(string.Empty);
        entity.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public void Person_NewInstance_HasExpectedDefaults()
    {
        var entity = new Person();

        entity.Id.Should().NotBe(Guid.Empty);
        entity.Name.Should().Be(string.Empty);
        entity.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public void Location_NewInstance_HasExpectedDefaults()
    {
        var entity = new Location();

        entity.Id.Should().NotBe(Guid.Empty);
        entity.Name.Should().Be(string.Empty);
        entity.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public void Event_NewInstance_HasExpectedDefaults()
    {
        var entity = new Event();

        entity.Id.Should().NotBe(Guid.Empty);
        entity.Name.Should().Be(string.Empty);
        entity.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public void InboxItem_NewInstance_HasExpectedDefaults()
    {
        var entity = new InboxItem();

        entity.Id.Should().NotBe(Guid.Empty);
        entity.Body.Should().Be(string.Empty);
        entity.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public void KnowledgeVault_NewInstance_HasExpectedDefaults()
    {
        var entity = new KnowledgeVault();

        entity.IsPrimary.Should().BeFalse();
        entity.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void VaultAncestor_NewInstance_HasExpectedDefaults()
    {
        var entity = new VaultAncestor();

        entity.Depth.Should().Be(0);
    }

    [Fact]
    public void KnowledgeType_HasAllExpectedValues()
    {
        var values = Enum.GetValues<KnowledgeType>();
        values.Should().HaveCount(14);
        values.Should().Contain(KnowledgeType.Note);
        values.Should().Contain(KnowledgeType.Document);
        values.Should().Contain(KnowledgeType.Transcript);
        values.Should().Contain(KnowledgeType.Image);
        values.Should().Contain(KnowledgeType.Video);
        values.Should().Contain(KnowledgeType.Audio);
        values.Should().Contain(KnowledgeType.Code);
        values.Should().Contain(KnowledgeType.QuestionAnswer);
        values.Should().Contain(KnowledgeType.Journal);
        values.Should().Contain(KnowledgeType.Link);
        values.Should().Contain(KnowledgeType.File);
        values.Should().Contain(KnowledgeType.Prompt);
        values.Should().Contain(KnowledgeType.CommitHistory);
        values.Should().Contain(KnowledgeType.Commit);
    }

    [Fact]
    public void VaultType_HasAllExpectedValues()
    {
        var values = Enum.GetValues<VaultType>();
        values.Should().HaveCount(8);
        values.Should().Contain(VaultType.GeneralKnowledge);
        values.Should().Contain(VaultType.Business);
        values.Should().Contain(VaultType.Product);
        values.Should().Contain(VaultType.CodeBase);
        values.Should().Contain(VaultType.DailyDiary);
        values.Should().Contain(VaultType.QuestionAnswer);
        values.Should().Contain(VaultType.PersonBound);
        values.Should().Contain(VaultType.LocationBound);
    }
}
