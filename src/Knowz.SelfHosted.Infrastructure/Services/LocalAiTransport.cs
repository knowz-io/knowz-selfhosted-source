using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Knowz.Core.Portability;

namespace Knowz.SelfHosted.Infrastructure.Services;

public interface ILocalAiTransport
{
    Task<string> AmendAsync(LocalAiProviderPolicy policy,string content,string instruction,CancellationToken ct);
}

/// <summary>One bounded local request; no proxy, redirect, tools, retries or provider fallback.</summary>
public sealed class LocalAiTransport : ILocalAiTransport
{
    public const int MaxInputBytes=65536, MaxInstructionBytes=4096, MaxOutputBytes=65536, MaxWireBytes=512*1024;
    public async Task<string> AmendAsync(LocalAiProviderPolicy policy,string content,string instruction,CancellationToken ct)
    {
        if(!policy.Supported || policy.Endpoint==null || Encoding.UTF8.GetByteCount(content)>MaxInputBytes || Encoding.UTF8.GetByteCount(instruction)>MaxInstructionBytes) throw new LocalAiException(400,"local-ai-invalid");
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new { model=policy.Model,stream=false,max_tokens=8192,
            messages=new[] { new { role="system",content="Apply the instruction to the supplied document. Return only the amended document, without explanations or tool calls." },new { role="user",content=$"Document:\n{content}\n\nInstruction:\n{instruction}" } } });
        try
        {
        if(bytes.Length>MaxWireBytes) throw new LocalAiException(413,"local-ai-invalid");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(120));
        using var handler=new SocketsHttpHandler { UseProxy=false,AllowAutoRedirect=false,UseCookies=false,AutomaticDecompression=DecompressionMethods.None,ConnectTimeout=TimeSpan.FromSeconds(10),MaxConnectionsPerServer=1 };
        handler.ConnectCallback=async (context,token)=>
        {
            var requestedHost=context.DnsEndPoint.Host.Trim('[',']');
            var configuredHost=policy.Endpoint.IdnHost.Trim('[',']');
            var sameHost=IPAddress.TryParse(requestedHost,out var requestedIp) && IPAddress.TryParse(configuredHost,out var configuredIp)
                ? (requestedIp.IsIPv4MappedToIPv6?requestedIp.MapToIPv4():requestedIp).Equals(configuredIp.IsIPv4MappedToIPv6?configuredIp.MapToIPv4():configuredIp)
                : string.Equals(requestedHost,configuredHost,StringComparison.OrdinalIgnoreCase);
            if(!sameHost || context.DnsEndPoint.Port!=policy.Endpoint.Port) throw new LocalAiException(502,"local-ai-provider-unavailable");
            var resolved=IPAddress.TryParse(requestedHost,out var literal) ? [literal] : await Dns.GetHostAddressesAsync(requestedHost,token);
            var addresses=resolved.Select(a=>a.IsIPv4MappedToIPv6?a.MapToIPv4():a).ToArray();
            if(addresses.Length==0 || addresses.Any(a=>!LocalAiPolicy.IsLocalAddress(a) || !policy.AllowedAddresses.Contains(a))) throw new LocalAiException(502,"local-ai-provider-unavailable");
            var socket=new Socket(addresses[0].AddressFamily,SocketType.Stream,ProtocolType.Tcp);
            try { await socket.ConnectAsync(new IPEndPoint(addresses[0],context.DnsEndPoint.Port),token); return new NetworkStream(socket,ownsSocket:true); }
            catch { socket.Dispose(); throw; }
        };
        using var client=new HttpClient(handler) { Timeout=Timeout.InfiniteTimeSpan,MaxResponseContentBufferSize=MaxWireBytes };
        using var request=new HttpRequestMessage(HttpMethod.Post,new Uri(policy.Endpoint,"chat/completions")) { Version=HttpVersion.Version11,VersionPolicy=HttpVersionPolicy.RequestVersionExact,Content=new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType=new("application/json"); request.Headers.Accept.Add(new("application/json"));
        if(policy.Credential.Length>0) request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",policy.Credential);
        try
        {
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token);
            if(!response.IsSuccessStatusCode || response.Content.Headers.ContentLength>MaxWireBytes || response.Content.Headers.ContentEncoding.Count!=0) throw new LocalAiException(502,"local-ai-provider-unavailable");
            await using var input=await response.Content.ReadAsStreamAsync(deadline.Token); using var buffer=new MemoryStream();
            var chunk=new byte[8192];
            try
            {
                int count; while((count=await input.ReadAsync(chunk,deadline.Token))>0) { if(buffer.Length+count>MaxWireBytes) throw new LocalAiException(502,"local-ai-provider-unavailable"); await buffer.WriteAsync(chunk.AsMemory(0,count),deadline.Token); }
                var responseBytes=buffer.ToArray();
                try
                {
                using var parsed=StrictJson.Parse(responseBytes,MaxWireBytes);
                var choices=parsed.RootElement.GetProperty("choices"); if(choices.GetArrayLength()!=1) throw new LocalAiException(502,"local-ai-provider-unavailable");
                var choice=choices[0]; var message=choice.GetProperty("message");
                if(choice.GetProperty("finish_reason").GetString()!="stop" || message.TryGetProperty("tool_calls",out _) || message.TryGetProperty("function_call",out _)) throw new LocalAiException(502,"local-ai-provider-unavailable");
                var result=message.GetProperty("content").GetString();
                if(result==null || Encoding.UTF8.GetByteCount(result)>MaxOutputBytes) throw new LocalAiException(502,"local-ai-provider-unavailable");
                return result;
                }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(responseBytes); }
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(chunk); if(buffer.TryGetBuffer(out var memory)) System.Security.Cryptography.CryptographicOperations.ZeroMemory(memory.AsSpan()); }
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
        catch(Exception ex) when(ex is not OutOfMemoryException) { throw new LocalAiException(502,"local-ai-provider-unavailable"); }
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }
}
