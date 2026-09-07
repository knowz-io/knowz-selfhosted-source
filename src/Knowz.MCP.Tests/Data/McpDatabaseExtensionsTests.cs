using FluentAssertions;
using Knowz.Core.Configuration;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Infrastructure.Data;
using Knowz.SelfHosted.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Knowz.MCP.Tests.Data;

public class McpDatabaseExtensionsTests
{
    [Fact]
    public void AddSelfHostedDatabase_RegistersDualPattern()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:McpDb"] = "Host=localhost;Database=knowz_selfhosted;Username=knowz;Password=test"
            })
            .Build();

        var services = new ServiceCollection();
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        services.AddSingleton<ITenantProvider>(new TestTenantProvider(tenantId));

        // Act
        services.AddSelfHostedDatabase(config);

        var provider = services.BuildServiceProvider();

        // Assert: Factory registration exists
        var factory = provider.GetService<IDbContextFactory<SelfHostedDbContext>>();
        factory.Should().NotBeNull("IDbContextFactory<SelfHostedDbContext> should be registered");

        // Assert: Scoped registration exists
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetService<SelfHostedDbContext>();
        context.Should().NotBeNull("SelfHostedDbContext should be registered as scoped");
    }

    [Fact]
    public void AddSelfHostedDatabase_ThrowsWhenConnectionStringMissing()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();

        // Act
        var act = () => services.AddSelfHostedDatabase(config);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ConnectionStrings:McpDb*");
    }

    private class TestTenantProvider(Guid tenantId) : ITenantProvider
    {
        public Guid TenantId => tenantId;
    }
}
