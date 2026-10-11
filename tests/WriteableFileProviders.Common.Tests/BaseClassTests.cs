using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.Common.Tests;

[TestClass]
public sealed class BaseClassTests
{
    private static StubProvider Stub(string? root = null) => new(new StubOptions { RootDirectoryPath = root });

    [TestMethod]
    public async Task StoreNotFoundErrors_BecomeFileNotFound()
    {
        using var provider = Stub();
        var e = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => provider.ReadFileAsync("missing.txt"));
        Assert.IsInstanceOfType<KeyNotFoundException>(e.InnerException);
        Assert.AreEqual("missing.txt", e.FileName);
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => provider.ReadRangeAsync("missing.txt", 0, 1));
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => provider.ReadRangeAsync("missing.txt", 0, null, new ReadOptions { IfMatch = "e1" }));
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => provider.OpenReadSeekableAsync("missing.txt"));
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.ReadRangeAsync("file.txt", 0, null, new ReadOptions { IfMatch = "other" }));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => provider.OpenReadSeekableAsync("file.txt", new SeekableReadOptions { BufferSize = 0 }));
    }

    [TestMethod]
    public async Task AtomicPreconditions_ReachTheProvider()
    {
        using var provider = Stub();
        provider.AtomicPreconditions = true;
        await provider.UpdateFileAsync("file.txt", TestStreams.Text("x"), new UpdateFileOptions { IfMatch = "anything" });
        Assert.AreEqual("anything", provider.LastUpdate!.IfMatch);
        provider.AtomicPreconditions = false;
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.UpdateFileAsync("file.txt", TestStreams.Text("x"), new UpdateFileOptions { IfMatch = "anything" }));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => provider.UpdateFileAsync("file.txt", null!));
    }

    [TestMethod]
    public async Task SignedUrl_ContentHeadersNeedTheProvidersSupport()
    {
        using var provider = Stub();
        provider.SignedUrls = true;
        var options = new SignedUrlOptions { Validity = TimeSpan.FromMinutes(1) };
        Assert.AreEqual(new Uri("https://stub.test/file.txt"), (await provider.GetSignedUrlAsync("file.txt", options)).Url);
        // A provider that has not said it honours them fails closed, so a URL never serves the stored name and type instead.
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.GetSignedUrlAsync("file.txt", options with { DownloadFileName = "a.csv" }));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.GetSignedUrlAsync("file.txt", options with { ContentType = "text/csv" }));
    }

    [TestMethod]
    public async Task AppendAndLocalStream_KeepTheFilesContentType()
    {
        using var provider = Stub();
        provider.ContentType = "text/html";
        await provider.AppendByRewrite("file.txt", TestStreams.Text("more"));
        Assert.AreEqual("text/html", provider.LastUpdate!.ContentType, "An append keeps the file's type");
        await provider.AppendByRewrite("file.txt", TestStreams.Text("more"), UpdateFileOptions.Append with { ContentType = "text/plain" });
        Assert.AreEqual("text/plain", provider.LastUpdate!.ContentType, "The caller's type wins");
        await using (var local = await provider.CreateLocalFileStreamAsync("file.txt", readOnly: false))
            local.WriteByte(1);
        Assert.AreEqual("text/html", provider.LastUpdate!.ContentType, "A local stream keeps the file's type");

        // A file the store keeps no type for, and a new file, commit with none: the store's default.
        provider.ContentType = null;
        await using (var local = await provider.CreateLocalFileStreamAsync("file.txt", readOnly: false))
            local.WriteByte(1);
        Assert.IsNull(provider.LastUpdate!.ContentType);
        await using (var local = await provider.CreateLocalFileStreamAsync("new.txt", readOnly: false))
            local.WriteByte(1);
        Assert.IsNull(provider.LastUpdate!.ContentType);
    }

    [TestMethod]
    public async Task FilesWithoutAnETag_RefusePinnedReadsAndAppendsByRewrite()
    {
        using var provider = Stub();
        provider.ETag = null;
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.OpenReadSeekableAsync("file.txt"));
        await using (var unpinned = await provider.OpenReadSeekableAsync("file.txt", new SeekableReadOptions { AllowModifications = true }))
            Assert.AreEqual(4, unpinned.Length);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.AppendByRewrite("file.txt", TestStreams.Text("more")));
        Assert.IsNull(provider.LastUpdate, "Nothing was written");
        provider.ETag = "e1";
        await provider.AppendByRewrite("file.txt", TestStreams.Text("more"));
        Assert.AreEqual("e1", provider.LastUpdate!.IfMatch);
    }

    [TestMethod]
    public async Task Disposed_RefusesEveryCall()
    {
        var provider = Stub();
        Assert.IsFalse(provider.Disposed);
        await provider.DisposeAsync();
        Assert.IsTrue(provider.Disposed);
        provider.Dispose();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => provider.GetFileInfoAsync("file.txt"));
        Assert.ThrowsExactly<ObjectDisposedException>(() => provider.EnumerateAsync(""));
        var sync = Stub();
        sync.Dispose();
        Assert.IsTrue(sync.Disposed);
    }

    [TestMethod]
    public void IsAddressable_IsWhatAOneSegmentPathAccepts_TheProvidersOwnRuleIncluded()
    {
        using var provider = Stub();
        string[] addressable = ["a.txt", "a%41", "50%.txt", "a:b", "a%3Ab", "x y", "..a", "%"];
        string[] refused = ["", " ", ".", "..", "...", ". .", "a/b", "a\\b", "x\u0001y", "%2e%2e", "%252e%252e", "a%2F..%2Fb", "%2F", "%00"];
        foreach (var name in addressable)
            Assert.IsTrue(provider.Addressable(name), name);
        foreach (var name in refused)
            Assert.IsFalse(provider.Addressable(name), name);

        // The provider's own rule applies, also to the decoded forms, as it does to a caller's path.
        provider.RefusedCharacter = ':';
        Assert.IsFalse(provider.Addressable("a:b"));
        Assert.IsFalse(provider.Addressable("a%3Ab"));
        Assert.IsTrue(provider.Addressable("ab"));

        // A name is addressable exactly when a path of that one segment resolves to it.
        foreach (var name in addressable.Concat(refused))
        {
            bool resolves;
            try
            {
                resolves = provider.ResolveForTest(new ProviderPath(name)).Segments is [var only] && only == name;
            }
            catch (ArgumentException)
            {
                resolves = false;
            }
            Assert.AreEqual(resolves, provider.Addressable(name), name);
        }
    }

    [TestMethod]
    public async Task Helpers()
    {
        using var provider = Stub("tenant/a");
        Assert.AreEqual(new ProviderPath("a", "b"), provider.ResolveForTest("a\\b"));
        await using (var spooled = await StubProvider.Spool(TestStreams.Text("spool")))
        {
            Assert.AreEqual(0, spooled.Position);
            Assert.AreEqual(5, spooled.Length);
        }
        await provider.DeleteFile("file.txt");
        Assert.IsFalse(await provider.FolderExists("file.txt"));
        Assert.IsNotNull(provider.Watch("*"));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.GetSignedUrlAsync("file.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(1) }));
    }

    [TestMethod]
    public void ResolvedPath_Members()
    {
        using var provider = new InMemoryFileProvider(new InMemoryFileProviderOptions { RootDirectoryPath = "r" });
        var info = provider.GetFileInfo(new ProviderPath("a", "b.txt"));
        Assert.AreEqual("b.txt", info.Name);
        var resolved = new ResolvedPath(["r"], ["a", "b.txt"]);
        CollectionAssert.AreEqual(new[] { "r", "a", "b.txt" }, resolved.AbsoluteSegments);
        CollectionAssert.AreEqual(new[] { "a", "b.txt" }, resolved.RelativeSegments);
        Assert.AreEqual("a", resolved.Parent.ToString());
        Assert.AreEqual("a/b.txt/c", resolved.Child("c").ToString());
        Assert.AreEqual("b.txt", resolved.Name);
        var root = new ResolvedPath(["r"], []);
        Assert.IsTrue(root.IsRoot);
        Assert.AreSame(root, root.Parent);
    }

    [TestMethod]
    public void Values()
    {
        Assert.IsNotNull(new PreconditionFailedException().Message);
        Assert.AreEqual("m", new PreconditionFailedException("m").Message);
        Assert.IsInstanceOfType<IOException>(new PreconditionFailedException("m", new InvalidOperationException()).InnerException is not null ? new PreconditionFailedException() : null);
        Assert.IsNotNull(new FileExistsException().Message);
        Assert.AreEqual("m", new FileExistsException("m").Message);
        Assert.IsNotNull(new FileExistsException("m", new InvalidOperationException()).InnerException);
        var url = new SignedUrl { Url = new Uri("https://example.test/f") };
        Assert.AreEqual("https://example.test/f", url.ToString());
        Assert.AreEqual("GET", url.Method);
        Assert.AreEqual(SignedUrlKind.Presigned, url.Kind);
        Assert.AreEqual(0, url.Headers.Count);
        Assert.IsFalse(DirectoryContents.NotFound.Exists);
        Assert.ThrowsExactly<ArgumentNullException>(() => new DirectoryContents(true, null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new WriteableFileInfo(null!, "a", true, false, 1, DateTimeOffset.UtcNow));
        System.Collections.IEnumerable untyped = new DirectoryContents(true, []);
        Assert.IsFalse(untyped.GetEnumerator().MoveNext());
        Assert.AreEqual("/", new FileProviderOptions { RootDirectorySegments = null }.RootDirectoryPath);
        Assert.AreEqual(0, new FileProviderOptions { RootDirectoryPath = null }.RootDirectorySegments.Length);
        Assert.AreEqual(UpdateFileMode.Append, UpdateFileOptions.Append.Mode);
    }

    [TestMethod]
    public async Task DependencyInjection_RegistersKeyedSingletons()
    {
        var store = "di-" + Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddWriteableFileProvider("memory", _ => new InMemoryFileProvider(new InMemoryFileProviderOptions { StoreName = store }));
        services.AddWriteableFileProvider("physical", new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Type"] = "WriteablePhysicalFileProvider",
            ["RootDirectoryPath"] = Path.Combine(Path.GetTempPath(), "wfp-di-" + Guid.NewGuid().ToString("N")),
        }).Build());
        await using (var container = services.BuildServiceProvider())
        {
            var memory = container.GetRequiredKeyedService<IWriteableFileProvider>("memory");
            Assert.AreSame(memory, container.GetRequiredKeyedService<IWriteableFileProvider>("memory"));
            Assert.IsInstanceOfType<InMemoryFileProvider>(memory);
            Assert.AreEqual("WriteablePhysicalFileProvider", container.GetRequiredKeyedService<IWriteableFileProvider>("physical").GetType().Name);
        }
        InMemoryFileProvider.ClearStore(store);
        Assert.ThrowsExactly<ArgumentNullException>(() => services.AddWriteableFileProvider("x", (IConfiguration)null!));
        Assert.ThrowsExactly<ArgumentException>(() => services.AddWriteableFileProvider("", _ => null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => services.AddWriteableFileProvider("x", (Func<IServiceProvider, IWriteableFileProvider>)null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => ((IServiceCollection)null!).AddWriteableFileProvider("x", _ => null!));
    }

    [TestMethod]
    public void Constructor_RefusesNullOptions() =>
        Assert.ThrowsExactly<ArgumentNullException>(() => new StubProvider(null!));
}
