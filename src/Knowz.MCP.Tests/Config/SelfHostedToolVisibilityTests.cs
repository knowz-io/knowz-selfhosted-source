using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Knowz.MCP.Config;
using Knowz.MCP.Services.Proxy;
using Knowz.MCP.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Xunit;

namespace Knowz.MCP.Tests.Config;

/// <summary>
/// MCP_SelfHostedToolContract VERIFY-T1, T2, T4, T5, T6, T13, T14.
///
/// The self-hosted edition ships the SAME binary as hosted MCP (api.knowz.io); only
/// MCP:BackendMode differs. These tests pin both halves of that switch: the SH surface is
/// filtered and SH-true, and the proxy surface is byte-unchanged.
/// </summary>
public class SelfHostedToolVisibilityTests
{
    /// <summary>Every [McpServerTool(Name = ...)] declared on KnowzProxyTools.</summary>
    private static IReadOnlyList<string> DeclaredToolNames() =>
        typeof(KnowzProxyTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
            .Where(a => a is not null)
            .Select(a => a!.Name!)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    private static McpServerOptions BuildOptions(string? backendMode)
    {
        var values = new Dictionary<string, string?>();
        if (backendMode is not null)
            values["MCP:BackendMode"] = backendMode;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddMcpServer().WithTools<KnowzProxyTools>();

        // Mirrors Program.cs exactly — the filter is registered through the same extension.
        services.AddSelfHostedToolVisibility(configuration);

        return services.BuildServiceProvider().GetRequiredService<IOptions<McpServerOptions>>().Value;
    }

    private static IReadOnlyList<string> AdvertisedNames(McpServerOptions options) =>
        options.ToolCollection!.Select(t => t.ProtocolTool.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

    // ---- VERIFY-T1 -------------------------------------------------------

    [Fact]
    public void SelfHostedMode_AdvertisesDeclaredMinusHiddenTools()
    {
        var advertised = AdvertisedNames(BuildOptions("selfhosted"));

        advertised.Should().HaveCount(
            DeclaredToolNames().Count - SelfHostedToolVisibility.HiddenTools.Count,
            "the advertised count is derived from the hidden set, never hard-coded");
        advertised.Should().NotIntersectWith(SelfHostedToolVisibility.HiddenTools);
    }

    [Fact]
    public void SelfHostedMode_HidesEveryNameInTheFrozenHiddenSet()
    {
        var advertised = AdvertisedNames(BuildOptions("selfhosted"));

        foreach (var hidden in SelfHostedToolVisibility.HiddenTools)
            advertised.Should().NotContain(hidden);
    }

    // ---- VERIFY-T2 (api.knowz.io regression guard, R5) --------------------

    [Theory]
    [InlineData("proxy")]
    [InlineData(null)]
    [InlineData("Proxy")]
    public void ProxyMode_AdvertisesEveryDeclaredTool(string? backendMode)
    {
        var advertised = AdvertisedNames(BuildOptions(backendMode));

        advertised.Should().BeEquivalentTo(DeclaredToolNames());
        foreach (var hidden in SelfHostedToolVisibility.HiddenTools)
            advertised.Should().Contain(hidden, "proxy mode must be byte-unchanged");
    }

    // ---- VERIFY-T4 -------------------------------------------------------

    [Fact]
    public void SelfHostedMode_AmendKnowledgeDescription_DoesNotLie()
    {
        var options = BuildOptions("selfhosted");
        options.ToolCollection!.TryGetPrimitive("amend_knowledge", out var tool).Should().BeTrue();

        var description = tool!.ProtocolTool.Description!;
        description.Should().NotContainEquivalentOf("DEPRECATED");
        description.Should().NotContainEquivalentOf("queued");
        description.Should().NotContain("amend_knowledge_async");
        description.Should().NotContain("get_amend_request_status");
        description.Should().ContainEquivalentOf("synchronous");
    }

    [Fact]
    public void ProxyMode_AmendKnowledgeDescription_KeepsOriginalWording()
    {
        var options = BuildOptions("proxy");
        options.ToolCollection!.TryGetPrimitive("amend_knowledge", out var tool).Should().BeTrue();

        tool!.ProtocolTool.Description.Should().Contain("DEPRECATED");
        tool.ProtocolTool.Description.Should().Contain("amend_knowledge_async");
    }

    [Theory]
    [InlineData("ask_question")]
    [InlineData("list_vaults")]
    public void SelfHostedMode_DropsSharedVaultProse(string toolName)
    {
        var options = BuildOptions("selfhosted");
        options.ToolCollection!.TryGetPrimitive(toolName, out var tool).Should().BeTrue();

        tool!.ProtocolTool.Description.Should().NotContainEquivalentOf("shared vault");

        var schema = tool.ProtocolTool.InputSchema.GetRawText();
        schema.Should().NotContainEquivalentOf("shared vault",
            "cross-tenant vault sharing does not exist in this edition");
    }

    [Fact]
    public void ProxyMode_KeepsSharedVaultProse()
    {
        var options = BuildOptions("proxy");
        options.ToolCollection!.TryGetPrimitive("list_vaults", out var tool).Should().BeTrue();

        tool!.ProtocolTool.InputSchema.GetRawText().Should().ContainEquivalentOf("shared");
    }

    [Fact]
    public void SelfHostedMode_UploadFileDescriptionParameter_SaysItHasNoEffect()
    {
        // The SH attach route binds AttachFileRequest(Guid FileRecordId) only, so upload_file's
        // `description` is inert here. It stays in the schema; the promise is what changes.
        var selfHosted = BuildOptions("selfhosted");
        selfHosted.ToolCollection!.TryGetPrimitive("upload_file", out var shTool).Should().BeTrue();

        using var shSchema = JsonDocument.Parse(shTool!.ProtocolTool.InputSchema.GetRawText());
        var shDescription = shSchema.RootElement
            .GetProperty("properties").GetProperty("description").GetProperty("description").GetString();
        shDescription.Should().Be(
            SelfHostedToolVisibility.SelfHostedParameterDescriptions["upload_file"]["description"]);

        var proxy = BuildOptions("proxy");
        proxy.ToolCollection!.TryGetPrimitive("upload_file", out var proxyTool).Should().BeTrue();
        using var proxySchema = JsonDocument.Parse(proxyTool!.ProtocolTool.InputSchema.GetRawText());
        proxySchema.RootElement.GetProperty("properties").GetProperty("description")
            .GetProperty("description").GetString().Should().NotBe(shDescription);
    }

    [Fact]
    public void SelfHostedMode_LeavesLoadBearingParameterProseAlone()
    {
        // create_vault's `description` IS forwarded to the SH API — the overlay must be tool-scoped.
        var options = BuildOptions("selfhosted");
        options.ToolCollection!.TryGetPrimitive("create_vault", out var tool).Should().BeTrue();

        using var schema = JsonDocument.Parse(tool!.ProtocolTool.InputSchema.GetRawText());
        schema.RootElement.GetProperty("properties").GetProperty("description")
            .GetProperty("description").GetString()
            .Should().NotContain("has no effect");
    }

    // ---- VERIFY-T5 (discriminates R9's two mechanisms) --------------------

    [Fact]
    public void SelfHostedMode_DescriptionOverlay_IsStableAcrossRepeatedReads()
    {
        var options = BuildOptions("selfhosted");
        options.ToolCollection!.TryGetPrimitive("amend_knowledge", out var tool).Should().BeTrue();

        var first = tool!.ProtocolTool.Description;
        var second = tool.ProtocolTool.Description;

        first.Should().Be(SelfHostedToolVisibility.SelfHostedDescriptions["amend_knowledge"]);
        second.Should().Be(first, "the overlay must survive a second read of ProtocolTool");
    }

    // ---- VERIFY-T6 -------------------------------------------------------

    [Theory]
    [InlineData("selfhosted", "ask_question")]
    [InlineData("selfhosted", "list_vaults")]
    [InlineData("proxy", "ask_question")]
    [InlineData("proxy", "list_vaults")]
    public void IncludeSharedVaults_StaysInTheSchema_InBothModes(string mode, string toolName)
    {
        var options = BuildOptions(mode);
        options.ToolCollection!.TryGetPrimitive(toolName, out var tool).Should().BeTrue();

        using var document = JsonDocument.Parse(tool!.ProtocolTool.InputSchema.GetRawText());
        document.RootElement.TryGetProperty("properties", out var properties).Should().BeTrue();
        properties.TryGetProperty("includeSharedVaults", out _)
            .Should().BeTrue("removing the parameter would change the hosted schema (R5/R8)");
    }

    [Fact]
    public void SelfHostedOverlay_DoesNotLeakIntoASubsequentProxyContainer()
    {
        // The SH overlay mutates tool instances. If the SDK handed out shared instances, filtering
        // one container would silently rewrite api.knowz.io's surface in the next one.
        _ = BuildOptions("selfhosted");

        var proxy = BuildOptions("proxy");

        AdvertisedNames(proxy).Should().BeEquivalentTo(DeclaredToolNames());
        proxy.ToolCollection!.TryGetPrimitive("amend_knowledge", out var tool).Should().BeTrue();
        tool!.ProtocolTool.Description.Should().Contain("DEPRECATED");
        tool.ProtocolTool.InputSchema.GetRawText().Should().NotContain("no cross-tenant vault sharing");
    }

    // ---- VERIFY-T13 (coverage guard, R13) --------------------------------

    [Fact]
    public void EveryDeclaredTool_IsInExactlyOneOf_Mappings_SpecialCases_Hidden()
    {
        var problems = SelfHostedToolVisibility.ClassifyCoverage(DeclaredToolNames());

        problems.Should().BeEmpty(
            "a tool in zero sets 404s or returns 'Unknown tool'; a tool in two sets has ambiguous dispatch");
    }

    [Fact]
    public void CoverageGuard_Fails_WhenAToolIsInNoSet()
    {
        var withProbe = DeclaredToolNames().Append("__probe__").ToList();

        var problems = SelfHostedToolVisibility.ClassifyCoverage(withProbe);

        problems.Should().ContainSingle().Which.Should().Contain("__probe__");
    }

    [Fact]
    public void CoverageGuard_Fails_WhenAToolIsInTwoSets()
    {
        // "upload_file" is a special case; pretending it is also hidden must be rejected.
        var problems = SelfHostedToolVisibility.ClassifyCoverage(
            DeclaredToolNames(),
            extraHidden: new[] { "upload_file" });

        problems.Should().ContainSingle().Which.Should().Contain("upload_file");
    }

    // ---- VERIFY-T14 (route manifest, R14) --------------------------------

    [Fact]
    public void EveryAdvertisedTool_CallsARouteTheSelfHostedApiActuallyExposes()
    {
        var missing = SelfHostedRouteManifest.MissingRoutes(SelfHostedRouteManifest.Routes);

        missing.Should().BeEmpty();
    }

    [Fact]
    public void RouteManifestGuard_Fails_WhenAnEntryIsPerturbed()
    {
        var perturbed = SelfHostedRouteManifest.Routes
            .Select(r => r == "/api/v1/knowledge" ? "/api/v2/knowledge" : r)
            .ToArray();

        var missing = SelfHostedRouteManifest.MissingRoutes(perturbed);

        missing.Should().NotBeEmpty("a renamed SH route must fail loudly, not 404 in a customer's agent");
    }

    [Fact]
    public void RouteManifest_CoversTheSpecialCaseUploadAndAttachHops()
    {
        SelfHostedRouteManifest.Routes.Should().Contain("/api/v1/files/upload");
        SelfHostedToolBackend.SelfHostedRouteTemplates["upload_file"]
            .Should().Contain("/api/v1/files/upload");
    }
}
