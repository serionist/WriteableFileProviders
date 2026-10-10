using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.AspNetCore.Tests;

/// <summary>The size cap on an upload's body, through every read path a provider might use.</summary>
[TestClass]
public sealed class UploadLimitStreamTests
{
    [TestMethod]
    public async Task ReadsUpToTheLimit_ThenThrows413()
    {
        using var inner = new MemoryStream(new byte[10]);
        await using var stream = new UploadLimitStream(inner, 8);
        var buffer = new byte[4];

        Assert.AreEqual(4, await stream.ReadAsync(buffer));
#pragma warning disable CA1835 // The array overload is the one under test here.
        Assert.AreEqual(4, await stream.ReadAsync(buffer, 0, 4, CancellationToken.None));
#pragma warning restore CA1835
        var failure = await Assert.ThrowsExactlyAsync<BadHttpRequestException>(async () => await stream.ReadExactlyAsync(buffer));
        Assert.AreEqual(StatusCodes.Status413PayloadTooLarge, failure.StatusCode);
    }

    [TestMethod]
    public void SyncReads_CountToo()
    {
        using var inner = new MemoryStream(new byte[10]);
        using var stream = new UploadLimitStream(inner, 6);

        Assert.AreEqual(3, stream.Read(new byte[3], 0, 3));
        Assert.AreEqual(3, stream.Read(new byte[3].AsSpan()));
        Assert.ThrowsExactly<BadHttpRequestException>(() => stream.Read(new byte[1], 0, 1));
    }

    [TestMethod]
    public void ABodyOfExactlyTheLimit_ReadsToTheEnd()
    {
        using var inner = new MemoryStream(new byte[6]);
        using var stream = new UploadLimitStream(inner, 6);
        using var copy = new MemoryStream();

        stream.CopyTo(copy);

        Assert.AreEqual(6, copy.Length);
    }

    [TestMethod]
    public void IsAForwardOnlyReadOnlyStream_ThatLeavesItsBodyOpen()
    {
        var inner = new MemoryStream(new byte[2]);
        var stream = new UploadLimitStream(inner, 6);

        Assert.IsTrue(stream.CanRead);
        Assert.IsFalse(stream.CanSeek);
        Assert.IsFalse(stream.CanWrite);
        Assert.ThrowsExactly<NotSupportedException>(() => stream.Length);
        Assert.ThrowsExactly<NotSupportedException>(() => stream.Position);
        Assert.ThrowsExactly<NotSupportedException>(() => stream.Position = 0);
        Assert.ThrowsExactly<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.ThrowsExactly<NotSupportedException>(() => stream.SetLength(0));
        Assert.ThrowsExactly<NotSupportedException>(() => stream.Write([1], 0, 1));
        stream.Flush();
        stream.Dispose();

        Assert.IsTrue(inner.CanRead);
        inner.Dispose();
    }
}
