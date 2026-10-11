using Box.Sdk.Gen;
using Box.Sdk.Gen.Schemas;
using Task = System.Threading.Tasks.Task;

namespace WriteableFileProviders.Box;

/// <summary>
/// The provider's own sign-in, with the token request left out of its errors. When Box refuses a token, the SDK's
/// <see cref="BoxApiException"/> keeps the request it sent, whose body holds the client secret and the signed assertion;
/// this rethrows it with Box's answer only.
/// </summary>
/// <param name="inner">The JWT authentication.</param>
internal sealed class RedactedAuthentication(IAuthentication inner) : IAuthentication
{
    public Task<AccessToken> RetrieveTokenAsync(NetworkSession? networkSession = null) =>
        RedactAsync(() => inner.RetrieveTokenAsync(networkSession));

    public Task<AccessToken> RefreshTokenAsync(NetworkSession? networkSession = null) =>
        RedactAsync(() => inner.RefreshTokenAsync(networkSession));

    public Task<string> RetrieveAuthorizationHeaderAsync(NetworkSession? networkSession = null) =>
        RedactAsync(() => inner.RetrieveAuthorizationHeaderAsync(networkSession));

    public Task RevokeTokenAsync(NetworkSession? networkSession = null) =>
        RedactAsync(async () =>
        {
            await inner.RevokeTokenAsync(networkSession).ConfigureAwait(false);
            return true;
        });

    public Task<AccessToken> DownscopeTokenAsync(IReadOnlyList<string> scopes, string? resource = null, string? sharedLink = null, NetworkSession? networkSession = null) =>
        RedactAsync(() => inner.DownscopeTokenAsync(scopes, resource, sharedLink, networkSession));

    /// <summary>The same error without the request's body, headers and parameters.</summary>
    internal static BoxApiException Redact(BoxApiException e) =>
        new(e.ResponseInfo.RawBody ?? "", e.Timestamp ?? DateTimeOffset.UtcNow,
            new RequestInfo(e.RequestInfo.Method, e.RequestInfo.Url, null, new Dictionary<string, string>(), e.RequestInfo.ContentType),
            e.ResponseInfo);

    private static async Task<T> RedactAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (BoxApiException e)
        {
            throw Redact(e);
        }
    }
}
