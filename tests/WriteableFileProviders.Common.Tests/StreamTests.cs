using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.Common.Tests;

[TestClass]
public sealed class StreamTests
{
    private static readonly byte[] _data = TestStreams.Bytes(1000);

    private static RangeReadStream Over(byte[] data, int bufferSize = 64, Func<long, long, byte[]>? slice = null) =>
        new(data.Length, (offset, count, _) => Task.FromResult<Stream>(new MemoryStream(slice?.Invoke(offset, count) ?? data[(int)offset..(int)(offset + count)])), bufferSize);

    [TestMethod]
    public void RangeReadStream_ReadsSynchronouslyAcrossBuffers()
    {
        using var stream = Over(_data);
        var buffer = new byte[200];
        var total = 0;
        while (total < buffer.Length)
            total += stream.Read(buffer, total, buffer.Length - total);
        CollectionAssert.AreEqual(_data[..200], buffer);
        Assert.AreEqual(200, stream.Position);
        Assert.AreEqual(0, stream.Read(new byte[1], 0, 0));
    }

    [TestMethod]
    public async Task RangeReadStream_SeeksAndReadsAsync()
    {
        await using var stream = Over(_data);
        Assert.IsTrue(stream.CanRead);
        Assert.IsTrue(stream.CanSeek);
        Assert.IsFalse(stream.CanWrite);
        Assert.AreEqual(990, stream.Seek(-10, SeekOrigin.End));
        var buffer = new byte[10];
        Assert.AreEqual(10, await stream.ReadAsync(buffer.AsMemory(0, 10)));
        CollectionAssert.AreEqual(_data[990..], buffer);
        Assert.AreEqual(0, await stream.ReadAsync(buffer));
        Assert.AreEqual(500, stream.Seek(500, SeekOrigin.Begin));
        Assert.AreEqual(510, stream.Seek(10, SeekOrigin.Current));
        await stream.ReadExactlyAsync(buffer);
        CollectionAssert.AreEqual(_data[510..520], buffer);
        stream.Position = 5000;
        Assert.AreEqual(0, stream.Read(buffer, 0, 10));
        stream.Flush();
    }

    [TestMethod]
    public void RangeReadStream_ValidatesAndRefusesWrites()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new RangeReadStream(-1, (_, _, _) => Task.FromResult(Stream.Null)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new RangeReadStream(1, (_, _, _) => Task.FromResult(Stream.Null), 0));
        Assert.ThrowsExactly<ArgumentNullException>(() => new RangeReadStream(1, null!));
        using var stream = Over(_data);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => stream.Position = -1);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => stream.Seek(0, (SeekOrigin)9));
        Assert.ThrowsExactly<NotSupportedException>(() => stream.SetLength(1));
        Assert.ThrowsExactly<NotSupportedException>(() => stream.Write([1], 0, 1));
    }

    [TestMethod]
    public async Task RangeReadStream_ShortFetchThrowsIOException()
    {
        await using var stream = Over(_data, slice: (offset, count) => _data[(int)offset..(int)(offset + count / 2)]);
        await Assert.ThrowsExactlyAsync<IOException>(async () => await stream.ReadExactlyAsync(new byte[10]));
    }

    [TestMethod]
    public async Task RangeReadStream_FailedFetchIsNeverServed()
    {
        // A buffer of 4 over 8 bytes. The second fetch writes two bytes into the buffer, then fails: reading the first
        // range again refetches it instead of serving the half-written buffer.
        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8];
        var fail = false;
        await using var stream = new RangeReadStream(data.Length, (offset, count, _) => Task.FromResult<Stream>(fail
            ? new FailingStream([99, 99])
            : new MemoryStream(data[(int)offset..(int)(offset + count)])), 4);
        var buffer = new byte[4];
        Assert.AreEqual(4, await stream.ReadAsync(buffer));
        fail = true;
        await Assert.ThrowsAsync<IOException>(async () => await stream.ReadExactlyAsync(buffer));
        fail = false;
        stream.Position = 0;
        Assert.AreEqual(4, await stream.ReadAsync(buffer));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, buffer);
        fail = true;
        Assert.ThrowsExactly<IOException>(() => stream.ReadByte());
        fail = false;
        stream.Position = 1;
        Assert.AreEqual(2, stream.ReadByte());
    }

    [TestMethod]
    public void RangeReadStream_CannotBeReadAfterDispose()
    {
        var stream = Over(_data);
        stream.Dispose();
        Assert.IsFalse(stream.CanRead);
        Assert.IsFalse(stream.CanSeek);
        Assert.ThrowsExactly<ObjectDisposedException>(() => stream.Read(new byte[1], 0, 1));
    }

    [TestMethod]
    public async Task BoundedReadStream_StopsAtItsLength()
    {
        var inner = new MemoryStream(Encoding.UTF8.GetBytes("0123456789"));
        await using var bounded = new BoundedReadStream(inner, 4);
        Assert.IsTrue(bounded.CanRead);
        Assert.IsFalse(bounded.CanSeek);
        Assert.IsFalse(bounded.CanWrite);
        var buffer = new byte[10];
        Assert.AreEqual(4, await bounded.ReadAsync(buffer.AsMemory(0, 10)));
        Assert.AreEqual(0, await bounded.ReadAsync(buffer));
        Assert.AreEqual("0123", Encoding.UTF8.GetString(buffer, 0, 4));
        Assert.ThrowsExactly<NotSupportedException>(() => bounded.Length);
        Assert.ThrowsExactly<NotSupportedException>(() => bounded.Position);
        Assert.ThrowsExactly<NotSupportedException>(() => bounded.Position = 1);
        Assert.ThrowsExactly<NotSupportedException>(() => bounded.Seek(0, SeekOrigin.Begin));
        Assert.ThrowsExactly<NotSupportedException>(() => bounded.SetLength(1));
        Assert.ThrowsExactly<NotSupportedException>(() => bounded.Write([1], 0, 1));
        bounded.Flush();
    }

    [TestMethod]
    public void BoundedReadStream_ReadsSynchronouslyAndDisposesItsInnerStream()
    {
        var inner = new MemoryStream(Encoding.UTF8.GetBytes("0123456789"));
        var bounded = new BoundedReadStream(inner, 3);
        var buffer = new byte[10];
        Assert.AreEqual(3, bounded.Read(buffer, 0, 10));
        Assert.AreEqual(0, bounded.Read(buffer, 0, 10));
        bounded.Dispose();
        Assert.IsFalse(inner.CanRead);
    }

    [TestMethod]
    public async Task SkipAsync_SkipsByReadingWhenTheStreamCannotSeek()
    {
        var failing = new FailingStream(Encoding.UTF8.GetBytes("0123456789"));
        await StreamUtilities.SkipAsync(failing, 4, default);
        Assert.AreEqual(4, failing.Position);
        await StreamUtilities.SkipAsync(failing, 0, default);
        var seekable = new MemoryStream(new byte[5]);
        await StreamUtilities.SkipAsync(seekable, 50, default);
        Assert.AreEqual(5, seekable.Position);
        var ended = new BoundedReadStream(new MemoryStream(new byte[3]), 3);
        await StreamUtilities.SkipAsync(ended, 50, default);
    }

    [TestMethod]
    public void TempFile_IsOwnerOnlyOnUnixAndDeletedOnDispose()
    {
        string name;
        using (var temp = StreamUtilities.CreateTempFile())
        {
            name = temp.Name;
            Assert.IsTrue(File.Exists(name));
            if (!OperatingSystem.IsWindows())
                Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(name));
        }
        Assert.IsFalse(File.Exists(name));
    }
}
