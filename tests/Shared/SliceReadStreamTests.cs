using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.Tests;

/// <summary>
/// The ranged read of a download (src/Shared/SliceReadStream.cs). Common, Box, GoogleCloudStorage and SharePoint each
/// compile their own copy, so this file is linked into each of their test projects and tests that copy.
/// </summary>
[TestClass]
public sealed class SliceReadStreamTests
{
    [TestMethod]
    public async Task SkipsThenStops_SyncAndAsync()
    {
        using (var sync = new SliceReadStream(ForwardOnly("0123456789"), 3, 4))
            Assert.AreEqual("3456", new StreamReader(sync).ReadToEnd());
        await using (var async = new SliceReadStream(ForwardOnly("0123456789"), 3, 4))
            Assert.AreEqual("3456", await new StreamReader(async).ReadToEndAsync());
        await using var bytes = new SliceReadStream(ForwardOnly("0123456789"), 8, 10);
        var buffer = new byte[10];
        Assert.AreEqual(2, await bytes.ReadAsync(buffer.AsMemory(0, 10)));
        Assert.AreEqual("89", Encoding.UTF8.GetString(buffer, 0, 2));
        Assert.AreEqual(0, await bytes.ReadAsync(buffer.AsMemory(0, 10)));
        // The array overload, which callers of the Stream API still use.
#pragma warning disable CA1835
        Assert.AreEqual(0, await bytes.ReadAsync(buffer, 0, 10));
#pragma warning restore CA1835
    }

    [TestMethod]
    public async Task WithoutALimit_ReadsTheRest()
    {
        await using var rest = new SliceReadStream(ForwardOnly("0123456789"), 6, null);
        Assert.AreEqual("6789", await new StreamReader(rest).ReadToEndAsync());
        using var whole = new SliceReadStream(ForwardOnly("0123"), 0, null);
        Assert.AreEqual("0123", new StreamReader(whole).ReadToEnd());
    }

    [TestMethod]
    public async Task SkipsBySeeking_WhenTheBodyCanSeek()
    {
        var body = new CountingStream(Encoding.UTF8.GetBytes("0123456789"));
        await using (var slice = new SliceReadStream(body, 3, 4))
        {
            await slice.SkipAsync(CancellationToken.None);
            Assert.AreEqual(3, body.Position);
            Assert.AreEqual(0, body.Reads, "A body that can seek is not read to skip");
            Assert.AreEqual("3456", await new StreamReader(slice).ReadToEndAsync());
        }
        var synchronous = new CountingStream(Encoding.UTF8.GetBytes("0123456789"));
        using (var slice = new SliceReadStream(synchronous, 7, null))
        {
            var buffer = new byte[10];
            Assert.AreEqual(3, slice.Read(buffer, 0, 10));
            Assert.AreEqual("789", Encoding.UTF8.GetString(buffer, 0, 3));
        }
    }

    [TestMethod]
    public async Task SkippingToOrPastTheEnd_ReadsNothing()
    {
        using (var sync = new SliceReadStream(ForwardOnly("012"), 5, 4))
            Assert.AreEqual(0, sync.Read(new byte[4], 0, 4));
        await using (var async = new SliceReadStream(ForwardOnly("012"), 5, null))
            Assert.AreEqual(0, await async.ReadAsync(new byte[4]));
        await using (var exactly = new SliceReadStream(ForwardOnly("012"), 3, null))
            Assert.AreEqual(0, await exactly.ReadAsync(new byte[4]));
        // A seekable body: no overflow even for the largest offset.
        await using (var seekable = new SliceReadStream(new MemoryStream(new byte[3]), long.MaxValue, long.MaxValue))
            Assert.AreEqual(0, await seekable.ReadAsync(new byte[4]));
        using (var seekable = new SliceReadStream(new MemoryStream(new byte[3]), 3, 1))
            Assert.AreEqual(0, seekable.Read(new byte[4], 0, 4));
    }

