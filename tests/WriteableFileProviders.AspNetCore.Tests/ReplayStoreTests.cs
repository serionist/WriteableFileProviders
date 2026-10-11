using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.AspNetCore.Tests;

/// <summary>The one-time token stores: the in-memory default and the distributed cache adapter.</summary>
[TestClass]
public sealed class ReplayStoreTests
{
    private static readonly DateTimeOffset _start = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task InMemory_AcceptsAnIdOnce()
    {
        var store = new InMemoryGatewayReplayStore(new ManualTimeProvider(_start));

        Assert.IsTrue(await store.TryConsumeAsync("a", _start.AddHours(1)));
        Assert.IsFalse(await store.TryConsumeAsync("a", _start.AddHours(1)));
        Assert.IsTrue(await store.TryConsumeAsync("b", _start.AddHours(1)));
    }

    [TestMethod]
    public async Task InMemory_ConcurrentCalls_AcceptExactlyOne()
    {
        var store = new InMemoryGatewayReplayStore(TimeProvider.System);

        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => store.TryConsumeAsync("same", DateTimeOffset.UtcNow.AddHours(1)))));

        Assert.AreEqual(1, results.Count(accepted => accepted));
    }

    [TestMethod]
    public async Task InMemory_ForgetsIdsAfterExpiryAndGrace()
    {
        var clock = new ManualTimeProvider(_start);
        var store = new InMemoryGatewayReplayStore(clock);
        Assert.IsTrue(await store.TryConsumeAsync("old", _start.AddMinutes(1)));
        Assert.IsTrue(await store.TryConsumeAsync("long", _start.AddHours(1)));

        // Expired, but within the grace: kept.
        clock.Now = _start.AddMinutes(4);
        Assert.IsTrue(await store.TryConsumeAsync("x", _start.AddHours(1)));
        Assert.AreEqual(3, store.Count);
        Assert.IsFalse(await store.TryConsumeAsync("old", _start.AddMinutes(1)));

        // Past the grace: the next call sweeps it away.
        clock.Now = _start.AddMinutes(1) + InMemoryGatewayReplayStore.Grace + TimeSpan.FromMinutes(1);
        Assert.IsTrue(await store.TryConsumeAsync("y", _start.AddHours(1)));
        Assert.AreEqual(3, store.Count);
        Assert.IsFalse(await store.TryConsumeAsync("long", _start.AddHours(1)));
    }

    [TestMethod]
    public async Task InMemory_SweepsAtMostOncePerInterval()
    {
        var clock = new ManualTimeProvider(_start);
        var store = new InMemoryGatewayReplayStore(clock);
        // Long expired when it is added, just after this call's sweep.
        Assert.IsTrue(await store.TryConsumeAsync("stale", _start.AddMinutes(-10)));

        clock.Now = _start.AddSeconds(30);
        Assert.IsTrue(await store.TryConsumeAsync("second", _start.AddHours(1)));
        Assert.AreEqual(2, store.Count);

        clock.Now = _start.AddSeconds(61);
        Assert.IsTrue(await store.TryConsumeAsync("third", _start.AddHours(1)));
        Assert.AreEqual(2, store.Count);
    }

    [TestMethod]
    public async Task InMemory_InvalidArguments_Throw()
    {
        var store = new InMemoryGatewayReplayStore(TimeProvider.System);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => store.TryConsumeAsync(null!, DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryConsumeAsync("", DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.TryConsumeAsync("a", DateTimeOffset.UtcNow, cancelled.Token));
    }

    [TestMethod]
    public async Task Distributed_AcceptsAnIdOnce_AcrossInstances()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var first = new DistributedCacheGatewayReplayStore(cache, TimeProvider.System);
        var second = new DistributedCacheGatewayReplayStore(cache, TimeProvider.System);
        var expires = DateTimeOffset.UtcNow.AddHours(1);

        Assert.IsTrue(await first.TryConsumeAsync("a", expires));
        Assert.IsFalse(await first.TryConsumeAsync("a", expires));
        Assert.IsFalse(await second.TryConsumeAsync("a", expires));
        Assert.IsTrue(await second.TryConsumeAsync("b", expires));
        Assert.IsFalse(await first.TryConsumeAsync("b", expires));
        Assert.IsNotNull(await cache.GetAsync("WriteableFileProviders.Gateway.v1.used:a"));
    }

    [TestMethod]
    public async Task Distributed_EntryExpiresAfterTheTokenAndGrace()
    {
        var cache = new RecordingCache();
        var store = new DistributedCacheGatewayReplayStore(cache, TimeProvider.System);
        var expires = DateTimeOffset.UtcNow.AddHours(1);

        Assert.IsTrue(await store.TryConsumeAsync("a", expires));

        Assert.AreEqual(expires + InMemoryGatewayReplayStore.Grace, cache.LastOptions?.AbsoluteExpiration);
    }

    [TestMethod]
    public void Distributed_NullArguments_Throw()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

        Assert.ThrowsExactly<ArgumentNullException>(() => new DistributedCacheGatewayReplayStore(null!, TimeProvider.System));
        Assert.ThrowsExactly<ArgumentNullException>(() => new DistributedCacheGatewayReplayStore(cache, null!));
    }

    private sealed class RecordingCache : IDistributedCache
    {
        private readonly Dictionary<string, byte[]> _values = [];

        public DistributedCacheEntryOptions? LastOptions { get; private set; }

        public byte[]? Get(string key) => _values.GetValueOrDefault(key);

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));

        public void Refresh(string key)
        {
        }

        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

        public void Remove(string key) => _values.Remove(key);

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            _values[key] = value;
            LastOptions = options;
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }
    }
}
