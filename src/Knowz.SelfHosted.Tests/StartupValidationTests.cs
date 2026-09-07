using Knowz.Core.Configuration;
using Knowz.SelfHosted.API.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Knowz.SelfHosted.Tests;

/// <summary>
/// VERIFY (SH_ENTERPRISE_RUNTIME_RESILIENCE §3.1-2.2):
/// 1.x — StartupDependencyValidator surfaces missing REQUIRED services as errors.
/// 2.x — Missing OPTIONAL services produce warnings (app keeps starting).
/// 2.2  — SelfHostedOptionalList.Default shape is stable; parity with Program.cs.
/// </summary>
public class StartupValidationTests
{
    private interface IRequiredA { }
    private sealed class RequiredA : IRequiredA { }

    private interface IRequiredB { }
    private sealed class RequiredB : IRequiredB { }

    private interface IOptional1 { }
    private sealed class Optional1 : IOptional1 { }

    private interface IOptional2 { }

    [Fact]
    public void RequiredServices_ResolveCleanly_ReturnsValidPass()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRequiredA, RequiredA>();
        services.AddSingleton<IRequiredB, RequiredB>();
        using var sp = services.BuildServiceProvider();

        var result = StartupDependencyValidator.ValidateSelfHostedDependencies(
            sp,
            requiredServices: new[] { typeof(IRequiredA), typeof(IRequiredB) },
            optionalServices: null);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.Successes.Count);
    }

    [Fact]
    public void MissingRequiredService_SurfacedAsError()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRequiredA, RequiredA>();
        // IRequiredB intentionally NOT registered
        using var sp = services.BuildServiceProvider();

        var result = StartupDependencyValidator.ValidateSelfHostedDependencies(
            sp,
            requiredServices: new[] { typeof(IRequiredA), typeof(IRequiredB) });

        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Equal("IRequiredB", result.Errors[0].ServiceTypeName);
        Assert.False(result.Errors[0].ResolutionSucceeded);
    }

    [Fact]
    public void ThrowIfInvalid_Throws_WhenErrorsPresent()
    {
        var services = new ServiceCollection();
        using var sp = services.BuildServiceProvider();

        var result = StartupDependencyValidator.ValidateSelfHostedDependencies(
            sp,
            requiredServices: new[] { typeof(IRequiredA) });

        Assert.False(result.IsValid);
        var ex = Assert.Throws<InvalidOperationException>(() => result.ThrowIfInvalid());
        Assert.Contains("failed with 1 error", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingOptionalService_SurfacedAsWarning_NotError()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRequiredA, RequiredA>();
        services.AddSingleton<IOptional1, Optional1>(); // present
        // IOptional2 NOT registered
        using var sp = services.BuildServiceProvider();

        var result = StartupDependencyValidator.ValidateSelfHostedDependencies(
            sp,
            requiredServices: new[] { typeof(IRequiredA) },
            optionalServices: new[] { "IOptional1", "IOptional2" },
            services: services);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Single(result.Warnings);
        Assert.Equal("IOptional2", result.Warnings[0].ServiceTypeName);
    }

    [Fact]
    public void OptionalList_Default_ContainsExpectedShape()
    {
        // SH_ENTERPRISE_RUNTIME_RESILIENCE §2.1 — attachment AI + DocIntel are the
        // only optional services today. Locking the exact list so drift between
        // Program.cs wiring and the list is detected by test.
        Assert.Equal(
            new[] { "IAttachmentAIProvider", "DocumentIntelligenceContentExtractor" },
            SelfHostedOptionalList.Default);
    }
}
