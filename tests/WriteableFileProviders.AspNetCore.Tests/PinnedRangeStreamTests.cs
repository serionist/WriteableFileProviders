using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.AspNetCore.Tests;

/// <summary>The gateway's download stream: one pinned ranged read per contiguous span, and the range hint.</summary>
[TestClass]
public sealed class PinnedRangeStreamTests
{
    private static readonly string _store = "pinned-" + Guid.NewGuid().ToString("N");

    private DelegatingFileProvider _files = null!;
    private string _etag = null!;

    [TestInitialize]
    public async Task Start()
    {
        _files = new DelegatingFileProvider(new InMemoryFileProvider(new InMemoryFileProviderOptions { StoreName = _store, RootDirectoryPath = Guid.NewGuid().ToString("N") }));
        await _files.UpdateFileAsync("doc.txt", TestStreams.Text("0123456789"));
        _etag = (await _files.GetFileInfoAsync("doc.txt")).ETag!;
    }

    [TestCleanup]
    public void Stop() => _files.Dispose();

    [ClassCleanup]
    public static void ClearStore() => InMemoryFileProvider.ClearStore(_store);

    [TestMethod]
    public async Task ContiguousReads_UseOneRange_ASeekOpensAnother()
    {
        await using var stream = Open();

        Assert.AreEqual("012", await ReadAsync(stream, 3));
        Assert.AreEqual("345", await ReadAsync(stream, 3));
        stream.Seek(-2, SeekOrigin.End);
        Assert.AreEqual("89", await ReadAsync(stream, 5));
        Assert.AreEqual(0, await stream.ReadAsync(new byte[1]));

        CollectionAssert.AreEqual(new (long, long?)[] { (0, 10), (8, 2) }, _files.RangeReads.ToArray());
        Assert.AreEqual(1, _files.OpenStreams);
    }

    [TestMethod]
    public async Task AReadInsideTheWindow_OpensOnlyTheWindow_AndOneOutsideReachesTheEnd()
    {
        await using var stream = Open(window: (2, 4));

        stream.Position = 2;
        Assert.AreEqual("234", await ReadAsync(stream, 10));
        Assert.AreEqual("56789", await ReadAsync(stream, 10));
        stream.Position = 0;
        Assert.AreEqual("01", await ReadAsync(stream, 2));

        CollectionAssert.AreEqual(new (long, long?)[] { (2, 3), (5, 5), (0, 10) }, _files.RangeReads.ToArray());
    }

    [TestMethod]
    public async Task AFileThatChanged_ThrowsWhenItsRangeOpens()
    {
        await using var stream = Open();
        await _files.UpdateFileAsync("doc.txt", TestStreams.Text("9876543210"));

        await Assert.ThrowsAsync<PreconditionFailedException>(async () => await ReadAsync(stream, 4));
    }

    [TestMethod]
    public async Task AFileShorterThanItsLength_ThrowsIOException()
    {
        await using var stream = new PinnedRangeStream(_files, "doc.txt", 12, _etag, null);
        stream.Position = 8;

        Assert.AreEqual("89", await ReadAsync(stream, 4));
        await Assert.ThrowsExactlyAsync<IOException>(async () => await ReadAsync(stream, 4));
    }

    [TestMethod]
    public void SyncReads_Work()
    {
        using var stream = Open();
        var buffer = new byte[4];

        Assert.AreEqual(4, stream.Read(buffer, 0, 4));
        Assert.AreEqual("0123", Encoding.UTF8.GetString(buffer));
        Assert.AreEqual(4, stream.Read(buffer.AsSpan()));
        Assert.AreEqual("4567", Encoding.UTF8.GetString(buffer));
        Assert.AreEqual(8, stream.Position);
    }

    [TestMethod]
    public async Task ArrayReads_Work()
    {
        await using var stream = Open();
        var buffer = new byte[3];

#pragma warning disable CA1835 // The array overload is the one under test here.
        Assert.AreEqual(3, await stream.ReadAsync(buffer, 0, 3, CancellationToken.None));
#pragma warning restore CA1835
        Assert.AreEqual("012", Encoding.UTF8.GetString(buffer));
    }

    [TestMethod]
    public async Task Dispose_ClosesTheOpenRange()
    {
        var stream = Open();
        Assert.AreEqual("0", await ReadAsync(stream, 1));
        Assert.AreEqual(1, _files.OpenStreams);

        stream.Dispose();

        Assert.AreEqual(0, _files.OpenStreams);
    }

    [TestMethod]
    public async Task IsASeekableReadOnlyStream()
    {
        await using var stream = Open();

        Assert.IsTrue(stream.CanRead);
        Assert.IsTrue(stream.CanSeek);
        Assert.IsFalse(stream.CanWrite);
        Assert.AreEqual(10, stream.Length);
        Assert.AreEqual(3, stream.Seek(3, SeekOrigin.Begin));
        Assert.AreEqual(5, stream.Seek(2, SeekOrigin.Current));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => stream.Position = -1);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => stream.Seek(0, (SeekOrigin)9));
        Assert.ThrowsExactly<NotSupportedException>(() => stream.SetLength(1));
        Assert.ThrowsExactly<NotSupportedException>(() => stream.Write([1], 0, 1));
        stream.Flush();
        Assert.AreEqual(0, await stream.ReadAsync(Memory<byte>.Empty));
        Assert.AreEqual(0, _files.RangeReads.Count);
    }

    [TestMethod]
    [DataRow(null, 10L, null, null)]
    [DataRow("bytes=0-4", 10L, 0L, 4L)]
    [DataRow("bytes=5-", 10L, 5L, 9L)]
    [DataRow("bytes=3-100", 10L, 3L, 9L)]
    [DataRow("bytes=-3", 10L, 7L, 9L)]
    [DataRow("bytes=-100", 10L, 0L, 9L)]
    [DataRow("bytes=20-30", 10L, null, null)]
    [DataRow("bytes=0-1,4-5", 10L, null, null)]
    [DataRow("items=0-4", 10L, null, null)]
    [DataRow("bytes=abc", 10L, null, null)]
    [DataRow("bytes=-0", 10L, null, null)]
    public void RangeWindow_ReadsASingleByteRange(string? header, long length, long? start, long? end)
    {
        var context = new DefaultHttpContext();
        if (header is not null)
            context.Request.Headers.Range = header;

        var window = GatewayRequestHandler.RangeWindow(context.Request, length);

        Assert.AreEqual(start, window?.Start);
        Assert.AreEqual(end, window?.End);
    }

    private PinnedRangeStream Open((long, long)? window = null) => new(_files, "doc.txt", 10, _etag, window);

    private static async Task<string> ReadAsync(Stream stream, int count)
    {
        var buffer = new byte[count];
        var read = await stream.ReadAsync(buffer);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }
}