    [TestMethod]
    public async Task SkipAsync_SkipsNow_AndOnlyOnce()
    {
        var body = ForwardOnly("0123456789");
        await using var slice = new SliceReadStream(body, 4, null);
        await slice.SkipAsync(CancellationToken.None);
        Assert.AreEqual(4, body.Position, "Skipped by reading, before the first read");
        await slice.SkipAsync(CancellationToken.None);
        Assert.AreEqual(4, body.Position);
        Assert.AreEqual("456789", await new StreamReader(slice).ReadToEndAsync());
        await using var ended = new SliceReadStream(ForwardOnly("01"), 10, null);
        await ended.SkipAsync(CancellationToken.None);
        Assert.AreEqual(0, await ended.ReadAsync(new byte[4]));
    }

    [TestMethod]
    public void IsReadOnly_AndKnowsOnlyTheLengthItWasGiven()
    {
        using var slice = new SliceReadStream(ForwardOnly("0123"), 0, 4);
        Assert.IsTrue(slice.CanRead);
        Assert.IsFalse(slice.CanSeek);
        Assert.IsFalse(slice.CanWrite);
        Assert.ThrowsExactly<NotSupportedException>(() => slice.Length);
        Assert.ThrowsExactly<NotSupportedException>(() => slice.Position);
        Assert.ThrowsExactly<NotSupportedException>(() => slice.Position = 1);
        Assert.ThrowsExactly<NotSupportedException>(() => slice.Seek(0, SeekOrigin.Begin));
        Assert.ThrowsExactly<NotSupportedException>(() => slice.SetLength(1));
        Assert.ThrowsExactly<NotSupportedException>(() => slice.Write([1], 0, 1));
        slice.Flush();
        Assert.AreEqual(0, slice.Read([], 0, 0));
        using var known = new SliceReadStream(ForwardOnly("0123"), 1, null, knownLength: 3);
        Assert.AreEqual(3, known.Length);
    }

    [TestMethod]
    public async Task DisposesTheBody_ThenItsOwner_Once()
    {
        var order = new List<string>();
        var body = new CountingStream([1, 2, 3], () => order.Add("body"));
        var owner = new Owner(() => order.Add("owner"));
        var slice = new SliceReadStream(body, 0, null, owner);
        slice.Dispose();
        slice.Dispose();
        CollectionAssert.AreEqual(new[] { "body", "owner" }, order);
        Assert.IsFalse(slice.CanRead);

        order.Clear();
        var asyncSlice = new SliceReadStream(new CountingStream([1], () => order.Add("body")), 0, 1, new Owner(() => order.Add("owner")));
        await asyncSlice.DisposeAsync();
        await asyncSlice.DisposeAsync();
        CollectionAssert.AreEqual(new[] { "body", "owner" }, order);

        // Without an owner, only the body.
        var alone = new CountingStream([1]);
        await new SliceReadStream(alone, 0, null).DisposeAsync();
        Assert.IsFalse(alone.CanRead);
    }

    [TestMethod]
    public async Task ALimitOfZero_ReadsNothing()
    {
        await using var slice = new SliceReadStream(ForwardOnly("0123"), 0, 0);
        Assert.AreEqual(0, await slice.ReadAsync(new byte[4]));
        Assert.AreEqual(0, slice.Read(new byte[4], 0, 4));
    }

    private static ForwardOnlyStream ForwardOnly(string text) => new(Encoding.UTF8.GetBytes(text));

    /// <summary>A body that reads forward only, as a network download does.</summary>
    private sealed class ForwardOnlyStream(byte[] data) : MemoryStream(data, writable: false)
    {
        public override bool CanSeek => false;
    }

    /// <summary>A seekable body that counts its reads and says when it is disposed.</summary>
    private sealed class CountingStream(byte[] data, Action? disposed = null) : MemoryStream(data, writable: false)
    {
        private bool _disposed;

        public int Reads { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            return base.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            Reads++;
            return base.Read(buffer);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            return base.ReadAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                disposed?.Invoke();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class Owner(Action disposed) : IDisposable
    {
        public void Dispose() => disposed();
    }
}
