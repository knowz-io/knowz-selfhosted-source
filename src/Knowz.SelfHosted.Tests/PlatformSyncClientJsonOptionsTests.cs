using System.Text.Json;
using Knowz.Core.Enums;
using Knowz.Core.Portability;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.SelfHosted.Application.Services;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// Regression: SaaS vault-sync export serializes KnowledgeType as camelCase strings
/// ("note"). PlatformSyncClient must deserialize that shape or live PullOnly returns 422.
/// </summary>
public class PlatformSyncClientJsonOptionsTests
{
    [Fact]
    public void JsonOptions_DeserializesSaaSExportEnvelope_WithCamelCaseKnowledgeTypeNote()
    {
        var knowledgeId = Guid.Parse("8c442fe9-f196-44aa-a6ea-8ca38ab972d5");
        var vaultId = Guid.Parse("937c7bed-9d54-419b-ba2c-69ee3dff5cb2");
        var tenantId = Guid.Parse("9c3e60a1-1d59-4f8e-aa40-41886fd70f16");

        // Shape mirrors api.dev GET /api/v1/sync/vaults/{id}/export (ApiResponse wrapping PortableExportPackage).
        var json = $$"""
            {
              "success": true,
              "data": {
                "schemaVersion": 1,
                "sourceEdition": "platform",
                "sourceTenantId": "{{tenantId}}",
                "exportedAt": "2026-09-23T22:35:00Z",
                "metadata": { "totalKnowledgeItems": 1 },
                "data": {
                  "vaults": [],
                  "knowledgeItems": [
                    {
                      "id": "{{knowledgeId}}",
                      "title": "G3 SaaS pending pull A",
                      "content": "Plain fixture item",
                      "type": "note",
                      "vaultIds": ["{{vaultId}}"],
                      "primaryVaultId": "{{vaultId}}",
                      "createdAt": "2026-09-23T22:34:36Z",
                      "updatedAt": "2026-09-23T22:34:36Z"
                    }
                  ]
                },
                "mode": "light",
                "isIncrementalSync": true
              },
              "errors": []
            }
            """;

        var envelope = JsonSerializer.Deserialize<PlatformApiResponse<PortableExportPackage>>(
            json, PlatformSyncClient.JsonOptions);

        Assert.NotNull(envelope);
        Assert.True(envelope.Success);
        Assert.NotNull(envelope.Data);
        Assert.Single(envelope.Data.Data.KnowledgeItems);
        var item = envelope.Data.Data.KnowledgeItems[0];
        Assert.Equal(knowledgeId, item.Id);
        Assert.Equal(KnowledgeType.Note, item.Type);
        Assert.Equal("G3 SaaS pending pull A", item.Title);
        Assert.Equal(DateTimeKind.Utc, item.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, item.UpdatedAt.Kind);
    }

    [Fact]
    public void JsonOptions_WithoutStringEnumConverter_WouldRejectCamelCaseNote()
    {
        // Guard: documents the pre-fix failure mode so we do not silently regress options.
        var brittle = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip
        };

        const string fragment = """{"id":"8c442fe9-f196-44aa-a6ea-8ca38ab972d5","title":"x","content":"y","type":"note"}""";

        Assert.ThrowsAny<JsonException>(() =>
            JsonSerializer.Deserialize<PortableKnowledge>(fragment, brittle));

        var ok = JsonSerializer.Deserialize<PortableKnowledge>(fragment, PlatformSyncClient.JsonOptions);
        Assert.NotNull(ok);
        Assert.Equal(KnowledgeType.Note, ok.Type);
    }

    [Fact]
    public void JsonOptions_NormalizesBareDateTimesToUtcKind()
    {
        // SaaS usually emits Zulu; STJ leaves bare timestamps Unspecified which Npgsql
        // timestamptz rejects on SaveChanges. Converter must force UTC.
        const string json = """
            {
              "schemaVersion": 1,
              "sourceEdition": "platform",
              "sourceTenantId": "9c3e60a1-1d59-4f8e-aa40-41886fd70f16",
              "exportedAt": "2026-09-23T22:47:05.9406905",
              "syncCursor": "2026-09-23T22:47:05.9406905",
              "data": {
                "knowledgeItems": [
                  {
                    "id": "8c442fe9-f196-44aa-a6ea-8ca38ab972d5",
                    "title": "t",
                    "content": "c",
                    "type": "note",
                    "createdAt": "2026-09-23T22:34:36.1234567",
                    "updatedAt": "2026-09-23T22:34:36Z"
                  }
                ]
              },
              "mode": "light",
              "isIncrementalSync": true
            }
            """;

        var pkg = JsonSerializer.Deserialize<PortableExportPackage>(json, PlatformSyncClient.JsonOptions)!;
        Assert.Equal(DateTimeKind.Utc, pkg.ExportedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, pkg.SyncCursor!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, pkg.Data.KnowledgeItems[0].CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, pkg.Data.KnowledgeItems[0].UpdatedAt.Kind);
    }
}
