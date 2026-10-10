using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Web;
using Box.Sdk.Gen;
using Task = System.Threading.Tasks.Task;

namespace WriteableFileProviders.Box.Tests.Fake;

/// <summary>
/// The Box SDK's network client, over an <see cref="HttpMessageHandler"/> instead of the internet: the SDK has no
/// HttpClient seam, only <see cref="NetworkSession.NetworkClient"/>. It copies what the SDK's internal
/// <c>BoxNetworkClient</c> (10.19.0) does that a provider can notice, so a test sees what production sees:
/// <list type="bullet">
/// <item>a request stream is rewound to 0 and must have a length; a multipart file part with both a file name and a
/// content type is renamed <c>pic</c> (the SDK's avatar upload);</item>
/// <item>headers are added with validation, so an unquoted <c>If-Match</c> throws <see cref="FormatException"/>;</item>
/// <item>a 3xx is returned as it is when <c>FollowRedirects</c> is false, else followed with a bare GET that drops the
/// headers (a <c>Range</c>), the parameters and the cancellation token, and carries the authentication only to the same
/// origin;</item>
/// <item>response headers come from the response, not its content;</item>
/// <item>a failure goes through the session's retry strategy (5xx, 429, and 401 with a token refresh), then throws
/// <see cref="BoxApiException"/>; a network exception is retried, then throws <see cref="BoxSdkException"/>; a 2xx is
/// never retried.</item>
/// </list>
/// </summary>
/// <param name="handler">Serves the requests: a <see cref="FakeBox"/>.</param>
internal sealed class FakeNetworkClient(HttpMessageHandler handler) : INetworkClient
{
    private readonly HttpMessageInvoker _invoker = new(handler, disposeHandler: false);

    public async Task<FetchResponse> FetchAsync(FetchOptions options)
    {
        // A request without a session would go to the real api.box.com.
        var session = options.NetworkSession ?? throw new InvalidOperationException("Every request must carry its network session.");
        var cancellationToken = options.CancellationToken ?? CancellationToken.None;
        byte[]? body = null;
        if (options.FileStream is { } fileStream && options.ContentType == "application/octet-stream")
            body = await ReadFromStartAsync(fileStream).ConfigureAwait(false);
        var attempt = 1;
        var networkAttempt = 1;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = await BuildRequestAsync(options, session, body).ConfigureAwait(false);
            HttpResponseMessage response;
            try
            {
                response = await _invoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException e)
            {
                var failed = new FetchResponse(0, []);
                if (!await session.RetryStrategy.ShouldRetryAsync(options, failed, networkAttempt).ConfigureAwait(false))
                    throw new BoxSdkException($"Network error. Max retry attempts exceeded. {e.Message}", DateTimeOffset.UtcNow);
                await Task.Delay(TimeSpan.FromSeconds(session.RetryStrategy.RetryAfter(options, failed, networkAttempt)), cancellationToken).ConfigureAwait(false);
                if (options.Auth is not null)
                    await options.Auth.RefreshTokenAsync(session).ConfigureAwait(false);
                networkAttempt++;
                continue;
            }

            var status = (int)response.StatusCode;
            var headers = response.Headers.ToDictionary(h => h.Key, h => h.Value.First());
            var url = request.RequestUri!.ToString();
            if (status is >= 300 and < 400)
            {
                if (options.FollowRedirects == false)
                    return await ReadAsync(options, response, status, headers, url, cancellationToken).ConfigureAwait(false);
                var location = response.Headers.Location ?? throw new BoxSdkException($"Redirect response missing Location header for: {options.Url}");
                var origin = request.RequestUri;
                var sameOrigin = origin.Scheme == location.Scheme && origin.Host == location.Host && origin.Port == location.Port;
                return await FetchAsync(new FetchOptions(location.ToString(), "GET", options.ContentType, options.ResponseFormat)
                {
                    Auth = sameOrigin ? options.Auth : null,
                    NetworkSession = session,
                }).ConfigureAwait(false);
            }
            if (response.IsSuccessStatusCode)
                return await ReadAsync(options, response, status, headers, url, cancellationToken).ConfigureAwait(false);

            var error = new FetchResponse(status, headers) { Url = url };
            if (await session.RetryStrategy.ShouldRetryAsync(options, error, attempt).ConfigureAwait(false))
            {
                await Task.Delay(TimeSpan.FromSeconds(session.RetryStrategy.RetryAfter(options, error, attempt)), cancellationToken).ConfigureAwait(false);
                response.Dispose();
                attempt++;
                continue;
            }
            throw await ApiExceptionAsync(request, response, options, status, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<FetchResponse> ReadAsync(FetchOptions options, HttpResponseMessage response, int status, Dictionary<string, string> headers, string url, CancellationToken cancellationToken) =>
        options.ResponseFormat == ResponseFormat.Binary
            ? new FetchResponse(status, headers) { Url = url, Content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false) }
            : new FetchResponse(status, headers) { Url = url, Data = JsonUtils.JsonToSerializedData(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)) };

