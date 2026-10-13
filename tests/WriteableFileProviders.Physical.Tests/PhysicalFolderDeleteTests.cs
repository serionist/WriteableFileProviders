using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WriteableFileProviders.Physical.Tests;

/// <summary>
/// Physical's folder delete through handles: a link swapped in while it runs is removed as itself, never entered, and
/// with <see cref="PhysicalFileProviderOptions.RefuseLinks"/> a link swapped in on the way is refused; the errors, the
/// rules each system's answers are read by, and the delete by path where the system is not known.
/// </summary>
[TestClass]
public sealed class PhysicalFolderDeleteTests
{
    private string _base = "";
    private string _root = "";
    private string _outside = "";

    [TestInitialize]
    public void Start()
    {
        _base = Path.Combine(Path.GetTempPath(), "wfp-delete-" + Guid.NewGuid().ToString("N")[..12]);
        _root = Directory.CreateDirectory(Path.Combine(_base, "root")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_base, "outside")).FullName;
        File.WriteAllText(Path.Combine(_outside, "keep.txt"), "outside");
        Directory.CreateDirectory(Path.Combine(_outside, "deep"));
        File.WriteAllText(Path.Combine(_outside, "deep", "keep.txt"), "outside, deeper");
    }

    [TestCleanup]
    public void End()
    {
        if (!Directory.Exists(_base))
            return;
        // .NET's own recursive delete fails at a junction: the links go first, as themselves.
        var links = new DirectoryInfo(_base).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .Where(entry => entry.LinkTarget is not null).ToList();
        foreach (var link in links)
            link.Delete();
        Directory.Delete(_base, recursive: true);
    }

    private WriteablePhysicalFileProvider Provider(bool refuseLinks = false, Action<string>? opening = null) =>
        new(new PhysicalFileProviderOptions { RootDirectoryPath = _root, RefuseLinks = refuseLinks }) { Opening = opening };

    private string Disk(params string[] segments) => Path.Combine([_root, .. segments]);

    private void Write(string text, params string[] segments)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Disk(segments))!);
        File.WriteAllText(Disk(segments), text);
    }

    private void AssertOutsideIsIntact()
    {
        Assert.AreEqual("outside", File.ReadAllText(Path.Combine(_outside, "keep.txt")));
        Assert.AreEqual("outside, deeper", File.ReadAllText(Path.Combine(_outside, "deep", "keep.txt")));
    }

    // A link to a folder: a symbolic link, or on Windows a junction, which needs no privilege there.
    private static void FolderLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target]) { UseShellExecute = false, RedirectStandardOutput = true })!;
        mklink.WaitForExit();
        Assert.AreEqual(0, mklink.ExitCode, "mklink /J");
    }

    // A symbolic link to a file: false on Windows without Developer Mode or elevation, which it needs there.
    private static bool TryFileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (OperatingSystem.IsWindows() && e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ----- Links swapped in while the delete runs -----

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ASubfolderSwappedForALinkMeanwhile_IsRemovedAsALink_NeverEntered(bool refuseLinks)
    {
        Write("inner", "dir", "sub", "inner.txt");
        Write("other", "dir", "other.txt");
        var swapped = false;
        using var provider = Provider(refuseLinks, opening: path =>
        {
            if (path != Disk("dir", "sub") || swapped)
                return;
            swapped = true;
            Directory.Move(Disk("dir", "sub"), Disk("moved"));
            FolderLink(Disk("dir", "sub"), _outside);
        });
        Assert.IsTrue(await provider.DeleteAsync("dir"));
        Assert.IsTrue(swapped);
        Assert.IsFalse(Path.Exists(Disk("dir")));
        AssertOutsideIsIntact();
        Assert.AreEqual("inner", File.ReadAllText(Disk("moved", "inner.txt")), "The folder moved away is not the delete's");
    }

    [TestMethod]
    public async Task WithRefuseLinks_AFolderOnTheWaySwappedForALink_IsRefused()
    {
        Write("x", "a", "b", "x.txt");
        Directory.CreateDirectory(Path.Combine(_outside, "b"));
        File.WriteAllText(Path.Combine(_outside, "b", "y.txt"), "outside");
        var swapped = false;
        using var provider = Provider(refuseLinks: true, opening: path =>
        {
            if (path != Disk("a") || swapped)
                return;
            swapped = true;
            Directory.Move(Disk("a"), Disk("a-moved"));
            FolderLink(Disk("a"), _outside);
        });
        var e = await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.DeleteAsync("a/b"));
        StringAssert.Contains(e.Message, "RefuseLinks");
        StringAssert.Contains(e.Message, "'a'");
        Assert.AreEqual("outside", File.ReadAllText(Path.Combine(_outside, "b", "y.txt")));
        Assert.AreEqual("x", File.ReadAllText(Disk("a-moved", "b", "x.txt")));
    }

    [TestMethod]
    public async Task WithRefuseLinks_TheTargetSwappedForALink_IsRefused()
    {
        Write("x", "dir", "x.txt");
        using var provider = Provider(refuseLinks: true, opening: path =>
        {
            if (path != Disk("dir") || Directory.Exists(Disk("dir-moved")))
                return;
            Directory.Move(Disk("dir"), Disk("dir-moved"));
            FolderLink(Disk("dir"), _outside);
        });
        var e = await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.DeleteAsync("dir"));
        StringAssert.Contains(e.Message, "RefuseLinks");
        Assert.IsTrue(Path.Exists(Disk("dir")), "The link stays");
        AssertOutsideIsIntact();
    }

    [TestMethod]
    public async Task WithRefuseLinks_AFolderOnTheWaySwappedForAFile_DeletesNothing()
    {
        Write("x", "a", "b", "x.txt");
        using var provider = Provider(refuseLinks: true, opening: path =>
        {
            if (path != Disk("a") || Directory.Exists(Disk("a-moved")))
                return;
            Directory.Move(Disk("a"), Disk("a-moved"));
            File.WriteAllText(Disk("a"), "a file");
        });
        Assert.IsFalse(await provider.DeleteAsync("a/b"));
        Assert.AreEqual("x", File.ReadAllText(Disk("a-moved", "b", "x.txt")));
    }

    [TestMethod]
    public async Task ATargetFolderReplacedByAFileSinceTheProbe_IsDeleted()
    {
        Write("x", "dir", "x.txt");
        using var provider = Provider(opening: path =>
        {
            if (path != Disk("dir") || File.Exists(Disk("dir")))
                return;
            Directory.Delete(Disk("dir"), recursive: true);
            File.WriteAllText(Disk("dir"), "a file now");
        });
        Assert.IsTrue(await provider.DeleteAsync("dir"));
        Assert.IsFalse(Path.Exists(Disk("dir")));
    }

    [TestMethod]
    public async Task AnEntryTurnedFromAFolderIntoAFileMeanwhile_IsDeleted()
    {
        Write("x", "dir", "sub", "x.txt");
        using var provider = Provider(opening: path =>
        {
            if (path != Disk("dir", "sub") || File.Exists(Disk("dir", "sub")))
                return;
            Directory.Delete(Disk("dir", "sub"), recursive: true);
            File.WriteAllText(Disk("dir", "sub"), "a file now");
        });
        Assert.IsTrue(await provider.DeleteAsync("dir"));
        Assert.IsFalse(Path.Exists(Disk("dir")));
    }

    [TestMethod]
    public async Task AnEntryGoneBetweenTheListingAndItsOpen_IsSkipped()
    {
        Write("x", "dir", "x.txt");
        Write("y", "dir", "y.txt");
        using var provider = Provider(opening: path =>
        {
            if (path == Disk("dir", "x.txt"))
                File.Delete(path);
        });
        Assert.IsTrue(await provider.DeleteAsync("dir"));
        Assert.IsFalse(Path.Exists(Disk("dir")));
    }

    [TestMethod]
    public async Task ATargetGoneBeforeItIsOpened_DeletesNothing()
    {
        Write("x", "dir", "x.txt");
        using var provider = Provider(opening: path =>
        {
            if (path == Disk("dir"))
                Directory.Delete(path, recursive: true);
        });
        Assert.IsFalse(await provider.DeleteAsync("dir"));
    }

    // ----- Links that were there -----

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ALinkAtTheTarget_IsRemoved_ItsTargetKept(bool targetInside)
    {
        Write("inside", "real", "x.txt");
        FolderLink(Disk("link"), targetInside ? Disk("real") : _outside);
        using var provider = Provider();
        Assert.IsTrue(await provider.DeleteAsync("link"));
        Assert.IsFalse(Path.Exists(Disk("link")));
        Assert.AreEqual("inside", File.ReadAllText(Disk("real", "x.txt")));
        AssertOutsideIsIntact();
    }

    [TestMethod]
    public async Task WithRefuseLinks_ALinkAtTheTarget_IsRefused()
    {
        FolderLink(Disk("link"), _outside);
        using var provider = Provider(refuseLinks: true);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => provider.DeleteAsync("link"));
        Assert.IsTrue(Path.Exists(Disk("link")));
        AssertOutsideIsIntact();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AFolderHoldingLinks_IsDeleted_TheirTargetsKept(bool refuseLinks)
    {
        Write("real", "real.txt");
        Write("x", "dir", "x.txt");
        FolderLink(Disk("dir", "folder-link"), _outside);
        var fileLinks = TryFileLink(Disk("dir", "file-link.txt"), Disk("real.txt")) && TryFileLink(Disk("dir", "dangling"), Disk("nothing"));
        using var provider = Provider(refuseLinks);
        Assert.IsTrue(await provider.DeleteAsync("dir"));
        Assert.IsFalse(Path.Exists(Disk("dir")));
        Assert.AreEqual("real", File.ReadAllText(Disk("real.txt")), fileLinks ? "A link to a file is removed as itself" : "");
        AssertOutsideIsIntact();
    }

    // ----- What the system refuses -----

    [TestMethod]
    public async Task AFileOpenForReadingInside_IsDeleted()
    {
        Write("x", "dir", "x.txt");
        Write("y", "dir", "sub", "y.txt");
        using var provider = Provider();
        using (new FileStream(Disk("dir", "sub", "y.txt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            Assert.IsTrue(await provider.DeleteAsync("dir"));
        Assert.IsFalse(Path.Exists(Disk("dir")));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task OnWindows_AReadOnlyFile_IsRefused_AndTheRestDeleted()
    {
        Write("other", "dir", "other.txt");
        Write("kept", "dir", "read-only.txt");
        File.SetAttributes(Disk("dir", "read-only.txt"), FileAttributes.ReadOnly);
        try
        {
            using var provider = Provider();
            var e = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => provider.DeleteAsync("dir"));
            StringAssert.Contains(e.Message, "'dir/read-only.txt'");
            Assert.IsFalse(File.Exists(Disk("dir", "other.txt")), "The rest is deleted");
            Assert.IsTrue(File.Exists(Disk("dir", "read-only.txt")));
        }
        finally
        {
            File.SetAttributes(Disk("dir", "read-only.txt"), FileAttributes.Normal);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    [UnsupportedOSPlatform("windows")]
    public async Task OnLinuxAndMacOS_AFolderThatMayNotBeChanged_IsRefused_AndTheRestDeleted()
    {
        Write("other", "dir", "other.txt");
        Write("x", "dir", "locked", "x.txt");
        // Readable and searchable, so its entry can be found, but not writable: x.txt cannot be removed from it.
        File.SetUnixFileMode(Disk("dir", "locked"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            if (Environment.UserName == "root")
                Assert.Inconclusive("The tests run as root, whom file permissions do not stop.");
            using var provider = Provider();
            var e = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => provider.DeleteAsync("dir"));
            StringAssert.Contains(e.Message, "'dir/locked/x.txt'");
            Assert.IsFalse(File.Exists(Disk("dir", "other.txt")), "The rest is deleted");
            Assert.IsTrue(File.Exists(Disk("dir", "locked", "x.txt")));
        }
        finally
        {
            File.SetUnixFileMode(Disk("dir", "locked"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    [UnsupportedOSPlatform("windows")]
    public async Task OnLinuxAndMacOS_AFolderThatMayNotBeSearched_IsRefused()
    {
        Write("x", "dir", "closed", "x.txt");
        // Readable, so it lists, but not searchable: its entries cannot be opened.
        File.SetUnixFileMode(Disk("dir", "closed"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        try
        {
            if (Environment.UserName == "root")
                Assert.Inconclusive("The tests run as root, whom file permissions do not stop.");
            using var provider = Provider();
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => provider.DeleteAsync("dir"));
        }
        finally
        {
            File.SetUnixFileMode(Disk("dir", "closed"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // ----- The other ways a folder is deleted -----

    [TestMethod]
    public async Task WhereTheSystemIsNotKnown_AFolderIsDeletedByItsPath()
    {
        Write("x", "dir", "sub", "x.txt");
        using var provider = new WriteablePhysicalFileProvider(_root) { Folders = null };
        Assert.IsTrue(await provider.DeleteAsync("dir"));
        Assert.IsFalse(Path.Exists(Disk("dir")));
    }

    [TestMethod]
    public async Task TheClassicDelete_DeletesTheSameTree()
    {
        Write("x", "dir", "x.txt");
        Write("y", "dir", "sub", "y.txt");
        FolderLink(Disk("dir", "link"), _outside);
        using var provider = new WriteablePhysicalFileProvider(_root) { ClassicDeletes = true };
        Assert.IsTrue(await provider.DeleteAsync("dir"));
        Assert.IsFalse(Path.Exists(Disk("dir")));
        AssertOutsideIsIntact();
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task OnWindows_TheClassicDelete_LeavesAFolderWithAFileOpenInIt_AndSaysSo()
    {
        // Without POSIX deletes, a file open elsewhere keeps its name until it is closed, so its folder is not empty.
        Write("x", "dir", "x.txt");
        using var provider = new WriteablePhysicalFileProvider(_root) { ClassicDeletes = true };
        using (new FileStream(Disk("dir", "x.txt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var e = await Assert.ThrowsExactlyAsync<IOException>(() => provider.DeleteAsync("dir"));
            StringAssert.Contains(e.Message, "'dir'");
        }
        Assert.IsFalse(File.Exists(Disk("dir", "x.txt")), "The file goes once it is closed");
    }

    // ----- Errors, through a system that reports what a disk can't be made to -----

    [TestMethod]
    public async Task ARemovalRefused_IsUnauthorizedAccess_AndTheRestDeleted()
    {
        Write("x", "dir", "x.txt");
        Write("y", "dir", "y.txt");
        var folders = new ScriptedFolders(FolderSystem.Current!) { RemoveErrors = { ["x.txt"] = WindowsRules.AccessDenied } };
        using var provider = new WriteablePhysicalFileProvider(_root) { Folders = folders };
        var e = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => provider.DeleteAsync("dir"));
        StringAssert.Contains(e.Message, "'dir/x.txt'");
        Assert.IsFalse(File.Exists(Disk("dir", "y.txt")));
    }

    [TestMethod]
    [DataRow(WindowsRules.InvalidParameter, true)]
    [DataRow(WindowsRules.NotSupported, true)]
    [DataRow(WindowsRules.InvalidFunction, true)]
    [DataRow(WindowsRules.AccessDenied, false)]
    public async Task APosixDeleteRefused_IsTriedTheClassicWay_OnlyForTheFileSystemsThatRefuseIt(int error, bool fallsBack)
    {
        Write("x", "dir", "x.txt");
        var folders = new ScriptedFolders(FolderSystem.Current!) { RemoveErrors = { ["x.txt"] = error } };
        using var provider = new WriteablePhysicalFileProvider(_root) { Folders = folders };
        if (fallsBack)
        {
            Assert.IsTrue(await provider.DeleteAsync("dir"));
            CollectionAssert.Contains(folders.Removed, ("x.txt", true));
        }
        else
        {
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => provider.DeleteAsync("dir"));
            CollectionAssert.DoesNotContain(folders.Removed, ("x.txt", true));
        }
    }

    [TestMethod]
    public async Task AFolderStillNotEmpty_IsIOException()
    {
        Write("x", "dir", "sub", "x.txt");
        var folders = new ScriptedFolders(FolderSystem.Current!) { RemoveErrors = { ["sub"] = WindowsRules.DirectoryNotEmpty } };
        using var provider = new WriteablePhysicalFileProvider(_root) { Folders = folders };
        var e = await Assert.ThrowsExactlyAsync<IOException>(() => provider.DeleteAsync("dir"));
        StringAssert.Contains(e.Message, "'dir/sub'");
    }

    [TestMethod]
    public async Task AnEntryThatCannotBeOpened_FailsTheDelete_AndTheRestGoes()
    {
        Write("x", "dir", "x.txt");
        Write("y", "dir", "y.txt");
        var folders = new ScriptedFolders(FolderSystem.Current!) { OpenErrors = { ["x.txt"] = WindowsRules.AccessDenied } };
        using var provider = new WriteablePhysicalFileProvider(_root) { Folders = folders };
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => provider.DeleteAsync("dir"));
        Assert.IsTrue(File.Exists(Disk("dir", "x.txt")));
        Assert.IsFalse(File.Exists(Disk("dir", "y.txt")));
    }

    [TestMethod]
    public async Task AFolderThatCannotBeListed_FailsTheDelete()
    {
        Write("x", "dir", "x.txt");
        var folders = new ScriptedFolders(FolderSystem.Current!) { ListsNothing = true };
        using var provider = new WriteablePhysicalFileProvider(_root) { Folders = folders };
        await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(() => provider.DeleteAsync("dir"));
        Assert.IsTrue(File.Exists(Disk("dir", "x.txt")));
    }

    [TestMethod]
    [DataRow(WindowsRules.PathNotFound, false)]
    [DataRow(WindowsRules.AccessDenied, true)]
    public async Task TheStart_GoneDeletesNothing_RefusedThrows(int error, bool throws)
    {
        Write("x", "a", "b", "x.txt");
        foreach (var refuseLinks in new[] { false, true })
        {
            // Without RefuseLinks the folder that holds the target is opened by its path; with it, the root is, then
            // each folder on the way is opened in its parent; then the target in its folder.
            foreach (var folders in new[]
            {
                new ScriptedFolders(FolderSystem.Current!) { OpenPathError = error },
                new ScriptedFolders(FolderSystem.Current!) { OpenErrors = { ["a"] = error } },
                new ScriptedFolders(FolderSystem.Current!) { OpenErrors = { ["b"] = error } },
            })
            {
                if (!refuseLinks && folders.OpenErrors.ContainsKey("a"))
                    continue;
                using var provider = new WriteablePhysicalFileProvider(new PhysicalFileProviderOptions { RootDirectoryPath = _root, RefuseLinks = refuseLinks }) { Folders = folders };
                if (throws)
                    await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => provider.DeleteAsync("a/b"), $"RefuseLinks {refuseLinks}");
                else
                    Assert.IsFalse(await provider.DeleteAsync("a/b"), $"RefuseLinks {refuseLinks}");
                Assert.IsTrue(File.Exists(Disk("a", "b", "x.txt")));
            }
        }
    }

    [TestMethod]
    public async Task TheTargetRefusingItsRemoval_Throws()
    {
        Write("x", "dir", "x.txt");
        var folders = new ScriptedFolders(FolderSystem.Current!) { RemoveErrors = { ["dir"] = WindowsRules.AccessDenied } };
        using var provider = new WriteablePhysicalFileProvider(_root) { Folders = folders };
        var e = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => provider.DeleteAsync("dir"));
        StringAssert.Contains(e.Message, "'dir'");
    }

    [TestMethod]
    public async Task TheTargetGoneAsItIsRemoved_StillCountsAsDeleted()
    {
        Write("x", "dir", "x.txt");
        var folders = new ScriptedFolders(FolderSystem.Current!) { RemoveErrors = { ["dir"] = WindowsRules.FileNotFound } };
        using var provider = new WriteablePhysicalFileProvider(_root) { Folders = folders };
        Assert.IsTrue(await provider.DeleteAsync("dir"));
    }

    // ----- The rules -----

    [TestMethod]
    public void UnixTable_IsPickedBySystemAndArchitecture()
    {
        Assert.AreSame(UnixTable.Linux, UnixTable.For(linux: true, macOS: false, Architecture.X64));
        Assert.AreSame(UnixTable.LinuxArm, UnixTable.For(linux: true, macOS: false, Architecture.Arm64));
        Assert.AreSame(UnixTable.LinuxArm, UnixTable.For(linux: true, macOS: false, Architecture.Arm));
        Assert.AreSame(UnixTable.MacOS, UnixTable.For(linux: false, macOS: true, Architecture.Arm64));
        Assert.AreSame(UnixTable.MacOS, UnixTable.For(linux: false, macOS: true, Architecture.X64));
        Assert.IsNull(UnixTable.For(linux: true, macOS: false, Architecture.S390x), "An architecture whose values aren't known");
        Assert.IsNull(UnixTable.For(linux: true, macOS: false, Architecture.RiscV64));
        Assert.IsNull(UnixTable.For(linux: false, macOS: false, Architecture.X64), "Another system");
        Assert.IsNotNull(FolderSystem.Current, "Every system CI runs on deletes through handles");
    }

    [TestMethod]
    public void UnixTable_ComposesItsFlags()
    {
        Assert.AreEqual(0x200000 | 0x10000 | 0x80000, UnixTable.Linux.OpenFolder);
        Assert.AreEqual(0x200000 | 0x10000 | 0x80000 | 0x20000, UnixTable.Linux.OpenEntry);
        Assert.AreEqual(0x200000 | 0x4000 | 0x80000 | 0x8000, UnixTable.LinuxArm.OpenEntry);
        Assert.AreEqual(0x100000 | 0x1000000 | 0x100, UnixTable.MacOS.OpenEntry);
        Assert.AreEqual(Failure.Gone, UnixTable.Linux.Classify(UnixTable.NoEntry));
        Assert.AreEqual(Failure.Gone, UnixTable.Linux.Classify(UnixTable.NotADirectory));
        Assert.AreEqual(Failure.Refused, UnixTable.Linux.Classify(UnixTable.AccessDenied));
        Assert.AreEqual(Failure.Refused, UnixTable.MacOS.Classify(UnixTable.NotPermitted));
        Assert.AreEqual(Failure.NotEmpty, UnixTable.Linux.Classify(39));
        Assert.AreEqual(Failure.NotEmpty, UnixTable.MacOS.Classify(66));
        Assert.AreEqual(Failure.Other, UnixTable.MacOS.Classify(39), "39 is another error on macOS");
    }

    [TestMethod]
    public void WindowsRules_ReadWhatWindowsReports()
    {
        const uint directory = WindowsRules.DirectoryAttribute;
        const uint reparse = WindowsRules.ReparsePointAttribute;
        Assert.AreEqual(EntryKind.Folder, WindowsRules.KindOf(directory, 0));
        Assert.AreEqual(EntryKind.Other, WindowsRules.KindOf(0x20, 0));
        Assert.AreEqual(EntryKind.Link, WindowsRules.KindOf(directory | reparse, 0xA0000003), "A junction or a mount point");
        Assert.AreEqual(EntryKind.Link, WindowsRules.KindOf(reparse, 0xA000000C), "A symbolic link to a file");
        Assert.AreEqual(EntryKind.Folder, WindowsRules.KindOf(directory | reparse, 0x9000001A), "A cloud placeholder holds its own content");
        Assert.AreEqual(EntryKind.Other, WindowsRules.KindOf(reparse, 0x80000013), "A deduplicated file");
        Assert.AreEqual(Failure.Gone, WindowsRules.Classify(WindowsRules.FileNotFound));
        Assert.AreEqual(Failure.Gone, WindowsRules.Classify(WindowsRules.PathNotFound));
        Assert.AreEqual(Failure.Refused, WindowsRules.Classify(WindowsRules.AccessDenied));
        Assert.AreEqual(Failure.NotEmpty, WindowsRules.Classify(WindowsRules.DirectoryNotEmpty));
        Assert.AreEqual(Failure.Other, WindowsRules.Classify(32));
        Assert.IsTrue(WindowsRules.FallsBack(WindowsRules.InvalidParameter));
        Assert.IsFalse(WindowsRules.FallsBack(WindowsRules.AccessDenied));
    }

    [TestMethod]
    public void WindowsRules_GiveAPathItsExtendedForm()
    {
        Assert.AreEqual(@"\\?\C:\data\files", WindowsRules.ExtendedPath(@"C:\data\files"));
        Assert.AreEqual(@"\\?\C:\", WindowsRules.ExtendedPath(@"C:\"));
        Assert.AreEqual(@"\\?\UNC\server\share\files", WindowsRules.ExtendedPath(@"\\server\share\files"));
    }

    // The system as it is, but for what a test scripts by entry name: an error opening it, an error removing it (the
    // POSIX way; the classic way is the system's own), a folder by path that cannot be opened, and listings that read
    // nowhere. It reads every error by Windows' rules, whatever the system.
    private sealed class ScriptedFolders(FolderSystem system) : FolderSystem
    {
        public Dictionary<string, int> OpenErrors { get; } = [];

        public Dictionary<string, int> RemoveErrors { get; } = [];

        public int? OpenPathError { get; init; }

        public bool ListsNothing { get; init; }

        public List<(string Name, bool Classic)> Removed { get; } = [];

        public override SafeHandle? OpenPath(string path, out int error)
        {
            if (OpenPathError is { } scripted)
            {
                error = scripted;
                return null;
            }
            return system.OpenPath(path, out error);
        }

        public override Entry Open(SafeHandle folder, string name, bool forRemoval) =>
            OpenErrors.TryGetValue(name, out var error) ? Entry.Failed(error) : system.Open(folder, name, forRemoval);

        public override int Remove(SafeHandle folder, string name, Entry entry, bool classic)
        {
            Removed.Add((name, classic));
            return !classic && RemoveErrors.TryGetValue(name, out var error) ? error : system.Remove(folder, name, entry, classic);
        }

        public override Failure Classify(int error) => WindowsRules.Classify(error);

        public override bool FallsBack(int error) => WindowsRules.FallsBack(error);

        public override string ListingPath(string path) => ListsNothing ? Path.Combine(path, "nowhere") : system.ListingPath(path);
    }
}
