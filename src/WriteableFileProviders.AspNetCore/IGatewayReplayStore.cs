namespace WriteableFileProviders.AspNetCore;

/// <summary>
/// Remembers which one-time upload tokens the gateway has accepted, so each works once
/// (<see cref="WriteableFileProviderGatewayOptions.OneTimeUploadTokens"/>).
/// </summary>
/// <remarks>
/// The default store holds the tokens in the memory of one app instance: it is exact there, but another instance of the
/// same app knows nothing of them, and a restart forgets them. An app that runs on several instances registers a store
/// they share, such as <see cref="DistributedCacheGatewayReplayStore"/> (best effort) or one built on an atomic
/// add-if-absent of its own store; registering one replaces the default.
/// </remarks>
public interface IGatewayReplayStore
{
    /// <summary>
    /// Marks a token as used, atomically: of any number of concurrent calls with the same <paramref name="tokenId"/>, only
    /// the first returns true.
    /// </summary>
    /// <param name="tokenId">The token's unique id.</param>
    /// <param name="expiresOn">When the token expires; the store need not remember it after that.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True the first time the token is seen; false when it was already used.</returns>
    Task<bool> TryConsumeAsync(string tokenId, DateTimeOffset expiresOn, CancellationToken cancellationToken = default);
}
