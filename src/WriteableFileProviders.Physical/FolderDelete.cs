using System.Diagnostics.CodeAnalysis;
using System.IO.Enumeration;
using System.Runtime.InteropServices;

namespace WriteableFileProviders.Physical;

// What an entry of a folder is, opened without following a link.
internal enum EntryKind
{
    // None: the entry could not be opened (Entry.Failed).
    Unknown,

    // A folder of its own, not a link to one.
    Folder,

    // A symbolic link, a junction or another name surrogate: removed as itself, never entered.
    Link,

    // Anything else: a file, a FIFO, a socket, a device.
    Other,
}

// What a failure of the system means to a delete.
internal enum Failure
{
    // The entry is gone: nothing left to delete.
    Gone,

    // The system refused the access.
    Refused,

    // Anything else, a folder that still has entries included.
    Other,
}

// An entry opened in its folder: what it is and its handle (a folder's on every system; any entry's on Windows), or
// the system's error number when it could not be opened.
internal readonly record struct Entry(EntryKind Kind, SafeHandle? Handle, int Error)
{
    public static Entry Failed(int error) => new(EntryKind.Unknown, null, error);
}

// The calls a folder delete makes to the system, through handles: an entry is always opened relative to its folder's
// handle and without following a link, so a link swapped in can never take the delete elsewhere. They report raw
// facts; what the facts mean is decided by the rules below and by FolderDelete, which run on every system.
internal abstract class FolderSystem
{
    // The system this process runs on, where its calls and their flags are known: Linux on x64, Arm and Arm64, macOS,
    // Windows. Null elsewhere: there a folder is deleted by its path, as .NET does.
    public static FolderSystem? Current { get; } =
        OperatingSystem.IsWindows() ? new WindowsFolders()
        : UnixTable.For(OperatingSystem.IsLinux(), OperatingSystem.IsMacOS(), RuntimeInformation.ProcessArchitecture) is { } table ? new UnixFolders(table)
        : null;

    // A folder by its path, following links, for the walk to start from: its handle, or null and the error number.
    public abstract SafeHandle? OpenPath(string path, out int error);

    // The entry 'name' of 'folder', without following a link. 'forRemoval' asks for the access a removal needs (Windows
    // asks for it as it opens); a folder on the way is only looked through.
    public abstract Entry Open(SafeHandle folder, string name, bool forRemoval);

    // Removes an entry Open returned: 0, or the error number. 'classic' asks Windows for its delete without POSIX
    // semantics; elsewhere it changes nothing.
    public abstract int Remove(SafeHandle folder, string name, Entry entry, bool classic);

    // What an error number means.
    public abstract Failure Classify(int error);

    // Whether a delete refused with this error number is tried again the classic way.
    public virtual bool FallsBack(int error) => false;

    // The path to list a folder by.
    public virtual string ListingPath(string path) => path;

    // The names in a folder, read from its path as the walk goes; opening it is where a folder that can't be listed
    // fails.
    public virtual IEnumerator<string> List(string path) =>
        new FileSystemEnumerable<string>(ListingPath(path), (ref FileSystemEntry entry) => entry.FileName.ToString(),
            _listing).GetEnumerator();

    private static readonly EnumerationOptions _listing = new() { AttributesToSkip = 0, IgnoreInaccessible = false };

    // The system's words for an error number.
    public virtual string Describe(int error) => Marshal.GetPInvokeErrorMessage(error);
}

// The flags and error numbers of the C library calls, which differ by system and, on Linux, by architecture. From the
// kernel's and Darwin's headers.
internal sealed record UnixTable(
    int Directory, int NoFollow, int PathOnly, int CloseOnExec, int RemoveDirectory, int Loop)
{
    // The same on Linux and macOS.
    public const int NoEntry = 2;
    public const int AccessDenied = 13;
    public const int NotPermitted = 1;
    public const int NotADirectory = 20;
    public const int Invalid = 22;

    // Linux on x64 (and the architectures that use the generic values); O_PATH opens a folder without reading it.
    public static UnixTable Linux { get; } = new(0x10000, 0x20000, 0x200000, 0x80000, 0x200, 40);

    // Linux on Arm and Arm64, whose O_DIRECTORY and O_NOFOLLOW differ.
    public static UnixTable LinuxArm { get; } = new(0x4000, 0x8000, 0x200000, 0x80000, 0x200, 40);

    // macOS, on Intel and Apple silicon; it has no O_PATH, so a folder is opened to read (O_RDONLY, 0).
    public static UnixTable MacOS { get; } = new(0x100000, 0x100, 0, 0x1000000, 0x80, 62);

    // The table of a system, by its architecture as this process runs (an x64 process emulated on Arm is x64); null
    // where the values are not known.
    public static UnixTable? For(bool linux, bool macOS, Architecture architecture) =>
        (linux, macOS, architecture) switch
        {
            (true, _, Architecture.X64) => Linux,
            (true, _, Architecture.Arm or Architecture.Arm64) => LinuxArm,
            (_, true, Architecture.X64 or Architecture.Arm64) => MacOS,
            _ => null,
        };

    // Opens a folder: without reading it where the system can.
    public int OpenFolder => PathOnly | Directory | CloseOnExec;

    // Opens an entry only if it is a folder of its own: a link fails (ENOTDIR or ELOOP), a file fails (ENOTDIR).
    public int OpenEntry => OpenFolder | NoFollow;

    // Not a folder on the way (ENOTDIR) is as gone as nothing there; the same numbers on Linux and macOS.
    public static Failure Classify(int error) =>
        error is NoEntry or NotADirectory ? Failure.Gone
        : error is AccessDenied or NotPermitted ? Failure.Refused
        : Failure.Other;
}

