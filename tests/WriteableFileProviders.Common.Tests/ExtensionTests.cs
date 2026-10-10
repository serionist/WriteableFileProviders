using Microsoft.Extensions.FileProviders;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.Common.Tests;

[TestClass]
public sealed class ExtensionTests
{
    private static readonly string _store = "extensions-" + Guid.NewGuid().ToString("N");

    private InMemoryFileProvider _provider = null!;

    [TestInitialize]
    public void Start() => _provider = new InMemoryFileProvider(new InMemoryFileProviderOptions { StoreName = _store, RootDirectoryPath = Guid.NewGuid().ToString("N") });

    [TestCleanup]
    public void End() => _provider.Dispose();

    [ClassCleanup]
    public static void ClearStore() => InMemoryFileProvider.ClearStore(_store);

    [TestMethod]
    public void SyncForms()
    {
        IWriteableFileProvider p = _provider;
        p.UpdateFile("a.txt", TestStreams.Text("one"));
        p.UpdateFile("a.txt", TestStreams.Text("+two"), UpdateFileMode.Append);
        Assert.AreEqual("one+two", new StreamReader(p.ReadFile("a.txt")).ReadToEnd());
        Assert.IsTrue(p.GetFileInfo(new ProviderPath("a.txt")).Exists);
        Assert.AreEqual(1, p.GetDirectoryContents(ProviderPath.Root).Count());
        p.Move("a.txt", "b.txt");
        using (var local = p.CreateLocalFileStream("b.txt", readOnly: true))
            Assert.AreEqual(7, local.Length);
        Assert.AreEqual("memory", p.GetDownloadUrl("b.txt", TimeSpan.FromMinutes(1)).Scheme);
        Assert.IsTrue(p.Delete("b.txt"));
    }

    [TestMethod]
    public async Task FileInfoOverloads()
    {
        IWriteableFileProvider p = _provider;
        await p.UpdateFileAsync("a.txt", TestStreams.Text("x"), UpdateFileMode.Overwrite);
        IFileInfo file = await p.GetFileInfoAsync("a.txt");
        await using (var stream = await p.ReadFileAsync(file))
            Assert.AreEqual("x", await new StreamReader(stream).ReadToEndAsync());
        using (var stream = p.ReadFile(file))
            Assert.AreEqual(1, stream.Length);
        await p.UpdateFileAsync(file, TestStreams.Text("y"));
        p.UpdateFile(file, TestStreams.Text("z"));
        await using (var local = await p.CreateLocalFileStreamAsync(file, readOnly: true))
            Assert.AreEqual(1, local.Length);
        using (var local = p.CreateLocalFileStream(file, readOnly: true))
            Assert.AreEqual(1, local.Length);
        Assert.AreEqual("memory", (await p.GetDownloadUrlAsync(file, TimeSpan.FromMinutes(1))).Scheme);
        Assert.AreEqual("memory", p.GetDownloadUrl(file, TimeSpan.FromMinutes(1)).Scheme);
        await p.MoveAsync(file, "b.txt");
        p.Move(await p.GetFileInfoAsync("b.txt"), "c.txt");
        Assert.IsTrue(await p.DeleteAsync(await p.GetFileInfoAsync("c.txt")));
        await p.UpdateFileAsync("d.txt", TestStreams.Text("d"));
        Assert.IsTrue(p.Delete(await p.GetFileInfoAsync("d.txt")));

        var foreign = new PhysicalFileProvider(Path.GetTempPath()).GetFileInfo("x");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => p.ReadFileAsync(foreign));
        Assert.ThrowsExactly<ArgumentNullException>(() => ((IWriteableFileProvider)null!).ReadFile("a"));
    }

#pragma warning disable CS0618 // the 2026 call shapes are what these tests pin
    [TestMethod]
    public async Task CompatibilityShims()
    {
        IWriteableFileProvider p = _provider;
        await p.UpdateFileAsync("dir/a.txt", UpdateFileMode.Overwrite, TestStreams.Text("1"));
        p.UpdateFile("dir/a.txt", UpdateFileMode.Append, TestStreams.Text("2"));
        await p.UpdateFileAsync(TestStreams.Text("3"), UpdateFileMode.Append, "dir", "a.txt");
        p.UpdateFile(TestStreams.Text("4"), UpdateFileMode.Append, "dir", "a.txt");
        var file = await p.GetFileInfoAsync("dir", "a.txt");
        await p.UpdateFileAsync(TestStreams.Text("5"), UpdateFileMode.Append, file);
        p.UpdateFile(TestStreams.Text("6"), UpdateFileMode.Append, file);
        Assert.AreEqual(6, p.GetFileInfo("dir", "a.txt").Length);
        Assert.AreEqual(1, (await p.GetDirectoryContentsAsync("dir", "x").ContinueWith(_ => p.GetDirectoryContents("dir", "x"), TaskScheduler.Default)).Count() + 1);
        await using (var stream = await p.ReadFileAsync("dir", "a.txt"))
            Assert.AreEqual("123456", await new StreamReader(stream).ReadToEndAsync());
        using (var stream = p.ReadFile("dir", "a.txt"))
            Assert.AreEqual(6, stream.Length);
        await using (var local = await p.CreateLocalFileStreamAsync(true, "dir", "a.txt"))
            Assert.AreEqual(6, local.Length);
        using (var local = p.CreateLocalFileStream(true, "dir", "a.txt"))
            Assert.AreEqual(6, local.Length);
        await using (var local = await p.CreateLocalFileStreamAsync(true, file))
            Assert.AreEqual(6, local.Length);
        using (var local = p.CreateLocalFileStream(true, file))
            Assert.AreEqual(6, local.Length);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => p.WatchAsync("*"));
        Assert.IsFalse(await p.DeleteAsync("dir", "missing.txt"));
        Assert.IsTrue(p.Delete("dir", "a.txt"));
    }
#pragma warning restore CS0618
}
