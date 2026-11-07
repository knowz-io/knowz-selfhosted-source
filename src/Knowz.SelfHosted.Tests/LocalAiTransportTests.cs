using System.Net;
using System.Text;
using System.Text.Json;
using Knowz.SelfHosted.Infrastructure.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Tests;

public sealed class LocalAiTransportTests
{
    private sealed class Server(WebApplication app):IAsyncDisposable
    {
        public string Origin=>app.Urls.Single();
        public ValueTask DisposeAsync()=>app.DisposeAsync();
    }
    private static async Task<Server> StartAsync(RequestDelegate reply)
    {
        var builder=WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app=builder.Build(); app.Run(reply); await app.StartAsync(); return new(app);
    }
    private static LocalAiProviderPolicy Policy(string origin,string address="127.0.0.1")=>new LocalAiPolicy(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
    { ["PortableLocalAi:Enabled"]="true",["PortableLocalAi:AllowedAddresses:0"]=address,["OpenAiCompatible:Endpoint"]=origin+"/v1/",["OpenAiCompatible:ChatModel"]="synthetic-exact-model",["OpenAiCompatible:ApiKey"]="synthetic-local-credential" }).Build()).Read();
    private static string Response(string content)=>JsonSerializer.Serialize(new { choices=new[] { new { finish_reason="stop",message=new { content } } } });

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task ExactPinnedRequestReturnsOnlyAnUnsavedDocumentIncludingExplicitEmptyResult(bool empty)
    {
        var calls=0; string? request=null,path=null,credential=null;
        await using var server=await StartAsync(async http=> { calls++; path=http.Request.Path; credential=http.Request.Headers.Authorization; using var reader=new StreamReader(http.Request.Body); request=await reader.ReadToEndAsync(); http.Response.ContentType="application/json"; await http.Response.WriteAsync(Response(empty?"":"amended é document")); });
        var result=await new LocalAiTransport().AmendAsync(Policy(server.Origin),"original é document","Amend only",default);
        Assert.Equal(empty?"":"amended é document",result); Assert.Equal(1,calls); Assert.Equal("/v1/chat/completions",path); Assert.Equal("Bearer synthetic-local-credential",credential);
        using var parsed=JsonDocument.Parse(request!); Assert.Equal("synthetic-exact-model",parsed.RootElement.GetProperty("model").GetString()); Assert.False(parsed.RootElement.GetProperty("stream").GetBoolean()); Assert.False(parsed.RootElement.TryGetProperty("tools",out _));
    }
    [Theory]
    [InlineData("redirect")][InlineData("tool")][InlineData("function")][InlineData("unfinished")][InlineData("duplicate")][InlineData("null")][InlineData("utf8-output")][InlineData("chunked-wire")][InlineData("declared-wire")][InlineData("compressed")][InlineData("malformed")]
    public async Task UnusableProviderReplyNeverRetriesOrExposesResponseContent(string mode)
    {
        var calls=0;
        await using var server=await StartAsync(async http=>
        {
            calls++;
            if(mode=="redirect") { http.Response.StatusCode=307;http.Response.Headers.Location="/must-not-follow";return; }
            if(mode=="declared-wire") { http.Response.ContentLength=LocalAiTransport.MaxWireBytes+1;await http.Response.StartAsync();return; }
            if(mode=="compressed") http.Response.Headers.ContentEncoding="gzip";
            if(mode=="chunked-wire") { await http.Response.StartAsync();for(var i=0;i<66;i++){await http.Response.WriteAsync(new string('x',8192));await http.Response.Body.FlushAsync();}return; }
            var body=mode switch
            {
                "tool"=>"{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"PRIVATE_OUTPUT\",\"tool_calls\":[]}}]}",
                "function"=>"{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"PRIVATE_OUTPUT\",\"function_call\":{}}}]}",
                "unfinished"=>"{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"PRIVATE_OUTPUT\"}}]}",
                "duplicate"=>"{\"choices\":[],\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"PRIVATE_OUTPUT\"}}]}",
                "null"=>"{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":null}}]}",
                "utf8-output"=>Response(new string('é',32769)),
                _=>"PRIVATE_OUTPUT not JSON"
            };
            await http.Response.WriteAsync(body);
        });
        var error=await Assert.ThrowsAsync<LocalAiException>(()=>new LocalAiTransport().AmendAsync(Policy(server.Origin),"PRIVATE_INPUT","PRIVATE_INSTRUCTION",default));
        Assert.Equal(502,error.StatusCode); Assert.Equal(1,calls); Assert.Null(error.InnerException); Assert.DoesNotContain("PRIVATE",error.ToString()); Assert.DoesNotContain("credential",error.ToString());
    }
    [Fact]
    public async Task CallerCancellationStopsPendingResponseWithoutRetry()
    {
        var called=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls=0;
        await using var server=await StartAsync(async http=>{calls++;called.TrySetResult();await Task.Delay(Timeout.Infinite,http.RequestAborted);});
        using var cancellation=new CancellationTokenSource(); var pending=new LocalAiTransport().AmendAsync(Policy(server.Origin),"content","instruction",cancellation.Token);
        await called.Task.WaitAsync(TimeSpan.FromSeconds(5));cancellation.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);Assert.Equal(1,calls);
    }
    [Fact]
    public async Task HostnameWithAnyAddressOutsideTheExactPinsCannotDispatch()
    {
        var calls=0;await using var server=await StartAsync(http=>{calls++;return http.Response.WriteAsync(Response("wrong destination"));});
        var port=new Uri(server.Origin).Port;var policy=Policy($"http://localhost:{port}","127.0.0.2");Assert.True(policy.Supported);
        var error=await Assert.ThrowsAsync<LocalAiException>(()=>new LocalAiTransport().AmendAsync(policy,"content","instruction",default));Assert.Equal(502,error.StatusCode);Assert.Equal(0,calls);
    }
    [Fact]
    public async Task MappedLoopbackLiteralUsesItsExactNormalizedLocalPin()
    {
        using var listener=new System.Net.Sockets.TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;
        using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var served=Task.Run(async ()=>
        {
            using var client=await listener.AcceptTcpClientAsync(limit.Token);await using var stream=client.GetStream();using var reader=new StreamReader(stream,Encoding.ASCII,leaveOpen:true);
            Assert.Equal("POST /v1/chat/completions HTTP/1.1",await reader.ReadLineAsync(limit.Token));
            string? line;var length=0;while(!string.IsNullOrEmpty(line=await reader.ReadLineAsync(limit.Token)))if(line.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase))length=int.Parse(line.Split(':')[1]);
            var body=new char[length];Assert.Equal(length,await reader.ReadBlockAsync(body.AsMemory(),limit.Token));
            var reply=Response("mapped local result");await stream.WriteAsync(Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(reply)}\r\nConnection: close\r\n\r\n{reply}"),limit.Token);
        },limit.Token);
        var policy=Policy($"http://[::ffff:127.0.0.1]:{port}");Assert.True(policy.Supported);
        Assert.Equal("mapped local result",await new LocalAiTransport().AmendAsync(policy,"content","instruction",limit.Token));await served;
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task OversizedUtf8InputCannotContactTheProvider(bool body)
    {
        var calls=0;await using var server=await StartAsync(http=>{calls++;return http.Response.WriteAsync(Response("unreachable"));});
        await Assert.ThrowsAsync<LocalAiException>(()=>new LocalAiTransport().AmendAsync(Policy(server.Origin),body?new string('é',32769):"body",body?"instruction":new string('é',2049),default));Assert.Equal(0,calls);
    }
}
