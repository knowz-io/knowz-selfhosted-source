using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Knowz.SelfHosted.Infrastructure.Data;

/// <summary>
/// Raw DDL for ContentChunks.EmbeddingVector (pgvector). Dimension comes from
/// Embedding:Dimensions (default 1536). HNSW cosine; halfvec when dim &gt; 2000.
/// </summary>
public static class SelfHostedPgvectorDdl
{
    public const int MaxVectorOpsHnswDims = 2000;
    public const int DefaultEmbeddingDimensions = 1536;
    public const string VectorColumn = "\"EmbeddingVector\"";
    public const string Table = "\"ContentChunks\"";

    public static IReadOnlyList<string> VectorColumnAndIndexes(int embeddingDimensions)
    {
        ValidateDimensions(embeddingDimensions);
        var dim = embeddingDimensions.ToString(CultureInfo.InvariantCulture);
        var indexExpr = embeddingDimensions <= MaxVectorOpsHnswDims
            ? $"{VectorColumn} vector_cosine_ops"
            : $"({VectorColumn}::halfvec({dim})) halfvec_cosine_ops";
        return
        [
            "CREATE EXTENSION IF NOT EXISTS vector;",
            $"ALTER TABLE {Table} ADD COLUMN IF NOT EXISTS {VectorColumn} vector({dim});",
            $"CREATE INDEX IF NOT EXISTS ix_content_chunks_embedding ON {Table} USING hnsw ({indexExpr});",
        ];
    }

    public static string SimilarityColumnExpr(int dimensions)
    {
        ValidateDimensions(dimensions);
        return dimensions <= MaxVectorOpsHnswDims
            ? VectorColumn
            : $"{VectorColumn}::halfvec({dimensions.ToString(CultureInfo.InvariantCulture)})";
    }

    public static string QueryVectorCast(int dimensions)
    {
        ValidateDimensions(dimensions);
        return dimensions <= MaxVectorOpsHnswDims
            ? "@qvec::vector"
            : $"@qvec::halfvec({dimensions.ToString(CultureInfo.InvariantCulture)})";
    }

    public static int ResolveDimensions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var raw = configuration["Embedding:Dimensions"] ?? configuration["Embedding__Dimensions"];
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dimensions)
            ? dimensions
            : DefaultEmbeddingDimensions;
    }

    public static void ValidateDimensions(int dimensions)
    {
        if (dimensions is < 1 or > 4000)
            throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, "Embedding dimensions must be 1–4000.");
    }

    public static string RankKnowledgeIdsSql(int dimensions) =>
        $"""
        SELECT c."KnowledgeId" AS "KnowledgeId",
               MIN({SimilarityColumnExpr(dimensions)} <=> {QueryVectorCast(dimensions)}) AS "Distance"
        FROM "ContentChunks" c
        WHERE c."EmbeddingVector" IS NOT NULL
          AND c."KnowledgeId" = ANY(@ids)
        GROUP BY c."KnowledgeId"
        """;

    /// <summary>
    /// BEFORE INSERT/UPDATE trigger that copies EmbeddingVectorJson into the typed pgvector column.
    /// JSON array literals such as [0.1,0.2] are valid pgvector input — no extra formatting.
    /// Concatenated form is for shape tests; <see cref="ApplyAsync"/> executes
    /// <see cref="SyncTriggerStatements"/> as separate Npgsql commands.
    /// </summary>
    public static string SyncTriggerSql() =>
        string.Join("\n", SyncTriggerStatements());

    public static IReadOnlyList<string> SyncTriggerStatements() =>
        [SyncTriggerFunctionSql(), DropTriggerSql(), CreateTriggerSql()];

    public static string SyncTriggerFunctionSql() =>
        """
        CREATE OR REPLACE FUNCTION knowz_sync_content_chunk_embedding_vector()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $$
        BEGIN
          IF NEW."EmbeddingVectorJson" IS NULL OR btrim(NEW."EmbeddingVectorJson") = '' THEN
            NEW."EmbeddingVector" := NULL;
          ELSE
            NEW."EmbeddingVector" := NEW."EmbeddingVectorJson"::vector;
          END IF;
          RETURN NEW;
        END;
        $$;
        """;

    public static string DropTriggerSql() =>
        """
        DROP TRIGGER IF EXISTS trg_content_chunks_sync_embedding_vector ON "ContentChunks";
        """;

    public static string CreateTriggerSql() =>
        """
        CREATE TRIGGER trg_content_chunks_sync_embedding_vector
          BEFORE INSERT OR UPDATE OF "EmbeddingVectorJson"
          ON "ContentChunks"
          FOR EACH ROW
          EXECUTE FUNCTION knowz_sync_content_chunk_embedding_vector();
        """;

    /// <summary>
    /// One-shot copy of existing JSON embeddings into the typed column. Dimension mismatches
    /// are skipped so startup is not aborted by a single bad row.
    /// </summary>
    public static string BackfillSql(int dimensions)
    {
        ValidateDimensions(dimensions);
        var dim = dimensions.ToString(CultureInfo.InvariantCulture);
        return $"""
            UPDATE {Table}
            SET {VectorColumn} = "EmbeddingVectorJson"::vector
            WHERE {VectorColumn} IS NULL
              AND "EmbeddingVectorJson" IS NOT NULL
              AND json_array_length("EmbeddingVectorJson"::json) = {dim};
            """;
    }

    public static async Task ApplyAsync(DbContext db, IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!string.Equals(db.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
            return;
        var dim = ResolveDimensions(configuration);
        foreach (var sql in VectorColumnAndIndexes(dim))
            await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
        foreach (var sql in SyncTriggerStatements())
            await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
        await db.Database.ExecuteSqlRawAsync(BackfillSql(dim), cancellationToken);
    }
}
