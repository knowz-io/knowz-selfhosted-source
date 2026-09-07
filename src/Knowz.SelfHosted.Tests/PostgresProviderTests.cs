using System.Reflection;
using Knowz.Core.Entities;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Knowz.SelfHosted.Tests;

public class PostgresProviderTests
{
    [Fact]
    public void DatabaseExtensions_UsesNpgsql_NotSqlServer()
    {
        var src = File.ReadAllText(Path.Combine(
            RepoRoot(),
            "src/Knowz.SelfHosted.Infrastructure/Extensions/DatabaseExtensions.cs"));
        Assert.Contains("UseNpgsql", src, StringComparison.Ordinal);
        Assert.DoesNotContain("UseSqlServer", src, StringComparison.Ordinal);
    }

    [Fact]
    public void DbContext_FilteredIndexes_UsePostgresQuotes()
    {
        var src = File.ReadAllText(Path.Combine(
            RepoRoot(),
            "src/Knowz.SelfHosted.Infrastructure/Data/SelfHostedDbContext.cs"));
        Assert.DoesNotContain("[ApiKey]", src, StringComparison.Ordinal);
        Assert.Contains("\\\"ApiKey\\\" IS NOT NULL", src, StringComparison.Ordinal);
        Assert.Contains("\\\"CompletedAt\\\" IS NULL", src, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigureNpgsql_SetsNpgsqlProvider()
    {
        var builder = new DbContextOptionsBuilder<SelfHostedDbContext>();
        DatabaseExtensions.ConfigureNpgsql(builder, "Host=localhost;Database=knowz_selfhosted;Username=knowz;Password=x");
        using var ctx = new SelfHostedDbContext(builder.Options);
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", ctx.Database.ProviderName);
    }

    [Fact]
    public void SystemConfiguration_RowVersion_IsClientGeneratedForPostgres()
    {
        var builder = new DbContextOptionsBuilder<SelfHostedDbContext>();
        DatabaseExtensions.ConfigureNpgsql(builder, "Host=localhost;Database=knowz_selfhosted;Username=knowz;Password=x");
        using var ctx = new SelfHostedDbContext(builder.Options);

        var rowVersion = ctx.Model.FindEntityType(typeof(SystemConfiguration))!
            .FindProperty(nameof(SystemConfiguration.RowVersion))!;

        Assert.True(rowVersion.IsConcurrencyToken);
        Assert.Equal(ValueGenerated.Never, rowVersion.ValueGenerated);
    }

    [Fact]
    public void SourceCompose_PersistsProtectedConfigurationKeysForNonRootApi()
    {
        var compose = File.ReadAllText(Path.Combine(RepoRoot(), "docker-compose.yml"));
        Assert.Contains("DataProtection__KeysPath: \"/data/keys\"", compose);
        Assert.Contains("knowz-protected-keys:/data/keys", compose);
        var dockerfile = File.ReadAllText(Path.Combine(RepoRoot(), "src/Knowz.SelfHosted.API/Dockerfile"));
        Assert.Contains("/data/keys", dockerfile);
    }

    [Fact]
    public void SourceCompose_IsPostgresPgvector()
    {
        var compose = File.ReadAllText(Path.Combine(RepoRoot(), "docker-compose.yml"));
        Assert.Contains("pgvector/pgvector", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("mssql", compose, StringComparison.Ordinal);
        Assert.Contains("POSTGRES_PASSWORD", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalVectorSearchService_ExecutesPgvectorRankSql_NotLogOnly()
    {
        var src = File.ReadAllText(Path.Combine(
            RepoRoot(),
            "src/Knowz.SelfHosted.Infrastructure/Services/LocalVectorSearchService.cs"));
        Assert.Contains("RankKnowledgeIdsSql", src, StringComparison.Ordinal);
        Assert.True(
            src.Contains("GetDbConnection", StringComparison.Ordinal)
            || src.Contains("SqlQueryRaw", StringComparison.Ordinal)
            || src.Contains("ExecuteReader", StringComparison.Ordinal),
            "Npgsql rank SQL must be executed (GetDbConnection / SqlQueryRaw / ExecuteReader), not only logged.");

        var logOnly = false;
        var idx = 0;
        while ((idx = src.IndexOf("RankKnowledgeIdsSql", idx, StringComparison.Ordinal)) >= 0)
        {
            var windowStart = Math.Max(0, idx - 180);
            var window = src.Substring(windowStart, Math.Min(260, src.Length - windowStart));
            if (window.Contains("LogDebug", StringComparison.Ordinal)
                && !window.Contains("GetDbConnection", StringComparison.Ordinal)
                && !window.Contains("SqlQueryRaw", StringComparison.Ordinal)
                && !window.Contains("ExecuteReader", StringComparison.Ordinal)
                && !window.Contains("CommandText", StringComparison.Ordinal))
            {
                logOnly = true;
                break;
            }
            idx += "RankKnowledgeIdsSql".Length;
        }

        Assert.False(logOnly, "RankKnowledgeIdsSql must not be used only as a LogDebug payload.");
    }

    private static string RepoRoot()
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
