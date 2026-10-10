using System.Runtime.Versioning;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.Physical.Tests;

/// <summary>What the conformance suite cannot reach on the local disk: the temp files, the races, the per-OS rules.</summary>
[TestClass]
public sealed class PhysicalProviderTests
{
    private string _root = "";
    private TestablePhysicalProvider _provider = null!;

    [TestInitialize]
    public void Start()
    {
        _root = Path.Combine(Path.GetTempPath(), "wfp-physical-unit-" + Guid.NewGuid().ToString("N")[..12]);
        _provider = new TestablePhysicalProvider(_root);
    }

    [TestCleanup]
    public void End()
    {
        _provider.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Disk(params string[] segments) => Path.Combine([_root, .. segments]);

    private Task Write(ProviderPath path, string text, UpdateFileOptions? options = null) =>
        _provider.UpdateFileAsync(path, TestStreams.Text(text), options);

    private async Task<string> Read(ProviderPath path)
    {
        await using var stream = await _provider.ReadFileAsync(path);
        return await new StreamReader(stream).ReadToEndAsync();
    }

    private string[] TempFiles() =>
        Directory.Exists(_root) ? Directory.GetFiles(_root, ".wfp-*.tmp", SearchOption.AllDirectories) : [];

    private string[] Names(string folder = "") =>
        [.. _provider.GetDirectoryContents(folder).Select(e => e.Name).Order(StringComparer.Ordinal)];

    // ----- Writes -----

    [TestMethod]
    public async Task Write_GoesThroughATempFileNextToTheTarget()
    {
        await Write("dir/f.txt", "old");
        string[] during = [];
        var contents = new InterceptingStream(Encoding.UTF8.GetBytes("new"), () =>
        {
            during = Directory.GetFiles(Disk("dir"), ".wfp-*.tmp");
            Assert.AreEqual("old", File.ReadAllText(Disk("dir", "f.txt")), "The target is untouched until the rename");
        });
        await _provider.UpdateFileAsync("dir/f.txt", contents);
        Assert.HasCount(1, during);
        Assert.AreEqual("new", await Read("dir/f.txt"));
        Assert.IsEmpty(TempFiles());
    }

    [TestMethod]
    public async Task Write_ToAFolderIsRefused()
    {
        await Write("dir/sub/f.txt", "x");
        await Assert.ThrowsExactlyAsync<IOException>(() => Write("dir/sub", "y"));
        Assert.AreEqual("x", await Read("dir/sub/f.txt"));
    }

    [TestMethod]
    public async Task Write_WhileTheFileIsReadKeepsWhatTheReaderOpened()
    {
        // P20: readers share the file with writers and deleters. Linux and macOS rename over it, and the reader keeps the
        // old content; Windows refuses to rename over an open file, and the write fails with the file unchanged.
        await Write("f.txt", "old content");
        await using (var reader = await _provider.ReadFileAsync("f.txt"))
        {
            var start = new byte[4];
            await reader.ReadExactlyAsync(start);
            if (OperatingSystem.IsWindows())
            {
                await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => Write("f.txt", "new"));
                Assert.AreEqual("old content", File.ReadAllText(Disk("f.txt")));
            }
            else
            {
                await Write("f.txt", "new");
                Assert.AreEqual("new", File.ReadAllText(Disk("f.txt")));
            }
            Assert.AreEqual("content", await new StreamReader(reader).ReadToEndAsync(), "The reader keeps the version it opened");
        }
        Assert.IsEmpty(TempFiles());
    }

    [TestMethod]
    public async Task Delete_WhileTheFileIsReadGoesAhead()
    {
        await Write("f.txt", "content");
        await using var reader = await _provider.ReadFileAsync("f.txt");
        Assert.IsTrue(await _provider.DeleteAsync("f.txt"));
        Assert.IsFalse((await _provider.GetFileInfoAsync("f.txt")).Exists);
        Assert.AreEqual("content", await new StreamReader(reader).ReadToEndAsync());
    }

