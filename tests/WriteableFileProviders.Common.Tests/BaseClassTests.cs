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

    private static async Task<string> ReadText(IWriteableFileProvider provider, string path)
    {
        await using var stream = await provider.ReadFileAsync(path);
        return await new StreamReader(stream).ReadToEndAsync();
    }

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
    public async Task IfNotExists_FindsOnlyAFile()
    {
        using var provider = Stub();
        var options = new UpdateFileOptions { IfNotExists = true };
        await Assert.ThrowsExactlyAsync<FileAlreadyExistsException>(() => provider.UpdateFileAsync("file.txt", TestStreams.Text("x"), options));
        await Assert.ThrowsExactlyAsync<FileAlreadyExistsException>(() => provider.AppendByRewrite("file.txt", TestStreams.Text("x"), UpdateFileOptions.Append with { IfNotExists = true }));

        // A folder at the path: the write reaches the store, whose own answer there is the one a caller gets.
        await provider.UpdateFileAsync("folder", TestStreams.Text("x"), options);
        Assert.IsTrue(provider.LastUpdate!.IfNotExists);
        await provider.AppendByRewrite("folder", TestStreams.Text("x"), UpdateFileOptions.Append with { IfNotExists = true });
        Assert.IsTrue(provider.LastUpdate!.IfNotExists);
        // An append there creates the file only if none appears meanwhile, as an append to a missing path does.
        await provider.AppendByRewrite("folder", TestStreams.Text("x"));
        Assert.IsTrue(provider.LastUpdate!.IfNotExists);
        Assert.IsNull(provider.LastUpdate.IfMatch);
    }

    [TestMethod]
    public async Task DefaultCopyAndMove_ReportADestinationCreatedMeanwhileAsExisting()
    {
        using var provider = Stub();
        provider.CreateFailure = new FileAlreadyExistsException("The store already has it.");
        var copy = await Assert.ThrowsExactlyAsync<FileExistsException>(() => provider.CopyAsync("file.txt", "new.txt"));
        Assert.AreSame(provider.CreateFailure, copy.InnerException);
        await Assert.ThrowsExactlyAsync<FileExistsException>(() => provider.MoveAsync("file.txt", "new.txt"));

        // Any other precondition failure, such as one from the source's own stream, passes as it is.
        provider.CreateFailure = new PreconditionFailedException("The source changed while it was read.");
        Assert.AreSame(provider.CreateFailure, await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.CopyAsync("file.txt", "new.txt")));
    }

    [TestMethod]
    public async Task InMemoryStore_SettlesConditionsTheBaseCheckMissed()
    {
        // The reference provider's conditions are checked before the write, not atomically; its store checks them
        // again under its lock, so a file written between the two refuses the create and keeps its content.
        var store = "create-" + Guid.NewGuid().ToString("N");
        using var provider = new InMemoryFileProvider(new InMemoryFileProviderOptions { StoreName = store });
        try
        {
            var racing = new InterceptingStream("mine"u8.ToArray(),
                () => provider.UpdateFileAsync("f.txt", TestStreams.Text("theirs")).GetAwaiter().GetResult());
            await Assert.ThrowsExactlyAsync<FileAlreadyExistsException>(() => provider.UpdateFileAsync("f.txt", racing, new UpdateFileOptions { IfNotExists = true }));
            Assert.AreEqual("theirs", await ReadText(provider, "f.txt"));

            // The same for a version: a write between the base check and the lock changes the ETag the caller gave.
            var etag = (await provider.GetFileInfoAsync("f.txt")).ETag;
            racing = new InterceptingStream("mine"u8.ToArray(),
                () => provider.UpdateFileAsync("f.txt", TestStreams.Text("newer")).GetAwaiter().GetResult());
            await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => provider.UpdateFileAsync("f.txt", racing, new UpdateFileOptions { IfMatch = etag }));
            Assert.AreEqual("newer", await ReadText(provider, "f.txt"));
        }
        finally
        {
            InMemoryFileProvider.ClearStore(store);
        }
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
    public async Task ResolvedPath_Members()
    {
        using var provider = new InMemoryFileProvider(new InMemoryFileProviderOptions { RootDirectoryPath = "r" });
        var info = await provider.GetFileInfoAsync(new ProviderPath("a", "b.txt"));
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
    public async Task DependencyInjection_OneProvider_IsASingletonByInterface_ThatTheContainerDisposes()
    {
        var services = new ServiceCollection();
        services.AddWriteableFileProvider(_ => Stub());
        StubProvider provider;
        await using (var container = services.BuildServiceProvider())
        {
            provider = (StubProvider)container.GetRequiredService<IWriteableFileProvider>();
            Assert.AreSame(provider, container.GetRequiredService<IWriteableFileProvider>());
            Assert.IsNull(container.GetKeyedService<IWriteableFileProvider>("any"));
            Assert.IsFalse(provider.Disposed);
        }
        Assert.IsTrue(provider.Disposed);
    }

    [TestMethod]
    public async Task DependencyInjection_FromConfiguration_BuildsTheProviderOnFirstUse()
    {
        var services = new ServiceCollection();
        services.AddWriteableFileProvider(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Type"] = "WriteablePhysicalFileProvider",
            ["RootDirectoryPath"] = Path.Combine(Path.GetTempPath(), "wfp-di-" + Guid.NewGuid().ToString("N")),
        }).Build());
        await using var container = services.BuildServiceProvider();

        Assert.AreEqual("WriteablePhysicalFileProvider", container.GetRequiredService<IWriteableFileProvider>().GetType().Name);
    }

    [TestMethod]
    public async Task DependencyInjection_TwoProviders_AreBothRegistered_AndTheLastIsTheOneInjected()
    {
        var store = "di-" + Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddWriteableFileProvider(_ => Stub());
        services.AddWriteableFileProvider(_ => new InMemoryFileProvider(new InMemoryFileProviderOptions { StoreName = store }));
        try
        {
            await using var container = services.BuildServiceProvider();

            var all = container.GetServices<IWriteableFileProvider>().ToList();
            Assert.HasCount(2, all);
            Assert.IsInstanceOfType<StubProvider>(all[0]);
            Assert.IsInstanceOfType<InMemoryFileProvider>(all[1]);
            Assert.AreSame(all[1], container.GetRequiredService<IWriteableFileProvider>());
        }
        finally
        {
            InMemoryFileProvider.ClearStore(store);
        }
    }

    [TestMethod]
    public void DependencyInjection_NullArguments_Throw()
    {
        var services = new ServiceCollection();
        Assert.ThrowsExactly<ArgumentNullException>(() => services.AddWriteableFileProvider((IConfiguration)null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => services.AddWriteableFileProvider((Func<IServiceProvider, IWriteableFileProvider>)null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => ((IServiceCollection)null!).AddWriteableFileProvider(_ => null!));
        Assert.IsEmpty(services);
    }

    [TestMethod]
    public void Constructor_RefusesNullOptions() =>
        Assert.ThrowsExactly<ArgumentNullException>(() => new StubProvider(null!));
}
