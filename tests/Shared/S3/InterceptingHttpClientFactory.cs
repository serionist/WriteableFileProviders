using System.Net;
using System.Text;
using Amazon.Runtime;

namespace WriteableFileProviders.S3.Testing;

/// <summary>
/// The HTTP seam of the AWS SDK: every request of a client built with this factory passes through
/// <see cref="Intercept"/>, which can answer it (an S3 error, a fault) or let it through to the server.
/// </summary>
internal sealed class InterceptingHttpClientFactory : HttpClientFactory
{
    private readonly List<string> _requests = [];

    /// <summary>Answers a request, or returns null to send it to the server.</summary>
    public Func<HttpRequestMessage, Task<HttpResponseMessage?>>? Intercept { get; set; }

    /// <summary>Every request seen, as <c>METHOD path?query</c>, in order.</summary>
    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_requests)
                return [.. _requests];
        }
    }

    /// <summary>The headers of the last request seen.</summary>
    public Dictionary<string, string> LastHeaders { get; private set; } = [];

    /// <inheritdoc />
    public override HttpClient CreateHttpClient(IClientConfig clientConfig) =>
        new(new Handler(this, new SocketsHttpHandler())) { Timeout = TimeSpan.FromMinutes(5) };

    /// <inheritdoc />
    public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

    /// <inheritdoc />
    public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;

    /// <summary>An S3 error response.</summary>
    /// <param name="status">The status.</param>
    /// <param name="code">The S3 error code, such as <c>NoSuchKey</c>.</param>
    public static HttpResponseMessage Error(HttpStatusCode status, string code) => Xml(status,
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>{code}</Code><Message>Injected {code}</Message><RequestId>test</RequestId></Error>");

    /// <summary>An XML response.</summary>
    /// <param name="status">The status.</param>
    /// <param name="xml">The body.</param>
    public static HttpResponseMessage Xml(HttpStatusCode status, string xml) =>
        new(status) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };

    /// <summary>An empty response.</summary>
    /// <param name="status">The status.</param>
    public static HttpResponseMessage Empty(HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new ByteArrayContent([]) };

    /// <summary>A ListObjectsV2 page with these keys and common prefixes.</summary>
    /// <param name="keys">The object keys.</param>
    /// <param name="prefixes">The common prefixes, each ending in <c>/</c>.</param>
    /// <param name="next">The continuation token of a next page, or null for the last page.</param>
    public static HttpResponseMessage ListResult(IEnumerable<string> keys, IEnumerable<string>? prefixes = null, string? next = null) => Xml(HttpStatusCode.OK,
        "<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Name>bucket</Name>"
        + (next is null ? "<IsTruncated>false</IsTruncated>" : $"<IsTruncated>true</IsTruncated><NextContinuationToken>{next}</NextContinuationToken>")
        + string.Concat(keys.Select(k => $"<Contents><Key>{k}</Key><LastModified>2026-10-10T00:00:00.000Z</LastModified><ETag>\"e\"</ETag><Size>1</Size></Contents>"))
        + string.Concat((prefixes ?? []).Select(p => $"<CommonPrefixes><Prefix>{p}</Prefix></CommonPrefixes>"))
        + "</ListBucketResult>");

    /// <summary>The answer to CreateMultipartUpload.</summary>
    /// <param name="uploadId">The upload id.</param>
    public static HttpResponseMessage InitiateResult(string uploadId = "upload-1") => Xml(HttpStatusCode.OK,
        $"<InitiateMultipartUploadResult><Bucket>bucket</Bucket><Key>key</Key><UploadId>{uploadId}</UploadId></InitiateMultipartUploadResult>");

    /// <summary>The answer to CompleteMultipartUpload.</summary>
    public static HttpResponseMessage CompleteResult() => Xml(HttpStatusCode.OK,
        "<CompleteMultipartUploadResult><Bucket>bucket</Bucket><Key>key</Key><ETag>\"done-2\"</ETag></CompleteMultipartUploadResult>");

    /// <summary>The answer to CopyObject or UploadPartCopy.</summary>
    /// <param name="part">Whether it answers UploadPartCopy.</param>
    public static HttpResponseMessage CopyResult(bool part = false) => Xml(HttpStatusCode.OK, part
        ? "<CopyPartResult><ETag>\"part\"</ETag><LastModified>2026-10-10T00:00:00.000Z</LastModified></CopyPartResult>"
        : "<CopyObjectResult><ETag>\"copy\"</ETag><LastModified>2026-10-10T00:00:00.000Z</LastModified></CopyObjectResult>");

    /// <summary>Whether the request starts a multipart upload.</summary>
    /// <param name="request">The request.</param>
    public static bool IsInitiate(HttpRequestMessage request) =>
        request.Method == HttpMethod.Post && Query(request).Contains("uploads", StringComparison.Ordinal) && !Query(request).Contains("uploadId=", StringComparison.Ordinal);

    /// <summary>Whether the request is on the bucket itself (HEAD or PUT, no key), in the path or in the host name.</summary>
    /// <param name="request">The request.</param>
    /// <param name="bucket">The bucket.</param>
    public static bool IsBucket(HttpRequestMessage request, string bucket = "bucket") =>
        request.RequestUri!.Query.Length == 0
        && (request.RequestUri.AbsolutePath.Trim('/') == bucket
            || (request.RequestUri.AbsolutePath == "/" && request.RequestUri.Host.StartsWith(bucket + ".", StringComparison.Ordinal)));

    /// <summary>Whether the request uploads a part of a multipart upload.</summary>
    /// <param name="request">The request.</param>
    public static bool IsUploadPart(HttpRequestMessage request) =>
        request.Method == HttpMethod.Put && Query(request).Contains("uploadId=", StringComparison.Ordinal) && !IsCopy(request);

    /// <summary>Whether the request completes a multipart upload.</summary>
    /// <param name="request">The request.</param>
    public static bool IsComplete(HttpRequestMessage request) =>
        request.Method == HttpMethod.Post && Query(request).Contains("uploadId=", StringComparison.Ordinal);

    /// <summary>Whether the request aborts a multipart upload.</summary>
    /// <param name="request">The request.</param>
    public static bool IsAbort(HttpRequestMessage request) =>
        request.Method == HttpMethod.Delete && Query(request).Contains("uploadId=", StringComparison.Ordinal);

    /// <summary>Whether the request copies a part into a multipart upload.</summary>
    /// <param name="request">The request.</param>
    public static bool IsCopyPart(HttpRequestMessage request) =>
        IsCopy(request) && Query(request).Contains("uploadId=", StringComparison.Ordinal);

    /// <summary>Whether the request copies an object (or a part).</summary>
    /// <param name="request">The request.</param>
    public static bool IsCopy(HttpRequestMessage request) =>
        request.Method == HttpMethod.Put && request.Headers.Contains("x-amz-copy-source");

    /// <summary>Whether the request deletes a batch of objects.</summary>
    /// <param name="request">The request.</param>
    public static bool IsDeleteObjects(HttpRequestMessage request) =>
        request.Method == HttpMethod.Post && Query(request).Contains("delete", StringComparison.Ordinal);

    /// <summary>Whether the request lists objects.</summary>
    /// <param name="request">The request.</param>
    public static bool IsList(HttpRequestMessage request) =>
        request.Method == HttpMethod.Get && Query(request).Contains("list-type=2", StringComparison.Ordinal);

    private static string Query(HttpRequestMessage request) => request.RequestUri?.Query ?? "";

    private sealed class Handler(InterceptingHttpClientFactory owner, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (owner._requests)
                owner._requests.Add($"{request.Method} {request.RequestUri?.PathAndQuery}");
            owner.LastHeaders = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            if (owner.Intercept is { } intercept && await intercept(request) is { } response)
            {
                response.RequestMessage = request;
                return response;
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
