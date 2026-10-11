using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.Box.Tests;

[TestClass]
public sealed class SliceStreamTests
{
    [TestMethod]
    public async Task SkipsThenBounds_SyncAndAsync()
    {
        using (var sync = new SliceStream(Source("0123456789"), 3, 4))
            Assert.AreEqual("3456", new StreamReader(sync).ReadToEnd());
        await using (var async = new SliceStream(Source("0123456789"), 3, 4))
            Assert.AreEqual("3456", await new StreamReader(async).ReadToEndAsync());
        await using var bytes = new SliceStream(Source("0123456789"), 8, 10);
        var buffer = new byte[10];
        Assert.AreEqual(2, await bytes.ReadAsync(buffer.AsMemory(0, 10)));
        Assert.AreEqual(0, await bytes.ReadAsync(buffer.AsMemory(0, 10)));
        // The array overload, which callers of the Stream API still use.
#pragma warning disable CA1835
        Assert.AreEqual(0, await bytes.ReadAsync(buffer, 0, 10));
#pragma warning restore CA1835
    }

    [TestMethod]
    public async Task SkippingPastTheEnd_ReadsNothing()
    {
        using (var sync = new SliceStream(Source("012"), 5, 4))
            Assert.AreEqual(0, sync.Read(new byte[4], 0, 4));
        await using var async = new SliceStream(Source("012"), 5, 4);
        Assert.AreEqual(0, await async.ReadAsync(new byte[4]));
    }

    [TestMethod]
    public void IsReadOnly_AndDisposesTheDownload()
    {
        var source = Source("0123");
        var slice = new SliceStream(source, 0, 4);
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
        slice.Dispose();
        Assert.IsFalse(source.CanRead, "The download is disposed with the slice");
    }

    private static MemoryStream Source(string text) => new(Encoding.UTF8.GetBytes(text));
}
