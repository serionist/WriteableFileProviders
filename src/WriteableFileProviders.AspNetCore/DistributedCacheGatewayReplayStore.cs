using Microsoft.Extensions.Caching.Distributed;

namespace WriteableFileProviders.AspNetCore;

/// <summary>
/// An <see cref="IGatewayReplayStore"/> over an <see cref="IDistributedCache"/>, such as Redis, shared by every instance
/// of the app. Best effort: <see cref="IDistributedCache"/> has no atomic add-if-absent, so two instances that receive
/// the same token at the same moment can both accept it. Within one instance it is exact.
/// </summary>
/// <remarks>
/// Register it in place of the default with
/// <c>services.AddSingleton&lt;IGatewayReplayStore, DistributedCacheGatewayReplayStore&gt;()</c>. Where exactly-once
/// across instances matters, implement <see cref="IGatewayReplayStore"/> on the cache's own atomic operation (Redis
/// <c>SET NX</c>, a database unique key).
/// </remarks>
/// <param name="cache">The shared cache.</param>
/// <param name="timeProvider">The clock.</param>
public sealed class DistributedCacheGatewayReplayStore(IDistributedCache cache, TimeProvider timeProvider) : IGatewayReplayStore
{
    private const string KeyPrefix = "WriteableFileProviders.Gateway.v1.used:";
    private static readonly byte[] _marker = [1];

    private readonly IDistributedCache _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    private readonly InMemoryGatewayReplayStore _local = new(timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)));

    /// <inheritdoc />
    public async Task<bool> TryConsumeAsync(string tokenId, DateTimeOffset expiresOn, CancellationToken cancellationToken = default)
    {
        // This instance first, atomically, so concurrent requests here race on memory rather than on the cache.
        if (!await _local.TryConsumeAsync(tokenId, expiresOn, cancellationToken).ConfigureAwait(false))
            return false;
        var key = KeyPrefix + tokenId;
        if (await _cache.GetAsync(key, cancellationToken).ConfigureAwait(false) is not null)
            return false;
        var entry = new DistributedCacheEntryOptions { AbsoluteExpiration = expiresOn + InMemoryGatewayReplayStore.Grace };
        await _cache.SetAsync(key, _marker, entry, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
