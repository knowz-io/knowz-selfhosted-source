using Knowz.Core.Entities;
using Knowz.Core.Interfaces;
using Knowz.Core.Portability;
using Knowz.Core.Schema;
using Knowz.SelfHosted.Application.Services;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Data.Entities;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Knowz.SelfHosted.Tests;

public class SelfHostedSealedStoreTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly ITenantProvider _tenant = Substitute.For<ITenantProvider>();
    private readonly SelfHostedDbContext _db;
    private readonly SelfHostedSealedStore _store;
    private readonly Guid _vaultId = Guid.NewGuid();

    public SelfHostedSealedStoreTests()
    {
        _tenant.TenantId.Returns(TenantA);
        _db = NewContext(_tenant);
        _db.Vaults.Add(new Vault { Id = _vaultId, TenantId = TenantA, Name = "sealed-vault" });
        _db.SaveChanges();
        _store = new SelfHostedSealedStore(_db, _tenant, Substitute.For<ILogger<SelfHostedSealedStore>>());
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyOriginalFallbackCannotChooseGraphCopiesOrAmbiguousLegacySubjects(bool modern)
    {
        var original = Guid.NewGuid(); var binding = Guid.NewGuid();
        foreach (var n in new[] { 1, 2 })
        {
            var id = Guid.NewGuid(); _db.KnowledgeItems.Add(new() { Id = id, TenantId = TenantA, Title = "preserved" });
            _db.KnowledgeVaults.Add(new() { KnowledgeId = id, VaultId = _vaultId, TenantId = TenantA });
            _db.ShSealedKnowledge.Add(new() { TenantId = TenantA, KnowledgeId = id, OriginalItemId = original, CryptoBindingId = binding, LineageScope = modern ? "modern-copy" : "", Ciphertext = "preserved-" + n });
        }
        await _db.SaveChangesAsync();
        var result = await _store.CreateAsync(new() { VaultId = _vaultId, Item = Item(original, binding) }, default);
        if (modern) { Assert.True(result.Ok, result.Error); Assert.Equal(3, await _db.ShSealedKnowledge.CountAsync()); }
        else { Assert.False(result.Ok); Assert.Equal(409, result.StatusCode); Assert.Contains("ambiguous", result.Error, StringComparison.OrdinalIgnoreCase); }
        Assert.Equal(2, await _db.ShSealedKnowledge.CountAsync(r => r.Ciphertext.StartsWith("preserved-")));
    }

    [Fact]
    public async Task Import_StoresCiphertext_LeavesPlaintextUntouched_AndDoesNotPromoteKeys()
    {
        var plainId = Guid.NewGuid();
        _db.KnowledgeItems.Add(new Knowledge { Id = plainId, TenantId = TenantA, Title = "plain", Content = "hello plaintext" });
        await _db.SaveChangesAsync();

        var originalId = Guid.NewGuid();
        var contextId = Guid.NewGuid();
        var bindingId = Guid.NewGuid();
        var result = await _store.ImportPackageAsync(Package(originalId, contextId, bindingId, lifecycle: "active"), _vaultId, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(201, result.StatusCode);
        var knowledge = await _db.KnowledgeItems.SingleAsync(k => k.Id == result.Id);
        Assert.Equal(string.Empty, knowledge.Content);
        Assert.NotEqual("cipher-body", knowledge.Title);
        Assert.Equal("hello plaintext", (await _db.KnowledgeItems.SingleAsync(k => k.Id == plainId)).Content);

        var sealedRow = await _db.ShSealedKnowledge.SingleAsync();
        Assert.Equal("cipher-body", sealedRow.Ciphertext);
        Assert.Equal(SelfHostedSealedStore.LockedStatus, sealedRow.LifecycleStatus);
        Assert.False((await _db.ShVaultCryptoContextLinks.SingleAsync()).IsPrimaryWriteContext);
        Assert.Equal("decrypt-only", (await _db.ShCryptoKeyIdentities.SingleAsync()).Lifecycle);
        Assert.Equal("customer-held-public-only", (await _db.ShCryptoKeyIdentities.SingleAsync()).Custody);
    }

    [Fact]
    public async Task Reexport_PreservesCiphertext_OnSealedSchema()
    {
        var originalId = Guid.NewGuid();
        var imported = await _store.ImportPackageAsync(
            Package(originalId, Guid.NewGuid(), Guid.NewGuid(), lifecycle: "decrypt-only"),
            _vaultId,
            CancellationToken.None);
        Assert.True(imported.Ok, imported.Error);

        var exported = await _store.ExportVaultAsync(_vaultId, CancellationToken.None);

        Assert.NotNull(exported);
        Assert.Equal(EncryptedPortabilityProfile.SchemaVersion, exported!.SchemaVersion);
        Assert.Equal(EncryptedPortabilityProfile.Capability, Assert.Single(exported.RequiredCapabilities!));
        Assert.Equal("cipher-body", Assert.Single(exported.SealedItems!).Ciphertext);
        Assert.Equal("file-bytes", Assert.Single(exported.SealedFiles!).BinaryContentBase64);
        Assert.Contains("\"sealedSnapshot\":true", Assert.Single(exported.SealedVersions!).EnvelopeMetadata, StringComparison.Ordinal);
        Assert.All(exported.Data.KnowledgeItems, item => Assert.Equal(string.Empty, item.Content));
        Assert.Null(EncryptedPortabilityProfile.RejectReason(exported));
        Assert.Null(EncryptedPortabilityProfile.ValidateSealedGraph(exported));
    }

    [Fact]
    public async Task IncompleteAndPlaintextPackages_WriteNothing()
    {
        var incomplete = Package(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), lifecycle: "decrypt-only");
        incomplete.SealedItems![0].EncryptedDek = null;
        var refused = await _store.ImportPackageAsync(incomplete, _vaultId, CancellationToken.None);
        Assert.False(refused.Ok);

        var plain = new PortableExportPackage { SchemaVersion = CoreSchema.Version, SourceEdition = "selfhosted" };
        var plainResult = await _store.ImportPackageAsync(plain, _vaultId, CancellationToken.None);
        Assert.False(plainResult.Ok);

        Assert.Equal(0, await _db.ShSealedKnowledge.CountAsync());
        Assert.Equal(0, await _db.KnowledgeItems.CountAsync());
    }

    [Fact]
    public async Task Update_SnapshotsPreviousCiphertext_AndLocalUnlockDoesNotUnwrap()
    {
        var imported = await _store.CreateAsync(new SealedItemWrite
        {
            Label = "Quarterly note",
            VaultId = _vaultId,
            Item = Item(Guid.NewGuid(), Guid.NewGuid()),
        }, CancellationToken.None);
        Assert.True(imported.Ok, imported.Error);

        var ack = await _store.RecordLocalUnlockAsync(imported.Id!.Value, new LocalUnlockAck
        {
            Verified = true,
            Fingerprint = "fingerprint-public",
        }, CancellationToken.None);
        Assert.True(ack.Ok, ack.Error);
        Assert.Equal("cipher-body", (await _db.ShSealedKnowledge.SingleAsync()).Ciphertext);
        Assert.Equal(SelfHostedSealedStore.ClientVerifiedStatus, (await _db.ShSealedKnowledge.SingleAsync()).LifecycleStatus);

        var replacement = Item((await _db.ShSealedKnowledge.SingleAsync()).OriginalItemId, Guid.NewGuid());
        replacement.Ciphertext = "cipher-next";
        var updated = await _store.UpdateAsync(imported.Id.Value, new SealedItemWrite { Item = replacement }, CancellationToken.None);
        Assert.True(updated.Ok, updated.Error);

        var row = await _db.ShSealedKnowledge.SingleAsync();
        Assert.Equal("cipher-next", row.Ciphertext);
        Assert.Equal(string.Empty, (await _db.KnowledgeItems.SingleAsync(k => k.Id == row.KnowledgeId)).Content);
        Assert.Equal(SelfHostedSealedStore.LockedStatus, row.LifecycleStatus);
        var version = await _db.ShSealedVersions.SingleAsync(v => v.Ciphertext == "cipher-body");
        Assert.Contains("\"sealedSnapshot\":true", version.EnvelopeMetadata, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_RemovesEnvelope_FromLaterExport()
    {
        var imported = await _store.CreateAsync(new SealedItemWrite
        {
            VaultId = _vaultId,
            Item = Item(Guid.NewGuid(), Guid.NewGuid()),
        }, CancellationToken.None);
        var deleted = await _store.DeleteAsync(imported.Id!.Value, CancellationToken.None);
        Assert.True(deleted.Ok, deleted.Error);

        var exported = await _store.ExportVaultAsync(_vaultId, CancellationToken.None);
        Assert.NotNull(exported);
        Assert.Empty(exported!.SealedItems!);
        Assert.Equal(0, await _db.ShSealedKnowledge.CountAsync());
    }

    [Fact]
    public async Task PlaintextUpdate_OfSealedItem_ThrowsBeforeIndexing()
    {
        var imported = await _store.CreateAsync(new SealedItemWrite
        {
            VaultId = _vaultId,
            Item = Item(Guid.NewGuid(), Guid.NewGuid()),
        }, CancellationToken.None);
        var openAi = Substitute.For<IOpenAIService>();
        var search = Substitute.For<ISearchService>();
        var knowledge = new KnowledgeService(
            new SelfHostedRepository<Knowledge>(_db),
            new SelfHostedRepository<Tag>(_db),
            _db,
            search,
            openAi,
            new SelfHostedChunkingService(),
            _tenant,
            Substitute.For<ILogger<KnowledgeService>>());

        var plain = await knowledge.CreateKnowledgeAsync("still plain", "Plain title", "Note", _vaultId.ToString(), new List<string>(), null, CancellationToken.None);
        Assert.Equal("still plain", (await _db.KnowledgeItems.SingleAsync(k => k.Id == plain.Id)).Content);

        openAi.ClearReceivedCalls();
        await Assert.ThrowsAsync<SealedItemLockedException>(() =>
            knowledge.UpdateKnowledgeAsync(imported.Id!.Value, "x", "plaintext-overwrite", null, null, null, CancellationToken.None));
        await openAi.DidNotReceive().GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal("cipher-body", (await _db.ShSealedKnowledge.SingleAsync()).Ciphertext);
    }

    [Fact]
    public async Task Import_WithoutDestinationVault_WritesNothing()
    {
        var refused = await _store.ImportPackageAsync(
            Package(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "decrypt-only"),
            destinationVaultId: null,
            CancellationToken.None);

        Assert.False(refused.Ok);
        Assert.Equal(400, refused.StatusCode);
        Assert.Equal(0, await _db.ShSealedKnowledge.CountAsync());
        Assert.Equal(0, await _db.KnowledgeItems.CountAsync());
    }

    [Fact]
    public async Task Import_RefusesVersionOwnedByAnotherItemOrTenant()
    {
        var first = await _store.ImportPackageAsync(
            Package(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "decrypt-only"),
            _vaultId,
            CancellationToken.None);
        Assert.True(first.Ok, first.Error);
        var hijackedVersionId = (await _db.ShSealedVersions.AsNoTracking().FirstAsync()).Id;

        var secondOriginal = Guid.NewGuid();
        var second = Package(secondOriginal, Guid.NewGuid(), Guid.NewGuid(), "decrypt-only");
        second.SealedVersions![0].Id = hijackedVersionId;
        second.SealedVersions[0].KnowledgeId = second.SealedItems![0].Id;
        var hijack = await _store.ImportPackageAsync(second, _vaultId, CancellationToken.None);
        Assert.False(hijack.Ok);
        Assert.Equal(409, hijack.StatusCode);

        var foreignVersionId = Guid.NewGuid();
        var otherTenant = Substitute.For<ITenantProvider>();
        otherTenant.TenantId.Returns(TenantB);
        await using var other = NewContext(otherTenant);
        other.ShSealedVersions.Add(new ShSealedVersion
        {
            Id = foreignVersionId,
            TenantId = TenantB,
            KnowledgeId = Guid.NewGuid(),
            OriginalKnowledgeId = Guid.NewGuid(),
            VersionNumber = 1,
            Ciphertext = "other-tenant-cipher",
            EnvelopeMetadata = "{\"sealedSnapshot\":true}",
        });
        await other.SaveChangesAsync();

        var foreign = Package(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "decrypt-only");
        foreign.SealedVersions![0].Id = foreignVersionId;
        var refused = await _store.ImportPackageAsync(foreign, _vaultId, CancellationToken.None);
        Assert.False(refused.Ok);
        Assert.Equal(403, refused.StatusCode);
        Assert.Equal("other-tenant-cipher", (await other.ShSealedVersions.IgnoreQueryFilters().SingleAsync(v => v.Id == foreignVersionId)).Ciphertext);
    }

    [Fact]
    public async Task PlaintextDeleteReprocessAndBatchMove_OfSealedItem_ThrowBeforeIndexing()
    {
        var imported = await _store.CreateAsync(new SealedItemWrite
        {
            VaultId = _vaultId,
            Item = Item(Guid.NewGuid(), Guid.NewGuid()),
        }, CancellationToken.None);
        var openAi = Substitute.For<IOpenAIService>();
        var search = Substitute.For<ISearchService>();
        var knowledge = new KnowledgeService(
            new SelfHostedRepository<Knowledge>(_db),
            new SelfHostedRepository<Tag>(_db),
            _db,
            search,
            openAi,
            new SelfHostedChunkingService(),
            _tenant,
            Substitute.For<ILogger<KnowledgeService>>());

        Assert.True(await knowledge.IsSealedAsync(imported.Id!.Value, CancellationToken.None));
        await Assert.ThrowsAsync<SealedItemLockedException>(() =>
            knowledge.DeleteKnowledgeAsync(imported.Id.Value, CancellationToken.None));
        await Assert.ThrowsAsync<SealedItemLockedException>(() =>
            knowledge.ReprocessKnowledgeAsync(imported.Id.Value, CancellationToken.None));
        await Assert.ThrowsAsync<SealedItemLockedException>(() =>
            knowledge.BatchMoveToVaultAsync(new List<Guid> { imported.Id.Value }, _vaultId, CancellationToken.None));
        await search.DidNotReceive().DeleteDocumentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await openAi.DidNotReceive().GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal("cipher-body", (await _db.ShSealedKnowledge.SingleAsync()).Ciphertext);
        Assert.False((await _db.KnowledgeItems.SingleAsync(k => k.Id == imported.Id)).IsDeleted);
    }

    [Fact]
    public async Task Import_RefusesContextOwnedByAnotherTenant()
    {
        var contextId = Guid.NewGuid();
        var otherTenant = Substitute.For<ITenantProvider>();
        otherTenant.TenantId.Returns(TenantB);
        await using var other = NewContext(otherTenant);
        other.ShPortableCryptoContexts.Add(new ShPortableCryptoContext
        {
            Id = contextId,
            TenantId = TenantB,
            BindingKind = "vault",
            OriginalBindingId = Guid.NewGuid(),
        });
        await other.SaveChangesAsync();

        var package = Package(Guid.NewGuid(), contextId, Guid.NewGuid(), "decrypt-only");
        var result = await _store.ImportPackageAsync(package, _vaultId, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(403, result.StatusCode);
        Assert.Equal(0, await _db.ShSealedKnowledge.CountAsync());
    }

    [Fact]
    public async Task SealedComment_CreateListGetUpdateDelete_RoundTrips()
    {
        var imported = await _store.CreateAsync(new SealedItemWrite
        {
            VaultId = _vaultId,
            Item = Item(Guid.NewGuid(), Guid.NewGuid()),
        }, CancellationToken.None);
        Assert.True(imported.Ok, imported.Error);

        var originalId = Guid.NewGuid();
        var bindingId = Guid.NewGuid();
        var createResult = await _store.CreateCommentAsync(imported.Id!.Value, new SealedCommentWrite
        {
            Comment = new SealedCommentRecord
            {
                OriginalCommentId = originalId,
                OriginalKnowledgeId = imported.Id.Value,
                Ciphertext = "comment-cipher",
                EnvelopeVersion = 2,
                EncryptedDek = "dek",
                EncryptedDekNonce = "dek-nonce",
                EphemeralPublicKey = "ephemeral",
                ContentNonce = "content-nonce",
                CryptoBindingId = bindingId,
            }
        }, CancellationToken.None);

        Assert.True(createResult.Ok, createResult.Error);
        Assert.Equal(201, createResult.StatusCode);
        var commentId = createResult.Id;

        var getResult = await _store.GetCommentAsync(commentId!.Value, CancellationToken.None);
        Assert.NotNull(getResult);
        Assert.Equal(commentId, getResult!.Id);
        Assert.Equal(originalId, getResult.OriginalCommentId);
        Assert.Equal("comment-cipher", getResult.Envelope.Ciphertext);

        var listResult = await _store.ListCommentsAsync(imported.Id.Value, CancellationToken.None);
        Assert.Single(listResult);
        Assert.Equal(commentId, listResult[0].Id);

        var updateResult = await _store.UpdateCommentAsync(commentId.Value, new SealedCommentWrite
        {
            Comment = new SealedCommentRecord
            {
                OriginalCommentId = originalId,
                OriginalKnowledgeId = imported.Id.Value,
                Ciphertext = "comment-updated",
                EnvelopeVersion = 2,
                EncryptedDek = "dek",
                EncryptedDekNonce = "dek-nonce",
                EphemeralPublicKey = "ephemeral",
                ContentNonce = "content-nonce",
                CryptoBindingId = bindingId,
            }
        }, CancellationToken.None);

        Assert.True(updateResult.Ok, updateResult.Error);
        Assert.Equal(200, updateResult.StatusCode);
        var updated = await _store.GetCommentAsync(commentId.Value, CancellationToken.None);
        Assert.Equal("comment-updated", updated!.Envelope.Ciphertext);

        var deleteResult = await _store.DeleteCommentAsync(commentId.Value, CancellationToken.None);
        Assert.True(deleteResult.Ok, deleteResult.Error);
        Assert.Null(await _store.GetCommentAsync(commentId.Value, CancellationToken.None));
    }

    [Fact]
    public async Task SealedComment_ExportedWithPackage()
    {
        var imported = await _store.CreateAsync(new SealedItemWrite
        {
            VaultId = _vaultId,
            Item = Item(Guid.NewGuid(), Guid.NewGuid()),
        }, CancellationToken.None);

        var bindingId = Guid.NewGuid();
        await _store.CreateCommentAsync(imported.Id!.Value, new SealedCommentWrite
        {
            Comment = new SealedCommentRecord
            {
                OriginalCommentId = Guid.NewGuid(),
                OriginalKnowledgeId = imported.Id.Value,
                Ciphertext = "cipher",
                EnvelopeVersion = 2,
                EncryptedDek = "dek",
                EncryptedDekNonce = "dek-nonce",
                EphemeralPublicKey = "ephemeral",
                ContentNonce = "content-nonce",
                CryptoBindingId = bindingId,
            }
        }, CancellationToken.None);

        var exported = await _store.ExportVaultAsync(_vaultId, CancellationToken.None);
        Assert.NotNull(exported);
        Assert.Single(exported!.SealedComments!);
        Assert.Equal("cipher", exported.SealedComments[0].Ciphertext);
    }

    [Fact]
    public async Task SealedComment_ImportedAndPreserved()
    {
        var originalId = Guid.NewGuid();
        var contextId = Guid.NewGuid();
        var bindingId = Guid.NewGuid();
        var commentId = Guid.NewGuid();

        var pkg = Package(originalId, contextId, bindingId, "decrypt-only");
        pkg.SealedComments = new List<SealedCommentRecord>
        {
            new()
            {
                Id = commentId,
                OriginalCommentId = Guid.NewGuid(),
                KnowledgeId = pkg.SealedItems![0].Id,
                OriginalKnowledgeId = originalId,
                Ciphertext = "comment-cipher",
                EnvelopeVersion = 2,
                EncryptedDek = "dek",
                EncryptedDekNonce = "dek-nonce",
                EphemeralPublicKey = "ephemeral",
                ContentNonce = "content-nonce",
                CryptoBindingId = bindingId,
            }
        };

        var imported = await _store.ImportPackageAsync(pkg, _vaultId, CancellationToken.None);
        Assert.True(imported.Ok, imported.Error);

        var comment = await _db.ShSealedComments.FirstOrDefaultAsync();
        Assert.NotNull(comment);
        Assert.Equal("comment-cipher", comment!.Ciphertext);
    }

    [Fact]
    public async Task PlaintextComment_OnSealedItem_ThrowsException()
    {
        var openAi = Substitute.For<IOpenAIService>();
        var search = Substitute.For<ISearchService>();
        var commentService = new Knowz.SelfHosted.Application.Services.CommentService(
            _db,
            _tenant,
            Substitute.For<ILogger<Knowz.SelfHosted.Application.Services.CommentService>>(),
            Substitute.For<Knowz.SelfHosted.Infrastructure.Interfaces.IFileStorageProvider>());

        var imported = await _store.CreateAsync(new SealedItemWrite
        {
            VaultId = _vaultId,
            Item = Item(Guid.NewGuid(), Guid.NewGuid()),
        }, CancellationToken.None);
        Assert.True(imported.Ok, imported.Error);

        await Assert.ThrowsAsync<SealedItemLockedException>(() =>
            commentService.AddCommentAsync(imported.Id!.Value, "plaintext body", "author", null, null, CancellationToken.None));
    }

    [Fact]
    public void PublicCore_ArchiveStub_IsRejectedByPlaintextReader()
    {
        Assert.False(CoreSchema.CanRead(KnowzArchiveProfile.SchemaVersion));
        var read = KnowzArchiveConsumer.Read(KnowzArchiveProducer.WriteStub(Guid.NewGuid(), "boundary", 1, 0));
        Assert.True(read.Accepted);
        Assert.False(read.WritesPerformed);
        Assert.False(read.MultipartImplemented);
        Assert.False(string.IsNullOrWhiteSpace(read.LegacyReaderRejectReason));
    }


    [Fact]
    public async Task SealedComment_Incomplete_RefusesWrite()
    {
        var imported = await _store.CreateAsync(new SealedItemWrite
        {
            VaultId = _vaultId,
            Item = Item(Guid.NewGuid(), Guid.NewGuid()),
        }, CancellationToken.None);
        Assert.True(imported.Ok, imported.Error);

        var refused = await _store.CreateCommentAsync(imported.Id!.Value, new SealedCommentWrite
        {
            Comment = new SealedCommentRecord
            {
                OriginalCommentId = Guid.NewGuid(),
                KnowledgeId = imported.Id.Value,
                Ciphertext = "cipher",
                EnvelopeVersion = 2,
            },
        }, CancellationToken.None);

        Assert.False(refused.Ok);
        Assert.Equal(400, refused.StatusCode);
        Assert.Equal(0, await _db.ShSealedComments.CountAsync());
    }

    [Fact]
    public async Task Delete_SealedItem_RemovesSealedComments()
    {
        var imported = await _store.CreateAsync(new SealedItemWrite
        {
            VaultId = _vaultId,
            Item = Item(Guid.NewGuid(), Guid.NewGuid()),
        }, CancellationToken.None);
        Assert.True(imported.Ok, imported.Error);

        var created = await _store.CreateCommentAsync(imported.Id!.Value, new SealedCommentWrite
        {
            Comment = new SealedCommentRecord
            {
                OriginalCommentId = Guid.NewGuid(),
                KnowledgeId = imported.Id.Value,
                Ciphertext = "cipher",
                EnvelopeVersion = 2,
                EncryptedDek = "dek",
                EncryptedDekNonce = "dek-nonce",
                EphemeralPublicKey = "eph",
                ContentNonce = "content-nonce",
                CryptoBindingId = Guid.NewGuid(),
            },
        }, CancellationToken.None);
        Assert.True(created.Ok, created.Error);
        Assert.Equal(1, await _db.ShSealedComments.CountAsync());

        var deleted = await _store.DeleteAsync(imported.Id.Value, CancellationToken.None);
        Assert.True(deleted.Ok, deleted.Error);
        Assert.Equal(0, await _db.ShSealedComments.CountAsync());
    }

    [Fact]
    public async Task Update_PreservesRecoverableEncryptedFieldsInPreviousVersion()
    {
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        var plaintext = System.Text.Encoding.UTF8.GetBytes("historical private title");
        var encrypted = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new System.Security.Cryptography.AesGcm(key, 16))
            aes.Encrypt(nonce, plaintext, encrypted, tag);
        var fields = System.Text.Json.JsonSerializer.Serialize(new
        {
            title = new { ciphertext = Convert.ToBase64String(encrypted.Concat(tag).ToArray()), nonce = Convert.ToBase64String(nonce) }
        });
        var original = Item(Guid.NewGuid(), Guid.NewGuid());
        original.EncryptedFieldsJson = fields;
        var created = await _store.CreateAsync(new SealedItemWrite { VaultId = _vaultId, Item = original }, CancellationToken.None);
        Assert.True(created.Ok, created.Error);
        var replacement = Item(original.OriginalItemId, original.CryptoBindingId);
        replacement.EncryptedFieldsJson = "{}";
        var updated = await _store.UpdateAsync(created.Id!.Value, new SealedItemWrite { Item = replacement }, CancellationToken.None);
        Assert.True(updated.Ok, updated.Error);

        var snapshot = await _db.ShSealedVersions.SingleAsync();
        using var metadata = System.Text.Json.JsonDocument.Parse(snapshot.EnvelopeMetadata);
        Assert.True(metadata.RootElement.TryGetProperty("encryptedFieldsJson", out var savedFields), "Historical field envelopes must be retained with each version.");
        using var saved = System.Text.Json.JsonDocument.Parse(savedFields.GetString()!);
        var title = saved.RootElement.GetProperty("title");
        var bytes = Convert.FromBase64String(title.GetProperty("ciphertext").GetString()!);
        var recovered = new byte[bytes.Length - 16];
        using (var aes = new System.Security.Cryptography.AesGcm(key, 16))
            aes.Decrypt(Convert.FromBase64String(title.GetProperty("nonce").GetString()!), bytes.AsSpan(0, recovered.Length), bytes.AsSpan(recovered.Length), recovered);
        Assert.Equal(plaintext, recovered);
    }

    [Fact]
    public async Task PlainExport_RefusesSealedRowsInsteadOfClaimingBlankContentIsComplete()
    {
        var created = await _store.CreateAsync(new SealedItemWrite { VaultId = _vaultId, Item = Item(Guid.NewGuid(), Guid.NewGuid()) }, CancellationToken.None);
        Assert.True(created.Ok, created.Error);
        var exporter = new PortableExportService(_db, _tenant,
            Substitute.For<Knowz.SelfHosted.Infrastructure.Interfaces.IFileStorageProvider>(),
            Microsoft.Extensions.Options.Options.Create(new Knowz.Core.Configuration.SelfHostedOptions()),
            Substitute.For<ILogger<PortableExportService>>());

        await Assert.ThrowsAsync<SealedItemLockedException>(() => exporter.ExportAsync(CancellationToken.None));
        await Assert.ThrowsAsync<SealedItemLockedException>(() => exporter.ExportAsync(new ExportScope { Type = ExportScopeType.Vault, VaultIds = [_vaultId] }, CancellationToken.None));
        var other = new Vault { Id = Guid.NewGuid(), TenantId = TenantA, Name = "plain-only" };
        _db.Vaults.Add(other);
        await _db.SaveChangesAsync();
        var plain = await exporter.ExportAsync(new ExportScope { Type = ExportScopeType.Vault, VaultIds = [other.Id] }, CancellationToken.None);
        Assert.Empty(plain.Data.KnowledgeItems);
        var item = new Knowledge { TenantId = TenantA, Title = "Plain with protected filename", Content = "plain" }; _db.KnowledgeItems.Add(item);
        _db.KnowledgeVaults.Add(new() { TenantId = TenantA, KnowledgeId = item.Id, VaultId = other.Id });
        var protectedFile = new FileRecord { TenantId = TenantA, FileName = "[Encrypted]", EncryptedFieldsJson = "{protected metadata}" }; _db.FileRecords.Add(protectedFile);
        _db.FileAttachments.Add(new() { TenantId = TenantA, FileRecordId = protectedFile.Id, KnowledgeId = item.Id }); await _db.SaveChangesAsync();
        await Assert.ThrowsAsync<SealedItemLockedException>(() => exporter.ExportAsync(new ExportScope { Type = ExportScopeType.Vault, VaultIds = [other.Id] }, CancellationToken.None));
    }

    private SelfHostedDbContext NewContext(ITenantProvider tenant)
    {
        var options = new DbContextOptionsBuilder<SelfHostedDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .Options;
        return new SelfHostedDbContext(options, tenant);
    }

    private PortableExportPackage Package(Guid originalId, Guid contextId, Guid bindingId, string lifecycle)
    {
        var item = Item(originalId, bindingId);
        var fileId = Guid.NewGuid();
        return new PortableExportPackage
        {
            SchemaVersion = EncryptedPortabilityProfile.SchemaVersion,
            RequiredCapabilities = new List<string> { EncryptedPortabilityProfile.Capability },
            SourceEdition = "platform",
            CryptoContexts = new List<ExportedCryptoContext>
            {
                new()
                {
                    ContextId = contextId,
                    BindingKind = "vault",
                    OriginalBindingId = bindingId,
                    AlgorithmProfile = "x25519-vault",
                    ProvenanceStatus = "archive-unverified",
                }
            },
            CryptoKeyCatalog = new List<ExportedCryptoKeyIdentity>
            {
                new()
                {
                    ContextId = contextId,
                    Fingerprint = "fingerprint-public",
                    PublicKey = "public-key-bytes",
                    Lifecycle = lifecycle,
                    Generation = 2,
                    Custody = "customer-held-public-only",
                }
            },
            SealedItems = new List<SealedKnowledgeItem> { item },
            SealedVersions = new List<SealedKnowledgeVersionRecord>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    KnowledgeId = item.Id,
                    OriginalKnowledgeId = originalId,
                    VersionNumber = 1,
                    Ciphertext = "cipher-old",
                    EnvelopeMetadata = "{\"sealedSnapshot\":true}",
                }
            },
            SealedFiles = new List<SealedFileRecord>
            {
                new()
                {
                    Id = fileId,
                    KnowledgeId = item.Id,
                    BinaryContentBase64 = "file-bytes",
                    SizeBytes = 10,
                    EnvelopeVersion = 2,
                    EncryptedDek = "dek",
                    EncryptedDekNonce = "dek-nonce",
                    EphemeralPublicKey = "ephemeral",
                    ContentNonce = "content-nonce",
                    CryptoBindingId = bindingId,
                }
            },
        };
    }

    private static SealedKnowledgeItem Item(Guid originalId, Guid bindingId) => new()
    {
        Id = Guid.NewGuid(),
        OriginalItemId = originalId,
        Ciphertext = "cipher-body",
        EnvelopeVersion = 2,
        EncryptedDek = "dek",
        EncryptedDekNonce = "dek-nonce",
        EphemeralPublicKey = "ephemeral",
        ContentNonce = "content-nonce",
        CryptoBindingId = bindingId,
        AlgorithmProfile = "x25519-vault",
        EncryptedFieldsJson = "{\"title\":true}",
    };
}
