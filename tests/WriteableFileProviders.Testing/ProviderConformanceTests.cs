using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.FileProviders;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.Testing;

/// <summary>
/// The behaviour contract every provider passes (docs/plans/2026-10-10-modernization.md, "The behaviour contract"), one
/// test per rule. A provider's test project derives a [TestClass] from this class and says how to build the provider and
/// what the store can do. Each test works under its own root folder, deleted afterwards.
/// </summary>
public abstract class ProviderConformanceTests
{
    private IWriteableFileProvider? _base;
    private IWriteableFileProvider? _provider;
    private string _root = "";

    /// <summary>The provider under test, rooted at a folder of its own.</summary>
    protected IWriteableFileProvider Provider => _provider ?? throw new InvalidOperationException("The test has not started.");

    /// <summary>A provider at the fixture's base, where the test's root folder is one entry.</summary>
    protected IWriteableFileProvider BaseProvider => _base ?? throw new InvalidOperationException("The test has not started.");

    /// <summary>The name of the test's root folder under the base.</summary>
    protected string RootName => _root;

    /// <summary>Builds the provider: at the fixture's base when <paramref name="root"/> is null, else under the folder.</summary>
    /// <param name="root">A folder under the fixture's base, or null.</param>
    protected abstract IWriteableFileProvider CreateProvider(string? root);

    /// <summary>Whether folders exist on their own, empty (Physical, Box, SharePoint); false on object stores.</summary>
    protected virtual bool HasRealFolders => false;

    /// <summary>Whether the store treats names that differ only in case as the same.</summary>
    protected virtual bool IsCaseInsensitive => false;

    /// <summary>Whether the provider issues native signed URLs for the access.</summary>
    /// <param name="access">The access.</param>
    protected virtual bool SupportsSignedUrls(SignedUrlAccess access) => false;

    /// <summary>
    /// The lifetime the store gives every signed URL whatever was asked (Box, SharePoint; D18), or null where the URL
    /// lives as long as asked.
    /// </summary>
    protected virtual TimeSpan? FixedSignedUrlLifetime => null;

    /// <summary>Whether the provider watches for changes.</summary>
    protected virtual bool SupportsWatch => false;

    /// <summary>Whether the store keeps content types.</summary>
    protected virtual bool KeepsContentType => false;

    /// <summary>
    /// Whether an append detects the concurrent write this suite stages (while the appended stream is read) and throws
    /// instead of losing it (D19); false where the staged race cannot be detected.
    /// </summary>
    protected virtual bool DetectsConcurrentAppends => true;

    /// <summary>
    /// Whether a file and a folder may share a name: true on the object stores (S3, MinIO, Azure Blob, Google Cloud
    /// Storage), where folders are name prefixes, so a file written at a folder's path, or a folder under a file's
    /// path, is stored and a listing shows both (D30); false where the store refuses either write with
    /// <see cref="IOException"/> (Physical, Box, SharePoint, the in-memory provider).
    /// </summary>
    protected virtual bool FileAndFolderMayShareAName => false;

    /// <summary>
    /// Where a file and a folder share a name, whether a one-level listing shows both. False on MinIO, which lists only
    /// the file, and hides what is under the folder from every listing while the file exists; both still read back.
    /// </summary>
    protected virtual bool ListsAFileAndAFolderOfOneName => FileAndFolderMayShareAName;

    /// <summary>Whether entries can have a local-disk <see cref="IFileInfo.PhysicalPath"/>.</summary>
    protected virtual bool HasPhysicalPaths => false;

    /// <summary>The size of the large-file test; cross the store's multipart or chunk thresholds with it.</summary>
    protected virtual long LargeFileSize => 100L * 1024 * 1024;

    /// <summary>
    /// Whether a large write that fails partway leaves the old content. False only for an emulator bug the plan records
    /// (S3Mock 5.2.3 deletes the object when a multipart upload to its key is aborted); the write must still throw.
    /// </summary>
    protected virtual bool FailedLargeWriteKeepsOldContent => true;

    /// <summary>
    /// Whether a large write with <see cref="UpdateFileOptions.IfMatch"/> succeeds, as the append of a large file does.
    /// False only for an emulator bug the plan records (S3Mock 5.2.3 answers 404 to a multipart completion with the right
    /// If-Match); the large append is then skipped.
    /// </summary>
    protected virtual bool SupportsLargeConditionalWrites => true;

    /// <summary>A local folder where the provider writes its own temp files (<c>.wfp-*.tmp</c>), checked for leftovers.</summary>
    protected virtual string? LocalTempFolder => null;

    /// <summary>A provider whose bucket or container does not exist, or null where that cannot happen.</summary>
    protected virtual IWriteableFileProvider? CreateProviderWithMissingContainer() => null;

    /// <summary>
    /// Whether the provider checks its root with the path rules when it is built, so a root with a dot segment throws
    /// <see cref="ArgumentException"/>. False only where the root is a folder of the local disk (Physical).
    /// </summary>
    protected virtual bool ChecksItsRoot => true;

    /// <summary>
    /// A provider at the test's root whose store throws <paramref name="failure"/> from every call, as a store or its SDK
    /// might (a network failure, an answer it cannot read, a timeout), or null where the fixture cannot make it fail. Its
    /// exception may reach the provider wrapped by the SDK.
    /// </summary>
    /// <param name="failure">What the store throws.</param>
    protected virtual IWriteableFileProvider? CreateProviderThatFails(Exception failure) => null;

    /// <summary>
    /// Stores a file straight in the store, past the provider and its path rules, as another client of the store could:
    /// how a test stages a name the provider refuses. The default stores nothing.
    /// </summary>
    /// <param name="names">
    /// The file's path under the fixture's base, one store name per level, starting with <see cref="RootName"/>; no name
    /// holds a <c>/</c>. The folders on the way are created as the store creates them.
    /// </param>
    /// <param name="text">The file's content, as UTF-8.</param>
    /// <returns>False where the store cannot hold one of the names, or the fixture cannot reach the store.</returns>
    protected virtual Task<bool> StoreRawFileAsync(IReadOnlyList<string> names, string text) => Task.FromResult(false);

    /// <summary>Creates the test's root folder and its provider.</summary>
    [TestInitialize]
    public void StartTest()
    {
        _root = "wfp-" + Guid.NewGuid().ToString("N")[..12];
        _base = CreateProvider(null);
        _provider = CreateProvider(_root);
    }

    /// <summary>Deletes the test's root folder.</summary>
    [TestCleanup]
    public async Task EndTest()
    {
        try
        {
            if (_base is not null)
                await _base.DeleteAsync(_root);
        }
        finally
        {
            if (_provider is not null)
                await _provider.DisposeAsync();
            if (_base is not null)
                await _base.DisposeAsync();
        }
    }

    // ----- Helpers -----

    /// <summary>Writes UTF-8 text to a file.</summary>
    protected Task Write(ProviderPath path, string text, UpdateFileOptions? options = null) =>
        Provider.UpdateFileAsync(path, TestStreams.Text(text), options);

    /// <summary>Reads a file as UTF-8 text.</summary>
    protected async Task<string> Read(ProviderPath path)
    {
        await using var stream = await Provider.ReadFileAsync(path);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static async Task<string> ReadAll(Stream stream)
    {
        await using (stream)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }
    }

