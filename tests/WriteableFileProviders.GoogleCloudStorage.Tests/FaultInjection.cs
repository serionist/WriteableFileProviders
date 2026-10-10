using System.Net;
using System.Text;
using System.Text.Json;
using Google.Apis.Http;

namespace WriteableFileProviders.GoogleCloudStorage.Tests;

/// <summary>
/// An HTTP seam for a <see cref="Google.Cloud.Storage.V1.StorageClient"/>: every request the client sends, the
/// provider's own downloads included, passes through <see cref="OnRequest"/> first, which can change it, act before it
/// (a write racing the provider), or answer it instead of the server.
/// </summary>
internal sealed class FaultInjection : HttpClientFactory
{
    /// <summary>Called for each request; return a response to answer it, or null to send it on.</summary>
    public Func<HttpRequestMessage, Task<HttpResponseMessage?>>? OnRequest { get; set; }

    /// <summary>The requests seen, as "METHOD path?query".</summary>
    public List<string> Requests { get; } = [];

    /// <summary>A JSON error response, as Cloud Storage sends one.</summary>
    public static HttpResponseMessage Error(HttpStatusCode status, string message = "injected") => new(status)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new { error = new { code = (int)status, message, errors = new[] { new { message, reason = "injected" } } } }),
            Encoding.UTF8, "application/json"),
    };

    /// <summary>Whether the request is a media download.</summary>
    public static bool IsDownload(HttpRequestMessage request) =>
        request.Method == HttpMethod.Get && request.RequestUri!.Query.Contains("alt=media", StringComparison.Ordinal);

    /// <summary>Whether the request is a server copy (rewrite).</summary>
    public static bool IsCopy(HttpRequestMessage request) =>
        request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.Contains("/rewriteTo/", StringComparison.Ordinal);

    /// <summary>Whether the request deletes an object.</summary>
    public static bool IsDelete(HttpRequestMessage request) => request.Method == HttpMethod.Delete;

    /// <summary>Whether the request starts an upload.</summary>
    public static bool IsUploadStart(HttpRequestMessage request) =>
        request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.StartsWith("/upload/", StringComparison.Ordinal);

    /// <summary>Whether the request creates a bucket.</summary>
    public static bool IsBucketCreate(HttpRequestMessage request) =>
        request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/b", StringComparison.Ordinal);

    protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => new Handler(this, base.CreateHandler(args));

    private sealed class Handler(FaultInjection owner, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (owner.Requests)
                owner.Requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            if (owner.OnRequest is { } hook && await hook(request) is { } answer)
            {
                answer.RequestMessage = request;
                return answer;
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