// What Windows reports, in numbers that do not depend on the architecture.
internal static class WindowsRules
{
    public const uint DirectoryAttribute = 0x10;
    public const uint ReparsePointAttribute = 0x400;

    // A reparse tag that names another entry (a symbolic link, a junction, a mount point) has this bit.
    public const uint NameSurrogate = 0x20000000;

    public const int FileNotFound = 2;
    public const int PathNotFound = 3;
    public const int AccessDenied = 5;
    public const int InvalidFunction = 1;
    public const int NotSupported = 50;
    public const int InvalidParameter = 87;
    public const int DirectoryNotEmpty = 145;

    // A name surrogate is a link, whatever else it is, as .NET's recursive delete reads it; other reparse points (cloud
    // placeholders, deduplicated files) hold their own content.
    public static EntryKind KindOf(uint attributes, uint tag) =>
        (attributes & ReparsePointAttribute) != 0 && (tag & NameSurrogate) != 0 ? EntryKind.Link
        : (attributes & DirectoryAttribute) != 0 ? EntryKind.Folder
        : EntryKind.Other;

    public static Failure Classify(int error) =>
        error switch
        {
            FileNotFound or PathNotFound => Failure.Gone,
            AccessDenied => Failure.Refused,
            _ => Failure.Other,
        };

    // A file system without POSIX deletes (FAT, exFAT, some network file systems) refuses them so; DeleteFile then
    // deletes the classic way, and so does this.
    public static bool FallsBack(int error) => error is InvalidParameter or NotSupported or InvalidFunction;

    // The extended form of a full path, which Windows takes as it is, past MAX_PATH and with any name: \\?\C:\a, and
    // \\?\UNC\server\share\a for \\server\share\a.
    public static string ExtendedPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
}

// Deletes a folder and everything in it through the handles of FolderSystem. The names come from listing each folder
// by its path; every entry is opened relative to its folder's handle without following a link and removed through
// that handle or its own. A name is only ever one entry of the folder: '.' and '..', which some file systems list as
// files, are skipped, and a name holding a separator is refused, so a listing that a rename or a swapped link made
// read another folder can only leave names out, which then fails the delete (the folder is not empty), never reach
// outside the folder. Like .NET's recursive delete, it goes on past an entry it cannot delete, skips one gone
// meanwhile, and throws the first failure at the end. Cancelled, it stops between entries, the folder partly deleted.
internal sealed class FolderDelete(FolderSystem system, Action<string>? opening, bool classicDeletes)
{
    private static readonly char[] _separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    // Deletes the folder at root/segments, whose provider path is 'path': true when something was deleted, false when
    // nothing was there by the time it was opened. With 'refuseLinks', the walk starts at the root and opens each
    // folder on the way without following a link, and a link there or at the target throws 'refused' for its path
    // under the root; without, the folder that holds the target is opened by its path, following links as every
    // member does.
    public bool Delete(ResolvedPath path, string root, IReadOnlyList<string> segments, bool refuseLinks,
        Func<string, Exception> refused, CancellationToken cancellationToken)
    {
        var fullPath = Path.Join([root, .. segments]);
        var parent = refuseLinks
            ? WalkDown(path, root, segments, refused, cancellationToken)
            : OpenPath(path, Path.GetDirectoryName(fullPath)!);
        if (parent is null)
            return false;
        using (parent)
        {
            var name = segments[^1];
            opening?.Invoke(fullPath);
            var target = system.Open(parent, name, forRemoval: true);
            using (target.Handle)
            {
                if (target.Error != 0)
                    return system.Classify(target.Error) == Failure.Gone ? false : throw Failed(path.ToString(), target.Error);
                switch (target.Kind)
                {
                    case EntryKind.Link when refuseLinks:
                        throw refused(string.Join('/', segments));
                    case EntryKind.Folder:
                        DeleteContents(target.Handle!, fullPath, path.ToString(), cancellationToken);
                        break;
                }
                // A link is removed as itself; a file put where the folder was is what the path names now.
                var error = Remove(parent, name, target);
                if (error != 0 && !IsGone(error, fullPath))
                    throw Failed(path.ToString(), error);
                return true;
            }
        }
    }

