using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.Common.Tests;

[TestClass]
public sealed class LocalFileStreamTests
{
    private static readonly string _store = "local-" + Guid.NewGuid().ToString("N");

    private static InMemoryFileProvider Provider() => new(new InMemoryFileProviderOptions { StoreName = _store, RootDirectoryPath = "root" });

    [ClassCleanup]
    public static void ClearStore() => InMemoryFileProvider.ClearStore(_store);

    [TestMethod]
    public async Task SyncDispose_Commits()
    {
        using var provider = Provider();
        var local = await provider.CreateLocalFileStreamAsync("sync.txt", readOnly: false);
        local.Write(Encoding.UTF8.GetBytes("abc"));
        local.Write(Encoding.UTF8.GetBytes("de"), 0, 2);
        Assert.AreEqual(new ProviderPath("sync.txt"), local.Path);
        Assert.AreSame(provider, local.Provider);
        Assert.IsFalse(local.ReadOnly);
        local.Dispose();
        local.Dispose();
        Assert.AreEqual("abcde", await provider.ReadAllText("sync.txt"));
        Assert.IsFalse(local.CanRead);
        Assert.IsFalse(local.CanWrite);
        Assert.IsFalse(local.CanSeek);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => local.CommitAsync());
    }

    [TestMethod]
    public async Task Reads_AndWritesAsync()
    {
        using var provider = Provider();
        await provider.UpdateFileAsync("f.txt", TestStreams.Text("hello"));
        await using var local = await provider.CreateLocalFileStreamAsync("f.txt", readOnly: false);
        var buffer = new byte[5];
        Assert.AreEqual(5, await local.ReadAsync(buffer.AsMemory(0, 5)));
        Assert.AreEqual("hello", Encoding.UTF8.GetString(buffer));
        local.Seek(0, SeekOrigin.Begin);
        Assert.AreEqual(5, local.Read(buffer, 0, 5));
        local.Position = 0;
        Assert.AreEqual(5, local.Read(buffer.AsSpan()));
        await local.WriteAsync(Encoding.UTF8.GetBytes("!!").AsMemory(0, 2));
        await local.FlushAsync();
        local.Flush();
        Assert.AreEqual(7, local.Length);
    }

    [TestMethod]
    public async Task FailedCommit_DeletesTheTempFileAndThrows()
    {
        using var provider = Provider();
        var local = await provider.CreateLocalFileStreamAsync("folder/f.txt", readOnly: false);
        var temp = local.TempFilePath;
        Assert.IsNotNull(temp);
        Assert.IsTrue(File.Exists(temp));
        await local.WriteAsync(Encoding.UTF8.GetBytes("x"));
        // A folder where the file should go makes the commit fail.
        await provider.UpdateFileAsync("folder/f.txt/inner.txt", TestStreams.Text("blocker"));
        await Assert.ThrowsAsync<IOException>(async () => await local.DisposeAsync());
        Assert.IsFalse(local.CanRead);
        Assert.IsFalse(File.Exists(temp), "The temp file is deleted although the commit failed");
    }

    [TestMethod]
    public async Task OnAFolder_Throws()
    {
        using var provider = Provider();
        await provider.UpdateFileAsync("dir/f.txt", TestStreams.Text("x"));
        await Assert.ThrowsExactlyAsync<IOException>(() => provider.CreateLocalFileStreamAsync("dir", readOnly: false));
    }
}

internal static class ProviderTextExtensions
{
    public static async Task<string> ReadAllText(this IWriteableFileProvider provider, ProviderPath path)
    {
        await using var stream = await provider.ReadFileAsync(path);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
