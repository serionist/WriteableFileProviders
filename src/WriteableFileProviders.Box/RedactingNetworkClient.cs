using Box.Sdk.Gen;

namespace WriteableFileProviders.Box;

/// <summary>
/// The provider's network client: the session's own, with the request left out of its errors. The SDK's
/// <see cref="BoxApiException"/> keeps the request it sent: its <c>Authorization</c> header (the access token), any extra
/// header a client was built with (a <c>BoxApi</c> shared-link password, a proxy's or a gateway's credentials), its query
/// string and its body (for a token request, the client secret and the signed assertion). This rethrows every one with
/// Box's status, code and message, and only what diagnoses the request: its method, its URL without the query, and the
/// headers and parameters on short allow-lists. It fails closed: anything not on a list is left out, the response's
/// headers too. Retries and token refreshes happen inside the session's client, before an error comes out.
/// </summary>
/// <param name="inner">The session's network client.</param>
internal sealed class RedactingNetworkClient(INetworkClient inner) : INetworkClient
{
    private static readonly HashSet<string> _requestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "As-User", "If-Match", "Range", "Digest", "Content-MD5", "Content-Type",
    };

    private static readonly HashSet<string> _queryParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "fields", "usemarker", "marker", "limit", "offset", "version", "recursive",
    };

    private static readonly HashSet<string> _responseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Retry-After", "Content-Type", "Date", "Box-Request-Id",
    };

    public async Task<FetchResponse> FetchAsync(FetchOptions options)
    {
        try
        {
            return await inner.FetchAsync(options).ConfigureAwait(false);
        }
        catch (BoxApiException e)
        {
            throw Redact(e);
        }
    }

    /// <summary>The same error, with only the allow-listed parts of the request and of the response's headers.</summary>
    internal static BoxApiException Redact(BoxApiException e)
    {
        var request = e.RequestInfo;
        var response = e.ResponseInfo;
        return new BoxApiException(e.Message, e.Timestamp ?? DateTimeOffset.UtcNow,
            new RequestInfo(request.Method, WithoutQuery(request.Url), Keep(request.QueryParams, _queryParameters), Keep(request.Headers, _requestHeaders), request.ContentType),
            new ResponseInfo(response.StatusCode, Keep(response.Headers, _responseHeaders), response.Body ?? JsonUtils.JsonToSerializedData("{}"),
                response.RawBody ?? "", response.Code, response.ContextInfo, response.RequestId, response.HelpUrl))
        {
            DataSanitizer = e.DataSanitizer,
        };
    }

    private static Dictionary<string, string> Keep(IReadOnlyDictionary<string, string>? values, HashSet<string> allowed) =>
        values?.Where(p => allowed.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value) ?? [];

    // The scheme, host, port and path: no user info, query or fragment. Only an http(s) URL is parsed: on Unix a rooted
    // path such as "/2.0/files/1?access_token=…" parses as an absolute file URI whose path holds the escaped query, so
    // anything else is cut at its first '?' or '#'.
    private static string? WithoutQuery(string? url) =>
        url is null ? null
        : Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
            : url.Split('?', '#')[0];
}
