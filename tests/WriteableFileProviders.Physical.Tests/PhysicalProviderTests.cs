using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.FileProviders;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.Physical.Tests;

/// <summary>What the conformance suite cannot reach on the local disk: the temp files, the races, the per-OS rules.</summary>
[TestClass]
public sealed class PhysicalProviderTests
{
    private string _root = "";
    private WriteablePhysicalFileProvider _provider = null!;

    [TestInitialize]
    public void Start()
    {
        _root = Path.Combine(Path.GetTempPath(), "wfp-physical-unit-" + Guid.NewGuid().ToString("N")[..12]);
        _provider = new WriteablePhysicalFileProvider(_root);
    }

    // Runs the test on the way new names are taken on Windows (a rename that does not replace) or on Linux and macOS (a
    // hard link, or where the system has none, as on Windows, an exclusive create, then a rename), whichever system
    // runs it. With linkRefused, the system refuses the hard link (as a file system without them does), so Linux and
    // macOS take the name the way they fall back to. 'tempCreated' is the provider's TempCreated hook.
    private void TakeNewNames(bool byRenameWithoutReplacing, bool linkRefused = false, Action<string>? tempCreated = null)
    {
        _provider.Dispose();
        _provider = linkRefused
            ? new WriteablePhysicalFileProvider(_root) { RenamesWithoutReplacing = byRenameWithoutReplacing, Link = (_, _) => 1, TempCreated = tempCreated }
            : new WriteablePhysicalFileProvider(_root) { RenamesWithoutReplacing = byRenameWithoutReplacing, TempCreated = tempCreated };
    }

    // A hook that runs 'change' the first time only: another writer, once.
    private static Action<string> Once(Action change)
    {
        var done = false;
        return _ =>
        {
            if (!done)
            {
                done = true;
                change();
            }
        };
    }

