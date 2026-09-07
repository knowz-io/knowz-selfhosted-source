using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;

namespace Knowz.SelfHosted.API.Services;

/// <summary>
/// Port of <c>Knowz.Infrastructure.Extensions.StartupDependencyValidator</c>
/// (used by the main Functions host) tailored for the self-hosted API surface.
///
/// SH_ENTERPRISE_RUNTIME_RESILIENCE §Rule 1-2: self-hosted enterprise containers
/// boot with `ASPNETCORE_ENVIRONMENT=Production` and silently lose enrichment
/// when a required DI registration is missing. Failing at boot (rather than in
/// the background queue) surfaces the misconfiguration immediately.
///
/// Two lists drive the validation:
/// - <c>requiredServices</c>: Type references (e.g. typeof(SelfHostedDbContext)).
///   Must resolve via GetRequiredService or the app throws at startup.
/// - <c>optionalServices</c>: string class/interface short names. Missing
///   registrations produce warnings only; runtime paths must tolerate null or
///   a NoOp fallback.
///
/// The required/optional split and wiring gate (<c>Knowz:StrictDIValidation</c>)
/// match the Functions-host pattern verbatim so the runbook stays portable.
/// </summary>
public static class StartupDependencyValidator
{
    /// <summary>
    /// Short-name set of the ServiceTypes registered in the builder's service
    /// collection, captured by <see cref="ValidateSelfHostedDependencies"/>
    /// when the caller supplies the collection. Used to resolve optional
    /// services by name without constructing them.
    /// </summary>
    [ThreadStatic]
    private static HashSet<string>? _registeredServiceTypes;

    /// <summary>
    /// Configuration hints for common missing services — maps short type names
    /// to guidance surfaced in validator output.
    /// </summary>
    private static readonly Dictionary<string, string> ConfigurationHints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SelfHostedDbContext"] = "Set ConnectionStrings:McpDb and ensure migrations have been applied (or Database:AutoMigrate=true).",
        ["IOpenAIService"] = "Set AzureOpenAI:Endpoint or enable KnowzPlatform proxy mode (KnowzPlatform:Enabled=true).",
        ["ISearchService"] = "Set AzureAISearch:Endpoint + IndexName, or rely on DatabaseSearchService fallback.",
        ["IFileStorageProvider"] = "Set Storage:Provider=AzureBlob + Storage:Azure:AccountUrl, or omit to fall back to LocalFileStorageProvider.",
        ["IAttachmentAIProvider"] = "Set AzureAIVision:Endpoint or AzureDocumentIntelligence:Endpoint to enable attachment AI.",
        ["DocumentIntelligenceContentExtractor"] = "Same as IAttachmentAIProvider — requires Azure Document Intelligence endpoint.",
    };

    /// <summary>
    /// Validate the required/optional DI surface. Returns a report that the
    /// caller can log and, if <c>ThrowIfInvalid</c> is invoked, throw on.
    /// </summary>
    /// <param name="services">
    /// The service collection used to build the provider. Passed so optional
    /// service presence can be checked by short name without resolving (cheap).
    /// </param>
    public static ValidationResult ValidateSelfHostedDependencies(
        IServiceProvider serviceProvider,
        Type[] requiredServices,
        string[]? optionalServices = null,
        IServiceCollection? services = null)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(requiredServices);

        _registeredServiceTypes = services?
            .Select(d => d.ServiceType.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new ValidationResult();

        // Required services — GetRequiredService throws if missing.
        foreach (var serviceType in requiredServices)
        {
            try
            {
                using var scope = serviceProvider.CreateScope();
                var _ = scope.ServiceProvider.GetRequiredService(serviceType);
                result.Successes.Add(new ServiceValidationEntry(
                    serviceType.Name, ResolutionSucceeded: true,
                    ErrorMessage: null, MissingService: null, ConfigurationHint: null));
            }
            catch (Exception ex)
            {
                var (missing, _) = ExtractDependencyInfo(ex);
                result.Errors.Add(new ServiceValidationEntry(
                    serviceType.Name, ResolutionSucceeded: false,
                    ErrorMessage: GetRootCause(ex),
                    MissingService: missing,
                    ConfigurationHint: GetConfigurationHint(missing ?? serviceType.Name)));
            }
        }

        // Optional services — resolved by matching short name over the full
        // descriptor list. When no descriptor matches, record a warning rather
        // than an error. Required list is the hard gate.
        if (optionalServices != null)
        {
            var registeredShortNames = _registeredServiceTypes;
            foreach (var serviceName in optionalServices)
            {
                try
                {
                    bool registered = registeredShortNames?.Contains(serviceName,
                        StringComparer.OrdinalIgnoreCase) == true;
                    if (!registered)
                    {
                        result.Warnings.Add(new ServiceValidationEntry(
                            serviceName, ResolutionSucceeded: false,
                            ErrorMessage: "Service not registered (optional)",
                            MissingService: serviceName,
                            ConfigurationHint: GetConfigurationHint(serviceName)));
                    }
                    else
                    {
                        result.Successes.Add(new ServiceValidationEntry(
                            serviceName, ResolutionSucceeded: true,
                            ErrorMessage: null, MissingService: null, ConfigurationHint: null));
                    }
                }
                catch (Exception ex)
                {
                    result.Warnings.Add(new ServiceValidationEntry(
                        serviceName, ResolutionSucceeded: false,
                        ErrorMessage: GetRootCause(ex),
                        MissingService: serviceName,
                        ConfigurationHint: GetConfigurationHint(serviceName)));
                }
            }
        }

        result.IsValid = result.Errors.Count == 0;
        return result;
    }

    private static string GetRootCause(Exception ex)
    {
        var current = ex;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }
        return current.Message;
    }

    private static (string? Missing, List<string>? Chain) ExtractDependencyInfo(Exception ex)
    {
        var chain = new List<string>();
        string? missing = null;
        var current = ex;
        while (current != null)
        {
            var match = Regex.Match(current.Message,
                @"Unable to resolve service for type '([^']+)'",
                RegexOptions.Singleline);
            if (match.Success)
            {
                missing = ExtractTypeName(match.Groups[1].Value);
                chain.Add(missing);
                break;
            }
            current = current.InnerException;
        }
        return (missing, chain.Count > 0 ? chain : null);
    }

    private static string ExtractTypeName(string fullTypeName)
    {
        if (string.IsNullOrEmpty(fullTypeName)) return fullTypeName;
        var generic = fullTypeName.IndexOf('`');
        if (generic > 0) fullTypeName = fullTypeName.Substring(0, generic);
        var lastDot = fullTypeName.LastIndexOf('.');
        return lastDot >= 0 ? fullTypeName.Substring(lastDot + 1) : fullTypeName;
    }

    private static string? GetConfigurationHint(string? missingServiceType)
    {
        if (string.IsNullOrEmpty(missingServiceType)) return null;
        if (ConfigurationHints.TryGetValue(missingServiceType, out var hint)) return hint;
        foreach (var kvp in ConfigurationHints)
        {
            if (missingServiceType.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }
        return null;
    }
}

