using System.Diagnostics;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Testing;

namespace WriteableFileProviders.Physical.Tests;

/// <summary>
/// <see cref="PhysicalFileProviderOptions.RefuseLinks"/>, the sweep of leftover temp files
/// (<see cref="WriteablePhysicalFileProvider.DeleteTempFilesAsync"/>), and a write whose temp file a sweep takes.
/// </summary>
[TestClass]
public sealed class PhysicalLinkAndSweepTests
{
    private string _base = "";
    private string _root = "";
    private string _outside = "";

    [TestInitialize]
    public void Start()
    {
        _base = Path.Combine(Path.GetTempPath(), "wfp-links-" + Guid.NewGuid().ToString("N")[..12]);
        _root = Directory.CreateDirectory(Path.Combine(_base, "root")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_base, "outside")).FullName;
    }

    [TestCleanup]
    public void End()
    {
        if (Directory.Exists(_base))
            Directory.Delete(_base, recursive: true);
    }

    private WriteablePhysicalFileProvider Provider(bool refuseLinks = true) =>
        new(new PhysicalFileProviderOptions { RootDirectoryPath = _root, RefuseLinks = refuseLinks });

    private string Disk(params string[] segments) => Path.Combine([_root, .. segments]);

    // A file and a folder inside the root, a folder outside it, and a link to each: "file-link.txt", "folder-link" and
    // "out-link".
    private void StageLinks()
    {
        File.WriteAllText(Disk("real.txt"), "real");
        Directory.CreateDirectory(Disk("real-folder"));
        File.WriteAllText(Disk("real-folder", "x.txt"), "inside");
        File.WriteAllText(Path.Combine(_outside, "x.txt"), "outside");
        try
        {
            File.CreateSymbolicLink(Disk("file-link.txt"), Disk("real.txt"));
            Directory.CreateSymbolicLink(Disk("folder-link"), Disk("real-folder"));
            Directory.CreateSymbolicLink(Disk("out-link"), _outside);
        }
        catch (Exception e) when (OperatingSystem.IsWindows() && e is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive($"Creating a symbolic link on Windows needs Developer Mode or elevation: {e.Message}");
        }
    }

    [TestMethod]
    [DataRow("file-link.txt")]
    [DataRow("folder-link")]
    [DataRow("folder-link/x.txt")]
    [DataRow("out-link")]
    [DataRow("out-link/x.txt")]
    [DataRow("out-link/new/y.txt")]
    public async Task RefuseLinks_RefusesAPathAtOrThroughALink_ForEveryMember(string path)
    {
        StageLinks();
        using var provider = Provider();
        await AssertEveryMemberRefusesAsync(provider, path);
        Assert.AreEqual("real", File.ReadAllText(Disk("real.txt")));
        Assert.AreEqual("inside", File.ReadAllText(Disk("real-folder", "x.txt")));
        CollectionAssert.AreEqual(new[] { "x.txt" }, Directory.GetFileSystemEntries(_outside).Select(Path.GetFileName).ToArray(), "Nothing outside the root changed");
        Assert.AreEqual("outside", File.ReadAllText(Path.Combine(_outside, "x.txt")));
    }

    [TestMethod]
    public async Task RefuseLinks_RefusesAJunction()
    {
        // A junction needs no privilege on Windows, where it is the usual way to link a folder.
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Junctions are a Windows feature.");
        File.WriteAllText(Disk("real.txt"), "real");
        File.WriteAllText(Path.Combine(_outside, "x.txt"), "outside");
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", Disk("junction"), _outside]) { UseShellExecute = false, RedirectStandardOutput = true })!)
        {
            await mklink.WaitForExitAsync();
            Assert.AreEqual(0, mklink.ExitCode, "mklink /J");
        }
        try
        {
            using var provider = Provider();
            await AssertEveryMemberRefusesAsync(provider, "junction/x.txt");
            await AssertEveryMemberRefusesAsync(provider, "junction");
            Assert.AreEqual("outside", File.ReadAllText(Path.Combine(_outside, "x.txt")));
        }
        finally
        {
            // A recursive delete of the test's folder is refused at a junction: remove the junction itself first.
            Directory.Delete(Disk("junction"));
        }
    }

    [TestMethod]
    public async Task RefuseLinks_ListingsLeaveLinksOut()
    {
        StageLinks();
        using (var refusing = Provider())
        {
            CollectionAssert.AreEquivalent(new[] { "real.txt", "real-folder" }, (await refusing.GetDirectoryContentsAsync("")).Select(e => e.Name).ToArray());
            CollectionAssert.AreEquivalent(new[] { "real.txt", "real-folder", "real-folder/x.txt" },
                await refusing.EnumerateAsync("", recursive: true).Select(e => e.Path.ToString()).ToArrayAsync());
        }

        // Off, links are listed and followed, as in 2026.
        using var following = Provider(refuseLinks: false);
        CollectionAssert.AreEquivalent(new[] { "real.txt", "real-folder", "file-link.txt", "folder-link", "out-link" },
            (await following.GetDirectoryContentsAsync("")).Select(e => e.Name).ToArray());
        await using var through = await following.ReadFileAsync("out-link/x.txt");
        Assert.AreEqual("outside", await new StreamReader(through).ReadToEndAsync());
    }

    [TestMethod]
    public void RefuseLinks_TheSynchronousMembersRefuseToo()
    {
        StageLinks();
        using var provider = Provider();
        Assert.ThrowsExactly<ArgumentException>(() => provider.GetFileInfo("out-link/x.txt"));
        Assert.ThrowsExactly<ArgumentException>(() => provider.GetDirectoryContents("out-link"));
    }

    [TestMethod]
    public async Task RefuseLinks_AFolderTheProcessMayNotEnter_ReadsAsMissing()
    {
        // As without the option: the system won't look past the folder for the member either, so nothing below it can
        // be reached through a link. Windows refuses the lookup instead, since a process can be let through a folder it
        // may not read.
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows lets a process through a folder it may not read; the check refuses there instead.");
            return;
        }
        var locked = Directory.CreateDirectory(Disk("locked")).FullName;
        File.WriteAllText(Path.Combine(locked, "x.txt"), "hidden");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            if (Directory.GetFiles(locked).Length > 0)
                Assert.Inconclusive("The process runs as root, which permissions don't stop.");
        }
        catch (UnauthorizedAccessException)
        {
        }
        try
        {
            using var provider = Provider();
            Assert.IsFalse((await provider.GetFileInfoAsync("locked/x.txt")).Exists);
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => provider.ReadFileAsync("locked/x.txt"));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [TestMethod]
    public async Task RefuseLinks_TheRootMayBeALink()
    {
        // Only entries under the root are checked: the root itself, and the folders above it, may be links.
        var rootLink = Path.Combine(_base, "root-link");
        try
        {
            Directory.CreateSymbolicLink(rootLink, _root);
        }
        catch (Exception e) when (OperatingSystem.IsWindows() && e is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive($"Creating a symbolic link on Windows needs Developer Mode or elevation: {e.Message}");
        }
        using var provider = new WriteablePhysicalFileProvider(new PhysicalFileProviderOptions { RootDirectoryPath = rootLink, RefuseLinks = true });
        await provider.UpdateFileAsync("a/b.txt", TestStreams.Text("through the root's link"));
        await using var read = await provider.ReadFileAsync("a/b.txt");
        Assert.AreEqual("through the root's link", await new StreamReader(read).ReadToEndAsync());
        Assert.IsTrue(File.Exists(Disk("a", "b.txt")));
    }

    private static async Task AssertEveryMemberRefusesAsync(WriteablePhysicalFileProvider provider, string path)
    {
        Func<Task>[] members =
        [
            () => provider.GetFileInfoAsync(path),
            () => provider.GetDirectoryContentsAsync(path),
            async () => await provider.EnumerateAsync(path).ToListAsync(),
            async () => await provider.EnumerateAsync(path, recursive: true).ToListAsync(),
            () => provider.ReadFileAsync(path),
            () => provider.ReadRangeAsync(path, 0, 1),
            () => provider.OpenReadSeekableAsync(path),
            () => provider.UpdateFileAsync(path, TestStreams.Text("new")),
            () => provider.UpdateFileAsync(path, TestStreams.Text("new"), UpdateFileOptions.Append),
            () => provider.UpdateFileAsync(path, TestStreams.Text("new"), new UpdateFileOptions { IfNotExists = true }),
            () => provider.DeleteAsync(path),
            () => provider.MoveAsync(path, "moved.txt"),
            () => provider.MoveAsync("real.txt", path),
            () => provider.CopyAsync(path, "copied.txt"),
            () => provider.CopyAsync("real.txt", path),
            () => provider.CreateLocalFileStreamAsync(path, readOnly: true),
            () => provider.CreateLocalFileStreamAsync(path, readOnly: false),
        ];
        for (var i = 0; i < members.Length; i++)
        {
            var e = await Assert.ThrowsExactlyAsync<ArgumentException>(members[i], $"member {i}, '{path}'");
            StringAssert.Contains(e.Message, "RefuseLinks");
        }
    }

    // ----- The sweep -----

    private static readonly DateTimeOffset _now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private WriteablePhysicalFileProvider Sweeper() => new(_root) { Clock = new FixedClock(_now) };

    private static string Leftover(string folder, TimeSpan age, string? name = null)
    {
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, name ?? ".wfp-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(file, "partial");
        File.SetLastWriteTimeUtc(file, (_now - age).UtcDateTime);
        return file;
    }

    [TestMethod]
    public async Task Sweep_DeletesOnlyTempFilesOlderThanTheAge()
    {
        var old = Leftover(_root, TimeSpan.FromHours(2));
        var oldBelow = Leftover(Disk("a", "b"), TimeSpan.FromDays(3));
        var fresh = Leftover(_root, TimeSpan.FromMinutes(5));
        var notes = Leftover(_root, TimeSpan.FromDays(3), ".wfp-notes.tmp");
        var renameCopy = Leftover(_root, TimeSpan.FromDays(3), ".wfp-rename-" + Guid.NewGuid().ToString("N") + ".tmp");
        var upperCase = Leftover(_root, TimeSpan.FromDays(3), ".wfp-" + Guid.NewGuid().ToString("N").ToUpperInvariant() + ".tmp");
        File.WriteAllText(Disk("data.txt"), "a caller's file");
        File.SetLastWriteTimeUtc(Disk("data.txt"), (_now - TimeSpan.FromDays(3)).UtcDateTime);

        using var provider = Sweeper();
        Assert.AreEqual(2, await provider.DeleteTempFilesAsync(TimeSpan.FromHours(1)));
        Assert.IsFalse(File.Exists(old));
        Assert.IsFalse(File.Exists(oldBelow));
        foreach (var kept in new[] { fresh, notes, renameCopy, upperCase, Disk("data.txt") })
            Assert.IsTrue(File.Exists(kept), Path.GetFileName(kept));

        // Zero takes every temp file, the fresh one included; a second sweep finds nothing.
        Assert.AreEqual(1, await provider.DeleteTempFilesAsync(TimeSpan.Zero));
        Assert.AreEqual(0, await provider.DeleteTempFilesAsync(TimeSpan.Zero));
    }

    [TestMethod]
    public async Task Sweep_DoesNotWalkIntoALinkedFolder()
    {
        var outsideLeftover = Leftover(_outside, TimeSpan.FromDays(3));
        try
        {
            Directory.CreateSymbolicLink(Disk("out-link"), _outside);
        }
        catch (Exception e) when (OperatingSystem.IsWindows() && e is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive($"Creating a symbolic link on Windows needs Developer Mode or elevation: {e.Message}");
        }
        using var provider = Sweeper();
        Assert.AreEqual(0, await provider.DeleteTempFilesAsync(TimeSpan.FromHours(1)));
        Assert.IsTrue(File.Exists(outsideLeftover), "A temp file behind a link is not the provider's");
    }

    [TestMethod]
    public async Task Sweep_LeavesFoldersAlone_AndGoesOnPastOneItMayNotList()
    {
        // A folder named like a temp file is not one; a folder the process may not list is skipped, and the rest swept.
        Directory.CreateDirectory(Disk(".wfp-" + Guid.NewGuid().ToString("N") + ".tmp"));
        var locked = Directory.CreateDirectory(Disk("a-locked")).FullName;
        var lockedLeftover = Leftover(locked, TimeSpan.FromDays(3));
        var after = Leftover(Disk("z-after"), TimeSpan.FromDays(3));
        var permissionsApply = false;
        int deleted;
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    Directory.GetFiles(locked);
                }
                catch (UnauthorizedAccessException)
                {
                    permissionsApply = true;
                }
            }
            using var provider = Sweeper();
            deleted = await provider.DeleteTempFilesAsync(TimeSpan.FromHours(1));
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Assert.IsFalse(File.Exists(after), "The folders after it are swept");
        Assert.HasCount(1, Directory.GetDirectories(_root, ".wfp-*.tmp"), "A folder named like a temp file stays");
        // Where permissions stop the process, the folder it may not list keeps its leftover; elsewhere it is swept.
        Assert.AreEqual(permissionsApply, File.Exists(lockedLeftover));
        Assert.AreEqual(permissionsApply ? 1 : 2, deleted);
    }

    [TestMethod]
    public async Task Sweep_SkipsAFolderNoPathCanName()
    {
        // No write puts a temp file in a folder whose name a path cannot hold (a backslash here), so nothing there is a
        // leftover.
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("Windows cannot create a folder with a backslash in its name.");
        var unnamed = Leftover(Disk(@"a\b"), TimeSpan.FromDays(3));
        var named = Leftover(Disk("c"), TimeSpan.FromDays(3));
        using var provider = Sweeper();
        Assert.AreEqual(1, await provider.DeleteTempFilesAsync(TimeSpan.FromHours(1)));
        Assert.IsTrue(File.Exists(unnamed));
        Assert.IsFalse(File.Exists(named));
    }

    [TestMethod]
    public async Task Sweep_OfARootThatIsAFile_Throws()
    {
        // A root that is there but cannot be read is a failure, not an empty root.
        Directory.Delete(_root);
        File.WriteAllText(_root, "a file where the root should be");
        using var provider = Sweeper();
        await Assert.ThrowsAsync<IOException>(() => provider.DeleteTempFilesAsync(TimeSpan.Zero));
    }

    [TestMethod]
    public async Task Sweep_StopsWhenCancelled()
    {
        Leftover(_root, TimeSpan.FromDays(3));
        using var provider = Sweeper();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => provider.DeleteTempFilesAsync(TimeSpan.Zero, cancelled.Token));
        Assert.AreEqual(1, Directory.GetFiles(_root, ".wfp-*.tmp").Length);
    }

    [TestMethod]
    public async Task Sweep_AnAgeLongerThanTheClockHasRunTakesNothing()
    {
        Leftover(_root, TimeSpan.FromDays(3));
        using var provider = Sweeper();
        Assert.AreEqual(0, await provider.DeleteTempFilesAsync(TimeSpan.MaxValue));
    }

    [TestMethod]
    public async Task NewName_WhoseTempFileIsGoneBeforeTheLink_FailsWithoutTakingTheName()
    {
        // The temp file swept between its last write and the link: the write fails as any swept write does, and no
        // empty file appears at the name meanwhile, as taking the name the old way would make.
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("Windows renames without replacing; it never takes a name with a hard link.");
        using var provider = new WriteablePhysicalFileProvider(_root)
        {
            Link = (existing, created) =>
            {
                File.Delete(existing);
                return NativeMethods.Link(existing, created) == 0 ? 0 : System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            },
            NameTaken = _ => Assert.Fail("No name is taken for a write whose temp file is gone"),
        };
        var e = await Assert.ThrowsExactlyAsync<IOException>(() => provider.UpdateFileAsync("f.txt", TestStreams.Text("new"), new UpdateFileOptions { IfNotExists = true }));
        Assert.DoesNotContain(".wfp-", e.Message);
        Assert.IsFalse(File.Exists(Disk("f.txt")));
    }

    [TestMethod]
    public async Task Sweep_SkipsATempFileAWriteHoldsOpen()
    {
        // Windows refuses to delete a file opened without delete sharing, as a write's temp file is.
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Linux and macOS delete a file that is open.");
        var held = Leftover(_root, TimeSpan.FromDays(3));
        var loose = Leftover(_root, TimeSpan.FromDays(3));
        using var provider = Sweeper();
        using (new FileStream(held, FileMode.Open, FileAccess.Write, FileShare.Read))
            Assert.AreEqual(1, await provider.DeleteTempFilesAsync(TimeSpan.FromHours(1)));
        Assert.IsTrue(File.Exists(held));
        Assert.IsFalse(File.Exists(loose));
    }

    [TestMethod]
    public async Task Sweep_RefusesANegativeAge_AndAMissingRootHasNothing()
    {
        using var provider = Sweeper();
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => provider.DeleteTempFilesAsync(TimeSpan.FromSeconds(-1)));
        Directory.Delete(_root, recursive: true);
        Assert.AreEqual(0, await provider.DeleteTempFilesAsync(TimeSpan.Zero));
        provider.Dispose();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => provider.DeleteTempFilesAsync(TimeSpan.Zero));
    }

    [TestMethod]
    public async Task Write_WhoseTempFileIsDeleted_FailsAndLeavesTheFile()
    {
        // What a sweep with too short an age does to a write in progress: on Linux and macOS the temp file goes and the
        // write fails, naming the file, not its temp; Windows refuses to delete the temp file while the write holds it.
        File.WriteAllText(Disk("f.txt"), "old");
        var refused = false;
        using var provider = new WriteablePhysicalFileProvider(_root)
        {
            TempCreated = temp =>
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                    refused = true;
                }
            },
        };
        if (OperatingSystem.IsWindows())
        {
            await provider.UpdateFileAsync("f.txt", TestStreams.Text("new"));
            Assert.IsTrue(refused, "Windows refuses the delete");
            Assert.AreEqual("new", File.ReadAllText(Disk("f.txt")));
            return;
        }
        var e = await Assert.ThrowsExactlyAsync<IOException>(() => provider.UpdateFileAsync("f.txt", TestStreams.Text("new")));
        StringAssert.Contains(e.Message, "'f.txt'");
        Assert.DoesNotContain(".wfp-", e.Message);
        Assert.AreEqual("old", File.ReadAllText(Disk("f.txt")));
        Assert.IsEmpty(Directory.GetFiles(_root, ".wfp-*.tmp"));
    }
}
