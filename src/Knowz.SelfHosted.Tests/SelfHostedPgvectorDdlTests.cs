using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.Extensions.Configuration;

namespace Knowz.SelfHosted.Tests;

public class SelfHostedPgvectorDdlTests
{
    [Fact]
    public void VectorColumnAndIndexes_1536_UsesNativeHnswCosine()
    {
        var sql = SelfHostedPgvectorDdl.VectorColumnAndIndexes(1536);
        Assert.Contains(sql, s => s.Contains("CREATE EXTENSION IF NOT EXISTS vector", StringComparison.Ordinal));
        Assert.Contains(sql, s => s.Contains("vector(1536)", StringComparison.Ordinal));
        Assert.Contains(sql, s => s.Contains("hnsw", StringComparison.Ordinal) && s.Contains("vector_cosine_ops", StringComparison.Ordinal));
        Assert.DoesNotContain(sql, s => s.Contains("halfvec", StringComparison.Ordinal));
    }

    [Fact]
    public void VectorColumnAndIndexes_3072_UsesHalfvecCosine()
    {
        var sql = SelfHostedPgvectorDdl.VectorColumnAndIndexes(3072);
        Assert.Contains(sql, s => s.Contains("halfvec(3072)", StringComparison.Ordinal));
        Assert.Contains(sql, s => s.Contains("halfvec_cosine_ops", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolveDimensions_DefaultsTo1536()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection().Build();
        Assert.Equal(1536, SelfHostedPgvectorDdl.ResolveDimensions(cfg));
    }

    [Fact]
    public void QueryVectorCast_ContainsVectorCast()
    {
        Assert.Equal("@qvec::vector", SelfHostedPgvectorDdl.QueryVectorCast(1536));
        Assert.Contains("<=>", $"{SelfHostedPgvectorDdl.SimilarityColumnExpr(1536)} <=> {SelfHostedPgvectorDdl.QueryVectorCast(1536)}");
    }

    [Fact]
    public void RankKnowledgeIdsSql_ContainsPgvectorDistance()
    {
        var sql = SelfHostedPgvectorDdl.RankKnowledgeIdsSql(1536);
        Assert.Contains("<=>", sql, StringComparison.Ordinal);
        Assert.Contains("@qvec::vector", sql, StringComparison.Ordinal);
        Assert.Contains("ContentChunks", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void RankKnowledgeIdsSql_ReturnsMinDistancePerFilteredKnowledge()
    {
        var sql = SelfHostedPgvectorDdl.RankKnowledgeIdsSql(1536);
        Assert.Contains("ANY(@ids)", sql, StringComparison.Ordinal);
        Assert.Contains("Distance", sql, StringComparison.Ordinal);
        Assert.Contains("<=>", sql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SyncTriggerSql_SyncsTypedVectorFromJson()
    {
        var sql = SelfHostedPgvectorDdl.SyncTriggerSql();
        Assert.Contains("CREATE OR REPLACE FUNCTION", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TRIGGER", sql, StringComparison.Ordinal);
        Assert.Contains("\"EmbeddingVectorJson\"", sql, StringComparison.Ordinal);
        Assert.Contains("::vector", sql, StringComparison.Ordinal);
        Assert.Contains("EXECUTE FUNCTION", sql, StringComparison.Ordinal);
        Assert.Contains("DROP TRIGGER IF EXISTS", sql, StringComparison.Ordinal);
        Assert.Contains("BEFORE INSERT OR UPDATE OF", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BackfillSql_GuardsDimensionAndCastsToVector()
    {
        var sql = SelfHostedPgvectorDdl.BackfillSql(1536);
        Assert.Contains("json_array_length", sql, StringComparison.Ordinal);
        Assert.Contains("::vector", sql, StringComparison.Ordinal);
        Assert.Contains("1536", sql, StringComparison.Ordinal);
        Assert.Contains("\"EmbeddingVector\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"EmbeddingVectorJson\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BackfillSql_InterpolatesRequestedDimension()
    {
        var sql = SelfHostedPgvectorDdl.BackfillSql(3072);
        Assert.Contains("3072", sql, StringComparison.Ordinal);
        Assert.Contains("json_array_length", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SyncTriggerStatements_AreThreeSeparateCommands()
    {
        var stmts = SelfHostedPgvectorDdl.SyncTriggerStatements();
        Assert.Equal(3, stmts.Count);
        Assert.Contains("CREATE OR REPLACE FUNCTION", stmts[0], StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TRIGGER", stmts[0], StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TRIGGER", stmts[0], StringComparison.Ordinal);
        Assert.Contains("DROP TRIGGER IF EXISTS", stmts[1], StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE OR REPLACE FUNCTION", stmts[1], StringComparison.Ordinal);
        Assert.Contains("CREATE TRIGGER", stmts[2], StringComparison.Ordinal);
        Assert.Contains("EXECUTE FUNCTION", stmts[2], StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TRIGGER", stmts[2], StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyAsync_SourceExecutesTriggerAndBackfillAfterColumn()
    {
        var src = File.ReadAllText(Path.Combine(
            SelfHostedRepoRoot(),
            "src/Knowz.SelfHosted.Infrastructure/Data/SelfHostedPgvectorDdl.cs"));
        Assert.Contains("SyncTriggerSql", src, StringComparison.Ordinal);
        Assert.Contains("BackfillSql", src, StringComparison.Ordinal);
        var applyIdx = src.IndexOf("public static async Task ApplyAsync", StringComparison.Ordinal);
        Assert.True(applyIdx >= 0);
        var applyBody = src[applyIdx..];
        Assert.Contains("VectorColumnAndIndexes", applyBody, StringComparison.Ordinal);
        Assert.Contains("SyncTriggerStatements", applyBody, StringComparison.Ordinal);
        Assert.Contains("BackfillSql", applyBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecuteSqlRawAsync(SyncTriggerSql()", applyBody, StringComparison.Ordinal);
    }

    private static string SelfHostedRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Knowz.SelfHosted.sln"))
                || File.Exists(Path.Combine(dir.FullName, "docker-compose.yml")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate selfhosted repo root.");
    }
}