    /// <summary>Signed URL options the provider can honour: five minutes, or its fixed lifetime where it has one.</summary>
    /// <param name="access">The access.</param>
    protected SignedUrlOptions SignedUrlOptionsFor(SignedUrlAccess access) =>
        FixedSignedUrlLifetime is { } lifetime
            ? new SignedUrlOptions { Validity = lifetime, Permission = access, AllowShorterValidity = true }
            : new SignedUrlOptions { Validity = TimeSpan.FromMinutes(5), Permission = access };

    private static async Task<byte[]> Hash(Stream stream)
    {
        await using (stream)
            return await SHA256.HashDataAsync(stream);
    }

    // ----- Paths -----

    [TestMethod]
    public async Task Path_StringFormsNameTheSameFile()
    {
        await Write("a/b.txt", "x");
        foreach (var path in new ProviderPath[] { "/a/b.txt", "a\\b.txt", "a//b.txt", "a/b.txt/", new[] { "a", "b.txt" }, new[] { "a/b.txt" } })
            Assert.AreEqual("x", await Read(path), $"'{path}'");
    }

    [TestMethod]
    [DataRow("..")]
    [DataRow(".")]
    [DataRow(" ")]
    [DataRow(". .")]
    [DataRow("a/../b")]
    [DataRow("a\\..\\b")]
    [DataRow("a/../../x")]
    [DataRow("%2e%2e")]
    [DataRow("%252e%252e")]
    [DataRow("a%2F..%2Fb")]
    [DataRow("x\u0001y")]
    public async Task Path_InvalidSegmentsAreRefused(string path)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Provider.GetFileInfoAsync(path));
        await Assert.ThrowsAsync<ArgumentException>(() => Provider.ReadFileAsync(path));
        await Assert.ThrowsAsync<ArgumentException>(() => Provider.UpdateFileAsync(path, TestStreams.Text("x")));
        await Assert.ThrowsAsync<ArgumentException>(() => Provider.DeleteAsync(path));
        await Assert.ThrowsAsync<ArgumentException>(() => Provider.GetDirectoryContentsAsync(path));
    }

    [TestMethod]
    public async Task Path_EmptySegmentInAnArrayIsRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Provider.GetFileInfoAsync(new[] { "a", "" }));
        await Assert.ThrowsAsync<ArgumentException>(() => Provider.UpdateFileAsync(new[] { "", "b" }, TestStreams.Text("x")));
    }

    [TestMethod]
    public async Task Path_RootCannotBeModified()
    {
        await Write("f.txt", "x");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Provider.UpdateFileAsync(ProviderPath.Root, TestStreams.Text("x")));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Provider.DeleteAsync(""));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Provider.MoveAsync("", "x"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Provider.MoveAsync("f.txt", "/"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Provider.CopyAsync("f.txt", ProviderPath.Root));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Provider.CreateLocalFileStreamAsync("", readOnly: false));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Provider.GetSignedUrlAsync("", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(1), Permission = SignedUrlAccess.Write }));
        Assert.AreEqual("x", await Read("f.txt"));
    }

    [TestMethod]
    public async Task Path_RootCannotBeReadAsAFile()
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => Provider.ReadFileAsync(""));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => Provider.ReadRangeAsync("", 0, 1));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => Provider.OpenReadSeekableAsync(""));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => Provider.CreateLocalFileStreamAsync("", readOnly: true));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => Provider.GetSignedUrlAsync("", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(1) }));
    }

    [TestMethod]
    public async Task Path_RootIsAFolder()
    {
        var root = await Provider.GetFileInfoAsync(ProviderPath.Root);
        Assert.IsTrue(root.Exists);
        Assert.IsTrue(root.IsDirectory);
        Assert.AreEqual("", root.Name);
    }

    [TestMethod]
    public async Task Path_NothingOutsideTheRootIsVisible()
    {
        var sibling = RootName + "-sibling";
        try
        {
            await BaseProvider.UpdateFileAsync(sibling + "/secret.txt", TestStreams.Text("secret"));
            await Write("mine.txt", "x");
            var names = (await Provider.GetDirectoryContentsAsync("")).Select(e => e.Name).ToList();
            CollectionAssert.AreEqual(new[] { "mine.txt" }, names);
            Assert.IsFalse((await Provider.GetFileInfoAsync("secret.txt")).Exists);
        }
        finally
        {
            await BaseProvider.DeleteAsync(sibling);
        }
    }

    [TestMethod]
    [DataRow("..")]
    [DataRow(".")]
    [DataRow("x/..")]
    [DataRow("x/../..")]
    [DataRow("%2e%2e")]
    [DataRow("%252e%252e/x")]
    public void Path_ARootWithADotSegmentIsRefused(string root)
    {
        if (!ChecksItsRoot)
            return;
        var e = Assert.ThrowsExactly<ArgumentException>(() => CreateProvider(root).Dispose());
        StringAssert.Contains(e.Message, "RootDirectoryPath");
    }

    // ----- File info -----

    [TestMethod]
    public async Task FileInfo_OfAFile()
    {
        await Write("dir/f.txt", "hello");
        var info = await Provider.GetFileInfoAsync("dir/f.txt");
        Assert.IsTrue(info.Exists);
        Assert.IsFalse(info.IsDirectory);
        Assert.AreEqual(5, info.Length);
        Assert.AreEqual("f.txt", info.Name);
        Assert.AreEqual(new ProviderPath("dir", "f.txt"), info.Path);
        Assert.IsTrue(Math.Abs((DateTimeOffset.UtcNow - info.LastModified).TotalDays) < 1, $"LastModified {info.LastModified}");
        Assert.IsFalse(string.IsNullOrEmpty(info.ETag), "A file has an ETag");
        if (!HasPhysicalPaths)
            Assert.IsNull(info.PhysicalPath);
    }

    [TestMethod]
    public async Task FileInfo_OfAFolder()
    {
        await Write("dir/f.txt", "x");
        var info = await Provider.GetFileInfoAsync("dir");
        Assert.IsTrue(info.Exists);
        Assert.IsTrue(info.IsDirectory);
        Assert.AreEqual(-1, info.Length);
        Assert.AreEqual("dir", info.Name);
    }

    [TestMethod]
    public async Task FileInfo_OfAMissingPath()
    {
        var info = await Provider.GetFileInfoAsync("missing/nothing.txt");
        Assert.IsFalse(info.Exists);
        Assert.IsFalse(info.IsDirectory);
        Assert.AreEqual(-1, info.Length);
    }

    [TestMethod]
    public async Task FileInfo_ContentTypeIsAHint()
    {
        await Write("page.html", "<p/>", new UpdateFileOptions { ContentType = "text/html" });
        var info = await Provider.GetFileInfoAsync("page.html");
        if (KeepsContentType)
            Assert.AreEqual("text/html", info.ContentType);
        Assert.AreEqual("<p/>", await Read("page.html"));
    }

    [TestMethod]
    public async Task FileInfo_AppendAndLocalStreamKeepTheContentType()
    {
        if (!KeepsContentType)
            return;
        await Write("page.html", "<p/>", new UpdateFileOptions { ContentType = "text/html" });
        await Write("page.html", "<br/>", UpdateFileOptions.Append);
        Assert.AreEqual("text/html", (await Provider.GetFileInfoAsync("page.html")).ContentType, "An append keeps the file's type");
        await using (var local = await Provider.CreateLocalFileStreamAsync("page.html", readOnly: false))
        {
            local.Position = local.Length;
            await local.WriteAsync(Encoding.UTF8.GetBytes("<hr/>"));
        }
        Assert.AreEqual("text/html", (await Provider.GetFileInfoAsync("page.html")).ContentType, "A local stream keeps the file's type");
        Assert.AreEqual("<p/><br/><hr/>", await Read("page.html"));
        await Write("page.html", "x", UpdateFileOptions.Append with { ContentType = "text/plain" });
        Assert.AreEqual("text/plain", (await Provider.GetFileInfoAsync("page.html")).ContentType, "The caller's type wins");
    }

    [TestMethod]
    public async Task FileInfo_SyncFormsOfIFileProvider()
    {
        await Write("dir/f.txt", "x");
        Assert.IsTrue(Provider.GetFileInfo("dir/f.txt").Exists);
        Assert.AreEqual(1, Provider.GetDirectoryContents("dir").Count());
        using var stream = Provider.GetFileInfo("dir/f.txt").CreateReadStream();
        Assert.AreEqual("x", new StreamReader(stream).ReadToEnd());
    }

    // ----- Listing -----

    [TestMethod]
    public async Task List_OneLevel()
    {
        await Write("a.txt", "a");
        await Write("dir/b.txt", "b");
        await Write("dir/sub/c.txt", "c");

        var root = (await Provider.GetDirectoryContentsAsync("")).Cast<IWriteableFileInfo>().ToList();
        CollectionAssert.AreEquivalent(new[] { "a.txt", "dir" }, root.Select(e => e.Name).ToList());
        Assert.IsTrue(root.Single(e => e.Name == "dir").IsDirectory);
        Assert.AreEqual(-1, root.Single(e => e.Name == "dir").Length);
        Assert.AreEqual(1, root.Single(e => e.Name == "a.txt").Length);
        Assert.IsTrue(root.All(e => e.Exists));

        var dir = (await Provider.GetDirectoryContentsAsync("dir")).Cast<IWriteableFileInfo>().ToList();
        CollectionAssert.AreEquivalent(new[] { "b.txt", "sub" }, dir.Select(e => e.Name).ToList());
        Assert.AreEqual(new ProviderPath("dir", "b.txt"), dir.Single(e => e.Name == "b.txt").Path);
    }

    [TestMethod]
    public async Task List_MissingFolderDoesNotExistAndIsEmpty()
    {
        var contents = await Provider.GetDirectoryContentsAsync("missing");
        Assert.IsFalse(contents.Exists);
        Assert.AreEqual(0, contents.Count());
    }

    [TestMethod]
    public async Task List_EmptyFolderExistsOnlyWhereFoldersAreReal()
    {
        await Write("dir/f.txt", "x");
        Assert.IsTrue(await Provider.DeleteAsync("dir/f.txt"));
        var contents = await Provider.GetDirectoryContentsAsync("dir");
        Assert.AreEqual(HasRealFolders, contents.Exists);
        Assert.AreEqual(0, contents.Count());
    }

    [TestMethod]
    public async Task Enumerate_OneLevelAndRecursive()
    {
        await Write("a.txt", "a");
        await Write("dir/b.txt", "b");
        await Write("dir/sub/c.txt", "c");

        var oneLevel = new List<IWriteableFileInfo>();
        await foreach (var entry in Provider.EnumerateAsync(""))
            oneLevel.Add(entry);
        CollectionAssert.AreEquivalent(new[] { "a.txt", "dir" }, oneLevel.Select(e => e.Name).ToList());

        var all = new List<IWriteableFileInfo>();
        await foreach (var entry in Provider.EnumerateAsync("", recursive: true))
            all.Add(entry);
        var paths = all.Select(e => e.Path.ToString()).ToList();
        CollectionAssert.AreEquivalent(new[] { "a.txt", "dir", "dir/b.txt", "dir/sub", "dir/sub/c.txt" }, paths);
        Assert.IsTrue(paths.IndexOf("dir") < paths.IndexOf("dir/b.txt"));
        Assert.IsTrue(paths.IndexOf("dir/sub") < paths.IndexOf("dir/sub/c.txt"));
    }

    [TestMethod]
    public async Task List_LeavesOutNamesThePathRulesRefuse()
    {
        // Names another client of the store could create, which the common path rules refuse (only dots, a percent-encoded
        // dot-dot, a backslash, a control character, only a space) or some providers refuse (a colon). Each is staged as a
        // file at the top and as a folder holding a file one level down; whatever a listing shows must read back.
        await Write("ok.txt", "ok");
        await Write("dir/ok.txt", "ok");
        var staged = new List<string>();
        foreach (var name in new[] { "...", "%2e%2e", "%252e%252e", "a\\b.txt", "x\u0001y.txt", "a:b.txt", " " })
        {
            var asFile = await StoreRawFileAsync([RootName, name], "staged");
            var asFolder = await StoreRawFileAsync([RootName, "dir", name, "inner.txt"], "staged");
            if (asFile || asFolder)
                staged.Add(name);
        }
        if (staged.Count == 0)
            Assert.Inconclusive("The fixture cannot store a file past the provider (StoreRawFileAsync).");

        var listed = new List<IWriteableFileInfo>();
        listed.AddRange((await Provider.GetDirectoryContentsAsync("")).Cast<IWriteableFileInfo>());
        listed.AddRange((await Provider.GetDirectoryContentsAsync("dir")).Cast<IWriteableFileInfo>());
        await foreach (var entry in Provider.EnumerateAsync("", recursive: true))
            listed.Add(entry);
        var paths = listed.Select(e => e.Path.ToString()).ToList();
        CollectionAssert.IsSubsetOf(new[] { "ok.txt", "dir", "dir/ok.txt" }, paths, string.Join(", ", paths));
        foreach (var entry in listed)
        {
            var label = $"'{entry.Path}' is listed (staged: {string.Join(", ", staged)})";
            IWriteableFileInfo info;
            try
            {
                info = await Provider.GetFileInfoAsync(entry.Path);
            }
            catch (ArgumentException e)
            {
                throw new AssertFailedException($"{label}, but its path is refused: {e.Message}", e);
            }
            Assert.IsTrue(info.Exists, label);
            Assert.AreEqual(entry.IsDirectory, info.IsDirectory, label);
            if (!entry.IsDirectory)
                await (await Provider.ReadFileAsync(entry.Path)).DisposeAsync();
        }

        // A folder delete takes what its listing leaves out, so nothing keeps the folder.
        Assert.IsTrue(await Provider.DeleteAsync("dir"));
        Assert.IsFalse((await Provider.GetFileInfoAsync("dir")).Exists, "The deleted folder still exists");
        Assert.IsFalse((await Provider.GetDirectoryContentsAsync("dir")).Exists, "The deleted folder still lists");
    }

    [TestMethod]
    public async Task List_RecursiveShowsTheFoldersTheOneLevelListingShows()
    {
        // A folder whose only content sits under a name the path rules refuse: the one-level listing shows the folder,
        // and so must the recursive one, which leaves out only the name below it (D27).
        await Write("ok.txt", "ok");
        var staged = new List<string>();
        foreach (var name in new[] { "%2e%2e", "...", " " })
        {
            if (await StoreRawFileAsync([RootName, "only", name, "x.txt"], "staged"))
                staged.Add(name);
        }
        if (staged.Count == 0)
            Assert.Inconclusive("The fixture cannot store a file past the provider (StoreRawFileAsync).");

        var oneLevel = (await Provider.GetDirectoryContentsAsync("")).Select(e => $"{e.Name}{(e.IsDirectory ? "/" : "")}").ToList();
        var recursive = new List<IWriteableFileInfo>();
        await foreach (var entry in Provider.EnumerateAsync("", recursive: true))
            recursive.Add(entry);
        var topLevel = recursive.Where(e => e.Path.Segments.Count == 1).Select(e => $"{e.Name}{(e.IsDirectory ? "/" : "")}").ToList();
        CollectionAssert.Contains(oneLevel, "only/", $"Staged under 'only': {string.Join(", ", staged)}");
        CollectionAssert.AreEquivalent(oneLevel, topLevel, $"One level: {string.Join(", ", oneLevel)}; recursive: {string.Join(", ", topLevel)}");
        Assert.IsTrue((await Provider.GetFileInfoAsync("only")).IsDirectory);
        Assert.IsFalse(recursive.Any(e => e.Path.Segments.Count > 1 && e.Path.Segments[0] == "only"), "Nothing addressable is under 'only'");
    }

    [TestMethod]
    public async Task List_ShowsACallersNamesThatLookLikeTheProvidersOwn()
    {
        // A provider hides only the temporary names it makes itself (D32): a caller's ".wfp-" names are listed like any
        // other, at the top and further down, and read back.
        await Write(".wfp-notes.txt", "notes");
        await Write(".wfp-draft.tmp", "draft");
        await Write("d/.wfp-cache/x.json", "{}");

        var root = (await Provider.GetDirectoryContentsAsync("")).Select(e => e.Name).ToList();
        CollectionAssert.IsSubsetOf(new[] { ".wfp-notes.txt", ".wfp-draft.tmp", "d" }, root, string.Join(", ", root));
        var d = (await Provider.GetDirectoryContentsAsync("d")).Cast<IWriteableFileInfo>().ToList();
        Assert.IsTrue(d.Any(e => e.Name == ".wfp-cache" && e.IsDirectory), string.Join(", ", d.Select(e => e.Name)));
        CollectionAssert.AreEqual(new[] { "x.json" }, (await Provider.GetDirectoryContentsAsync("d/.wfp-cache")).Select(e => e.Name).ToArray());
        var all = new List<string>();
        await foreach (var entry in Provider.EnumerateAsync("", recursive: true))
            all.Add(entry.Path.ToString());
        CollectionAssert.AreEquivalent(new[] { ".wfp-notes.txt", ".wfp-draft.tmp", "d", "d/.wfp-cache", "d/.wfp-cache/x.json" }, all, string.Join(", ", all));
        Assert.AreEqual("notes", await Read(".wfp-notes.txt"));
        Assert.AreEqual("{}", await Read("d/.wfp-cache/x.json"));
    }

    // ----- Reads -----

    [TestMethod]
    public async Task Read_MissingOrFolderThrowsFileNotFound()
    {
        await Write("dir/f.txt", "x");
        await Assert.ThrowsAsync<FileNotFoundException>(() => Provider.ReadFileAsync("missing.txt"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => Provider.ReadFileAsync("dir"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => Provider.ReadRangeAsync("missing.txt", 0, 1));
        await Assert.ThrowsAsync<FileNotFoundException>(() => Provider.OpenReadSeekableAsync("missing.txt"));
    }

    [TestMethod]
    public async Task Range_ReadsTheSlice()
    {
        await Write("f.txt", "0123456789");
        Assert.AreEqual("345", await ReadAll(await Provider.ReadRangeAsync("f.txt", 3, 3)));
        Assert.AreEqual("789", await ReadAll(await Provider.ReadRangeAsync("f.txt", 7, null)));
        Assert.AreEqual("89", await ReadAll(await Provider.ReadRangeAsync("f.txt", 8, 100)));
        Assert.AreEqual("", await ReadAll(await Provider.ReadRangeAsync("f.txt", 10, 5)));
        Assert.AreEqual("", await ReadAll(await Provider.ReadRangeAsync("f.txt", 50, null)));
        Assert.AreEqual("", await ReadAll(await Provider.ReadRangeAsync("f.txt", 2, 0)));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => Provider.ReadRangeAsync("f.txt", -1, 1));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => Provider.ReadRangeAsync("f.txt", 0, -1));
    }

    [TestMethod]
    public async Task Range_LongerThanARangeCanNameReadsToTheEnd()
    {
        await Write("f.txt", "0123");
        // offset + length - 1 overflows for the last two: the read must still go to the end, not fail or read from 0.
        foreach (var (offset, length) in new[] { (1L, long.MaxValue), (2L, long.MaxValue - 1), (2L, long.MaxValue), (3L, long.MaxValue - 1) })
            Assert.AreEqual("0123"[(int)offset..], await ReadAll(await Provider.ReadRangeAsync("f.txt", offset, length)), $"{offset}, {length}");
    }

    [TestMethod]
    public async Task Range_IfMatchPinsTheVersion()
    {
        await Write("f.txt", "0123456789");
        var etag = (await Provider.GetFileInfoAsync("f.txt")).ETag;
        Assert.AreEqual("23", await ReadAll(await Provider.ReadRangeAsync("f.txt", 2, 2, new ReadOptions { IfMatch = etag })));
        await Write("f.txt", "changed!!!");
        await Assert.ThrowsAsync<IOException>(async () => await ReadAll(await Provider.ReadRangeAsync("f.txt", 2, 2, new ReadOptions { IfMatch = etag })));
    }

    [TestMethod]
    public async Task Seekable_SeeksAndReads()
    {
        var bytes = TestStreams.Bytes(1000);
        await Provider.UpdateFileAsync("f.bin", new MemoryStream(bytes));
        await using var stream = await Provider.OpenReadSeekableAsync("f.bin", new SeekableReadOptions { BufferSize = 64 });
        Assert.IsTrue(stream.CanSeek);
        Assert.IsFalse(stream.CanWrite);
        Assert.AreEqual(1000, stream.Length);
        stream.Position = 900;
        var buffer = new byte[50];
        await stream.ReadExactlyAsync(buffer);
        CollectionAssert.AreEqual(bytes[900..950], buffer);
        stream.Seek(-300, SeekOrigin.End);
        await stream.ReadExactlyAsync(buffer);
        CollectionAssert.AreEqual(bytes[700..750], buffer);
        stream.Seek(10, SeekOrigin.Begin);
        await stream.ReadExactlyAsync(buffer);
        CollectionAssert.AreEqual(bytes[10..60], buffer);
        stream.Position = 1000;
        Assert.AreEqual(0, await stream.ReadAsync(buffer));
        using var all = new MemoryStream();
        stream.Position = 0;
        await stream.CopyToAsync(all);
        CollectionAssert.AreEqual(bytes, all.ToArray());
    }

    [TestMethod]
    public async Task Seekable_ThrowsIOExceptionWhenTheFileChanges()
    {
        await Provider.UpdateFileAsync("f.bin", new MemoryStream(TestStreams.Bytes(1000)));
        await using var stream = await Provider.OpenReadSeekableAsync("f.bin", new SeekableReadOptions { BufferSize = 64 });
        var buffer = new byte[10];
        await stream.ReadExactlyAsync(buffer);
        await Provider.UpdateFileAsync("f.bin", new MemoryStream(TestStreams.Bytes(1000, seed: 2)));
        stream.Position = 500;
        await Assert.ThrowsAsync<IOException>(async () => await stream.ReadExactlyAsync(buffer));
    }

    [TestMethod]
    public async Task Seekable_WithModificationsAllowedReadsTheNewContent()
    {
        await Write("f.txt", "aaaaaaaaaa");
        await using var stream = await Provider.OpenReadSeekableAsync("f.txt", new SeekableReadOptions { BufferSize = 4, AllowModifications = true });
        var buffer = new byte[2];
        await stream.ReadExactlyAsync(buffer);
        await Write("f.txt", "bbbbbbbbbb");
        stream.Position = 8;
        await stream.ReadExactlyAsync(buffer);
        Assert.AreEqual("bb", Encoding.UTF8.GetString(buffer));
    }

    // ----- Writes -----

    [TestMethod]
    public async Task Write_OverwriteReplacesAndAppendAdds()
    {
        await Write("f.txt", "one");
        await Write("f.txt", "two");
        Assert.AreEqual("two", await Read("f.txt"));
        await Write("f.txt", "+three", UpdateFileOptions.Append);
        Assert.AreEqual("two+three", await Read("f.txt"));
        await Write("new.txt", "fresh", UpdateFileOptions.Append);
        Assert.AreEqual("fresh", await Read("new.txt"));
    }

    [TestMethod]
    public async Task Write_CreatesParentFolders()
    {
        await Write("a/b/c/d.txt", "deep");
        Assert.AreEqual("deep", await Read("a/b/c/d.txt"));
        Assert.IsTrue((await Provider.GetFileInfoAsync("a/b")).IsDirectory);
    }

    [TestMethod]
    public async Task Write_ReadsFromTheStreamPositionAndLeavesItOpen()
    {
        var source = TestStreams.Text("HEADERbody");
        source.Position = 6;
        await Provider.UpdateFileAsync("f.txt", source);
        Assert.AreEqual("body", await Read("f.txt"));
        Assert.IsTrue(source.CanRead, "The caller's stream is left open");

        var more = TestStreams.Text("XX-tail");
        more.Position = 2;
        await Provider.UpdateFileAsync("f.txt", more, UpdateFileOptions.Append);
        Assert.AreEqual("body-tail", await Read("f.txt"));
        Assert.IsTrue(more.CanRead, "The caller's stream is left open");
    }

    [TestMethod]
    public async Task Write_FailedOverwriteKeepsTheOldContent()
    {
        await Write("f.txt", "original");
        var failing = new FailingStream(Encoding.UTF8.GetBytes("partial"));
        await Assert.ThrowsAsync<IOException>(() => Provider.UpdateFileAsync("f.txt", failing));
        Assert.AreEqual("original", await Read("f.txt"));
        Assert.IsFalse(failing.IsDisposed, "The caller's stream is left open");
    }

    [TestMethod]
    public async Task Write_FailedAppendKeepsTheOldContent()
    {
        await Write("f.txt", "original");
        await Assert.ThrowsAsync<IOException>(() => Provider.UpdateFileAsync("f.txt", new FailingStream(Encoding.UTF8.GetBytes("partial")), UpdateFileOptions.Append));
        Assert.AreEqual("original", await Read("f.txt"));
    }

    [TestMethod]
    public async Task Write_IfNotExists()
    {
        await Write("f.txt", "first", new UpdateFileOptions { IfNotExists = true });
        await Assert.ThrowsAsync<PreconditionFailedException>(() => Write("f.txt", "second", new UpdateFileOptions { IfNotExists = true }));
        Assert.AreEqual("first", await Read("f.txt"));
    }

    [TestMethod]
    public async Task Write_IfMatch()
    {
        await Write("f.txt", "first");
        var etag = (await Provider.GetFileInfoAsync("f.txt")).ETag;
        await Write("f.txt", "second", new UpdateFileOptions { IfMatch = etag });
        Assert.AreEqual("second", await Read("f.txt"));
        await Assert.ThrowsAsync<PreconditionFailedException>(() => Write("f.txt", "third", new UpdateFileOptions { IfMatch = etag }));
        Assert.AreEqual("second", await Read("f.txt"));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => Write("f.txt", "x", new UpdateFileOptions { IfMatch = etag, IfNotExists = true }));
    }

    [TestMethod]
    public async Task Write_ConcurrentAppendIsNeverLost()
    {
        if (!DetectsConcurrentAppends)
            Assert.Inconclusive("The provider cannot detect the concurrent write this test stages (D19).");
        await Write("f.txt", "start");
        var racing = new InterceptingStream(Encoding.UTF8.GetBytes("+mine"),
            () => Provider.UpdateFileAsync("f.txt", TestStreams.Text("theirs")).GetAwaiter().GetResult());
        try
        {
            await Provider.UpdateFileAsync("f.txt", racing, UpdateFileOptions.Append);
        }
        catch (PreconditionFailedException)
        {
            Assert.AreEqual("theirs", await Read("f.txt"), "The concurrent write is kept");
            return;
        }
        StringAssert.Contains(await Read("f.txt"), "theirs", "The concurrent write is kept");
    }

    [TestMethod]
    public async Task Write_LargeFile()
    {
        var size = LargeFileSize;
        var first = TestStreams.Bytes(size, seed: 3);
        await Provider.UpdateFileAsync("large.bin", new MemoryStream(first));
        Assert.AreEqual(size, (await Provider.GetFileInfoAsync("large.bin")).Length);
        CollectionAssert.AreEqual(SHA256.HashData(first), await Hash(await Provider.ReadFileAsync("large.bin")));

        var second = TestStreams.Bytes(size, seed: 4);
        await Provider.UpdateFileAsync("large.bin", new MemoryStream(second));
        var expected = second;
        if (SupportsLargeConditionalWrites)
        {
            await Provider.UpdateFileAsync("large.bin", TestStreams.Text("tail"), UpdateFileOptions.Append);
            expected = [.. second, .. Encoding.UTF8.GetBytes("tail")];
        }
        CollectionAssert.AreEqual(SHA256.HashData(expected), await Hash(await Provider.ReadFileAsync("large.bin")));

        await using (var local = await Provider.CreateLocalFileStreamAsync("large.bin", readOnly: false))
        {
            local.Position = size / 2;
            await local.WriteAsync(Encoding.UTF8.GetBytes("MARK"));
        }
        Encoding.UTF8.GetBytes("MARK").CopyTo(expected, size / 2);
        CollectionAssert.AreEqual(SHA256.HashData(expected), await Hash(await Provider.ReadFileAsync("large.bin")));

        await Assert.ThrowsAsync<IOException>(() => Provider.UpdateFileAsync("large.bin", new FailingStream(first[..(int)(size / 2)])));
        if (FailedLargeWriteKeepsOldContent)
            CollectionAssert.AreEqual(SHA256.HashData(expected), await Hash(await Provider.ReadFileAsync("large.bin")));
    }

    [TestMethod]
    public async Task Write_AFileAtAFolderPath()
    {
        await Write("dir/x.txt", "below");
        if (!FileAndFolderMayShareAName)
        {
            await Assert.ThrowsExactlyAsync<IOException>(() => Write("dir", "file"));
            Assert.IsTrue((await Provider.GetFileInfoAsync("dir")).IsDirectory, "The folder is left as it was");
            Assert.AreEqual("below", await Read("dir/x.txt"));
            return;
        }
        // Folders are name prefixes: the file is stored beside the folder, and a listing shows both (D30).
        await Write("dir", "file");
        Assert.AreEqual("file", await Read("dir"));
        Assert.AreEqual("below", await Read("dir/x.txt"));
        await AssertListsAFileAndAFolderAsync("dir");
        await AssertDeletesTheFileThenTheFolderAsync("dir", "dir/x.txt");
    }

    [TestMethod]
    public async Task Write_AFolderUnderAFilePath()
    {
        await Write("f.txt", "file");
        if (!FileAndFolderMayShareAName)
        {
            await Assert.ThrowsExactlyAsync<IOException>(() => Write("f.txt/y.txt", "below"));
            Assert.AreEqual("file", await Read("f.txt"));
            Assert.IsFalse((await Provider.GetFileInfoAsync("f.txt/y.txt")).Exists, "Nothing is written under the file");
            return;
        }
        // Folders are name prefixes: the file under the name is stored, and a listing shows both (D30).
        await Write("f.txt/y.txt", "below");
        Assert.AreEqual("file", await Read("f.txt"));
        Assert.AreEqual("below", await Read("f.txt/y.txt"));
        await AssertListsAFileAndAFolderAsync("f.txt");
        await AssertDeletesTheFileThenTheFolderAsync("f.txt", "f.txt/y.txt");
    }

    // A delete of a name a file and a folder share removes the file first, and a second delete the folder.
    private async Task AssertDeletesTheFileThenTheFolderAsync(string name, string below)
    {
        Assert.IsTrue(await Provider.DeleteAsync(name), "The first delete removes the file");
        var info = await Provider.GetFileInfoAsync(name);
        Assert.IsTrue(info.Exists && info.IsDirectory, "The folder stays after the first delete");
        await Assert.ThrowsAsync<FileNotFoundException>(() => Provider.ReadFileAsync(name));
        Assert.IsTrue((await Provider.GetDirectoryContentsAsync("")).Any(e => e.Name == name && e.IsDirectory), "The folder lists again");
        Assert.IsTrue((await Provider.GetFileInfoAsync(below)).Exists);
        Assert.IsTrue(await Provider.DeleteAsync(name), "The second delete removes the folder");
        Assert.IsFalse((await Provider.GetFileInfoAsync(name)).Exists);
        Assert.IsFalse((await Provider.GetFileInfoAsync(below)).Exists);
        Assert.IsFalse(await Provider.DeleteAsync(name), "Nothing is left");
    }

    // The root's listing of a name a file and a folder share: both, or, where the store lists only the file, the file.
    private async Task AssertListsAFileAndAFolderAsync(string name)
    {
        var listed = (await Provider.GetDirectoryContentsAsync("")).ToList();
        var named = listed.Where(e => e.Name == name).ToList();
        var shown = string.Join(", ", listed.Select(e => e.IsDirectory ? e.Name + "/" : e.Name));
        Assert.IsTrue(named.Any(e => !e.IsDirectory), $"A file named '{name}': {shown}");
        if (ListsAFileAndAFolderOfOneName)
            Assert.IsTrue(named.Count == 2 && named.Any(e => e.IsDirectory), $"A folder named '{name}' too: {shown}");
        else
            Assert.HasCount(1, named, shown);
        Assert.IsTrue((await Provider.GetFileInfoAsync(name)).Exists);
    }

    [TestMethod]
    public async Task Write_LeavesNoTempFiles()
    {
        if (LocalTempFolder is not { } folder)
            return;
        await Write("f.txt", "one");
        await Write("f.txt", "two", UpdateFileOptions.Append);
        await Assert.ThrowsAsync<IOException>(() => Provider.UpdateFileAsync("f.txt", new FailingStream([1, 2, 3])));
        Assert.AreEqual(0, Directory.GetFiles(folder, ".wfp-*.tmp", SearchOption.AllDirectories).Length, "Temp files were left behind");
    }

    // ----- Delete -----

    [TestMethod]
    public async Task Delete_FileFolderAndMissing()
    {
        await Write("f.txt", "x");
        await Write("dir/a.txt", "a");
        await Write("dir/sub/b.txt", "b");
        Assert.IsTrue(await Provider.DeleteAsync("f.txt"));
        Assert.IsFalse((await Provider.GetFileInfoAsync("f.txt")).Exists);
        Assert.IsTrue(await Provider.DeleteAsync("dir"));
        Assert.IsFalse((await Provider.GetFileInfoAsync("dir/sub/b.txt")).Exists);
        Assert.IsFalse((await Provider.GetDirectoryContentsAsync("dir")).Exists);
        Assert.IsFalse(await Provider.DeleteAsync("dir"));
        Assert.IsFalse(await Provider.DeleteAsync("missing.txt"));
    }

    [TestMethod]
    public async Task Delete_FolderSparesSiblingsSharingItsPrefix()
    {
        await Write("a/x.txt", "x");
        await Write("ab.txt", "1");
        await Write("a-b/y.txt", "2");
        await Write("a.txt", "3");
        Assert.IsTrue(await Provider.DeleteAsync("a"));
        Assert.AreEqual("1", await Read("ab.txt"));
        Assert.AreEqual("2", await Read("a-b/y.txt"));
        Assert.AreEqual("3", await Read("a.txt"));
    }

    // ----- Move and copy -----

    [TestMethod]
    public async Task Move_MovesAFileAndCreatesParents()
    {
        await Write("f.txt", "x");
        await Provider.MoveAsync("f.txt", "new/dir/g.txt");
        Assert.IsFalse((await Provider.GetFileInfoAsync("f.txt")).Exists);
        Assert.AreEqual("x", await Read("new/dir/g.txt"));
    }

    [TestMethod]
    public async Task Move_RefusesMissingAndFolderSources()
    {
        await Write("dir/f.txt", "x");
        await Assert.ThrowsAsync<FileNotFoundException>(() => Provider.MoveAsync("missing.txt", "g.txt"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => Provider.MoveAsync("dir", "dir2"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => Provider.CopyAsync("dir", "dir2"));
        Assert.AreEqual("x", await Read("dir/f.txt"));
    }

    [TestMethod]
    public async Task Move_OntoAnExistingFileThrowsAndChangesNothing()
    {
        await Write("a.txt", "a");
        await Write("b.txt", "b");
        await Assert.ThrowsExactlyAsync<FileExistsException>(() => Provider.MoveAsync("a.txt", "b.txt"));
        await Assert.ThrowsExactlyAsync<FileExistsException>(() => Provider.CopyAsync("a.txt", "b.txt"));
        Assert.AreEqual("a", await Read("a.txt"));
        Assert.AreEqual("b", await Read("b.txt"));
    }

    [TestMethod]
    public async Task Move_OntoItselfDoesNothing()
    {
        await Write("a.txt", "a");
        await Provider.MoveAsync("a.txt", "a.txt");
        Assert.AreEqual("a", await Read("a.txt"));
    }

    [TestMethod]
    public async Task Move_ToANameDifferingOnlyInCase()
    {
        await Write("a.txt", "a");
        await Provider.MoveAsync("a.txt", "A.txt");
        Assert.AreEqual("a", await Read("A.txt"));
        var names = (await Provider.GetDirectoryContentsAsync("")).Select(e => e.Name).ToList();
        CollectionAssert.AreEqual(new[] { "A.txt" }, names);
        if (IsCaseInsensitive)
            Assert.AreEqual("a", await Read("a.txt"), "On a store that ignores case, the old name finds the renamed file");
        else
            Assert.IsFalse((await Provider.GetFileInfoAsync("a.txt")).Exists);
    }

    [TestMethod]
    public async Task Move_RemovesExactlyTheSourceKey()
    {
        await Write("a.txt", "a");
        await Write("a.txt.bak", "backup");
        await Provider.MoveAsync("a.txt", "b.txt");
        Assert.AreEqual("a", await Read("b.txt"));
        Assert.AreEqual("backup", await Read("a.txt.bak"));
        Assert.IsFalse((await Provider.GetFileInfoAsync("a.txt")).Exists);
        if (!FileAndFolderMayShareAName)
            return;
        // On an object store a key and a "folder" of the same name can both exist: moving the key leaves the folder.
        await Write("k", "key");
        await Write("k/x.txt", "below");
        await Provider.MoveAsync("k", "moved");
        Assert.AreEqual("key", await Read("moved"));
        Assert.AreEqual("below", await Read("k/x.txt"));
    }

    [TestMethod]
    public async Task Copy_KeepsTheSource()
    {
        await Write("a.txt", "a");
        await Provider.CopyAsync("a.txt", "copies/b.txt");
        Assert.AreEqual("a", await Read("a.txt"));
        Assert.AreEqual("a", await Read("copies/b.txt"));
    }

    // ----- Local file stream -----

    [TestMethod]
    public async Task LocalStream_ReadOnlyOfAMissingFileThrows() =>
        await Assert.ThrowsAsync<FileNotFoundException>(() => Provider.CreateLocalFileStreamAsync("missing.txt", readOnly: true));

    [TestMethod]
    public async Task LocalStream_OfAFolderIsRefused()
    {
        await Write("dir/f.txt", "x");
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => Provider.CreateLocalFileStreamAsync("dir", readOnly: true));
        await Assert.ThrowsAsync<IOException>(() => Provider.CreateLocalFileStreamAsync("dir", readOnly: false));
        Assert.AreEqual("x", await Read("dir/f.txt"));
    }

    [TestMethod]
    public async Task LocalStream_ReadOnlyCannotBeWrittenAndNeverUploads()
    {
        await Write("f.txt", "abc");
        await using (var local = await Provider.CreateLocalFileStreamAsync("f.txt", readOnly: true))
        {
            Assert.IsFalse(local.CanWrite);
            Assert.AreEqual(3, local.Length);
            Assert.ThrowsExactly<NotSupportedException>(() => local.WriteByte(1));
            await Assert.ThrowsExactlyAsync<NotSupportedException>(() => local.CommitAsync());
        }
        Assert.AreEqual("abc", await Read("f.txt"));
    }

    [TestMethod]
    public async Task LocalStream_WriteableCommitsOnDispose()
    {
        await using (var local = await Provider.CreateLocalFileStreamAsync("new/f.txt", readOnly: false))
        {
            Assert.AreEqual(0, local.Length);
            await local.WriteAsync(Encoding.UTF8.GetBytes("created"));
            Assert.IsFalse((await Provider.GetFileInfoAsync("new/f.txt")).Exists, "Nothing is uploaded before the commit");
        }
        Assert.AreEqual("created", await Read("new/f.txt"));

        await using (var local = await Provider.CreateLocalFileStreamAsync("new/f.txt", readOnly: false))
        {
            local.Position = local.Length;
            await local.WriteAsync(Encoding.UTF8.GetBytes("+edit"));
        }
        Assert.AreEqual("created+edit", await Read("new/f.txt"));
    }

    [TestMethod]
    public async Task LocalStream_SkipDoesNotUpload()
    {
        await Write("f.txt", "keep");
        await using (var local = await Provider.CreateLocalFileStreamAsync("f.txt", readOnly: false))
        {
            local.SkipReUploadOnDispose = true;
            local.SetLength(0);
        }
        Assert.AreEqual("keep", await Read("f.txt"));
    }

    [TestMethod]
    public async Task LocalStream_CommitAsyncThenFurtherWritesCommitAgain()
    {
        var local = await Provider.CreateLocalFileStreamAsync("f.txt", readOnly: false);
        await local.WriteAsync(Encoding.UTF8.GetBytes("one"));
        await local.CommitAsync();
        Assert.AreEqual("one", await Read("f.txt"));
        Assert.AreEqual(3, local.Position, "The position survives the commit");
        await local.WriteAsync(Encoding.UTF8.GetBytes("two"));
        await local.DisposeAsync();
        await local.DisposeAsync();
        Assert.AreEqual("onetwo", await Read("f.txt"));
    }

    [TestMethod]
    public async Task LocalStream_DisposeAfterAnExceptionUploadsWhatWasWritten()
    {
        await Write("f.txt", "old");
        try
        {
            await using var local = await Provider.CreateLocalFileStreamAsync("f.txt", readOnly: false);
            local.SetLength(0);
            await local.WriteAsync(Encoding.UTF8.GetBytes("half"));
            throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException)
        {
        }
        Assert.AreEqual("half", await Read("f.txt"));
    }

    // ----- Signed URLs -----

    [TestMethod]
    public async Task SignedUrl_ValidityMustBePositive()
    {
        await Write("f.txt", "x");
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => Provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.Zero }));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => Provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = TimeSpan.FromSeconds(-1) }));
    }

    [TestMethod]
    [DataRow(SignedUrlAccess.Read)]
    [DataRow(SignedUrlAccess.Create)]
    [DataRow(SignedUrlAccess.Write)]
    public async Task SignedUrl_IsNativeOrNotSupported(SignedUrlAccess access)
    {
        await Write("f.txt", "x");
        var options = SignedUrlOptionsFor(access);
        if (!SupportsSignedUrls(access))
        {
            await Assert.ThrowsExactlyAsync<NotSupportedException>(() => Provider.GetSignedUrlAsync("f.txt", options));
            return;
        }
        var before = DateTimeOffset.UtcNow;
        var url = await Provider.GetSignedUrlAsync(access == SignedUrlAccess.Create ? "new.txt" : "f.txt", options);
        Assert.IsTrue(url.Url.IsAbsoluteUri);
        if (url.ExpiresOn is { } expires)
            Assert.IsTrue(expires <= DateTimeOffset.UtcNow.Add(options.Validity).AddSeconds(5) && expires >= before, $"ExpiresOn {expires}");
        Assert.AreEqual(access == SignedUrlAccess.Read ? "GET" : "PUT", url.Method);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            Provider.GetSignedUrlAsync("f.txt", options with { Validity = TimeSpan.FromDays(3650), AllowShorterValidity = false }));
    }

    [TestMethod]
    public async Task SignedUrl_ContentHeadersAreHonouredOrRefused()
    {
        await Write("f.txt", "x");
        var read = SignedUrlOptionsFor(SignedUrlAccess.Read);
        foreach (var (options, expected) in new[] { (read with { DownloadFileName = "report.csv" }, "report.csv"), (read with { ContentType = "text/csv" }, "text/csv") })
        {
            SignedUrl url;
            try
            {
                url = await Provider.GetSignedUrlAsync("f.txt", options);
            }
            catch (NotSupportedException)
            {
                continue;
            }
            // A URL that ignored the option would serve the stored name and type: the URL itself must carry it.
            StringAssert.Contains(Uri.UnescapeDataString(url.Url.Query), expected);
        }
    }

    [TestMethod]
    public async Task SignedUrl_ShorterThanAskedNeedsAllowShorterValidity()
    {
        if (FixedSignedUrlLifetime is not { } lifetime || !SupportsSignedUrls(SignedUrlAccess.Read))
            return;
        await Write("f.txt", "x");
        var longer = new SignedUrlOptions { Validity = lifetime + TimeSpan.FromMinutes(1) };
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => Provider.GetSignedUrlAsync("f.txt", longer));
        var before = DateTimeOffset.UtcNow;
        var url = await Provider.GetSignedUrlAsync("f.txt", longer with { AllowShorterValidity = true });
        if (url.ExpiresOn is { } expires)
            Assert.IsTrue(expires <= DateTimeOffset.UtcNow.Add(lifetime).AddSeconds(5) && expires >= before, $"ExpiresOn {expires}");
        // A URL never outlives what was asked, so a validity shorter than the fixed lifetime is refused.
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            Provider.GetSignedUrlAsync("f.txt", new SignedUrlOptions { Validity = lifetime / 2, AllowShorterValidity = true }));
    }

    [TestMethod]
    public async Task SignedUrl_ReadOfAMissingFileThrows()
    {
        if (!SupportsSignedUrls(SignedUrlAccess.Read))
            return;
        await Assert.ThrowsAsync<FileNotFoundException>(() => Provider.GetSignedUrlAsync("missing.txt", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(1) }));
    }

    // ----- Unsupported, cancellation, errors, missing container -----

    [TestMethod]
    public void Watch_IsNativeOrNotSupported()
    {
        if (SupportsWatch)
            Assert.IsNotNull(Provider.Watch("**/*"));
        else
            Assert.ThrowsExactly<NotSupportedException>(() => Provider.Watch("**/*"));
    }

    [TestMethod]
    public async Task Cancellation_IsHonoured()
    {
        await Write("f.txt", "x");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Provider.UpdateFileAsync("g.txt", TestStreams.Text("x"), cancellationToken: cancelled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => Provider.ReadFileAsync("f.txt", cancelled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => Provider.GetFileInfoAsync("f.txt", cancelled.Token));
    }

    [TestMethod]
    public async Task Errors_OutsideTheContractAreIOException()
    {
        var failure = new StoreFailureException("The store failed.");
        var provider = CreateProviderThatFails(failure) ?? throw new AssertInconclusiveException("The fixture cannot make its store fail (CreateProviderThatFails).");
        await using (provider)
        {
            foreach (var (name, call) in CallsReachingTheStore(provider))
            {
                var e = await Assert.ThrowsExactlyAsync<IOException>(call, name);
                Assert.IsTrue(Causes(e).Contains(failure), $"{name}: the store's exception is inside");
                StringAssert.Contains(e.Message, name.Contains("listing", StringComparison.Ordinal) ? "root" : "f.txt", name);
            }
        }
    }

    [TestMethod]
    public async Task Errors_ATimeoutIsIOException_NotACancellation()
    {
        var timeout = new TaskCanceledException("The store's client gave up waiting.", new TimeoutException());
        var provider = CreateProviderThatFails(timeout) ?? throw new AssertInconclusiveException("The fixture cannot make its store fail (CreateProviderThatFails).");
        await using (provider)
        {
            foreach (var (name, call) in CallsReachingTheStore(provider))
            {
                // The timeout is inside: the exception raised, or its own inner TimeoutException, which the AWS SDK
                // passes on for a request that nobody cancelled.
                var e = await Assert.ThrowsExactlyAsync<IOException>(call, name);
                Assert.IsTrue(Causes(e).Any(c => ReferenceEquals(c, timeout) || ReferenceEquals(c, timeout.InnerException)),
                    $"{name}: the timeout is inside: {string.Join(" > ", Causes(e).Select(c => c.GetType().Name + ": " + c.Message))}");
            }
        }
    }

    [TestMethod]
    public async Task Errors_OfTheContractPassThrough()
    {
        var refused = new UnauthorizedAccessException("The store refused the credential.");
        var provider = CreateProviderThatFails(refused) ?? throw new AssertInconclusiveException("The fixture cannot make its store fail (CreateProviderThatFails).");
        await using (provider)
        {
            foreach (var (name, call) in CallsReachingTheStore(provider))
            {
                var e = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(call, name);
                Assert.IsTrue(Causes(e).Contains(refused), $"{name}: the store's exception is the one thrown, or inside it");
            }
        }
    }

    // Every member that asks the store, each named for the messages.
    private IEnumerable<(string Name, Func<Task> Call)> CallsReachingTheStore(IWriteableFileProvider provider)
    {
        yield return ("info", () => provider.GetFileInfoAsync("f.txt"));
        yield return ("listing", () => provider.GetDirectoryContentsAsync(""));
        yield return ("listing, one level", async () => await provider.EnumerateAsync("").FirstOrDefaultAsync());
        yield return ("listing, recursive", async () => await provider.EnumerateAsync("", recursive: true).FirstOrDefaultAsync());
        yield return ("read", () => provider.ReadFileAsync("f.txt"));
        yield return ("range", () => provider.ReadRangeAsync("f.txt", 1, 2));
        yield return ("seekable", () => provider.OpenReadSeekableAsync("f.txt"));
        yield return ("overwrite", () => provider.UpdateFileAsync("f.txt", TestStreams.Text("x")));
        yield return ("append", () => provider.UpdateFileAsync("f.txt", TestStreams.Text("x"), UpdateFileOptions.Append));
        yield return ("delete", () => provider.DeleteAsync("f.txt"));
        yield return ("move", () => provider.MoveAsync("f.txt", "g.txt"));
        yield return ("copy", () => provider.CopyAsync("f.txt", "g.txt"));
        yield return ("local copy, read-only", () => provider.CreateLocalFileStreamAsync("f.txt", readOnly: true));
        yield return ("local copy", () => provider.CreateLocalFileStreamAsync("f.txt", readOnly: false));
        if (SupportsSignedUrls(SignedUrlAccess.Read))
            yield return ("read URL", () => provider.GetSignedUrlAsync("f.txt", SignedUrlOptionsFor(SignedUrlAccess.Read)));
    }

    private static IEnumerable<Exception> Causes(Exception exception)
    {
        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            yield return e;
            if (e is AggregateException aggregate)
            {
                foreach (var inner in aggregate.Flatten().InnerExceptions.SelectMany(Causes))
                    yield return inner;
            }
        }
    }

    [TestMethod]
    public async Task MissingContainer_ThrowsInvalidOperationEverywhere()
    {
        var provider = CreateProviderWithMissingContainer();
        if (provider is null)
            return;
        await using (provider)
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.GetFileInfoAsync("f.txt"));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.GetDirectoryContentsAsync(""));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.ReadFileAsync("f.txt"));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.UpdateFileAsync("f.txt", TestStreams.Text("x")));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.DeleteAsync("f.txt"));
        }
    }
}

/// <summary>A failure of a store that is none of the contract's types, as an SDK's own exception is.</summary>
public sealed class StoreFailureException : Exception
{
    /// <summary>Creates the exception.</summary>
    public StoreFailureException()
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message.</param>
    public StoreFailureException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public StoreFailureException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
