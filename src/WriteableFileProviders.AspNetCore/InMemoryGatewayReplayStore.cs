using System.Collections.Concurrent;

namespace WriteableFileProviders.AspNetCore;

/// <summary>
/// The default <see cref="IGatewayReplayStore"/>: the used token ids in this process's memory, added atomically. Exact on
/// one instance; other instances and a restart do not see them.
/// </summary>
/// <param name="timeProvider">The clock that decides when a used id can be forgotten.</param>
internal sealed class InMemoryGatewayReplayStore(TimeProvider timeProvider) : IGatewayReplayStore
{
    // An id is kept this long past its token's expiry, so a clock a little behind Data Protection's cannot forget a token
    // that still unprotects.
    internal static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _sweepInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _used = new(StringComparer.Ordinal);
    private long _nextSweepTicks;

    /// <summary>How many used ids the store holds.</summary>
    internal int Count => _used.Count;

    public Task<bool> TryConsumeAsync(string tokenId, DateTimeOffset expiresOn, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tokenId);
        cancellationToken.ThrowIfCancellationRequested();
        Sweep(timeProvider.GetUtcNow());
        return Task.FromResult(_used.TryAdd(tokenId, expiresOn));
    }

    private void Sweep(DateTimeOffset now)
    {
        var due = Interlocked.Read(ref _nextSweepTicks);
        if (now.UtcTicks < due || Interlocked.CompareExchange(ref _nextSweepTicks, (now + _sweepInterval).UtcTicks, due) != due)
            return;
        foreach (var entry in _used)
        {
            if (entry.Value + Grace < now)
                _used.TryRemove(entry);
        }
    }
}