    // Whether the process can be refused access to its own files: not when it runs as root.
    private static bool PermissionsApply(string file)
    {
        try
        {
            File.OpenWrite(file).Dispose();
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
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
        // Readers share the file with writers and deleters. Linux and macOS rename over it, and the reader keeps the
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

        // A private file's new content is never readable by others, not even while it is written.
        const UnixFileMode secret = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.SetUnixFileMode(Disk("f.txt"), secret);
        var during = UnixFileMode.None;
        var contents = new InterceptingStream(Encoding.UTF8.GetBytes("four"),
            () => during = File.GetUnixFileMode(Directory.GetFiles(_root, ".wfp-*.tmp").Single()));
        await _provider.UpdateFileAsync("f.txt", contents);
        Assert.AreEqual(UnixFileMode.None, during & ~secret, $"The temp file was {during}");
        Assert.AreEqual(secret, File.GetUnixFileMode(Disk("f.txt")));

        // The setuid, setgid and sticky bits are not carried over, as writing to a file clears them.
        File.SetUnixFileMode(Disk("f.txt"), mode | UnixFileMode.SetUser | UnixFileMode.StickyBit);
        await Write("f.txt", "five");
        Assert.AreEqual(mode, File.GetUnixFileMode(Disk("f.txt")));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    [UnsupportedOSPlatform("windows")]
    public async Task Copy_KeepsTheSourcesUnixPermissions()
    {
        const UnixFileMode secret = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await Write("secret.txt", "s");
        File.SetUnixFileMode(Disk("secret.txt"), secret);
        await _provider.CopyAsync("secret.txt", "copies/secret.txt");
        Assert.AreEqual(secret, File.GetUnixFileMode(Disk("copies", "secret.txt")));

        const UnixFileMode shared = secret | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;
        File.SetUnixFileMode(Disk("secret.txt"), shared);
        await _provider.CopyAsync("secret.txt", "copies/shared.txt");
        Assert.AreEqual(shared, File.GetUnixFileMode(Disk("copies", "shared.txt")), "The exact mode, whatever the umask");
        Assert.AreEqual("s", await Read("copies/shared.txt"));
    }

    [TestMethod]
    public async Task Write_OfAFileTheProcessMayNotWriteIsRefused()
    {
        // As in 2026, and as on Windows, where the rename itself is refused; Linux and macOS would rename over it.
        await Write("f.txt", "kept");
        if (OperatingSystem.IsWindows())
            File.SetAttributes(Disk("f.txt"), FileAttributes.ReadOnly);
        else
            File.SetUnixFileMode(Disk("f.txt"), UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        try
        {
            if (!PermissionsApply(Disk("f.txt")))
                Assert.Inconclusive("The tests run as root, whom file permissions do not stop.");
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => Write("f.txt", "new"));
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => Write("f.txt", "+more", UpdateFileOptions.Append));
            Assert.AreEqual("kept", await Read("f.txt"));
            Assert.IsEmpty(TempFiles());
        }
        finally
        {
            if (OperatingSystem.IsWindows())
                File.SetAttributes(Disk("f.txt"), FileAttributes.Normal);
            else
                File.SetUnixFileMode(Disk("f.txt"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task Write_InAFolderWhereTheProcessMayNotCreateFilesIsRefusedNamingTheFile()
    {
        // The file itself can be written; its folder denies creating files, which the new content needs.
        await Write("f.txt", "kept");
        var folder = new DirectoryInfo(_root);
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateFiles, AccessControlType.Deny);
        var security = folder.GetAccessControl();
        security.AddAccessRule(deny);
        folder.SetAccessControl(security);
        try
        {
            var e = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => Write("f.txt", "new"));
            StringAssert.Contains(e.Message, "'f.txt'");
            Assert.DoesNotContain(".wfp-", e.Message, "The message names the file, not the provider's temp file");
            // An append's first file there is the copy of the caller's bytes.
            var appended = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => Write("f.txt", "+more", UpdateFileOptions.Append));
            StringAssert.Contains(appended.Message, "'f.txt'");
            Assert.DoesNotContain(".wfp-", appended.Message, "The message names the file, not the append's spool");
        }
        finally
        {
            security.RemoveAccessRule(deny);
            folder.SetAccessControl(security);
        }
        Assert.AreEqual("kept", await Read("f.txt"));
        Assert.IsEmpty(TempFiles());
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    [UnsupportedOSPlatform("windows")]
    public async Task Write_InAFolderTheProcessMayNotWriteIsRefusedNamingTheFile()
    {
        // The rename needs the folder: the new content is created there, next to the file, which itself can be written.
        await Write("f.txt", "kept");
        File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            if (!PermissionsApply(Disk("new.txt")))
                Assert.Inconclusive("The tests run as root, whom file permissions do not stop.");
            var e = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => Write("f.txt", "new"));
            StringAssert.Contains(e.Message, "'f.txt'");
            Assert.DoesNotContain(".wfp-", e.Message, "The message names the file, not the provider's temp file");
            // An append's first file there is the copy of the caller's bytes.
            var appended = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => Write("f.txt", "+more", UpdateFileOptions.Append));
            StringAssert.Contains(appended.Message, "'f.txt'");
            Assert.DoesNotContain(".wfp-", appended.Message, "The message names the file, not the append's spool");
        }
        finally
        {
            File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Assert.AreEqual("kept", await Read("f.txt"));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    [UnsupportedOSPlatform("windows")]
    public async Task Write_CreatesItsTempFileNoMoreReadableThanTheFileItReplaces()
    {
        // Created with the mode, not narrowed after: in between, another user could open it and read the new content.
        var created = new List<UnixFileMode>();
        _provider.Dispose();
        _provider = new WriteablePhysicalFileProvider(_root) { TempCreated = temp => created.Add(File.GetUnixFileMode(temp)) };
        const UnixFileMode secret = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await Write("f.txt", "old");
        File.SetUnixFileMode(Disk("f.txt"), secret);
        await Write("f.txt", "new");
        await Write("f.txt", "+more", UpdateFileOptions.Append);
        await _provider.CopyAsync("f.txt", "copy.txt");
        Assert.HasCount(4, created);
        foreach (var mode in created.Skip(1))
            Assert.AreEqual(UnixFileMode.None, mode & ~secret, $"A temp file was created {mode}");
        Assert.AreEqual(secret, File.GetUnixFileMode(Disk("copy.txt")));
    }

    [TestMethod]
    [DataRow(UpdateFileMode.Overwrite)]
    [DataRow(UpdateFileMode.Append)]
    public async Task Write_OverALinkToNothingReplacesTheLink(UpdateFileMode mode)
    {
        Directory.CreateDirectory(_root);
        try
        {
            File.CreateSymbolicLink(Disk("link.txt"), Disk("nowhere.txt"));
        }
        catch (Exception e) when (OperatingSystem.IsWindows() && e is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive($"Creating a symbolic link on Windows needs Developer Mode or elevation: {e.Message}");
        }
        await _provider.UpdateFileAsync("link.txt", TestStreams.Text("new"), new UpdateFileOptions { Mode = mode });
        Assert.AreEqual("new", await Read("link.txt"));
        Assert.IsNull(new FileInfo(Disk("link.txt")).LinkTarget, "The link is replaced by a file");
        Assert.IsFalse(File.Exists(Disk("nowhere.txt")), "Its target is left as it was");
        if (!OperatingSystem.IsWindows())
            Assert.AreEqual(UnixFileMode.None, File.GetUnixFileMode(Disk("link.txt")) & UnixFileMode.OtherWrite, "Not the link's own mode");
    }

    [TestMethod]
    public async Task IfNotExists_OverALinkToNothingIsRefused()
    {
        // The name is taken, by the link itself: refused before any name is taken, the link left as it was.
        Directory.CreateDirectory(_root);
        try
        {
            File.CreateSymbolicLink(Disk("link.txt"), Disk("nowhere.txt"));
        }
        catch (Exception e) when (OperatingSystem.IsWindows() && e is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive($"Creating a symbolic link on Windows needs Developer Mode or elevation: {e.Message}");
        }
        await Assert.ThrowsExactlyAsync<FileAlreadyExistsException>(() => _provider.UpdateFileAsync("link.txt", TestStreams.Text("new"), new UpdateFileOptions { IfNotExists = true }));
        Assert.IsNotNull(new FileInfo(Disk("link.txt")).LinkTarget, "The link is left as it was");
        Assert.IsFalse(File.Exists(Disk("nowhere.txt")));
        Assert.IsEmpty(TempFiles());
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ALinkToAFile_IsDescribedPinnedAndWrittenAsItsTarget(bool resolvesAsTheKernel)
    {
        // A file reached through a symbolic link: its info, its listing entry and its ETag describe the target, which reads
        // open, so a pinned read and an append through the link see one version (the link's own length is 0 on Windows,
        // the length of its target's path on Linux). Describing it never opens the target. A write replaces the link with
        // a file, and leaves the target as it was; ALinkBehindALinkedFolder checks the mode the new file takes. Both ways
        // of following a link run on each system.
        _provider.Dispose();
        _provider = new WriteablePhysicalFileProvider(_root) { ResolvesLinksAsTheKernel = resolvesAsTheKernel };
        await Write("target.txt", "0123456789");
        try
        {
            File.CreateSymbolicLink(Disk("link.txt"), Disk("target.txt"));
        }
        catch (Exception e) when (OperatingSystem.IsWindows() && e is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive($"Creating a symbolic link on Windows needs Developer Mode or elevation: {e.Message}");
        }

        var accessed = File.GetLastAccessTimeUtc(Disk("target.txt"));
        var written = File.GetLastWriteTimeUtc(Disk("target.txt"));
        var info = await _provider.GetFileInfoAsync("link.txt");
        Assert.AreEqual(10, info.Length);
        Assert.AreEqual(10, (await _provider.GetDirectoryContentsAsync("")).Single(e => e.Name == "link.txt").Length);
        Assert.AreEqual(accessed, File.GetLastAccessTimeUtc(Disk("target.txt")), "Describing the link does not touch its target");
        Assert.AreEqual(written, File.GetLastWriteTimeUtc(Disk("target.txt")), "Describing the link does not touch its target");
        Assert.AreEqual((await _provider.GetFileInfoAsync("target.txt")).ETag, info.ETag);
        Assert.AreEqual(Disk("link.txt"), info.PhysicalPath, "The path is the link's, inside the root");
        Assert.AreEqual(10, (await _provider.GetDirectoryContentsAsync("")).Single(e => e.Name == "link.txt").Length);
        await using (var range = await _provider.ReadRangeAsync("link.txt", 2, 3, new ReadOptions { IfMatch = info.ETag }))
            Assert.AreEqual("234", await new StreamReader(range).ReadToEndAsync());
        await using (var seekable = await _provider.OpenReadSeekableAsync("link.txt"))
            Assert.AreEqual("0123456789", await new StreamReader(seekable).ReadToEndAsync());

        await Write("link.txt", "ab", UpdateFileOptions.Append with { IfMatch = info.ETag });
        Assert.AreEqual("0123456789ab", await Read("link.txt"));
        Assert.IsNull(new FileInfo(Disk("link.txt")).LinkTarget, "The link is replaced by a file");
        Assert.AreEqual("0123456789", await Read("target.txt"), "Its target is left as it was");

        File.Delete(Disk("link.txt"));
        File.CreateSymbolicLink(Disk("link.txt"), Disk("target.txt"));
        await Write("link.txt", "new", new UpdateFileOptions { IfMatch = (await _provider.GetFileInfoAsync("link.txt")).ETag });
        Assert.AreEqual("new", await Read("link.txt"));
        Assert.AreEqual("0123456789", await Read("target.txt"));

        // A link to a file the process may read but not write is described as well.
        await Write("readonly.txt", "ro");
        File.CreateSymbolicLink(Disk("ro-link.txt"), Disk("readonly.txt"));
        File.SetAttributes(Disk("readonly.txt"), FileAttributes.ReadOnly);
        try
        {
            Assert.AreEqual(2, (await _provider.GetFileInfoAsync("ro-link.txt")).Length);
        }
        finally
        {
            File.SetAttributes(Disk("readonly.txt"), FileAttributes.Normal);
        }

        // Links in a loop lead to no file: they are described as themselves, and a listing still works.
        File.CreateSymbolicLink(Disk("loop1.txt"), Disk("loop2.txt"));
        File.CreateSymbolicLink(Disk("loop2.txt"), Disk("loop1.txt"));
        Assert.IsNotNull((await _provider.GetFileInfoAsync("loop1.txt")).ETag);
        CollectionAssert.IsSubsetOf(new[] { "loop1.txt", "loop2.txt" }, Names());
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    [UnsupportedOSPlatform("windows")]
    public async Task ALinkBehindALinkedFolder_IsDescribedAndWrittenAsTheSystemResolvesIt()
    {
        // A deployment layout: current -> releases/r1, and releases/r1/secret.txt -> ../../shared/secret.txt. The system
        // resolves the link from where it physically is, releases/r1, so it names site/shared/secret.txt, a private file;
        // resolved as a string from site/current, the same text names a decoy outside the root. The info, the pinned read
        // and the append all see the private file, and the file that replaces the link takes its mode (0600), not the
        // decoy's (0644).
        const UnixFileMode secret = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var site = Disk("site");
        Directory.CreateDirectory(Path.Combine(site, "releases", "r1"));
        Directory.CreateDirectory(Path.Combine(site, "shared"));
        Directory.CreateDirectory(Disk("shared"));
        File.WriteAllText(Path.Combine(site, "shared", "secret.txt"), "private");
        File.SetUnixFileMode(Path.Combine(site, "shared", "secret.txt"), secret);
        File.WriteAllText(Disk("shared", "secret.txt"), "decoy, longer");
        File.SetUnixFileMode(Disk("shared", "secret.txt"), secret | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        Directory.CreateSymbolicLink(Path.Combine(site, "current"), Path.Combine("releases", "r1"));
        File.CreateSymbolicLink(Path.Combine(site, "releases", "r1", "secret.txt"), Path.Combine("..", "..", "shared", "secret.txt"));
        using var provider = new WriteablePhysicalFileProvider(site);

        var info = await provider.GetFileInfoAsync("current/secret.txt");
        Assert.AreEqual(7, info.Length);
        await using (var range = await provider.ReadRangeAsync("current/secret.txt", 1, 3, new ReadOptions { IfMatch = info.ETag }))
            Assert.AreEqual("riv", await new StreamReader(range).ReadToEndAsync());
        await provider.UpdateFileAsync("current/secret.txt", TestStreams.Text("+more"), UpdateFileOptions.Append with { IfMatch = info.ETag });

        var replaced = Path.Combine(site, "releases", "r1", "secret.txt");
        Assert.IsNull(new FileInfo(replaced).LinkTarget, "The link is replaced by a file");
        Assert.AreEqual("private+more", File.ReadAllText(replaced));
        Assert.AreEqual(secret, File.GetUnixFileMode(replaced), "The private file's mode");
        Assert.AreEqual("private", File.ReadAllText(Path.Combine(site, "shared", "secret.txt")));
        Assert.AreEqual("decoy, longer", File.ReadAllText(Disk("shared", "secret.txt")));

        // A link to a file the process may read but not write is described as well.
        File.WriteAllText(Path.Combine(site, "shared", "readonly.txt"), "ro");
        File.SetUnixFileMode(Path.Combine(site, "shared", "readonly.txt"), UnixFileMode.UserRead);
        File.CreateSymbolicLink(Path.Combine(site, "ro-link.txt"), Path.Combine("shared", "readonly.txt"));
        Assert.AreEqual(2, (await provider.GetFileInfoAsync("ro-link.txt")).Length);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    public async Task ALinkToAFifo_IsListedAndRefusedWithoutWaiting()
    {
        // Opening a FIFO to read waits for a writer, and to write waits for a reader: a link to one is described, and a
        // write through it refused as not a regular file, without either wait.
        await MakeFifo("pipe");
        File.CreateSymbolicLink(Disk("pipe-link"), Disk("pipe"));
        var names = await Task.Run(() => Names()).WaitAsync(TimeSpan.FromSeconds(30));
        CollectionAssert.Contains(names, "pipe-link");
        var info = await _provider.GetFileInfoAsync("pipe-link").WaitAsync(TimeSpan.FromSeconds(30));
        Assert.IsTrue(info.Exists);
        Assert.AreEqual(0, info.Length);
        var refused = await Assert.ThrowsExactlyAsync<IOException>(() => _provider.UpdateFileAsync("pipe-link", TestStreams.Text("x")).WaitAsync(TimeSpan.FromSeconds(30)));
        StringAssert.Contains(refused.Message, "not a regular file");
        Assert.IsEmpty(TempFiles());
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    public async Task ALinkToAFifo_IsDescribedWithoutOpeningIt()
    {
        // Describing a file never opens it. A FIFO the process may read but not write, which an open would wait on for a
        // writer, is listed and described at once; and a reader waiting on a FIFO for a writer is not woken by a listing or
        // an info of a link to it, as an open and close would wake it with an early end of file.
        await MakeFifo("readonly-pipe", "0444");
        File.CreateSymbolicLink(Disk("readonly-link"), Disk("readonly-pipe"));
        await MakeFifo("pipe");
        File.CreateSymbolicLink(Disk("pipe-link"), Disk("pipe"));
        var reader = Task.Run(async () =>
        {
            await using var stream = new FileStream(Disk("pipe"), FileMode.Open, FileAccess.Read);
            using var text = new StreamReader(stream);
            return await text.ReadToEndAsync();
        });
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200)); // the reader waits in its open, for a writer

            CollectionAssert.IsSubsetOf(new[] { "pipe-link", "readonly-link" }, await Task.Run(() => Names()).WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.AreEqual(0, (await _provider.GetFileInfoAsync("readonly-link").WaitAsync(TimeSpan.FromSeconds(30))).Length);
            Assert.AreEqual(0, (await _provider.GetFileInfoAsync("pipe-link").WaitAsync(TimeSpan.FromSeconds(30))).Length);
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            Assert.IsFalse(reader.IsCompleted, "The reader still waits for a writer");
        }
        finally
        {
            // Opened to read and write, which never waits, whether or not the reader still does.
            await using var writer = new FileStream(Disk("pipe"), FileMode.Open, FileAccess.ReadWrite);
            await writer.WriteAsync("hello"u8.ToArray());
        }
        Assert.AreEqual("hello", await reader.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [TestMethod]
    public void PhysicalPathOf_ResolvesLinksFromWhereTheyAre()
    {
        // The walk Linux and macOS describe links with, run on every system: a relative target resolves against the part
        // of the path already resolved, '..' climbs from where a linked folder leads, and a loop gives up.
        var site = Disk("site");
        Directory.CreateDirectory(Path.Combine(site, "releases", "r1"));
        Directory.CreateDirectory(Path.Combine(site, "shared"));
        File.WriteAllText(Path.Combine(site, "shared", "secret.txt"), "private");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(site, "current"), Path.Combine("releases", "r1"));
        }
        catch (Exception e) when (OperatingSystem.IsWindows() && e is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive($"Creating a symbolic link on Windows needs Developer Mode or elevation: {e.Message}");
        }
        File.CreateSymbolicLink(Path.Combine(site, "releases", "r1", "secret.txt"), Path.Combine("..", "..", "shared", "secret.txt"));
        File.CreateSymbolicLink(Path.Combine(site, "absolute.txt"), Path.Combine(site, "current", "secret.txt"));
        File.CreateSymbolicLink(Path.Combine(site, "loop1"), "loop2");
        File.CreateSymbolicLink(Path.Combine(site, "loop2"), "loop1");

        // The temp folder may itself be reached through a link (macOS: /var -> /private/var).
        var secret = WriteablePhysicalFileProvider.PhysicalPathOf(Path.Combine(site, "shared", "secret.txt"));
        Assert.AreEqual(secret, WriteablePhysicalFileProvider.PhysicalPathOf(Path.Combine(site, "current", "secret.txt")));
        Assert.AreEqual(secret, WriteablePhysicalFileProvider.PhysicalPathOf(Path.Combine(site, "absolute.txt")));
        Assert.AreEqual(secret, WriteablePhysicalFileProvider.PhysicalPathOf(Path.Combine(site, "current", ".", "..", "r1", "secret.txt")));
        Assert.IsNull(WriteablePhysicalFileProvider.PhysicalPathOf(Path.Combine(site, "loop1")));
        var root = Path.GetPathRoot(site)!;
        Assert.AreEqual(root, WriteablePhysicalFileProvider.PhysicalPathOf(Path.Join(root, "..")));
    }

    [TestMethod]
    public void PhysicalPathOf_StopsWhereTheKernelDoes()
    {
        // A component before the last must be a folder that is there (ENOENT, ENOTDIR), a trailing separator or '.'
        // included; a missing last name is still a path. Links count up to the kernel's limit (ELOOP past it).
        var folder = Disk("stops");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "file.txt");
        File.WriteAllText(file, "x");
        var physicalFile = WriteablePhysicalFileProvider.PhysicalPathOf(file);
        Assert.IsNotNull(physicalFile);
        Assert.IsNull(WriteablePhysicalFileProvider.PhysicalPathOf(Path.Combine(folder, "missing", "..", "file.txt")));
        Assert.IsNull(WriteablePhysicalFileProvider.PhysicalPathOf(Path.Combine(file, "..", "file.txt")));
        Assert.IsNull(WriteablePhysicalFileProvider.PhysicalPathOf(file + Path.DirectorySeparatorChar));
        Assert.IsNull(WriteablePhysicalFileProvider.PhysicalPathOf(Path.Combine(file, ".")));
        Assert.AreEqual(WriteablePhysicalFileProvider.PhysicalPathOf(folder), WriteablePhysicalFileProvider.PhysicalPathOf(folder + Path.DirectorySeparatorChar));
        Assert.AreEqual(Path.Combine(Path.GetDirectoryName(physicalFile)!, "missing.txt"), WriteablePhysicalFileProvider.PhysicalPathOf(Path.Combine(folder, "missing.txt")));

        // Chains of links: one through exactly the limit resolves, one link more does not (32 on macOS, 40 elsewhere).
        Assert.AreEqual(OperatingSystem.IsMacOS() ? 32 : 40, WriteablePhysicalFileProvider.MaxLinks);
        try
        {
            Chain("a", WriteablePhysicalFileProvider.MaxLinks);
        }
        catch (Exception e) when (OperatingSystem.IsWindows() && e is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive($"Creating a symbolic link on Windows needs Developer Mode or elevation: {e.Message}");
        }
        Chain("b", WriteablePhysicalFileProvider.MaxLinks + 1);
        Assert.AreEqual(physicalFile, WriteablePhysicalFileProvider.PhysicalPathOf(Path.Combine(folder, "a0")));
        Assert.IsNull(WriteablePhysicalFileProvider.PhysicalPathOf(Path.Combine(folder, "b0")));

        // prefix0 -> prefix1 -> ... -> prefix(count - 1) -> file.txt: a path through count links.
        void Chain(string prefix, int count)
        {
            for (var i = 0; i < count; i++)
                File.CreateSymbolicLink(Path.Combine(folder, $"{prefix}{i}"), i == count - 1 ? "file.txt" : $"{prefix}{i + 1}");
        }
    }

    // Makes a FIFO under the root with mkfifo, with the given permission bits; inconclusive where there is no mkfifo.
    private async Task MakeFifo(string name, string mode = "0644")
    {
        Directory.CreateDirectory(_root);
        try
        {
            using var mkfifo = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { "-m", mode, Disk(name) }, UseShellExecute = false })!;
            await mkfifo.WaitForExitAsync();
            if (mkfifo.ExitCode != 0)
                Assert.Inconclusive($"mkfifo exited with {mkfifo.ExitCode}.");
        }
        catch (Win32Exception e)
        {
            Assert.Inconclusive($"mkfifo is not available: {e.Message}");
        }
    }

    [TestMethod]
    public async Task Write_OverAnEmptyFileReplacesIt()
    {
        // An empty file is checked to be a regular file before it is replaced.
        await Write("empty.txt", "");
        await Write("empty.txt", "full");
        Assert.AreEqual("full", await Read("empty.txt"));
        await Write("empty2.txt", "");
        await Write("empty2.txt", "+more", UpdateFileOptions.Append);
        Assert.AreEqual("+more", await Read("empty2.txt"));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    public async Task Write_OverAFifoIsRefused()
    {
        // Opening a FIFO to write waits for a reader, forever: the provider refuses it without waiting.
        Directory.CreateDirectory(_root);
        try
        {
            using var mkfifo = Process.Start(new ProcessStartInfo("mkfifo", Disk("pipe")) { UseShellExecute = false })!;
            await mkfifo.WaitForExitAsync();
            if (mkfifo.ExitCode != 0)
                Assert.Inconclusive($"mkfifo exited with {mkfifo.ExitCode}.");
        }
        catch (Win32Exception e)
        {
            Assert.Inconclusive($"mkfifo is not available: {e.Message}");
        }
        foreach (var options in new[] { UpdateFileOptions.Overwrite, UpdateFileOptions.Append })
        {
            var e = await Assert.ThrowsExactlyAsync<IOException>(() => _provider.UpdateFileAsync("pipe", TestStreams.Text("x"), options).WaitAsync(TimeSpan.FromSeconds(30)));
            StringAssert.Contains(e.Message, "not a regular file");
        }
        Assert.IsEmpty(TempFiles());
    }

    // ----- Versions and preconditions -----

    [TestMethod]
    public async Task ETag_ChangesWithEveryWriteEvenWhenTheClockStandsStill()
    {
        var now = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        _provider.Dispose();
        _provider = new WriteablePhysicalFileProvider(_root) { Clock = new FixedClock(now) };
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
    [DataRow(0L, 1)]
    [DataRow(10_000L, 2)] // 1 ms
    [DataRow(100_000L, 3)] // 10 ms, exFAT
    [DataRow(10_000_000L, 4)] // 1 s, HFS+
    [DataRow(20_000_000L, 5)] // 2 s, FAT
    public void Stamp_StepsPastTheReplacedTimeAsTheFileSystemStoresIt(long resolutionTicks, int tries)
    {
        // A file system that keeps times in whole units of its resolution, and a clock inside the replaced file's unit.
        var replaced = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        var now = replaced.AddTicks(Math.Max(1, resolutionTicks / 2));
        var stored = new List<DateTime>();
        WriteablePhysicalFileProvider.Stamp(replaced, now, time =>
        {
            var kept = resolutionTicks == 0 ? time : new DateTime(time.Ticks - time.Ticks % resolutionTicks, DateTimeKind.Utc);
            stored.Add(kept);
            return kept;
        });
        Assert.HasCount(tries, stored);
        Assert.IsGreaterThan(replaced, stored[^1]);
    }

    [TestMethod]
    public void Stamp_GivesUpAfterTwoSeconds()
    {
        var replaced = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        var tries = 0;
        WriteablePhysicalFileProvider.Stamp(replaced, replaced, _ =>
        {
            tries++;
            return replaced;
        });
        Assert.AreEqual(5, tries, "Where no stored time is later, the ETag may repeat; the write still goes ahead");
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
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(false, true)]
    public async Task IfNotExists_FailsWhenTheFileAppearsDuringTheWrite(bool byRenameWithoutReplacing, bool linkRefused)
    {
        TakeNewNames(byRenameWithoutReplacing, linkRefused);
        var contents = new InterceptingStream(Encoding.UTF8.GetBytes("mine"), () => File.WriteAllText(Disk("f.txt"), "theirs"));
        var e = await Assert.ThrowsExactlyAsync<FileAlreadyExistsException>(() => _provider.UpdateFileAsync("f.txt", contents, new UpdateFileOptions { IfNotExists = true }));
        Assert.IsInstanceOfType<IOException>(e.InnerException);
        Assert.AreEqual("theirs", await Read("f.txt"));
        Assert.IsEmpty(TempFiles());
        await Write("g.txt", "new", new UpdateFileOptions { IfNotExists = true });
        Assert.AreEqual("new", await Read("g.txt"));
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(false, true)]
    public async Task IfNotExists_WhenAFolderTakesTheNameDuringTheWrite_IsAFolder(bool byRenameWithoutReplacing, bool linkRefused)
    {
        // Only a file counts as existing: a folder made meanwhile is what a write without conditions would find there.
        if (!byRenameWithoutReplacing && OperatingSystem.IsWindows())
            Assert.Inconclusive("Windows refuses an exclusive create over a folder as access denied; Linux and macOS, which take names this way, answer that the name exists.");
        TakeNewNames(byRenameWithoutReplacing, linkRefused);
        var contents = new InterceptingStream(Encoding.UTF8.GetBytes("mine"), () => Directory.CreateDirectory(Disk("f.txt")));
        var e = await Assert.ThrowsExactlyAsync<IOException>(() => _provider.UpdateFileAsync("f.txt", contents, new UpdateFileOptions { IfNotExists = true }));
        StringAssert.Contains(e.Message, "is a folder");
        Assert.IsTrue(Directory.Exists(Disk("f.txt")), "The folder is left as it was");
        Assert.IsEmpty(TempFiles());
    }

    [TestMethod]
    public async Task Append_WhenTheFileChangesWhileItCopies_AppendsToTheNewVersion()
    {
        // On Physical, the check before the rename catches a change made while the append copies, but is not atomic; the
        // append then reads the file again.
        await Write("f.txt", "start");
        _provider.Dispose();
        _provider = new WriteablePhysicalFileProvider(_root) { TempCreated = Once(() => File.WriteAllText(Disk("f.txt"), "theirs")) };
        await Write("f.txt", "+mine", UpdateFileOptions.Append);
        Assert.AreEqual("theirs+mine", await Read("f.txt"));
        Assert.IsEmpty(TempFiles());
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(false, true)]
    public async Task Append_ToAMissingFile_AppendsToOneThatAppearsMeanwhile(bool byRenameWithoutReplacing, bool linkRefused)
    {
        TakeNewNames(byRenameWithoutReplacing, linkRefused, Once(() => File.WriteAllText(Disk("f.txt"), "theirs")));
        await Write("f.txt", "mine", UpdateFileOptions.Append);
        Assert.AreEqual("theirsmine", await Read("f.txt"));
        Assert.IsEmpty(TempFiles());
    }

    [TestMethod]
    public async Task Append_ThatLosesANameTakenByAnExclusiveCreate_NeverAppendsToItsEmptyFile()
    {
        // Where links are refused, a name is taken with an empty file, which the winner's content is renamed onto
        // afterwards: an append made to that empty file would be replaced. The winner (A) takes the name, then waits; the
        // loser (B) must not read past A's empty file.
        using var aHoldsTheName = new ManualResetEventSlim();
        using var bIsDone = new ManualResetEventSlim();
        using var a = new WriteablePhysicalFileProvider(_root)
        {
            RenamesWithoutReplacing = false,
            Link = (_, _) => 1,
            NameTaken = _ =>
            {
                aHoldsTheName.Set();
                bIsDone.Wait(TimeSpan.FromSeconds(30));
            },
        };
        Task? aWrites = null;
        _provider.Dispose();
        _provider = new WriteablePhysicalFileProvider(_root)
        {
            RenamesWithoutReplacing = false,
            Link = (_, _) => 1,
            TempCreated = Once(() =>
            {
                aWrites = Task.Run(() => a.UpdateFileAsync("f.txt", TestStreams.Text("first;"), UpdateFileOptions.Append));
                Assert.IsTrue(aHoldsTheName.Wait(TimeSpan.FromSeconds(30)), "The other writer took the name");
            }),
        };
        try
        {
            await Assert.ThrowsExactlyAsync<FileAlreadyExistsException>(() => Write("f.txt", "second;", UpdateFileOptions.Append));
        }
        finally
        {
            bIsDone.Set();
        }
        await aWrites!;
        Assert.AreEqual("first;", await Read("f.txt"));
        Assert.IsEmpty(TempFiles());
    }

    [TestMethod]
    public async Task Append_ToAFileThatChangesEveryTime_ThrowsAfterFiveAttempts()
    {
        await Write("f.txt", "start");
        var attempts = 0;
        _provider.Dispose();
        _provider = new WriteablePhysicalFileProvider(_root) { TempCreated = _ => File.WriteAllText(Disk("f.txt"), new string('x', ++attempts)) };
        var e = await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => Write("f.txt", "+mine", UpdateFileOptions.Append));
        Assert.AreEqual(5, attempts);
        StringAssert.Contains(e.Message, "5 attempts");
        Assert.IsInstanceOfType<PreconditionFailedException>(e.InnerException);
        Assert.AreEqual("xxxxx", await Read("f.txt"), "Every other write is kept, and nothing of the append");
        Assert.IsEmpty(TempFiles());
    }

    [TestMethod]
    public async Task Append_WithTheCallersVersion_FailsAtTheFirstChange()
    {
        await Write("f.txt", "start");
        var etag = (await _provider.GetFileInfoAsync("f.txt")).ETag;
        var attempts = 0;
        _provider.Dispose();
        _provider = new WriteablePhysicalFileProvider(_root)
        {
            TempCreated = _ =>
            {
                attempts++;
                File.WriteAllText(Disk("f.txt"), "theirs");
            },
        };
        var e = await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => Write("f.txt", "+mine", UpdateFileOptions.Append with { IfMatch = etag }));
        Assert.AreEqual(1, attempts);
        Assert.DoesNotContain("attempts", e.Message);
        Assert.AreEqual("theirs", await Read("f.txt"));
        Assert.IsEmpty(TempFiles());
    }

    [TestMethod]
    public async Task Append_WithTheCallersVersion_RefusesAChangeBetweenTheLookAndTheOpen()
    {
        await Write("f.txt", "start");
        var etag = (await _provider.GetFileInfoAsync("f.txt")).ETag;
        _provider.Dispose();
        _provider = new WriteablePhysicalFileProvider(_root) { Probed = target => File.WriteAllText(target, "theirs") };
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => Write("f.txt", "+mine", UpdateFileOptions.Append with { IfMatch = etag }));
        Assert.AreEqual("theirs", await Read("f.txt"));
        Assert.IsEmpty(TempFiles());
    }

    [TestMethod]
    public async Task Append_WhoseStreamFails_EndsWithItsFailure()
    {
        // A pinned read of a file elsewhere throws the base type when that file changes: it is not this file's change.
        await Write("f.txt", "start");
        var contents = new InterceptingStream("+mine"u8.ToArray(), () => throw new PreconditionFailedException("The source changed."));
        var e = await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => _provider.UpdateFileAsync("f.txt", contents, UpdateFileOptions.Append));
        Assert.AreEqual("The source changed.", e.Message);
        Assert.AreEqual("start", await Read("f.txt"));
        Assert.IsEmpty(TempFiles());
    }