    // The folder that holds the target, reached from the root one folder at a time without following a link: null when
    // a folder on the way is gone or is not a folder.
    private SafeHandle? WalkDown(ResolvedPath path, string root, IReadOnlyList<string> segments,
        Func<string, Exception> refused, CancellationToken cancellationToken)
    {
        // The root may itself be a link: it is opened by its path.
        var folder = OpenPath(path, root);
        for (var i = 0; folder is not null && i < segments.Count - 1; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                folder.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }
            opening?.Invoke(Path.Join([root, .. segments.Take(i + 1)]));
            Entry entry;
            using (folder)
                entry = system.Open(folder, segments[i], forRemoval: false);
            if (entry.Error != 0)
                return system.Classify(entry.Error) == Failure.Gone ? null : throw Failed(path.ToString(), entry.Error);
            if (entry.Kind == EntryKind.Folder)
            {
                folder = entry.Handle;
                continue;
            }
            entry.Handle?.Dispose();
            return entry.Kind == EntryKind.Link ? throw refused(string.Join('/', segments.Take(i + 1))) : null;
        }
        return folder;
    }

    // A folder by its path: null when it is gone or is not a folder.
    private SafeHandle? OpenPath(ResolvedPath path, string folderPath)
    {
        var handle = system.OpenPath(folderPath, out var error);
        if (handle is not null)
            return handle;
        return system.Classify(error) == Failure.Gone ? null : throw Failed(path.ToString(), error);
    }

    // Empties the folder whose handle is 'folder' and whose path is 'fullPath' ('relative' under the root, for messages).
    private void DeleteContents(SafeHandle folder, string fullPath, string relative,
        CancellationToken cancellationToken)
    {
        Exception? first = null;
        IEnumerator<string>? names = null;
        try
        {
            while (Next(fullPath, ref names, ref first))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = names.Current;
                // A folder's own entries, which some file systems list as files (FUSE, NFS): opened in the folder, '..'
                // would be its parent and '.' itself.
                if (name is "." or "..")
                    continue;
                if (name.AsSpan().IndexOfAny(_separators) >= 0)
                {
                    first ??= new IOException(
                        $"'{relative}' could not be emptied: its listing named '{name}', which is not the name of an entry.");
                    continue;
                }
                var childPath = Path.Join(fullPath, name);
                var childRelative = relative + "/" + name;
                opening?.Invoke(childPath);
                var entry = system.Open(folder, name, forRemoval: true);
                using (entry.Handle)
                {
                    if (entry.Error != 0)
                    {
                        if (system.Classify(entry.Error) != Failure.Gone)
                            first ??= Failed(childRelative, entry.Error);
                        continue;
                    }
                    if (entry.Kind == EntryKind.Folder)
                    {
                        try
                        {
                            DeleteContents(entry.Handle!, childPath, childRelative, cancellationToken);
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                        {
                            // Not empty: it stays, and the delete goes on with the rest.
                            first ??= e;
                            continue;
                        }
                    }
                    var error = Remove(folder, name, entry);
                    if (error != 0 && !IsGone(error, childPath))
                        first ??= Failed(childRelative, error);
                }
            }
        }
        finally
        {
            names?.Dispose();
        }
        if (first is not null)
            throw first;
    }

    // The next name of the folder, listed by its path as the walk goes: false at the end, or when the listing fails,
    // which is recorded, but for a folder gone since it was opened, whose removal then finds nothing to do.
    private bool Next(string fullPath, [NotNullWhen(true)] ref IEnumerator<string>? names, ref Exception? first)
    {
        try
        {
            names ??= system.List(fullPath);
            return names.MoveNext();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (e is not DirectoryNotFoundException || names is not null)
                first ??= e;
            return false;
        }
    }

    // Whether a removal failed because the entry is gone: as the system says, or, where it answers that the access is
    // refused (Windows, for a folder another process deleted while this one held it), because nothing has the name any
    // more. Only the answer depends on it, never what is deleted.
    private bool IsGone(int error, string fullPath) =>
        system.Classify(error) switch
        {
            Failure.Gone => true,
            Failure.Refused => (int)new FileInfo(fullPath).Attributes == -1,
            _ => false,
        };

    private int Remove(SafeHandle folder, string name, Entry entry)
    {
        var error = system.Remove(folder, name, entry, classicDeletes);
        if (error != 0 && !classicDeletes && system.FallsBack(error))
            error = system.Remove(folder, name, entry, classic: true);
        return error;
    }

    private Exception Failed(string relative, int error)
    {
        var message = $"'{relative}' could not be deleted: {system.Describe(error)}";
        return system.Classify(error) == Failure.Refused ? new UnauthorizedAccessException(message) : new IOException(message);
    }
}