    private static async Task<HttpRequestMessage> BuildRequestAsync(FetchOptions options, NetworkSession session, byte[]? body)
    {
        var uri = options.Parameters is { Count: > 0 } parameters
            ? options.Url + "?" + string.Join('&', parameters.Select(p => $"{HttpUtility.UrlEncode(p.Key)}={HttpUtility.UrlEncode(p.Value)}"))
            : options.Url;
        var request = new HttpRequestMessage(new HttpMethod(options.Method), new Uri(uri)) { Content = await BuildContentAsync(options, body).ConfigureAwait(false) };
        foreach (var header in session.AdditionalHeaders)
            request.Headers.Add(header.Key, header.Value);
        foreach (var header in options.Headers ?? [])
        {
            if (header.Key == "content-range")
                request.Content!.Headers.Add(header.Key, header.Value);
            else
                request.Headers.Add(header.Key, header.Value);
        }
        if (options.Auth is not null)
            request.Headers.Add("Authorization", await options.Auth.RetrieveAuthorizationHeaderAsync(session).ConfigureAwait(false));
        return request;
    }

    private static async Task<HttpContent?> BuildContentAsync(FetchOptions options, byte[]? body)
    {
        switch (options.ContentType)
        {
            case "multipart/form-data":
                var multipart = new MultipartFormDataContent();
                foreach (var part in options.MultipartData ?? throw new BoxSdkException("Could not upload file. MultipartData on FetchOptions is null"))
                {
                    if (part.FileStream is { } stream)
                    {
                        var content = new ByteArrayContent(await ReadFromStartAsync(stream).ConfigureAwait(false));
                        // The SDK's avatar upload: a part with both is renamed "pic", whatever its name.
                        multipart.Add(content, part.ContentType is not null && part.FileName is not null ? "pic" : part.PartName, "file");
                    }
                    else
                    {
                        multipart.Add(new StringContent(JsonUtils.SdToJson(part.Data!)), part.PartName);
                    }
                }
                return multipart;
            case "application/x-www-form-urlencoded" when options.Data is not null:
                return new FormUrlEncodedContent(JsonSerializer.Deserialize<Dictionary<string, string>>(JsonUtils.SdToJson(options.Data))!);
            case "application/octet-stream":
                return body is null ? null : new ByteArrayContent(body);
            default:
                if (options.Data is null)
                    return null;
                var json = new StringContent(JsonUtils.SdToJson(options.Data), Encoding.UTF8);
                json.Headers.ContentType = new MediaTypeHeaderValue(options.ContentType);
                return json;
        }
    }

    // The SDK's ReusableContent: reads the length (so the stream must seek), rewinds to 0, and sends to the end.
    private static async Task<byte[]> ReadFromStartAsync(Stream stream)
    {
        _ = stream.Length;
        stream.Position = 0;
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy).ConfigureAwait(false);
        return copy.ToArray();
    }

    private static async Task<BoxApiException> ApiExceptionAsync(HttpRequestMessage request, HttpResponseMessage response, FetchOptions options, int status, CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var headers = response.Headers.Where(h => h.Value.Any()).ToDictionary(h => h.Key, h => h.Value.First());
        response.Dispose();
        string? code = null, requestId = null, helpUrl = null;
        Dictionary<string, object>? contextInfo = null;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                using var document = JsonDocument.Parse(raw);
                var root = document.RootElement;
                if (root.TryGetProperty("code", out var c))
                    code = c.GetString();
                if (root.TryGetProperty("request_id", out var r))
                    requestId = r.GetString();
                if (root.TryGetProperty("help_url", out var h))
                    helpUrl = h.GetString();
                if (root.TryGetProperty("context_info", out var info) && info.ValueKind == JsonValueKind.Object)
                    contextInfo = JsonSerializer.Deserialize<Dictionary<string, object>>(info.GetRawText());
            }
            catch (JsonException)
            {
            }
        }
        var requestInfo = new RequestInfo(request.Method.Method, request.RequestUri?.ToString(), options.Parameters,
            request.Headers.Where(h => h.Value.Any()).ToDictionary(h => h.Key, h => h.Value.First()), options.ContentType);
        var responseInfo = new ResponseInfo(status, headers, JsonUtils.JsonToSerializedData(string.IsNullOrWhiteSpace(raw) ? "{}" : raw), raw, code, contextInfo, requestId, helpUrl);
        return new BoxApiException(raw, DateTimeOffset.UtcNow, requestInfo, responseInfo);
    }
}