/// <summary>Outcome of a startup DI validation pass.</summary>
public sealed class ValidationResult
{
    public bool IsValid { get; set; }
    public List<ServiceValidationEntry> Successes { get; } = new();
    public List<ServiceValidationEntry> Warnings { get; } = new();
    public List<ServiceValidationEntry> Errors { get; } = new();

    public string GetDetailedReport()
    {
        var lines = new List<string>
        {
            $"Validation Result: {(IsValid ? "PASS" : "FAIL")}",
            $"Successes: {Successes.Count}, Warnings: {Warnings.Count}, Errors: {Errors.Count}",
            string.Empty
        };
        if (Errors.Count > 0)
        {
            lines.Add("ERRORS:");
            foreach (var e in Errors)
            {
                lines.Add($"  [X] {e.ServiceTypeName}: {e.ErrorMessage}");
                if (!string.IsNullOrEmpty(e.MissingService)) lines.Add($"      Missing: {e.MissingService}");
                if (!string.IsNullOrEmpty(e.ConfigurationHint)) lines.Add($"      Fix: {e.ConfigurationHint}");
            }
            lines.Add(string.Empty);
        }
        if (Warnings.Count > 0)
        {
            lines.Add("WARNINGS:");
            foreach (var w in Warnings)
            {
                lines.Add($"  [!] {w.ServiceTypeName}: {w.ErrorMessage}");
                if (!string.IsNullOrEmpty(w.ConfigurationHint)) lines.Add($"      Fix: {w.ConfigurationHint}");
            }
            lines.Add(string.Empty);
        }
        if (Successes.Count > 0)
        {
            lines.Add($"SUCCESSES: {Successes.Count} services validated");
        }
        return string.Join(Environment.NewLine, lines);
    }

    public void ThrowIfInvalid()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException(
                $"Self-hosted DI validation failed with {Errors.Count} error(s):\n{GetDetailedReport()}");
        }
    }
}

public record ServiceValidationEntry(
    string ServiceTypeName,
    bool ResolutionSucceeded,
    string? ErrorMessage,
    string? MissingService = null,
    string? ConfigurationHint = null);
