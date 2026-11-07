using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Knowz.SelfHosted.Infrastructure.Services;

public sealed class LocalAiException(int statusCode,string code) : Exception(code switch
{
    "local-ai-denied" => "Local AI access is unavailable.",
    "local-ai-stale" => "The item, key selection or provider changed. Review a fresh consent request.",
    "local-ai-consent-unavailable" => "This consent request expired, was already used or belongs to another server. Review a new request.",
    "local-ai-capacity" => "A local AI operation is already running or the service is busy. Try again later.",
    "local-ai-provider-unavailable" => "The configured local model did not return a usable result. No change was saved.",
    _ => "The local AI request is unavailable or invalid. No change was saved."
})
{
    public int StatusCode { get; }=statusCode;
    public string Code { get; }=code;
}

/// <summary>Host-only policy. Never resolves through the general AI provider/fallback chain.</summary>
public sealed class LocalAiPolicy(IConfiguration configuration)
{
    private readonly byte[] revisionKey=RandomNumberGenerator.GetBytes(32);
    public LocalAiProviderPolicy Read()
    {
        if(AiRuntimePolicy.IsDisabled(configuration) || !string.Equals(configuration["PortableLocalAi:Enabled"],"true",StringComparison.OrdinalIgnoreCase)) return LocalAiProviderPolicy.Disabled("local-ai-disabled");
        var raw=configuration["OpenAiCompatible:Endpoint"]; var model=configuration["OpenAiCompatible:ChatModel"];
        var addresses=configuration.GetSection("PortableLocalAi:AllowedAddresses").GetChildren().Select(c=>c.Value).ToArray();
        if(!Uri.TryCreate(raw,UriKind.Absolute,out var endpoint) || endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length!=0 || endpoint.Query.Length!=0 || endpoint.Fragment.Length!=0
            || string.IsNullOrWhiteSpace(model) || model.Length>256 || model.Any(char.IsControl) || addresses.Length is 0 or >32) return LocalAiProviderPolicy.Disabled("local-ai-unconfigured");
        var allowed=new List<IPAddress>();
        foreach(var value in addresses)
        {
            if(!IPAddress.TryParse(value,out var address) || !IsLocalAddress(address)) return LocalAiProviderPolicy.Disabled("local-ai-unconfigured");
            allowed.Add(address.IsIPv4MappedToIPv6?address.MapToIPv4():address);
        }
        if(IPAddress.TryParse(endpoint.Host,out var literal) && !allowed.Contains(literal.IsIPv4MappedToIPv6?literal.MapToIPv4():literal)) return LocalAiProviderPolicy.Disabled("local-ai-unconfigured");
        endpoint=new Uri(endpoint.AbsoluteUri.TrimEnd('/')+"/");
        var credential=configuration["OpenAiCompatible:ApiKey"] ?? "";
        if(credential.Length>8192 || credential.Any(char.IsControl)) return LocalAiProviderPolicy.Disabled("local-ai-unconfigured");
        var encoded=JsonSerializer.SerializeToUtf8Bytes(new { endpoint=endpoint.AbsoluteUri,model,credential,addresses=allowed.Select(a=>a.ToString()).Distinct().Order().ToArray() });
        try { return new(endpoint,model,credential,allowed,Convert.ToHexStringLower(HMACSHA256.HashData(revisionKey,encoded))); }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }
    public static bool IsLocalAddress(IPAddress address)
    {
        if(address.IsIPv4MappedToIPv6) address=address.MapToIPv4();
        if(IPAddress.IsLoopback(address)) return true;
        var bytes=address.GetAddressBytes();
        return bytes.Length==4 ? bytes[0]==10 || bytes[0]==172 && bytes[1] is >=16 and <=31 || bytes[0]==192 && bytes[1]==168
            : bytes.Length==16 && (bytes[0]&0xfe)==0xfc;
    }
}

public sealed class LocalAiProviderPolicy
{
    private LocalAiProviderPolicy(string reason) { Reason=reason; }
    internal LocalAiProviderPolicy(Uri endpoint,string model,string credential,List<IPAddress> addresses,string revision)
    { Endpoint=endpoint; Model=model; Credential=credential; AllowedAddresses=addresses; PolicyRevision=revision; Supported=true; }
    public bool Supported { get; }
    public string? Reason { get; }
    public string? PolicyRevision { get; }
    public object Public => new { displayOrigin=Endpoint?.GetLeftPart(UriPartial.Authority),model=Model,policyRevision=PolicyRevision };
    internal Uri? Endpoint { get; }
    internal string? Model { get; }
    internal string Credential { get; }="";
    internal IReadOnlyList<IPAddress> AllowedAddresses { get; }=[];
    internal static LocalAiProviderPolicy Disabled(string reason)=>new(reason);
}