    [TestMethod]
    public async Task Write_ReportsItsOwnFailureWhenTheTempFileCannotBeRemoved()
    {
        await Write("f.txt", "old");
        FileStream? holder = null;
        var contents = new InterceptingStream([1, 2, 3], () =>
        {
            // Keep the temp file from being deleted: Windows refuses to delete a file open without delete sharing, Unix to
            // unlink in a folder without write permission (unless the tests run as root).
            var temp = Directory.GetFiles(_root, ".wfp-*.tmp").Single();
            if (OperatingSystem.IsWindows())
                holder = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            else
                File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            throw new IOException("The source failed.");
        });
        try
        {
            var e = await Assert.ThrowsExactlyAsync<IOException>(() => _provider.UpdateFileAsync("f.txt", contents));
            Assert.AreEqual("The source failed.", e.Message);
        }
        finally
        {
            holder?.Dispose();
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Assert.AreEqual("old", await Read("f.txt"));
        CollectionAssert.AreEqual(new[] { "f.txt" }, Names(), "A temp file left behind stays out of listings");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    [UnsupportedOSPlatform("windows")]
    public async Task Write_KeepsTheFilesUnixPermissions()
    {
        await Write("f.txt", "one");
        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;
        File.SetUnixFileMode(Disk("f.txt"), mode);
        await Write("f.txt", "two");
        Assert.AreEqual(mode, File.GetUnixFileMode(Disk("f.txt")), "The exact mode, group write included, whatever the umask");
        await Write("f.txt", "+three", UpdateFileOptions.Append);
        Assert.AreEqual(mode, File.GetUnixFileMode(Disk("f.txt")));
        Assert.AreEqual("two+three", await Read("f.txt"));

        // A private file's new content is never readable by others, not even while it is written (P28).
        const UnixFileMode secret = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.SetUnixFileMode(Disk("f.txt"), secret);
        var during = UnixFileMode.None;
        var contents = new InterceptingStream(Encoding.UTF8.GetBytes("four"),
            () => during = File.GetUnixFileMode(Directory.GetFiles(_root, ".wfp-*.tmp").Single()));
        await _provider.UpdateFileAsync("f.txt", contents);
        Assert.AreEqual(UnixFileMode.None, during & ~secret, $"The temp file was {during}");
        Assert.AreEqual(secret, File.GetUnixFileMode(Disk("f.txt")));
    }

    // ----- Versions and preconditions -----

    [TestMethod]
    public async Task ETag_ChangesWithEveryWriteEvenWhenTheClockStandsStill()
    {
        var now = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        _provider.Clock = new FixedClock(now);
        await Write("f.txt", "aaaa");
        var first = await _provider.GetFileInfoAsync("f.txt");
        await Write("f.txt", "bbbb");
        var second = await _provider.GetFileInfoAsync("f.txt");
        Assert.AreEqual(now, first.LastModified);
        Assert.AreEqual(now.AddTicks(1), second.LastModified, "One tick after the file it replaced");
        Assert.AreNotEqual(first.ETag, second.ETag);
        Assert.AreEqual(second.ETag, _provider.GetDirectoryContents("").Cast<IWriteableFileInfo>().Single().ETag, "A listing gives the same ETag");
    }

    [TestMethod]
    public async Task ETag_FollowsAChangeMadeByAnotherProgram()
    {
        await Write("f.txt", "abc");
        var before = (await _provider.GetFileInfoAsync("f.txt")).ETag;
        File.SetLastWriteTimeUtc(Disk("f.txt"), DateTime.UtcNow.AddMinutes(-5));
        var touched = (await _provider.GetFileInfoAsync("f.txt")).ETag;
        File.AppendAllText(Disk("f.txt"), "d");
        var appended = (await _provider.GetFileInfoAsync("f.txt")).ETag;
        Assert.AreEqual(3, new[] { before, touched, appended }.Distinct().Count());
    }

    [TestMethod]
    public async Task IfMatch_FailsWhenTheFileChangesDuringTheWrite()
    {
        await Write("f.txt", "first");
        var etag = (await _provider.GetFileInfoAsync("f.txt")).ETag;
        var contents = new InterceptingStream(Encoding.UTF8.GetBytes("mine"), () => File.WriteAllText(Disk("f.txt"), "theirs!"));
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => _provider.UpdateFileAsync("f.txt", contents, new UpdateFileOptions { IfMatch = etag }));
        Assert.AreEqual("theirs!", await Read("f.txt"));
        Assert.IsEmpty(TempFiles());
    }

    [TestMethod]
    public async Task IfMatch_OfAMissingFileFails() =>
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => Write("missing.txt", "x", new UpdateFileOptions { IfMatch = "1-1" }));

