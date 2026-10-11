using Box.Sdk.Gen;

namespace WriteableFileProviders.Box;

/// <summary>
/// The provider's network client: the session's own, with the request left out of its errors. The SDK's
/// <see cref="BoxApiException"/> keeps the request it sent: its <c>Authorization</c> header (the access token) and its
/// body (for a token request, the client secret and the signed assertion). This rethrows every one with the status,
/// the code and the message, and the request without its body, without those headers and without such parameters.
/// Retries and token refreshes happen inside the session's client, before an error comes out.
/// </summary>
/// <param name="inner">The session's network client.</param>
internal sealed class RedactingNetworkClient(INetworkClient inner) : INetworkClient
{
    // The SDK's DataSanitizer keys, and the JWT assertion of a token request.
    private static readonly HashSet<string> _secrets = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "access_token", "refresh_token", "subject_token", "token", "client_id", "client_secret", "shared_link",
        "download_url", "jwt_private_key", "jwt_private_key_passphrase", "password", "assertion",
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

    /// <summary>The same error, with the request's body, credentials and secret parameters left out.</summary>
    internal static BoxApiException Redact(BoxApiException e) =>
        new(e.Message, e.Timestamp ?? DateTimeOffset.UtcNow,
            new RequestInfo(e.RequestInfo.Method, e.RequestInfo.Url, Keep(e.RequestInfo.QueryParams), Keep(e.RequestInfo.Headers), e.RequestInfo.ContentType),
            e.ResponseInfo)
        {
            DataSanitizer = e.DataSanitizer,
        };

    private static Dictionary<string, string> Keep(IReadOnlyDictionary<string, string>? values) =>
        values?.Where(p => !_secrets.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value) ?? [];
}