    [TestMethod]
    public async Task Append_ToAFileDeletedBeforeItIsOpened_CreatesIt()
    {
        // The caller's stream is copied between the look at the path and the file's open: the file goes there.
        await Write("f.txt", "old");
        var contents = new InterceptingStream("mine"u8.ToArray(), () => File.Delete(Disk("f.txt")));
        await _provider.UpdateFileAsync("f.txt", contents, UpdateFileOptions.Append);
        Assert.AreEqual("mine", await Read("f.txt"));
        Assert.IsEmpty(TempFiles());
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(false, true)]
    public async Task Append_ToAMissingFile_WhenAFolderTakesTheName_IsAFolder(bool byRenameWithoutReplacing, bool linkRefused)
    {
        // A folder is what a write without conditions finds there: the append ends, unretried.
        if (!byRenameWithoutReplacing && OperatingSystem.IsWindows())
            Assert.Inconclusive("Windows refuses an exclusive create over a folder as access denied; Linux and macOS, which take names this way, answer that the name exists.");
        var attempts = 0;
        TakeNewNames(byRenameWithoutReplacing, linkRefused, _ =>
        {
            attempts++;
            Directory.CreateDirectory(Disk("f.txt"));
        });
        var e = await Assert.ThrowsExactlyAsync<IOException>(() => Write("f.txt", "mine", UpdateFileOptions.Append));
        StringAssert.Contains(e.Message, "is a folder");
        Assert.AreEqual(1, attempts);
        Assert.IsTrue(Directory.Exists(Disk("f.txt")), "The folder is left as it was");
        Assert.IsEmpty(TempFiles());
    }

