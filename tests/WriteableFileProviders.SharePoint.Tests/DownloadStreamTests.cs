using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.SharePoint.Tests;

/// <summary>The download body: a forward-only stream that stops at its limit and releases its response.</summary>
[TestClass]
public sealed class DownloadStreamTests
{
    [TestMethod]
    public void ReadsUpToItsLimit_AndIsReadOnly()
    {
        var response = new HttpResponseMessage();
        var stream = new DownloadStream(response, new MemoryStream([1, 2, 3, 4, 5]), limit: 3);
        Assert.IsTrue(stream.CanRead);
        Assert.IsFalse(stream.CanSeek);
        Assert.IsFalse(stream.CanWrite);
        var buffer = new byte[10];
        Assert.AreEqual(3, stream.Read(buffer, 0, 10));
        Assert.AreEqual(0, stream.Read(buffer, 0, 10));
        Assert.ThrowsExactly<NotSupportedException>(() => _ = stream.Length);
        Assert.ThrowsExactly<NotSupportedException>(() => _ = stream.Position);
        Assert.ThrowsExactly<NotSupportedException>(() => stream.Position = 0);
        Assert.ThrowsExactly<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.ThrowsExactly<NotSupportedException>(() => stream.SetLength(0));
        Assert.ThrowsExactly<NotSupportedException>(() => stream.Write(buffer, 0, 1));
        stream.Flush();
        stream.Dispose();
    }

    [TestMethod]
    public async Task ReadsAllWithoutALimit_AndSkipsPastTheEnd()
    {
        var stream = new DownloadStream(new HttpResponseMessage(), new MemoryStream([1, 2, 3]), limit: null);
        await stream.SkipAsync(10, CancellationToken.None);
#pragma warning disable CA1835 // The array overload is the one under test.
        Assert.AreEqual(0, await stream.ReadAsync(new byte[4], 0, 4));
#pragma warning restore CA1835
        await stream.DisposeAsync();
    }
}
