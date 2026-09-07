using System.Net;

namespace Knowz.MCP.Tests.Services;

/// <summary>
/// Records every outbound request a backend makes and answers from a caller-supplied responder.
/// Bodies are buffered eagerly because <see cref="HttpRequestMessage.Content"/> is disposed once
/// the caller's <c>using</c> scope ends — asserting on it afterwards would otherwise throw.
/// </summary>
public sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _responder;

    public RecordingHttpMessageHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public List<RecordedRequest> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);

        Requests.Add(new RecordedRequest(
            request.Method,
            request.RequestUri!,
            request.Content?.Headers.ContentType?.MediaType,
            body,
            request.Headers.TryGetValues("X-Api-Key", out var keys) ? keys.FirstOrDefault() : null));

        return _responder(request, Requests.Count - 1);
    }

    public static HttpResponseMessage Json(HttpStatusCode statusCode, string json) =>
        new(statusCode) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
}

public sealed record RecordedRequest(
    HttpMethod Method,
    Uri Uri,
    string? ContentType,
    string Body,
    string? ApiKey)
{
    public string Path => Uri.AbsolutePath;
    public string Query => Uri.Query;
}

public sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}
