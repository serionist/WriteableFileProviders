using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WriteableFileProviders.Physical;

// A folder delete's calls on Linux and macOS, through the C library: openat with O_NOFOLLOW and O_DIRECTORY opens an
// entry only if it is a folder of its own, readlinkat tells a link from a file, and unlinkat removes an entry from the
// folder whose handle it is given. open and openat are declared with their fixed arguments only: O_CREAT is never set,
// so the mode they would take is never read.
internal sealed partial class UnixFolders(UnixTable table) : FolderSystem
{
    public override SafeHandle? OpenPath(string path, out int error)
    {
        var fd = Open(path, table.OpenFolder);
        error = fd < 0 ? Marshal.GetLastPInvokeError() : 0;
        return fd < 0 ? null : new SafeFileHandle(fd, ownsHandle: true);
    }

    public override Entry Open(SafeHandle folder, string name, bool forRemoval)
    {
        var fd = With(folder, dirfd => OpenAt(dirfd, name, table.OpenEntry));
        if (fd >= 0)
            return new Entry(EntryKind.Folder, new SafeFileHandle(fd, ownsHandle: true), 0);
        var error = Marshal.GetLastPInvokeError();
        if (error != UnixTable.NotADirectory && error != table.Loop)
            return Entry.Failed(error);
        // Not a folder of its own (a link gives ENOTDIR on Linux, ELOOP or ENOTDIR on macOS): a link has a target to read.
        byte target = 0;
        if (With(folder, dirfd => ReadLinkAt(dirfd, name, ref target, 1)) >= 0)
            return new Entry(EntryKind.Link, null, 0);
        error = Marshal.GetLastPInvokeError();
        return error == UnixTable.Invalid ? new Entry(EntryKind.Other, null, 0) : Entry.Failed(error);
    }

    public override int Remove(SafeHandle folder, string name, Entry entry, bool classic) =>
        With(folder, dirfd => UnlinkAt(dirfd, name, entry.Kind == EntryKind.Folder ? table.RemoveDirectory : 0)) == 0
            ? 0
            : Marshal.GetLastPInvokeError();

    public override Failure Classify(int error) => UnixTable.Classify(error);

    // Runs a call with the file descriptor a handle holds, which stays open until the call returns.
    private static T With<T>(SafeHandle handle, Func<int, T> call)
    {
        var added = false;
        handle.DangerousAddRef(ref added);
        try
        {
            return call((int)handle.DangerousGetHandle());
        }
        finally
        {
            if (added)
                handle.DangerousRelease();
        }
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenAt(int folder, string name, int flags);

    [LibraryImport("libc", EntryPoint = "readlinkat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial nint ReadLinkAt(int folder, string name, ref byte buffer, nuint size);

    [LibraryImport("libc", EntryPoint = "unlinkat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int UnlinkAt(int folder, string name, int flags);
}