    [TestMethod]
    public async Task Append_RefusedBeforeItReadsTheStream_LeavesNoFolder()
    {
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => Write("new/f.txt", "x", UpdateFileOptions.Append with { IfMatch = "1-1" }));
        Assert.IsFalse(Directory.Exists(Disk("new")), "The caller's stream is copied only once the append's refusals have passed");
    }

    [TestMethod]
    public async Task Append_ToAFileDeletedAsItIsLookedAt_CreatesIt()
    {
        await Write("f.txt", "old");
        _provider.Dispose();
        _provider = new WriteablePhysicalFileProvider(_root) { Probed = Once(() => File.Delete(Disk("f.txt"))) };
        await Write("f.txt", "mine", UpdateFileOptions.Append);
        Assert.AreEqual("mine", await Read("f.txt"), "Created with the caller's bytes");
        Assert.IsEmpty(TempFiles());
    }

    [TestMethod]
    public async Task Append_AfterALostCreate_ChecksTheFileItFindsAndTakesItsMode()
    {
        // The second attempt looks at the path again: the file another writer made is the one it replaces.
        TakeNewNames(byRenameWithoutReplacing: OperatingSystem.IsWindows(), tempCreated: Once(() =>
        {
            File.WriteAllText(Disk("f.txt"), "theirs");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Disk("f.txt"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }));
        await Write("f.txt", "+mine", UpdateFileOptions.Append);
        Assert.AreEqual("theirs+mine", await Read("f.txt"));
        if (!OperatingSystem.IsWindows())
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Disk("f.txt")), "The new file takes the bits of the one it replaces");
    }

    [TestMethod]
    public async Task Append_SpoolsTheCallersBytesBesideTheFile_HiddenAndGoneAfterwards()
    {
        await Write("dir/f.txt", "old");
        var spools = new List<string>();
        var names = new List<string>();
        _provider.Dispose();
        _provider = new WriteablePhysicalFileProvider(_root)
        {
            TempCreated = temp =>
            {
                spools.AddRange(Directory.GetFiles(Path.GetDirectoryName(temp)!, ".wfp-*.tmp").Where(file => file != temp).Select(file => Path.GetFileName(file)));
                names.AddRange(_provider.EnumerateAsync("dir").ToBlockingEnumerable().Select(entry => entry.Name));
            },
        };
        await Write("dir/f.txt", "+more", UpdateFileOptions.Append);
        Assert.HasCount(1, spools, "One spool, in the file's folder");
        Assert.IsTrue(WriteablePhysicalFileProvider.IsTempFile(spools[0]), "Named as a write's temp file, which the sweep takes when a crash leaves it");
        CollectionAssert.AreEqual(new[] { "f.txt" }, names, "Listings hide it");
        Assert.IsEmpty(TempFiles(), "Gone once the append ends");
        Assert.AreEqual("old+more", await Read("dir/f.txt"));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    [UnsupportedOSPlatform("windows")]
    public async Task Append_CreatesItsSpoolReadableOnlyByItsUser()
    {
        await Write("f.txt", "old");
        File.SetUnixFileMode(Disk("f.txt"), File.GetUnixFileMode(Disk("f.txt")) | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var modes = new List<UnixFileMode>();
        _provider.Dispose();
        _provider = new WriteablePhysicalFileProvider(_root)
        {
            TempCreated = temp => modes.AddRange(Directory.GetFiles(Path.GetDirectoryName(temp)!, ".wfp-*.tmp").Where(file => file != temp).Select(File.GetUnixFileMode)),
        };
        await Write("f.txt", "+more", UpdateFileOptions.Append);
        Assert.HasCount(1, modes);
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, modes[0], "Only the process's user may read the caller's bytes, whatever the file's mode");
    }

    [TestMethod]
    public async Task Append_ChecksTheConditionsOnTheVersionItOpens()
    {
        await Write("f.txt", "a");
        var etag = (await _provider.GetFileInfoAsync("f.txt")).ETag;
        await Write("f.txt", "b", UpdateFileOptions.Append with { IfMatch = etag });
        Assert.AreEqual("ab", await Read("f.txt"));
        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => Write("f.txt", "c", UpdateFileOptions.Append with { IfMatch = etag }));
        await Assert.ThrowsExactlyAsync<FileAlreadyExistsException>(() => Write("f.txt", "c", UpdateFileOptions.Append with { IfNotExists = true }));
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
        // Exactly the provider's own temp name: ".wfp-", a GUID's 32 lowercase hex digits, ".tmp". Every other name is
        // the caller's, and listed, however like it it looks.
        await Write("f.txt", "x");
        var guid = Guid.NewGuid().ToString("N");
        var temp = $".wfp-{guid}.tmp";
        string[] listed = [".hidden", "x.tmp", ".wfp-notes.txt", ".wfp-notes.tmp", ".wfp-0123abcd.tmp", $".wfp-{Guid.NewGuid().ToString("N").ToUpperInvariant()}.tmp",
            $".wfp-{guid}x.tmp", $".wfp-{guid[..^1]}g.tmp", $"a.wfp-{guid}.tmp"];
        foreach (var name in listed.Append(temp))
            File.WriteAllText(Disk(name), name);
        Directory.CreateDirectory(Disk($".wfp-{Guid.NewGuid():N}.tmp"));
        var folder = Path.GetFileName(Directory.GetDirectories(_root).Single());
        CollectionAssert.AreEquivalent(listed.Append("f.txt").Append(folder).ToArray(), Names(), "Only a temp file is hidden; a folder of that name is listed");
        var recursive = new List<string>();
        await foreach (var entry in _provider.EnumerateAsync("", recursive: true))
            recursive.Add(entry.Name);
        CollectionAssert.DoesNotContain(recursive, temp);
        Assert.HasCount(listed.Length + 2, recursive);
    }

    [TestMethod]
    [DataRow(".wfp-0123456789abcdef0123456789abcdef.tmp", true)]
    [DataRow(".wfp-0123456789ABCDEF0123456789abcdef.tmp", false)]
    [DataRow(".wfp-0123456789abcdef0123456789abcde.tmp", false)]
    [DataRow(".wfp-0123456789abcdef0123456789abcdef0.tmp", false)]
    [DataRow(".wfp-0123456789abcdef0123456789abcdeg.tmp", false)]
    [DataRow(".wfp-0123456789abcdef0123456789abcdef.txt", false)]
    [DataRow(".wfp-rename-0123456789abcdef0123456789abcdef.tmp", false)]
    [DataRow(".wfp-notes.tmp", false)]
    [DataRow("wfp-0123456789abcdef0123456789abcdef.tmp", false)]
    public void TempFile_IsExactlyTheProvidersOwnName(string name, bool temp) =>
        Assert.AreEqual(temp, WriteablePhysicalFileProvider.IsTempFile(name), name);

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

    [TestMethod]
    public async Task List_RecursiveListsALinkedFolderWithoutWalkingIt()
    {
        // A link back up the tree would walk forever; a link outside the root would list what is not the provider's.
        await Write("dir/f.txt", "x");
        var outside = Path.Combine(Path.GetDirectoryName(_root)!, Path.GetFileName(_root) + "-outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "outside");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(Disk("dir", "up"), Disk("dir"));
                Directory.CreateSymbolicLink(Disk("out"), outside);
            }
            catch (Exception e) when (OperatingSystem.IsWindows() && e is IOException or UnauthorizedAccessException)
            {
                Assert.Inconclusive($"Creating a symbolic link on Windows needs Developer Mode or elevation: {e.Message}");
            }
            var paths = new List<string>();
            await foreach (var entry in _provider.EnumerateAsync("", recursive: true))
                paths.Add(entry.Path.ToString());
            CollectionAssert.AreEquivalent(new[] { "dir", "dir/f.txt", "dir/up", "out" }, paths);
            Assert.IsTrue((await _provider.GetFileInfoAsync("out")).IsDirectory, "A linked folder is listed as a folder");
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    // ----- Move and copy -----

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Move_RefusesADestinationThatAppearedMeanwhile(bool byRenameWithoutReplacing)
    {
        // The core members, called past the base class's own destination check: the rename itself refuses.
        TakeNewNames(byRenameWithoutReplacing);
        await Write("a.txt", "a");
        await Write("b.txt", "b");
        var move = await Assert.ThrowsExactlyAsync<FileExistsException>(() => _provider.MoveCore("a.txt", "b.txt"));
        Assert.IsInstanceOfType<IOException>(move.InnerException);
        var copy = await Assert.ThrowsExactlyAsync<FileExistsException>(() => _provider.CopyCore("a.txt", "b.txt"));
        Assert.IsInstanceOfType<IOException>(copy.InnerException);
        Assert.AreEqual("a", await Read("a.txt"));
        Assert.AreEqual("b", await Read("b.txt"));

        // A folder that took the destination is a destination that exists, for a copy as for a move. (Windows refuses an
        // exclusive create over a folder as access denied; only Linux and macOS take names that way in production.)
        if (byRenameWithoutReplacing || !OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(Disk("d"));
            await Assert.ThrowsExactlyAsync<FileExistsException>(() => _provider.CopyCore("a.txt", "d"));
            await Assert.ThrowsExactlyAsync<FileExistsException>(() => _provider.MoveCore("a.txt", "d"));
            Assert.IsTrue(Directory.Exists(Disk("d")));
            Assert.AreEqual("a", await Read("a.txt"));
        }
        Assert.IsEmpty(TempFiles());
    }

    // ----- Taking a new name on Linux and macOS -----

    [TestMethod]
    public async Task NewName_ByLink_IsNeverEmpty_AndAnAppendThereIsKept()
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("Windows renames without replacing; it never takes a name with a hard link.");
        // A second writer that finds the name taken appends to what is there: the first writer's content, never an
        // empty file that the first writer's rename would then replace.
        string? seen = null;
        WriteablePhysicalFileProvider? provider = null;
        provider = new WriteablePhysicalFileProvider(_root)
        {
            NameTaken = target =>
            {
                seen = File.ReadAllText(target);
                provider!.UpdateFileAsync("f.txt", TestStreams.Text("+theirs"), UpdateFileOptions.Append).GetAwaiter().GetResult();
            },
        };
        using (provider)
            await provider.UpdateFileAsync("f.txt", TestStreams.Text("mine"), new UpdateFileOptions { IfNotExists = true });
        Assert.AreEqual("mine", seen);
        Assert.AreEqual("mine+theirs", await Read("f.txt"));
        Assert.IsEmpty(TempFiles());
    }

    [TestMethod]
    public async Task NewName_WhereLinksAreRefused_IsTakenAsBefore()
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("Windows renames without replacing; it never takes a name with a hard link.");
        // A file system without hard links (EPERM): the name is taken with an empty file, then the rename.
        var seen = new List<string>();
        var provider = new WriteablePhysicalFileProvider(_root) { Link = (_, _) => 1, NameTaken = target => seen.Add(File.ReadAllText(target)) };
        using (provider)
        {
            await provider.UpdateFileAsync("f.txt", TestStreams.Text("mine"), new UpdateFileOptions { IfNotExists = true });
            await Assert.ThrowsExactlyAsync<FileAlreadyExistsException>(() => provider.UpdateFileAsync("f.txt", TestStreams.Text("x"), new UpdateFileOptions { IfNotExists = true }));
        }
        CollectionAssert.AreEqual(new[] { "" }, seen);
        Assert.AreEqual("mine", await Read("f.txt"));
        Assert.IsEmpty(TempFiles());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NewName_ByLink_IsDoneWhateverBecomesOfTheTempName(bool deleteRefused)
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("Windows renames without replacing; it never takes a name with a hard link.");
        // Once the link holds the content, the write is done: a temp name that a sweep removed first, or one whose
        // delete is refused (here a folder took the name, which a file delete cannot remove), changes nothing.
        var provider = new WriteablePhysicalFileProvider(_root)
        {
            Link = (existing, created) =>
            {
                var error = NativeMethods.Link(existing, created) == 0 ? 0 : System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                File.Delete(existing);
                if (deleteRefused)
                    File.WriteAllText(Path.Combine(Directory.CreateDirectory(existing).FullName, "x"), "");
                return error;
            },
        };
        using (provider)
            await provider.UpdateFileAsync("f.txt", TestStreams.Text("mine"), new UpdateFileOptions { IfNotExists = true });
        Assert.AreEqual("mine", await Read("f.txt"));
        Assert.IsEmpty(TempFiles());
    }

    [TestMethod]
    public async Task NewName_OfAMoveStillTakesItWithAnEmptyFile()
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("Windows renames without replacing; it never takes a name with an exclusive create.");
        // A move renames the caller's own file, which a hard link would leave under two names after a crash.
        var seen = new List<string>();
        await Write("a.txt", "a");
        var provider = new WriteablePhysicalFileProvider(_root) { NameTaken = target => seen.Add(File.ReadAllText(target)) };
        using (provider)
            await provider.MoveAsync("a.txt", "b.txt");
        CollectionAssert.AreEqual(new[] { "" }, seen);
        Assert.AreEqual("a", await Read("b.txt"));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Move_OfASourceThatVanishedLeavesNoDestination(bool byRenameWithoutReplacing)
    {
        TakeNewNames(byRenameWithoutReplacing);
        var e = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => _provider.MoveCore("gone.txt", "new/b.txt"));
        Assert.AreEqual("gone.txt", e.FileName);
        Assert.IsInstanceOfType<FileNotFoundException>(e.InnerException);
        Assert.IsFalse(File.Exists(Disk("new", "b.txt")), "A name taken for the move is given back");
    }

    [TestMethod]
    public async Task Move_ToANameDifferingOnlyInCaseRenames()
    {
        // Forced on every system, so the case-only path runs on Linux too, where it is a plain rename.
        using var provider = new WriteablePhysicalFileProvider(_root) { IgnoresCase = true };
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
    public async Task Paths_NamesWindowsReadsAsOthersAreRefused()
    {
        // The rule of Windows, forced here on every system: each path would name 'a.txt' or 'dir' there, or no file at
        // all, so a write would replace another file and a delete remove another folder.
        using var provider = new WriteablePhysicalFileProvider(_root) { RefusesWindowsNames = true };
        await provider.UpdateFileAsync("a.txt", TestStreams.Text("original"));
        await provider.UpdateFileAsync("dir/inner.txt", TestStreams.Text("inner"));

        var write = await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.UpdateFileAsync("a.txt.", TestStreams.Text("REPLACED")));
        Assert.AreEqual("path", write.ParamName);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.UpdateFileAsync("a.txt ", TestStreams.Text("REPLACED"), UpdateFileOptions.Append));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.DeleteAsync("dir "));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.DeleteAsync("dir."));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.MoveAsync("a.txt", "dir /a.txt"));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.GetFileInfoAsync("x?"));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.ReadFileAsync("a.txt%2E"));
        foreach (var name in new[] { "CON.txt", "con", "NUL.tar.gz", "Com1.log", "lpt9", "COM¹", "aux .txt", "prn", "a\"b", "a<b", "a>b", "a|b", "a*b" })
        {
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.GetFileInfoAsync(name), name);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.UpdateFileAsync("dir/" + name, TestStreams.Text("x")), name);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.ReadFileAsync(name), name);
        }

        Assert.AreEqual("original", File.ReadAllText(Disk("a.txt")));
        Assert.AreEqual("inner", File.ReadAllText(Disk("dir", "inner.txt")));
        CollectionAssert.AreEqual(new[] { "a.txt", "dir" }, Directory.GetFileSystemEntries(_root).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());

        // Names that only look like those are names like any other.
        foreach (var name in new[] { "CONSOLE.txt", "COM10", "report.con", "nul_", ".nul", "a.b c", "x.y.z" })
        {
            await provider.UpdateFileAsync("ok/" + name, TestStreams.Text(name));
            Assert.AreEqual(name, File.ReadAllText(Disk("ok", name)), name);
        }
    }

    [TestMethod]
    public void RefusesWindowsNames_IsOnByDefaultOnlyOnWindows() =>
        Assert.AreEqual(OperatingSystem.IsWindows(), _provider.RefusesWindowsNames);

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task Paths_OnWindows_NamesWindowsReadsAsOthersAreRefusedByDefault()
    {
        await Write("a.txt", "original");
        await Write("dir/inner.txt", "inner");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => Write("a.txt.", "REPLACED"));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => _provider.DeleteAsync("dir "));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => _provider.GetFileInfoAsync("CON.txt"));
        Assert.AreEqual("original", await Read("a.txt"));
        Assert.AreEqual("inner", await Read("dir/inner.txt"));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task Paths_ShortNamesOfOtherNamesAreRefused()
    {
        // Where the volume keeps 8.3 short names, Windows answers LONGFO~1 for "long folder name" and REPORT~1.TXT for
        // "report-2024.txt": such a path would write or delete another name.
        await Write("long folder name/report-2024.txt", "original");
        if (!Directory.Exists(Disk("LONGFO~1")))
            Assert.Inconclusive("This volume keeps no 8.3 short names (fsutil 8dot3name).");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => Write("LONGFO~1/REPORT~1.TXT", "REPLACED"));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => Write("long folder name/REPORT~1.TXT", "REPLACED"));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => _provider.GetFileInfoAsync("long folder name/report~1.txt"));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => _provider.DeleteAsync("LONGFO~1"));
        Assert.AreEqual("original", await Read("long folder name/report-2024.txt"));

        // A long name that holds a tilde is a name like any other.
        await Write("a~1.txt", "tilde");
        Assert.AreEqual("tilde", await Read("A~1.TXT"));
        Assert.IsFalse((await _provider.GetFileInfoAsync("x~1")).Exists);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [DataRow(@"\\?\")]
    [DataRow(@"\\.\")]
    public async Task Root_InTheExtendedFormIsTakenPlainSoShortNamesAreStillRefused(string prefix)
    {
        // .NET does not normalize an extended path, so under such a root 8.3 short names would go unexpanded while NTFS
        // still resolves them: the root is taken in its plain form.
        await Write("long folder name/report-2024.txt", "original");
        using var provider = new WriteablePhysicalFileProvider(prefix + _root);
        Assert.AreEqual(_root, provider.Options.RootDirectoryPath);
        Assert.IsTrue((await provider.GetFileInfoAsync("long folder name/report-2024.txt")).Exists);
        if (!Directory.Exists(Disk("LONGFO~1")))
            Assert.Inconclusive("This volume keeps no 8.3 short names (fsutil 8dot3name).");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.UpdateFileAsync("LONGFO~1/REPORT~1.TXT", TestStreams.Text("REPLACED")));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.DeleteAsync("LONGFO~1"));
        Assert.AreEqual("original", await Read("long folder name/report-2024.txt"));
    }

    [TestMethod]
    [DataRow(@"\\?\C:\files", @"C:\files")]
    [DataRow(@"\\?\c:\", @"c:\")]
    [DataRow(@"\\?\D:", @"D:\")]
    [DataRow(@"\\.\C:\files\x", @"C:\files\x")]
    [DataRow("//?/C:/files", @"C:\files")]
    [DataRow(@"\\?\UNC\server\share\files", @"\\server\share\files")]
    [DataRow(@"\\?\unc\server\share", @"\\server\share")]
    [DataRow(@"\\.\UNC\server\share", @"\\server\share")]
    [DataRow(@"C:\files", @"C:\files")]
    [DataRow(@"\\server\share\files", @"\\server\share\files")]
    [DataRow("relative/folder", "relative/folder")]
    public void PlainRootOf_TakesExtendedAndDevicePathsPlain(string root, string plain) =>
        Assert.AreEqual(plain, WriteablePhysicalFileProvider.PlainRootOf(root));

    [TestMethod]
    [DataRow(@"\\?\Volume{12345678-1234-1234-1234-123456789abc}\files")]
    [DataRow(@"\\.\PhysicalDrive0")]
    [DataRow(@"\\.\pipe\name")]
    [DataRow(@"\\?\GLOBALROOT\Device\HarddiskVolume1\files")]
    // Names the plain form would change: it drops a trailing dot or space and applies '.' and '..', which the extended
    // form names as they are.
    [DataRow(@"\\?\C:\data.")]
    [DataRow(@"\\?\C:\data ")]
    [DataRow(@"\\?\C:\data.\files")]
    [DataRow(@"\\?\C:\data\..\..\Windows")]
    [DataRow(@"\\?\C:\.\files")]
    [DataRow(@"\\?\UNC\server\share.\files")]
    [DataRow(@"\\?\UNC\server\share\files ")]
    [DataRow(@"\\.\C:\data.")]
    public void PlainRootOf_HasNoneForAPathWithNoPlainForm(string root) =>
        Assert.IsNull(WriteablePhysicalFileProvider.PlainRootOf(root));

    [TestMethod]
    public void PlainRootOf_TakesALongPathPlain()
    {
        var path = @"C:\" + new string('a', 200) + @"\" + new string('b', 200);
        Assert.AreEqual(path, WriteablePhysicalFileProvider.PlainRootOf(@"\\?\" + path));
        Assert.AreEqual(@"C:\a.b\c", WriteablePhysicalFileProvider.PlainRootOf(@"\\?\C:\a.b\c"), "A dot inside a name changes nothing");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void Root_WithNoPlainFormIsRefused()
    {
        // "rootdot." is a folder of its own under \\?\, which the plain form would take for "rootdot".
        Directory.CreateDirectory(Disk("rootdot"));
        Directory.CreateDirectory(@"\\?\" + Disk("rootdot."));
        try
        {
            foreach (var root in new[] { @"\\?\Volume{12345678-1234-1234-1234-123456789abc}\files", @"\\?\" + Disk("rootdot."), @"\\?\" + Disk("x", "..", "..") })
            {
                var e = Assert.ThrowsExactly<ArgumentException>(() => new WriteablePhysicalFileProvider(root), root);
                Assert.AreEqual("options", e.ParamName);
            }
        }
        finally
        {
            Directory.Delete(@"\\?\" + Disk("rootdot."));
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task Root_InTheExtendedFormThatIsLongStillRefusesShortNames()
    {
        // Longer than MAX_PATH once a file is under it: the plain form works, and the short-name check holds.
        var deep = Path.Combine(_root, new string('a', 150), new string('b', 150));
        using var provider = new WriteablePhysicalFileProvider(@"\\?\" + deep);
        Assert.AreEqual(deep, provider.Options.RootDirectoryPath);
        await provider.UpdateFileAsync("long folder name/report-2024.txt", TestStreams.Text("original"));
        Assert.IsTrue((await provider.GetFileInfoAsync("long folder name/report-2024.txt")).Exists);
        if (!Directory.Exists(Path.Combine(deep, "LONGFO~1")))
            Assert.Inconclusive("This volume keeps no 8.3 short names (fsutil 8dot3name).");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.UpdateFileAsync("LONGFO~1/REPORT~1.TXT", TestStreams.Text("REPLACED")));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.DeleteAsync("LONGFO~1"));
        Assert.IsTrue((await provider.GetFileInfoAsync("long folder name/report-2024.txt")).Exists);
    }

    [TestMethod]
    public async Task List_LeavesOutNamesWindowsReadsAsOthers()
    {
        // Such names exist where another program made them: through a \\?\ path on Windows, or any path on Linux and
        // macOS (where the rule is forced). Listings leave them out, and never walk into such a folder.
        using var provider = new WriteablePhysicalFileProvider(_root) { RefusesWindowsNames = true };
        await provider.UpdateFileAsync("dir/ok.txt", TestStreams.Text("ok"));
        string[] names = ["a.txt.", "b ", "CON.txt", "nul"];
        if (!OperatingSystem.IsWindows())
            names = [.. names, "x?y", "a<b"];
        string Raw(params string[] segments) => (OperatingSystem.IsWindows() ? @"\\?\" : "") + Path.Combine([_root, "dir", .. segments]);
        foreach (var name in names)
        {
            File.WriteAllText(Raw(name), name);
            Directory.CreateDirectory(Raw(name + "-folder."));
            File.WriteAllText(Raw(name + "-folder.", "inner.txt"), "inner");
        }
        try
        {
            CollectionAssert.AreEqual(new[] { "ok.txt" }, (await provider.GetDirectoryContentsAsync("dir")).Select(e => e.Name).ToArray());
            var all = new List<string>();
            await foreach (var entry in provider.EnumerateAsync("", recursive: true))
                all.Add(entry.Path.ToString());
            CollectionAssert.AreEqual(new[] { "dir", "dir/ok.txt" }, all);
        }
        finally
        {
            foreach (var name in names)
            {
                File.Delete(Raw(name));
                File.Delete(Raw(name + "-folder.", "inner.txt"));
                Directory.Delete(Raw(name + "-folder."));
            }
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    public async Task Paths_NamesWindowsReadsAsOthersLandAsGivenElsewhere()
    {
        // Linux and macOS hold every one of these names as itself, so the provider takes them there.
        await Write("a.txt", "original");
        foreach (var name in new[] { "a.txt.", "a.txt ", "CON.txt", "nul", "x?", "a<b>" })
        {
            await Write(name, name);
            Assert.AreEqual(name, File.ReadAllText(Disk(name)), name);
        }
        Assert.AreEqual("original", File.ReadAllText(Disk("a.txt")));
        CollectionAssert.IsSubsetOf(new[] { "a.txt.", "CON.txt", "x?" }, Names());
    }

    [TestMethod]
    [DataRow("a.txt", true)]
    [DataRow(".hidden", true)]
    [DataRow("a..b", true)]
    [DataRow("CONSOLE", true)]
    [DataRow("COM10", true)]
    [DataRow("COM0", true)]
    [DataRow("file.con", true)]
    [DataRow("a.", false)]
    [DataRow("a ", false)]
    [DataRow("a\"b", false)]
    [DataRow("a<b", false)]
    [DataRow("a>b", false)]
    [DataRow("a|b", false)]
    [DataRow("a?b", false)]
    [DataRow("a*b", false)]
    [DataRow("CON", false)]
    [DataRow("con.txt", false)]
    [DataRow("Nul.tar.gz", false)]
    [DataRow("AUX  .log", false)]
    [DataRow("prn", false)]
    [DataRow("COM1", false)]
    [DataRow("com9.txt", false)]
    [DataRow("LPT1", false)]
    [DataRow("lpt9", false)]
    [DataRow("COM²", false)]
    [DataRow("LPT³.txt", false)]
    public void WindowsName_IsOneWindowsReadsAsItself(string name, bool windowsName) =>
        Assert.AreEqual(windowsName, WriteablePhysicalFileProvider.IsWindowsName(name), name);

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
        var file = (IWriteableFileInfo)(await _provider.GetDirectoryContentsAsync("list/sub")).Single();
        await _provider.MoveAsync(file.Path, "moved/g.txt");
        Assert.AreEqual("x", await Read("moved/g.txt"));
        var sub = (IWriteableFileInfo)(await _provider.GetDirectoryContentsAsync("list")).Single();
        Assert.IsTrue(await _provider.DeleteAsync(sub.Path));
        Assert.IsFalse(Directory.Exists(Disk("list", "sub")));
        Assert.IsTrue(Directory.Exists(Disk("list")));
    }

    [TestMethod]
    public async Task Paths_ARefusedSegmentIsNamedWithoutItsParents()
    {
        var e = await Assert.ThrowsExactlyAsync<ArgumentException>(() => _provider.GetFileInfoAsync(new[] { "secret-parent", ".." }));
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
    [DataRow("/f.txt")]
    [DataRow(@"\f.txt")]
    public async Task Watch_TakesAFilterWithALeadingSeparator(string filter)
    {
        var token = _provider.Watch(filter);
        var fired = new TaskCompletionSource();
        using var registration = token.RegisterChangeCallback(_ => fired.TrySetResult(), null);
        await Write("f.txt", "x");
        await fired.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [TestMethod]
    public void Watch_OfAFilterThatCannotMatchNeverFires()
    {
        // As PhysicalFileProvider: no filter, a character no file name holds, a path above the root, or a drive. On Unix
        // an absolute path is the same path under the root: its leading separator is ignored.
        foreach (var filter in new[] { null!, "a\0b", "../x", "/../x" })
            Assert.AreSame(NullChangeToken.Singleton, _provider.Watch(filter), filter);
        if (OperatingSystem.IsWindows())
            Assert.AreSame(NullChangeToken.Singleton, _provider.Watch(Path.GetFullPath("x")));
    }

    [TestMethod]
    public void Watch_AfterDisposeIsRefused()
    {
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