    [TestMethod]
    public async Task IfNotExists_FailsWhenTheFileAppearsDuringTheWrite()
    {
        var contents = new InterceptingStream(Encoding.UTF8.GetBytes("mine"), () => File.WriteAllText(Disk("f.txt"), "theirs"));
        var e = await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => _provider.UpdateFileAsync("f.txt", contents, new UpdateFileOptions { IfNotExists = true }));
        Assert.IsInstanceOfType<IOException>(e.InnerException);
        Assert.AreEqual("theirs", await Read("f.txt"));
        Assert.IsEmpty(TempFiles());
    }

    [TestMethod]
    public async Task Append_FailsWhenTheFileChangesWhileItCopies()
    {
        // D19 on Physical: the check before the rename catches a change made while the append reads, but is not atomic.
        await Write("f.txt", "start");
        var contents = new InterceptingStream(Encoding.UTF8.GetBytes("+mine"), () => File.WriteAllText(Disk("f.txt"), "theirs"));
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => _provider.UpdateFileAsync("f.txt", contents, UpdateFileOptions.Append));
        Assert.AreEqual("theirs", await Read("f.txt"));
        Assert.IsEmpty(TempFiles());
    }

    [TestMethod]
    public async Task Append_ToAMissingFileFailsWhenOneAppearsMeanwhile()
    {
        var contents = new InterceptingStream(Encoding.UTF8.GetBytes("mine"), () => File.WriteAllText(Disk("f.txt"), "theirs"));
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => _provider.UpdateFileAsync("f.txt", contents, UpdateFileOptions.Append));
        Assert.AreEqual("theirs", await Read("f.txt"));
        Assert.IsEmpty(TempFiles());
    }

    [TestMethod]
    public async Task Append_ChecksTheConditionsOnTheVersionItOpens()
    {
        await Write("f.txt", "a");
        var etag = (await _provider.GetFileInfoAsync("f.txt")).ETag;
        await Write("f.txt", "b", UpdateFileOptions.Append with { IfMatch = etag });
        Assert.AreEqual("ab", await Read("f.txt"));
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => Write("f.txt", "c", UpdateFileOptions.Append with { IfMatch = etag }));
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => Write("f.txt", "c", UpdateFileOptions.Append with { IfNotExists = true }));
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => Write("missing.txt", "c", UpdateFileOptions.Append with { IfMatch = etag }));
        Assert.AreEqual("ab", await Read("f.txt"));
        Assert.IsFalse(File.Exists(Disk("missing.txt")));
    }

    // ----- Reads -----

    [TestMethod]
    public async Task Read_OfAFolderOrBelowAFileIsFileNotFound()
    {
        await Write("dir/f.txt", "x");
        var folder = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => _provider.ReadFileAsync("dir"));
        Assert.AreEqual("dir", folder.FileName, "The message names the path, never the disk path");
        Assert.IsNotNull(folder.InnerException);
        var below = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => _provider.ReadFileAsync("dir/f.txt/x"));
        Assert.IsNotNull(below.InnerException);
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => _provider.ReadFileAsync("missing/f.txt"));
    }

    [TestMethod]
    public async Task Read_OfAFileThatCannotBeOpenedReportsTheStoresError()
    {
        // The file is there: its open failure is not a FileNotFoundException.
        await Write("f.txt", "x");
        if (OperatingSystem.IsWindows())
        {
            using var locked = new FileStream(Disk("f.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
            await Assert.ThrowsExactlyAsync<IOException>(() => _provider.ReadFileAsync("f.txt"));
            return;
        }
        File.SetUnixFileMode(Disk("f.txt"), UnixFileMode.None);
        try
        {
            bool readable;
            try
            {
                File.OpenRead(Disk("f.txt")).Dispose();
                readable = true; // root reads anything
            }
            catch (UnauthorizedAccessException)
            {
                readable = false;
            }
            if (!readable)
                await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => _provider.ReadFileAsync("f.txt"));
        }
        finally
        {
            File.SetUnixFileMode(Disk("f.txt"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [TestMethod]
    public async Task Range_ChecksIfMatchOnTheFileItReads()
    {
        await Write("f.txt", "0123456789");
        var etag = (await _provider.GetFileInfoAsync("f.txt")).ETag;
        await using (var range = await _provider.ReadRangeAsync("f.txt", 4, 3, new ReadOptions { IfMatch = etag }))
            Assert.AreEqual("456", await new StreamReader(range).ReadToEndAsync());
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => _provider.ReadRangeAsync("f.txt", 0, 1, new ReadOptions { IfMatch = "0-0" }));
        // The refused read closed its handle: the file can be replaced and deleted, on Windows too.
        await Write("f.txt", "replaced");
        Assert.IsTrue(await _provider.DeleteAsync("f.txt"));
    }

    [TestMethod]
    public async Task Range_IsAForwardOnlyStream()
    {
        await Write("f.txt", "0123456789");
        var range = await _provider.ReadRangeAsync("f.txt", 2, 5);
        Assert.IsTrue(range.CanRead);
        Assert.IsFalse(range.CanSeek);
        Assert.IsFalse(range.CanWrite);
        Assert.ThrowsExactly<NotSupportedException>(() => range.Length);
        Assert.ThrowsExactly<NotSupportedException>(() => range.Position);
        Assert.ThrowsExactly<NotSupportedException>(() => range.Position = 0);
        Assert.ThrowsExactly<NotSupportedException>(() => range.Seek(0, SeekOrigin.Begin));
        Assert.ThrowsExactly<NotSupportedException>(() => range.SetLength(1));
        Assert.ThrowsExactly<NotSupportedException>(() => range.Write([1], 0, 1));
        range.Flush();
        var buffer = new byte[10];
        Assert.AreEqual(2, range.Read(buffer, 0, 2));
        Assert.AreEqual("23", Encoding.UTF8.GetString(buffer, 0, 2));
        Assert.AreEqual(3, await range.ReadAsync(buffer.AsMemory(0, 10)));
        Assert.AreEqual("456", Encoding.UTF8.GetString(buffer, 0, 3));
#pragma warning disable CA1835 // The array overload is the one under test.
        Assert.AreEqual(0, await range.ReadAsync(buffer, 0, 10, CancellationToken.None), "The range ends at its length");
#pragma warning restore CA1835
        await range.DisposeAsync();
        Assert.IsFalse(range.CanRead);
        Assert.IsTrue(await _provider.DeleteAsync("f.txt"), "Disposing the range closed its handle");
    }

    // ----- Listing -----

    [TestMethod]
    public async Task List_HidesOnlyTheProvidersTempFiles()
    {
        await Write("f.txt", "x");
        foreach (var name in new[] { ".wfp-0123abcd.tmp", ".hidden", "x.tmp", ".wfp-notes.txt" })
            File.WriteAllText(Disk(name), name);
        Directory.CreateDirectory(Disk(".wfp-folder.tmp"));
        CollectionAssert.AreEqual(new[] { ".hidden", ".wfp-folder.tmp", ".wfp-notes.txt", "f.txt", "x.tmp" }, Names());
    }

    [TestMethod]
    public async Task List_EntriesCarryTheirDiskPaths()
    {
        await Write("dir/f.txt", "x");
        var entries = (await _provider.GetDirectoryContentsAsync("")).Cast<IWriteableFileInfo>().ToList();
        Assert.AreEqual(Disk("dir"), entries.Single().PhysicalPath);
        Assert.AreEqual(Disk("dir", "f.txt"), (await _provider.GetFileInfoAsync("dir/f.txt")).PhysicalPath);
        Assert.AreEqual(Disk("dir"), (await _provider.GetFileInfoAsync("dir")).PhysicalPath);
        Assert.IsNull((await _provider.GetFileInfoAsync("missing")).PhysicalPath);
    }

    [TestMethod]
    public async Task List_OfAMissingRootIsAnEmptyFolder()
    {
        var root = await _provider.GetDirectoryContentsAsync("");
        Assert.IsTrue(root.Exists);
        Assert.IsFalse(root.Any());
        Assert.IsFalse(Directory.Exists(_root), "Listing creates nothing");
    }

    [TestMethod]
    public async Task List_StopsWhenCancelled()
    {
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
            await Write(name, name);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in _provider.EnumerateAsync("", cancellationToken: cancellation.Token))
                await cancellation.CancelAsync();
        });
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in _provider.EnumerateAsync("", cancellationToken: cancellation.Token))
                Assert.Fail("A cancelled listing yields nothing");
        });
    }

    // ----- Move and copy -----

    [TestMethod]
    public async Task Move_RefusesADestinationThatAppearedMeanwhile()
    {
        // The core members, called past the base class's own destination check: the rename itself refuses.
        await Write("a.txt", "a");
        await Write("b.txt", "b");
        var move = await Assert.ThrowsExactlyAsync<FileExistsException>(() => _provider.MoveCore("a.txt", "b.txt"));
        Assert.IsInstanceOfType<IOException>(move.InnerException);
        var copy = await Assert.ThrowsExactlyAsync<FileExistsException>(() => _provider.CopyCore("a.txt", "b.txt"));
        Assert.IsInstanceOfType<PreconditionFailedException>(copy.InnerException);
        Assert.AreEqual("a", await Read("a.txt"));
        Assert.AreEqual("b", await Read("b.txt"));
        Assert.IsEmpty(TempFiles());
    }

    [TestMethod]
    public async Task Move_OfASourceThatVanishedGivesTheDestinationBack()
    {
        var e = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => _provider.MoveCore("gone.txt", "new/b.txt"));
        Assert.AreEqual("gone.txt", e.FileName);
        Assert.IsInstanceOfType<FileNotFoundException>(e.InnerException);
        Assert.IsFalse(File.Exists(Disk("new", "b.txt")), "The name taken for the move is given back");
    }

    [TestMethod]
    public async Task Move_ToANameDifferingOnlyInCaseRenames()
    {
        // Forced on every system, so the case-only path runs on Linux too, where it is a plain rename.
        using var provider = new TestablePhysicalProvider(_root) { CaseInsensitive = true };
        await provider.UpdateFileAsync("a.txt", TestStreams.Text("a"));
        await provider.MoveAsync("a.txt", "A.txt");
        CollectionAssert.AreEqual(new[] { "A.txt" }, Directory.GetFiles(_root).Select(Path.GetFileName).ToArray());
    }

    // ----- Paths -----

    [TestMethod]
    public async Task Paths_OutsideTheRootAreRefused()
    {
        // Validated segments cannot leave the root; the provider checks the resolved disk path anyway.
        Directory.CreateDirectory(_root);
        var outside = Path.Combine(Path.GetDirectoryName(_root)!, Path.GetFileName(_root) + "-outside.txt");
        File.WriteAllText(outside, "outside");
        try
        {
            var escape = _provider.Unvalidated("..", Path.GetFileName(outside));
            var e = await Assert.ThrowsExactlyAsync<ArgumentException>(() => _provider.GetFileInfoCore(escape));
            Assert.AreEqual("path", e.ParamName);
            Assert.DoesNotContain(_root, e.Message);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => _provider.ReadCore(escape));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => _provider.ReadRangeCore(escape));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => _provider.UpdateCore(escape, TestStreams.Text("x"), UpdateFileOptions.Overwrite));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => _provider.DeleteCore(escape));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => _provider.FolderExistsCore(_provider.Unvalidated("..")));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => _provider.ListCore(_provider.Unvalidated("..")));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => _provider.MoveCore(escape, _provider.Resolved("in.txt")));
            Assert.AreEqual("outside", File.ReadAllText(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [TestMethod]
    [DataRow("a:b")]
    [DataRow("c:")]
    [DataRow("f.txt:stream")]
    public async Task Paths_WithAColonAreRefused(string name)
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => _provider.GetFileInfoAsync(name));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => Write("dir/" + name, "x"));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => _provider.DeleteAsync(name));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => _provider.MoveAsync("a.txt", name));
        Assert.IsFalse(Directory.Exists(_root));
    }

    [TestMethod]
    [DataRow("my file.txt")]
    [DataRow("árvíztűrő tükörfúrógép.txt")]
    [DataRow("日本語.txt")]
    [DataRow("a..b.txt")]
    [DataRow(".hidden")]
    public async Task Paths_NamesLandOnDiskAsGiven(string name)
    {
        await Write("dir/" + name, "x");
        Assert.IsTrue(File.Exists(Disk("dir", name)));
        Assert.IsTrue((await _provider.GetFileInfoAsync(new[] { "dir", name })).Exists);
        CollectionAssert.AreEqual(new[] { name }, Names("dir"));
    }

    [TestMethod]
    public async Task Paths_AnEmptySegmentNeverReachesItsParent()
    {
        await Write("keep/f.txt", "x");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => _provider.DeleteAsync(new[] { "keep", "" }));
        Assert.AreEqual("x", await Read("keep/f.txt"));
    }

    [TestMethod]
    public async Task List_EntriesCanBeUsedAsPaths()
    {
        await Write("list/sub/f.txt", "x");
        var file = (await _provider.GetDirectoryContentsAsync("list/sub")).Single();
        await _provider.MoveAsync(file, "moved/g.txt");
        Assert.AreEqual("x", await Read("moved/g.txt"));
        var sub = (await _provider.GetDirectoryContentsAsync("list")).Single();
        Assert.IsTrue(await _provider.DeleteAsync(sub));
        Assert.IsFalse(Directory.Exists(Disk("list", "sub")));
        Assert.IsTrue(Directory.Exists(Disk("list")));
    }

    [TestMethod]
    public void Paths_ARefusedSegmentIsNamedWithoutItsParents()
    {
        var e = Assert.ThrowsExactly<ArgumentException>(() => _provider.GetFileInfo(new[] { "secret-parent", ".." }));
        StringAssert.Contains(e.Message, "'..'");
        Assert.DoesNotContain("secret-parent", e.Message);
        Assert.DoesNotContain(_root, e.Message);
    }

    // ----- Watch and disposal -----

    [TestMethod]
    public async Task Watch_FiresWhenAWriteLands()
    {
        var token = _provider.Watch("*.txt");
        Assert.IsTrue(Directory.Exists(_root), "Watching a missing root creates it");
        var fired = new TaskCompletionSource();
        using var registration = token.RegisterChangeCallback(_ => fired.TrySetResult(), null);
        await Write("f.txt", "x");
        await fired.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.IsTrue(token.HasChanged);
        Assert.IsNotNull(_provider.Watch("**/*"), "Later calls reuse the watcher");
    }

    [TestMethod]
    public void Watch_AfterDisposeIsRefused()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => _provider.Watch(null!));
        _provider.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => _provider.Watch("*"));
    }

    [TestMethod]
    public void Dispose_FromAFinalizerLeavesTheWatcherAlone()
    {
        Assert.IsNotNull(_provider.Watch("*"));
        _provider.DisposeFromFinalizer();
        Assert.IsNotNull(_provider.Watch("*"), "Only Dispose() stops watching");
        _provider.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => _provider.Watch("*"));
    }

    // ----- The root -----

    [TestMethod]
    public async Task Root_AtTheTopOfTheFileSystemResolvesItsChildren()
    {
        // Read only: a root that ends in a separator ("/" or "C:\") still reaches what is below it.
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var top = Path.GetPathRoot(temp)!;
        using var provider = new WriteablePhysicalFileProvider(top);
        Assert.AreEqual(top, provider.Options.RootDirectoryPath);
        var info = await provider.GetFileInfoAsync(Path.GetRelativePath(top, temp));
        Assert.IsTrue(info.IsDirectory);
        Assert.AreEqual(temp, info.PhysicalPath);
    }
}
