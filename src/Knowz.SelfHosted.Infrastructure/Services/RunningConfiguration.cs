using Microsoft.Extensions.Configuration;

namespace Knowz.SelfHosted.Infrastructure.Services;

/// <summary>Settings used when fixed provider registrations were selected for this process.</summary>
public sealed class RunningConfiguration
{
    public IConfiguration Values { get; }
    private readonly Dictionary<string, string?> authorities;
    public RunningConfiguration(IConfiguration configuration)
    {
        var entries = configuration.AsEnumerable().ToArray();
        Values = new ConfigurationBuilder().AddInMemoryCollection(entries).Build();
        authorities = entries.ToDictionary(e => e.Key, e => ConfigurationAuthority.For(configuration, e.Key), StringComparer.OrdinalIgnoreCase);
    }
    public string? Authority(string key) => authorities.GetValueOrDefault(key);
    private bool Set(string key) => !string.IsNullOrWhiteSpace(Values[key]);
    public string ActiveProvider => AiRuntimePolicy.IsDisabled(Values) ? "offline" : string.Equals(Values["KnowzPlatform:Enabled"], "true", StringComparison.OrdinalIgnoreCase)
            && Set("KnowzPlatform:BaseUrl") && Set("KnowzPlatform:ApiKey") ? "KnowzPlatform"
        : Set("OpenAiCompatible:Endpoint") && Set("OpenAiCompatible:ChatModel") && Set("OpenAiCompatible:EmbeddingModel") ? "OpenAiCompatible"
        : Set("AzureOpenAI:Endpoint") ? "AzureOpenAI" : "offline";
}
