using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;
using Microsoft.Extensions.Primitives;
using Microsoft.Win32.SafeHandles;

namespace WriteableFileProviders.Physical;

/// <summary>A writeable file provider over a folder on the local disk.</summary>
/// <remarks>
/// <para>
/// <b>Writes are atomic.</b> An overwrite, an append, a copy and a local stream's commit write a temp file named
/// <c>.wfp-&lt;guid&gt;.tmp</c> (32 lowercase hex digits) in the target's folder, then rename it over the target: the
/// file has its old content or its new content, never a part. A write that fails removes its temp file; listings never
/// show one, and show every other name, <c>.wfp-notes.tmp</c> included. An append copies the file and the new bytes
/// into the temp file; it first copies the caller's stream into a second temp file named the same way beside it, which
/// only the process's user may read on Linux and macOS, and which goes when the append ends.
/// </para>
/// <para>
/// <b>Replacing by rename</b> makes the new content a new file at the path, so a write is refused, the file keeping its
/// content: with <see cref="UnauthorizedAccessException"/>, on every system, when the process may not write the file
/// (read-only, or another user's) or may not create files in its folder, where the new content is created; with
/// <see cref="IOException"/> when another process holds the file open without sharing writes (Windows), when the path
/// is not a regular file or a link to one (a FIFO, a socket, a device that cannot seek), or when the file is a mount
/// point (a single-file bind mount).
/// </para>
/// <para>
/// On Linux and macOS the new file takes the replaced file's permission bits (not setuid, setgid or sticky), and a copy
/// its source's bits exactly, whatever the umask, as <see cref="File.Copy(string, string)"/> does; both are set as the
/// file is created, before any content is written. Access control lists and extended attributes are not carried over,
/// nor, on Windows, the replaced file's own permissions. Other hard links to the file keep the old content, and a
/// symbolic link at the path, a link to nothing included, is replaced by a file, its target left as it was; the new file
/// takes the bits of the link's target, not the link's own.
/// </para>
/// <para>
/// <b>Versions.</b> A file's <see cref="IWriteableFileInfo.ETag"/> is made from its last-write time and its length; for a
/// symbolic link to a file, those of its target, which reads open, as its <see cref="IFileInfo.Length"/> is.
/// Each write through the provider stamps a last-write time later than the one it replaces, read back from the file
/// system and stepped forward where it stores times coarsely (exFAT, HFS+, FAT), so it changes the ETag. Writes faster
/// than that resolution step it further each time, so the time can run ahead of the clock without bound; bounding it
/// would let an ETag repeat. A change by another program that keeps the length within the file system's time resolution
/// may not change the ETag. <see cref="UpdateFileOptions.IfMatch"/> is checked just before the rename, which is not
/// atomic: another process can change the file between the check and the rename. An append with no condition of the
/// caller's that finds the file changed since it read it, created, or deleted, reads it again and appends after what is
/// there now, up to five attempts in all, then throws <see cref="PreconditionFailedException"/>; its check has the same
/// window. With the caller's <see cref="UpdateFileOptions.IfMatch"/> or <see cref="UpdateFileOptions.IfNotExists"/>, the
/// first change throws. On Windows a write cannot replace a file another write is reading, so one of two appends to one
/// file at once can fail with <see cref="UnauthorizedAccessException"/> (see Reads).
/// </para>
/// <para>
/// <b>New names are taken atomically:</b> <see cref="UpdateFileOptions.IfNotExists"/>, an append to a missing file, and
/// the destination of a move or a copy never replace a file that is there; a write with
/// <see cref="UpdateFileOptions.IfNotExists"/> that finds one throws <see cref="FileAlreadyExistsException"/>, an append
/// appends after it, a move or a copy throws <see cref="FileExistsException"/>. Windows renames without replacing. On
/// Linux and macOS a write or a copy gives its temp file the name as a second hard link, which
/// fails when anything has the name, then drops the temp name: the name never holds a part of the file, and a process
/// that dies in between leaves the file in place and a hidden temp name. Where the file system refuses hard links (FAT
/// and exFAT, some network and FUSE file systems), and for the destination of a move, the name is taken with an
/// exclusive create and the content renamed onto it, so the file exists empty for that moment: a watcher sees it, a
/// process that dies there leaves an empty file, and another writer that appends to it then has its append replaced by
/// that rename. On Linux and macOS, an append that loses a new name reads past the file that took it only once that file
/// has content, so it never appends to such an empty file: while it is empty (a move's destination, a file system
/// without hard links, or a file another writer left empty) it throws <see cref="FileAlreadyExistsException"/>. On a
/// network file system that may answer a resent request again (NFS), a write can find its own new name taken and throw
/// <see cref="FileAlreadyExistsException"/>, a copy <see cref="FileExistsException"/>, with its content in place; an
/// append then appends its bytes a second time.
/// </para>
/// <para>
/// <b>Reads</b> share the file with writers and deleters, and keep the content they opened. A delete goes ahead while a
/// file is read. So does a write on Linux and macOS; on Windows, which does not rename over a file that is open, a
/// write to a file that is being read fails and the file keeps its content: with
/// <see cref="UnauthorizedAccessException"/> when the reader shares writing, as this provider's readers do, and with
/// <see cref="IOException"/> when it does not. A range read checks <see cref="ReadOptions.IfMatch"/> on the handle it
/// reads through; a seekable read re-checks the ETag on each fetch.
/// </para>
/// <para>
/// <b>Names</b> are matched without regard to case on Windows and macOS, so a move between names that differ only in
/// case renames the file, and with regard to case elsewhere. A <c>:</c> is not allowed in a name. On Windows, a name is
/// also refused (<see cref="ArgumentException"/>) when Windows would read it as another name or cannot hold it: one
/// that ends in <c>.</c> or a space, which Windows drops (so <c>a.txt.</c> would name <c>a.txt</c>), one that holds
/// <c>"</c>, <c>&lt;</c>, <c>&gt;</c>, <c>|</c>, <c>?</c> or <c>*</c>, and a reserved device name (<c>CON</c>,
/// <c>PRN</c>, <c>AUX</c>, <c>NUL</c>, <c>COM1</c> to <c>COM9</c>, <c>LPT1</c> to <c>LPT9</c>, and <c>COM</c> or
/// <c>LPT</c> followed by <c>¹</c>, <c>²</c> or <c>³</c>), in any case, alone or with an extension, such as
/// <c>con.txt</c>; and an 8.3 short name (<c>REPORT~1.TXT</c>) of a file or folder whose long name differs, on a volume
/// that keeps them. Listings leave out the files and folders whose names a path cannot name (those names on Windows; on
/// Linux and macOS, a name holding <c>:</c>, <c>\</c> or a control character, or only dots and spaces; on any system, a
/// percent-encoded form of those, such as <c>%2e%2e</c>), and never walk into such a folder, so everything listed can
/// be read back. Containment in the root is checked on the path: symbolic links and junctions inside the root are
/// followed, except that a recursive listing lists a linked folder without walking into it. With
/// <see cref="PhysicalFileProviderOptions.RefuseLinks"/>, a path that names or goes through a link inside the root is
/// refused instead, and listings leave links out. <see cref="DeleteTempFilesAsync"/> deletes the temp files a process
/// that died during a write left behind.
/// </para>
/// <para>
/// <b>Access.</b> Where the operating system refuses the process access to a file or a folder that a member opens,
/// lists, writes, moves or deletes, the member throws <see cref="UnauthorizedAccessException"/>, a read or a listing as
/// well as a write. A path the system will not look up at all, such as one inside a folder the process may not enter,
/// reads as missing.
/// </para>
/// <para>
/// <b>Not native:</b> signed URLs (WriteableFileProviders.AspNetCore issues gateway URLs) and content types (a given
/// <see cref="UpdateFileOptions.ContentType"/> is not kept).
/// </para>
/// </remarks>
public sealed class WriteablePhysicalFileProvider : WriteableFileProvider<PhysicalFileProviderOptions>
{
    private const string TempPrefix = ".wfp-";
    private const string TempSuffix = ".tmp";
    private const int BufferSize = 4096;

    // How many attempts an append with no condition of the caller's makes in all: as many as Azure Blob's.
    private const int AppendAttempts = 5;

    // Readers let writers replace and delete the file they read.
    private const FileShare ReadShare = FileShare.ReadWrite | FileShare.Delete;

    // The permission bits a new file takes: not setuid, setgid or sticky, which writing a file clears.
    private const UnixFileMode Permissions =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private static readonly char[] _separators = ['/', '\\'];

    // The digits of the GUID in a temp file's name, as Guid.ToString("N") writes them.
    private static readonly SearchValues<char> _tempDigits = SearchValues.Create("0123456789abcdef");

    // What Windows refuses in a name besides ':' and the separators, which every system refuses here.
    private static readonly SearchValues<char> _windowsReserved = SearchValues.Create("\"<>|?*");

    // Windows' reserved device names, which name a device in every folder; COM and LPT also take the superscripts.
    private static readonly FrozenSet<string> _windowsDevices = new[] { "CON", "PRN", "AUX", "NUL" }
        .Concat(new[] { "COM", "LPT" }.SelectMany(device => "123456789¹²³".Select(digit => device + digit)))
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // The same set, looked up by a span, so a check allocates nothing.
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> _windowsDeviceLookup = _windowsDevices.GetAlternateLookup<ReadOnlySpan<char>>();

    // Every entry, hidden and system ones included, as 2026 listed them.
    private static readonly EnumerationOptions _listing = new() { AttributesToSkip = 0, IgnoreInaccessible = false };

    // How far past the replaced file's time a write may stamp its own, for file systems that store times coarsely:
    // NTFS, ext4 and APFS keep 100 ns or finer, exFAT 10 ms, HFS+ 1 s, FAT 2 s.
    private static readonly TimeSpan[] _stampSteps =
        [TimeSpan.FromTicks(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

    private readonly string _root;
    private readonly string _rootPrefix;
    private readonly bool _refuseLinks;
    private readonly Lock _watchLock = new();
    private PhysicalFileProvider? _watcher;
    private bool _disposed;

    /// <summary>Creates a provider over a folder.</summary>
    /// <param name="rootPath">The folder: an absolute path, or a path relative to the current directory.</param>
    /// <exception cref="ArgumentException">
    /// The path is empty or not valid, or, on Windows, an extended or device path (<c>\\?\</c>, <c>\\.\</c>) that has
    /// no plain form.
    /// </exception>
    public WriteablePhysicalFileProvider(string rootPath)
        : this(new PhysicalFileProviderOptions { RootDirectoryPath = rootPath })
    {
    }

    /// <summary>
    /// Creates a provider from its options. A relative root is resolved against the current directory here, once, and
    /// the absolute path is written back to <paramref name="options"/>, so its JSON names the same folder wherever it
    /// is read. On Windows, a root in the extended or device form (<c>\\?\C:\files</c>, <c>\\?\UNC\server\share</c>,
    /// <c>\\.\C:\files</c>) is written back in its plain form (<c>C:\files</c>, <c>\\server\share</c>), so every path
    /// under it is checked as a plain path is. The folder need not exist: the first write creates it. Nothing on disk
    /// is touched here.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The root is empty or not a valid path, or, on Windows, an extended or device path that has no plain form: a
    /// volume GUID, a device, or a name the plain form would change (one ending in a dot or a space, <c>.</c> or
    /// <c>..</c>).
    /// </exception>
    public WriteablePhysicalFileProvider(PhysicalFileProviderOptions options) : base(options)
    {
        if (string.IsNullOrEmpty(Options.RootDirectoryPath))
            throw new ArgumentException("The root folder, RootDirectoryPath, is not set.", nameof(options));
        var root = !OperatingSystem.IsWindows() ? Options.RootDirectoryPath : PlainRootOf(Options.RootDirectoryPath)
            ?? throw new ArgumentException(
                "RootDirectoryPath is an extended or device path with no plain form (a volume GUID, a device, or a name the plain form would change: one ending in a dot or a space, '.' or '..'): give the folder as a drive or share path.",
                nameof(options));
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        Options.RootDirectoryPath = _root;
        _rootPrefix = Path.EndsInDirectorySeparator(_root) ? _root : _root + Path.DirectorySeparatorChar;
        _refuseLinks = Options.RefuseLinks;
    }

    // The choices that differ by system, settable so the tests run both on each.
    internal bool IgnoresCase { get; init; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    internal bool RenamesWithoutReplacing { get; init; } = OperatingSystem.IsWindows();

    // Whether a write takes a new name with link(2), atomically and never as an empty file (Linux, macOS); where the
    // system refuses it, or it is off and names are not taken by a rename that does not replace, the name is taken with
    // an exclusive create, then a rename.
    internal bool TakesNamesWithLink { get; init; } = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    // link(2): 0, or the error number. Settable so the tests can refuse it.
    internal Func<string, string, int> Link { get; init; } =
        (existingPath, newPath) => NativeMethods.Link(existingPath, newPath) == 0 ? 0 : Marshal.GetLastPInvokeError();

    // Called with the target as soon as a new name is taken, before the write or move goes on: the tests stage a second
    // writer there.
    internal Action<string>? NameTaken { get; init; }

    // Whether names Windows reads as another name, or cannot hold, are refused (IsSegmentAllowed).
    internal bool RefusesWindowsNames { get; init; } = OperatingSystem.IsWindows();

    // How a link is followed to describe it: by walking the path as the kernel does (Linux, macOS), or by asking the system
    // for the final target (Windows).
    internal bool ResolvesLinksAsTheKernel { get; init; } = !OperatingSystem.IsWindows();

    // Called with the temp file's path as soon as it is created, before anything else touches it: the tests read its mode.
    internal Action<string>? TempCreated { get; init; }

    // Called with the target each time a write has looked at it (a folder, a file there), before IsReplaceable opens it:
    // the tests change the file there.
    internal Action<string>? Probed { get; init; }

    /// <summary>True on Windows and macOS, whose file systems match names without regard to case by default.</summary>
    protected override bool IsCaseInsensitive => IgnoresCase;

    /// <summary>
    /// False: the root is a folder of the local disk, an operating system path such as <c>C:\data</c> or
    /// <c>/srv/../files</c>, which the constructor resolves to a full path; every path under it is checked to stay there.
    /// </summary>
    protected override bool ValidatesRootDirectory => false;

    /// <summary>
    /// True: <see cref="UpdateFileCoreAsync"/> checks the conditions itself. <see cref="UpdateFileOptions.IfNotExists"/>
    /// is atomic; <see cref="UpdateFileOptions.IfMatch"/> is checked just before the rename, which is not.
    /// </summary>
    protected override bool SupportsAtomicPreconditions => true;

    /// <inheritdoc />
    protected override Task<IWriteableFileInfo> GetFileInfoCoreAsync(ResolvedPath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = FullPath(path);
        var file = new FileInfo(fullPath);
        if (file.Exists)
            return Task.FromResult<IWriteableFileInfo>(FileEntry(path.Path, file));
        var folder = new DirectoryInfo(fullPath);
        return Task.FromResult<IWriteableFileInfo>(folder.Exists ? FolderEntry(path.Path, folder) : WriteableFileInfo.Missing(this, path.Path));
    }

    /// <inheritdoc />
    protected override IAsyncEnumerable<IWriteableFileInfo> EnumerateCoreAsync(ResolvedPath path, CancellationToken cancellationToken) =>
        Entries(path, cancellationToken).Select(e => e.Entry).ToAsyncEnumerable();

    /// <summary>
    /// Walks every level, each folder before its contents. A linked folder (a symbolic link or a junction) is listed but
    /// not walked: it can lead outside the root, or back into it without end.
    /// </summary>
    /// <inheritdoc />
    protected override IAsyncEnumerable<IWriteableFileInfo> EnumerateRecursiveCoreAsync(ResolvedPath path, CancellationToken cancellationToken) =>
        EnumerateRecursive(path, cancellationToken).ToAsyncEnumerable();

    /// <inheritdoc />
    protected override Task<bool> FolderExistsCoreAsync(ResolvedPath path, CancellationToken cancellationToken) =>
        Task.FromResult(Directory.Exists(FullPath(path)));

    /// <inheritdoc />
    protected override Task<Stream> ReadFileCoreAsync(ResolvedPath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<Stream>(new FileStream(OpenRead(path, FullPath(path)), FileAccess.Read, BufferSize, isAsync: true));
    }

    /// <summary>
    /// Reads the range through <see cref="RandomAccess"/> on one handle, whose ETag is the one checked against
    /// <paramref name="ifMatch"/>: the bytes come from that version even if the file is replaced while they are read.
    /// </summary>
    /// <inheritdoc />
    protected override Task<Stream> ReadRangeCoreAsync(ResolvedPath path, long offset, long? length, string? ifMatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handle = OpenRead(path, FullPath(path));
        try
        {
            if (ifMatch is not null && ifMatch != VersionOf(handle))
                throw Changed(path, ifMatch);
            var count = Math.Max(0, Math.Min(length ?? long.MaxValue, RandomAccess.GetLength(handle) - offset));
            return Task.FromResult<Stream>(new FileRangeStream(handle, offset, count));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    protected override async Task UpdateFileCoreAsync(ResolvedPath path, Stream contents, UpdateFileOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        var target = FullPath(path);
        if (options.Mode != UpdateFileMode.Append)
        {
            var (replaced, like) = Look(path, target, options);
            CheckPreconditions(path, options, VersionOf(replaced));
            // A folder that takes the name meanwhile is what a write without conditions finds there, not a file.
            Func<IOException, IOException>? taken = options.IfNotExists ? e => Directory.Exists(target) ? IsAFolder(path, e) : AlreadyExists(path, e) : null;
            await CommitAsync(path, target, options.IfMatch, changed: null, taken, like, contents.CopyToAsync, cancellationToken).ConfigureAwait(false);
            return;
        }
        // An append with no condition of the caller's reads the file again after a concurrent change and appends after
        // what is there now, as the base class's append by rewrite does. Only this provider's own signals of a change
        // count, each recorded as it is thrown: a failure of the caller's stream, whatever its type, ends the append.
        var retries = !options.IfNotExists && options.IfMatch is null;
        var spool = new AppendSpool();
        await using (spool.ConfigureAwait(false))
        {
            for (var attempt = 1; ; attempt++)
            {
                IOException? signal = null;
                try
                {
                    await AppendOnceAsync(path, target, contents, options, spool, e => signal = e, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (IOException e) when (retries && ReferenceEquals(e, signal))
                {
                    if (attempt == AppendAttempts)
                        throw new PreconditionFailedException($"The file '{path}' kept changing while it was appended to; nothing was appended after {AppendAttempts} attempts.", e);
                }
            }
        }
    }

    // One attempt of an append, which looks at the path afresh; 'signal' receives the exception that says the file changed
    // since it was looked at, just before it is thrown.
    private async Task AppendOnceAsync(ResolvedPath path, string target, Stream contents, UpdateFileOptions options, AppendSpool spool,
        Action<IOException> signal, CancellationToken cancellationToken)
    {
        FileInfo replaced;
        FileInfo? like;
        try
        {
            (replaced, like) = Look(path, target, options);
        }
        catch (IOException e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            // Deleted between the look and IsReplaceable's open.
            signal(e);
            throw;
        }
        CheckPreconditions(path, options, VersionOf(replaced));
        // The caller's bytes are read here once, before the file is opened, and only after the refusals that need none.
        await spool.FillAsync(path, target, contents, cancellationToken).ConfigureAwait(false);
        if (like is null)
        {
            // An append to a missing file creates it, and fails rather than replace one that appears meanwhile; over a link
            // to nothing, it replaces the link, as an overwrite does.
            Func<IOException, IOException>? taken = options.IfNotExists || !replaced.Exists
                ? e =>
                {
                    // A folder that takes the name meanwhile is what a write without conditions finds there, not a file.
                    if (Directory.Exists(target))
                        return IsAFolder(path, e);
                    var exists = AlreadyExists(path, e);
                    // Read past only once the other writer is done: on Linux and macOS a name taken with an exclusive
                    // create holds an empty file until that writer's content is renamed onto it, which would replace an
                    // append made to it meanwhile. Windows takes a name with its content in it.
                    if (RenamesWithoutReplacing || new FileInfo(target) is not { Exists: true, Length: 0 })
                        signal(exists);
                    return exists;
                }
                : null;
            await CommitAsync(path, target, options.IfMatch, signal, taken, like, spool.CopyFromStartAsync, cancellationToken).ConfigureAwait(false);
            return;
        }
        SafeFileHandle existing;
        try
        {
            existing = OpenRead(path, target);
        }
        catch (FileNotFoundException e)
        {
            // Deleted since the look.
            signal(e);
            throw;
        }
        // The file as this handle opened it, then the new bytes; the rename happens only if the file is still that version.
        var source = new FileStream(existing, FileAccess.Read, BufferSize, isAsync: true);
        await using (source.ConfigureAwait(false))
        {
            var version = VersionOf(existing);
            CheckPreconditions(path, options, version);
            await CommitAsync(path, target, version, signal, taken: null, like, async (output, token) =>
            {
                await source.CopyToAsync(output, token).ConfigureAwait(false);
                // Closed before the rename: Windows does not rename over a file that is open, by this handle or any.
                await source.DisposeAsync().ConfigureAwait(false);
                await spool.CopyFromStartAsync(output, token).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    // The path as a write finds it: refuses a folder there, and a file under IfNotExists; gives the file the write
    // replaces (which may not exist) and the file whose permission bits the new one takes (null for none, or for a link
    // to nothing).
    private (FileInfo Replaced, FileInfo? Like) Look(ResolvedPath path, string target, UpdateFileOptions options)
    {
        if (Directory.Exists(target))
            throw new IOException($"The path '{path}' is a folder.");
        var replaced = new FileInfo(target);
        // Read once: what the write found there, even if it goes before IsReplaceable opens it.
        var exists = replaced.Exists;
        if (options.IfNotExists && exists)
            throw AlreadyExists(path);
        Probed?.Invoke(target);
        // Before anything else opens it: a file the write must not replace is refused, and the new file takes the mode of
        // the one it replaces.
        var like = exists && IsReplaceable(path, target, replaced) ? replaced : null;
        return (replaced, like);
    }

    /// <inheritdoc />
    protected override Task<bool> DeleteCoreAsync(ResolvedPath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = FullPath(path);
        if (File.Exists(fullPath))
            File.Delete(fullPath);
        else if (Directory.Exists(fullPath))
            Directory.Delete(fullPath, recursive: true);
        else
            return Task.FromResult(false);
        return Task.FromResult(true);
    }

    /// <summary>Renames the file; the destination is taken atomically, so an existing one is never replaced.</summary>
    /// <inheritdoc />
    protected override Task MoveCoreAsync(ResolvedPath source, ResolvedPath destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var from = FullPath(source);
        var to = FullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        try
        {
            if (IgnoresCase && string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
                File.Move(from, to, overwrite: false); // A case-only rename: the "destination" is the source itself.
            else
                RenameToNew(from, to, e => new FileExistsException($"The destination '{destination}' already exists.", e));
        }
        catch (FileNotFoundException e)
        {
            throw NotFound(source, e);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Copies through the same atomic create as a write with <see cref="UpdateFileOptions.IfNotExists"/>. On Linux and
    /// macOS the copy takes the source's permission bits exactly, whatever the umask, as
    /// <see cref="File.Copy(string, string)"/> does; they are set as the file is created, before any content is written.
    /// </summary>
    /// <inheritdoc />
    protected override async Task CopyCoreAsync(ResolvedPath source, ResolvedPath destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var from = FullPath(source);
        var input = new FileStream(OpenRead(source, from), FileAccess.Read, BufferSize, isAsync: true);
        await using (input.ConfigureAwait(false))
        {
            // Whatever took the destination meanwhile, a file or a folder, the copy's answer is that it exists.
            await CommitAsync(destination, FullPath(destination), expected: null, changed: null,
                e => new FileExistsException($"The destination '{destination}' already exists.", e),
                new FileInfo(from), input.CopyToAsync, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Watches the root as ASP.NET Core's <see cref="PhysicalFileProvider"/> does, through
    /// <see cref="PhysicalFilesWatcher"/>: the filter is a glob relative to the root, and leading <c>/</c> and <c>\</c>
    /// are ignored, so <c>/a.txt</c> is <c>a.txt</c>. A filter that is null, holds a character no file name can, names
    /// a drive or leaves the root never fires. The polling watcher walks into linked folders, so a change behind a link
    /// can fire a token, even with <see cref="PhysicalFileProviderOptions.RefuseLinks"/>; the event-based one does not.
    /// Dot-prefixed, hidden and system files are not watched, which keeps the provider's own temp files out; a write
    /// fires when its rename puts the new content in place. <c>DOTNET_USE_POLLING_FILE_WATCHER</c> set to <c>1</c> or
    /// <c>true</c> polls instead of relying on file system events. The first call creates the root folder if it is
    /// missing.
    /// </summary>
    /// <inheritdoc />
    protected override IChangeToken WatchCore(string filter)
    {
        lock (_watchLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_watcher is null)
            {
                Directory.CreateDirectory(_root);
                _watcher = new PhysicalFileProvider(_root, ExclusionFilters.Sensitive);
            }
            // PhysicalFileProvider ignores only this system's separators; a backslash separates on every system here.
            return _watcher.Watch(filter?.TrimStart(_separators)!);
        }
    }

    /// <summary>
    /// Deletes the temp files that writes left behind under the root: a write names its temp file <c>.wfp-</c>, 32
    /// lowercase hex digits and <c>.tmp</c>, and removes it when it ends, but a process that dies during a write leaves
    /// it. Listings never show those files; they only take space. Only exactly that name is deleted, so
    /// <c>.wfp-notes.tmp</c>, or a <c>.wfp-rename-…</c> copy that holds a file's content, never is; a file of the
    /// caller's with exactly that name, which listings hide too, is taken for a leftover.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A temp file is deleted when its last write is older than <paramref name="olderThan"/> on the provider's clock,
    /// the system clock unless a test sets another. A write in progress moves its temp file's last write only when it
    /// writes, so choose an age longer than your longest write, plus any lag of a file server's clock behind this
    /// machine's. On Linux and macOS a write whose temp file goes anyway fails with <see cref="IOException"/> and
    /// leaves the file as it was; on Windows the write refuses the delete while it holds the file open, and between
    /// closing it and the rename only an age near zero can take it.
    /// </para>
    /// <para>
    /// Every folder under the root is walked, except a linked folder (a symbolic link or a junction), whatever
    /// <see cref="PhysicalFileProviderOptions.RefuseLinks"/> says, and a folder whose name no path can name, where no
    /// write puts a temp file. Just before each folder is walked, its path from the root is checked for a link, but not
    /// atomically with the walk: a process that can create links inside the root could swap one in and steer the sweep
    /// into a folder elsewhere, where it still deletes only files named exactly like a write's temp file and older than
    /// the age. A folder the process may not list, and a temp file it cannot delete, are skipped, and the sweep goes
    /// on. A root that is there but is not a folder, or cannot be read, throws; a root that is not there yet, or is a
    /// link to nothing (on Linux or macOS, a share not mounted), returns 0.
    /// </para>
    /// </remarks>
    /// <param name="olderThan">
    /// How old a temp file's last write must be; zero takes every one stamped at or before now (a write may stamp its
    /// file ahead of the clock).
    /// </param>
    /// <param name="cancellationToken">Cancels the sweep between folders and between files.</param>
    /// <returns>How many temp files were deleted; 0 when the root does not exist yet.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="olderThan"/> is negative.</exception>
    /// <exception cref="ObjectDisposedException">The provider is disposed.</exception>
    public Task<int> DeleteTempFilesAsync(TimeSpan olderThan, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(olderThan, TimeSpan.Zero);
        Resolve(ProviderPath.Root);
        // A file where the root should be, or a link to one, is a failure, not an empty root: Linux and macOS would list
        // it as missing. A link to nothing is a root that is not there yet, as on Windows.
        if (File.Exists(_root))
        {
            var root = new FileInfo(_root);
            if (root.LinkTarget is null || root.ResolveLinkTarget(returnFinalTarget: true) is { Exists: true })
                throw new IOException("The provider's root is not a folder.");
        }
        var now = TimeProvider.GetUtcNow().UtcDateTime;
        // An age longer than the clock has run takes nothing.
        var cutoff = olderThan.Ticks > now.Ticks ? DateTime.MinValue : now - olderThan;
        var deleted = 0;
        var folders = new Queue<string[]>();
        folders.Enqueue([]);
        while (folders.TryDequeue(out var relative))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                // Checked just before the folder is walked, the whole way from the root: a folder that was found plain
                // and has become a link since, or sits under one, is not walked.
                if (LinkOnTheWay(relative) is not null)
                    continue;
                // Read as it goes, so a folder of any size takes no more memory than one entry; the folder is opened
                // here, so a folder that cannot be listed fails here. A file deleted meanwhile may hide another from
                // this pass; the next sweep finds it.
                entries = new DirectoryInfo(Path.Join([_root, .. relative])).EnumerateFileSystemInfos("*", _listing);
            }
            catch (Exception e) when (relative.Length > 0 ? e is IOException or UnauthorizedAccessException : e is DirectoryNotFoundException)
            {
                // A folder deleted since it was found, or one the process may not list or look into: nothing there for
                // the sweep to reach. The root itself counts as empty only when it is not there yet; one that cannot be
                // read is a failure the caller sees, as from any other member.
                continue;
            }
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry is DirectoryInfo)
                {
                    // A folder no path can name holds nothing the provider wrote.
                    if (IsAddressable(entry.Name))
                        folders.Enqueue([.. relative, entry.Name]);
                    continue;
                }
                if (!IsTempFile(entry.Name))
                    continue;
                try
                {
                    // Read afresh: counted only when it was there to delete (deleting a missing file throws nothing), and
                    // a file the process may not look at is skipped like one it may not delete.
                    entry.Refresh();
                    if (!entry.Exists || entry.LastWriteTimeUtc > cutoff)
                        continue;
                    entry.Delete();
                    deleted++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // In use by a write (Windows), or the system refuses: left for a later sweep.
                }
            }
        }
        return Task.FromResult(deleted);
    }

    /// <summary>
    /// Refuses a <c>:</c> on every system. On Windows, which would read such a name as another one or not at all, also
    /// refuses a name that ends in <c>.</c> or a space (Windows drops them, so <c>a.txt.</c> would name <c>a.txt</c>),
    /// holds <c>"</c>, <c>&lt;</c>, <c>&gt;</c>, <c>|</c>, <c>?</c> or <c>*</c>, or is a reserved device name
    /// (<c>CON</c>, <c>PRN</c>, <c>AUX</c>, <c>NUL</c>, <c>COM1</c> to <c>COM9</c>, <c>LPT1</c> to <c>LPT9</c>, and
    /// <c>COM</c> or <c>LPT</c> followed by <c>¹</c>, <c>²</c> or <c>³</c>), in any case, alone or followed by an
    /// extension, such as <c>con.txt</c>.
    /// </summary>
    /// <param name="part">A single path segment.</param>
    protected override bool IsSegmentAllowed(string part) =>
        part is not null && !part.Contains(':', StringComparison.Ordinal)
        && (!RefusesWindowsNames || IsWindowsName(part));

    // Whether Windows names exactly this file: no trailing dot or space, no reserved character, no device name.
    internal static bool IsWindowsName(string part)
    {
        if (part.EndsWith('.') || part.EndsWith(' ') || part.AsSpan().IndexOfAny(_windowsReserved) >= 0)
            return false;
        // A device name stands for the device whatever extension follows it, and with spaces before that extension.
        var dot = part.IndexOf('.', StringComparison.Ordinal);
        var stem = (dot < 0 ? part.AsSpan() : part.AsSpan(0, dot)).TrimEnd(' ');
        return !_windowsDeviceLookup.Contains(stem);
    }

    // A Windows root in its plain form. An extended path (\\?\C:\files, \\?\UNC\server\share) skips .NET's
    // normalization, and with it the expansion of 8.3 short names that FullPath relies on to refuse them; a device path
    // (\\.\C:\files) is taken the same way. Null for a form with no plain equivalent: a volume GUID or a device, and a
    // path holding a name the plain form would change, as it drops a trailing dot or space and applies '.' and '..'
    // that an extended path names as they are (\\?\C:\data. is not C:\data). Any other path is returned as it is.
    internal static string? PlainRootOf(string root)
    {
        var text = root.Replace('/', '\\');
        if (!text.StartsWith(@"\\?\", StringComparison.Ordinal) && !text.StartsWith(@"\\.\", StringComparison.Ordinal))
            return root;
        var rest = text[4..];
        string names;
        string plain;
        if (rest.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
        {
            names = rest[4..];
            plain = @"\\" + names;
        }
        else if (rest.Length >= 2 && char.IsAsciiLetter(rest[0]) && rest[1] == ':' && (rest.Length == 2 || rest[2] == '\\'))
        {
            names = rest[2..];
            plain = rest.Length == 2 ? rest + @"\" : rest;
        }
        else
        {
            return null;
        }
        foreach (var name in names.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (name.EndsWith('.') || name.EndsWith(' '))
                return null;
        }
        return plain;
    }

    /// <summary>Stops watching.</summary>
    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_watchLock)
            {
                _disposed = true;
                _watcher?.Dispose();
            }
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Stamps a write's last-write time: now, or later than the replaced file's time as the file system stores it, trying
    /// each step past it until the stored time is later. Where none is, the ETag may repeat.
    /// </summary>
    /// <param name="replaced">The replaced file's time, as stored; long ago when there is none.</param>
    /// <param name="now">The current time.</param>
    /// <param name="store">Sets the time on the file and returns it as the file system stored it.</param>
    internal static void Stamp(DateTime replaced, DateTime now, Func<DateTime, DateTime> store)
    {
        foreach (var step in _stampSteps)
        {
            var later = replaced + step;
            if (store(later > now ? later : now) > replaced)
                return;
        }
    }

    // The path on disk; outside the root is refused, although validated segments cannot get there.
    private string FullPath(ResolvedPath path)
    {
        if (path.IsRoot)
            return _root;
        var relative = string.Join(Path.DirectorySeparatorChar, path.RelativeSegments);
        var fullPath = Path.GetFullPath(Path.Join(_rootPrefix, relative));
        if (!fullPath.StartsWith(_rootPrefix, StringComparison.Ordinal))
            throw new ArgumentException($"The path '{path}' resolves outside the provider root.", nameof(path));
        // On Windows, .NET expands an 8.3 short name (REPORT~1.TXT) into the long name it stands for, so a segment that
        // is the short name of another name would reach that name: refused, as a trailing dot is.
        if (RefusesWindowsNames && !fullPath.AsSpan(_rootPrefix.Length).Equals(relative, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"The path '{path}' is the short name of another file or folder.", nameof(path));
        if (_refuseLinks)
            RefuseLinksOn(path);
        return fullPath;
    }

    // With RefuseLinks, refuses a path any of whose entries under the root is a link: one stat per entry, from the root
    // down, stopping at the first that does not exist, since nothing below it can.
    private void RefuseLinksOn(ResolvedPath path)
    {
        if (LinkOnTheWay(path.RelativeSegments) is { } link)
            throw new ArgumentException($"The path '{path}' goes through '{link}', a link, which this provider refuses (RefuseLinks).", nameof(path));
    }

    // The first entry on the way to these segments under the root that is a link, as its path under the root, or null.
    // One stat per entry, from the root down, stopping at the first that is not there, since nothing below it can be.
    // On Linux and macOS it also stops at an entry the process may not look up: the system won't go past it for the
    // member either: a read there finds nothing, and a write is refused. On Windows a process can be let through a
    // folder it may not read, so there that refusal is thrown.
    private string? LinkOnTheWay(IReadOnlyList<string> segments)
    {
        var current = _root;
        for (var i = 0; i < segments.Count; i++)
        {
            current = Path.Join(current, segments[i]);
            var entry = new FileInfo(current);
            try
            {
                // Attributes are -1 when nothing is there; a link reports itself, whatever it leads to.
                if ((int)entry.Attributes == -1)
                    return null;
                if (IsLink(entry))
                    return string.Join('/', segments.Take(i + 1));
            }
            catch (UnauthorizedAccessException) when (!OperatingSystem.IsWindows())
            {
                return null;
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                // Gone between its attributes and its target, which are read apart: nothing is below it.
                return null;
            }
        }
        return null;
    }

    private IEnumerable<(IWriteableFileInfo Entry, FileSystemInfo Info)> Entries(ResolvedPath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var folder = new DirectoryInfo(FullPath(path));
        if (!folder.Exists)
            yield break;
        foreach (var entry in folder.EnumerateFileSystemInfos("*", _listing))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A name a path cannot name again (on Linux, one with ':' or '\', a control character or only dots; on
            // Windows, a trailing dot or space, a reserved character or a device name; anywhere, a percent-encoded form
            // of those) is left out, and never walked into: everything listed can be read back.
            if (!IsAddressable(entry.Name))
                continue;
            // With RefuseLinks a link cannot be read back, so it is not listed either.
            if (_refuseLinks && IsLink(entry))
                continue;
            if (entry is FileInfo file)
            {
                if (!IsTempFile(file.Name))
                    yield return (FileEntry(path.Child(file.Name).Path, file), file);
            }
            else
            {
                yield return (FolderEntry(path.Child(entry.Name).Path, entry), entry);
            }
        }
    }

    private IEnumerable<IWriteableFileInfo> EnumerateRecursive(ResolvedPath path, CancellationToken cancellationToken)
    {
        var folders = new Queue<ResolvedPath>();
        folders.Enqueue(path);
        while (folders.TryDequeue(out var folder))
        {
            foreach (var (entry, info) in Entries(folder, cancellationToken))
            {
                yield return entry;
                if (entry.IsDirectory && !IsLink(info))
                    folders.Enqueue(folder.Child(info.Name));
            }
        }
    }

    // A symbolic link or a junction; other reparse points, such as cloud placeholders, are folders like any other.
    private static bool IsLink(FileSystemInfo info) =>
        (info.Attributes & FileAttributes.ReparsePoint) != 0 && info.LinkTarget is not null;

    // Writes the content to a temp file next to the target, then renames it over the target. 'expected' is the version the
    // target must still have just before the rename (checked, not atomic), and 'changed' receives the exception thrown
    // when it doesn't; 'taken', when set, renames only onto a free name (atomic), and gives the exception for a name that
    // something took; the new file takes the permission bits of 'like', the file it replaces or copies, else the default
    // mode.
    private async Task CommitAsync(ResolvedPath path, string target, string? expected, Action<IOException>? changed, Func<IOException, IOException>? taken,
        FileInfo? like, Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(folder);
        var temp = Path.Join(folder, TempPrefix + Guid.NewGuid().ToString("N") + TempSuffix);
        var create = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.Read,
            BufferSize = BufferSize,
            Options = FileOptions.Asynchronous,
        };
        // The mode must be set as the file is created: created with the default mode (0666 less the umask) and narrowed
        // afterwards, it could be opened in between by another user, who would then read the new content of a private
        // file. The umask can only narrow the mode given here; the exact bits follow on the open handle.
        if (like is not null && !OperatingSystem.IsWindows())
            create.UnixCreateMode = like.UnixFileMode & Permissions;
        var written = false;
        try
        {
            FileStream output;
            try
            {
                output = new FileStream(temp, create);
            }
            catch (UnauthorizedAccessException e)
            {
                // The message would name the temp file.
                throw new UnauthorizedAccessException($"The file '{path}' cannot be written: the process may not create files in its folder.", e);
            }
            await using (output.ConfigureAwait(false))
            {
                TempCreated?.Invoke(temp);
                if (like is not null && !OperatingSystem.IsWindows())
                    File.SetUnixFileMode(output.SafeFileHandle, like.UnixFileMode & Permissions);
                await write(output, cancellationToken).ConfigureAwait(false);
            }
            written = true;
            var current = new FileInfo(target);
            if (expected is not null && VersionOf(current) != expected)
            {
                var exception = Changed(path, expected);
                changed?.Invoke(exception);
                throw exception;
            }
            Stamp(Described(current).LastWriteTimeUtc, TimeProvider.GetUtcNow().UtcDateTime, time =>
            {
                File.SetLastWriteTimeUtc(temp, time);
                return File.GetLastWriteTimeUtc(temp);
            });
            if (taken is not null)
                TakeNewName(temp, target, taken);
            else
                File.Move(temp, target, overwrite: true);
        }
        catch (FileNotFoundException e) when (written && !File.Exists(temp))
        {
            // Something removed the temp file once its content was written, as a sweep of old temp files can on Linux
            // and macOS, where a file being written can be deleted: the target is as it was. The exception would name
            // the temp file, and read as "the file does not exist".
            throw new IOException($"The file '{path}' was not written, and is as it was: its temp file was deleted before the write finished (as {nameof(DeleteTempFilesAsync)} does to one older than its age).", e);
        }
        catch
        {
            DeleteQuietly(temp);
            throw;
        }
    }

    // Refuses, before anything is written, a file the rename must not replace, and says whether the path is a file whose
    // mode the new one takes (false for a link to nothing, which the rename simply replaces):
    // - one the process may not write, as 2026 did and as the rename does on Windows (on Linux and macOS a rename needs
    //   only the folder's permission);
    // - one that is not a regular file. .NET 10 cannot tell a FIFO, a socket or a device from a file without opening it
    //   (SafeFileHandle.Type comes in .NET 11), and opening a FIFO to write waits for a reader, forever. Only an empty
    //   name can be one, so an empty file is opened to read and write, which does not wait, even on a FIFO, and a handle
    //   that cannot seek is not a regular file. A device that seeks, such as /dev/null, passes as an empty file, and an
    //   empty file the process may write but not read is refused.
    private bool IsReplaceable(ResolvedPath path, string target, FileInfo replaced)
    {
        try
        {
            var access = Described(replaced).Length == 0 ? FileAccess.ReadWrite : FileAccess.Write;
            using var probe = new FileStream(target, FileMode.Open, access, ReadShare, bufferSize: 0);
            return probe.CanSeek ? true : throw new IOException($"The path '{path}' is not a regular file: a FIFO, a socket or a device.");
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException && replaced.LinkTarget is not null)
        {
            return false;
        }
    }

    // Gives a write's temp file the name 'target' only if nothing is there. On Linux and macOS a second name, link(2),
    // takes it atomically with the content already in it, and the temp name then goes; once the link is made the write
    // is done, whether the temp name is already gone or its delete is refused (a name that stays is hidden from
    // listings). Where link(2) is refused for any reason but the name being taken (a file system without hard links, a
    // policy, a vanished temp file), or the name it found taken is free again, the name is taken as RenameToNew does.
    private void TakeNewName(string temp, string target, Func<IOException, IOException> taken)
    {
        if (TakesNamesWithLink && !RenamesWithoutReplacing)
        {
            var error = Link(temp, target);
            if (error == 0)
            {
                NameTaken?.Invoke(target);
                DeleteQuietly(temp);
                return;
            }
            if (error == NativeMethods.AlreadyExists && Path.Exists(target))
                throw taken(new IOException("The name is already taken."));
            // The temp file is gone: taking the name as RenameToNew does would put an empty file there for nothing.
            if (error == NativeMethods.NoSuchFile && !File.Exists(temp))
                throw new FileNotFoundException("The write's temp file is gone.", temp);
        }
        RenameToNew(temp, target, taken);
    }

    // Puts 'source' at 'target' only if nothing is there. Windows renames without replacing, atomically. On Linux and
    // macOS, .NET checks the name and then renames, so a file created in between would be replaced: an exclusive create
    // takes the name instead, failing when anything has it, and the rename replaces that empty file.
    private void RenameToNew(string source, string target, Func<IOException, IOException> exists)
    {
        try
        {
            if (RenamesWithoutReplacing)
            {
                File.Move(source, target, overwrite: false);
                return;
            }
            new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None).Dispose();
            NameTaken?.Invoke(target);
        }
        catch (IOException e) when (e is not FileNotFoundException && Path.Exists(target))
        {
            throw exists(e);
        }
        try
        {
            File.Move(source, target, overwrite: true);
        }
        catch
        {
            // Give the name back, unless something has filled it since.
            var placeholder = new FileInfo(target);
            if (placeholder.Exists && placeholder.Length == 0)
                DeleteQuietly(target);
            throw;
        }
    }

    private static string ETag(DateTime lastWriteTimeUtc, long length) =>
        string.Create(CultureInfo.InvariantCulture, $"{lastWriteTimeUtc.Ticks:x}-{length:x}");

    private string? VersionOf(FileInfo file)
    {
        if (!file.Exists)
            return null;
        var described = Described(file);
        return ETag(described.LastWriteTimeUtc, described.Length);
    }

    // What a path names as a file, as reads see it, found without opening it: describing a file never opens it, so a
    // listing cannot wake a FIFO's reader, lose a writer's data, wait on a device or hold a share another process needs.
    // FileInfo describes a symbolic link itself (its length is 0 on Windows, the length of its target's text on Linux,
    // and its time is the link's), so a link is described by the file it leads to: on Windows the target the system
    // resolves (ResolveLinkTarget asks for the final path on a handle without access), on Linux and macOS the path
    // resolved as the kernel does (PhysicalPathOf). A link that leads nowhere (to nothing, in a loop, through too many
    // links) is described as itself. Only a reparse point is asked for its target, a round trip on Windows and SMB.
    private FileInfo Described(FileInfo file)
    {
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) == 0 || file.LinkTarget is null)
            return file;
        try
        {
            var target = ResolvesLinksAsTheKernel
                ? PhysicalPathOf(file.FullName) is { } resolved ? new FileInfo(resolved) : null
                : file.ResolveLinkTarget(returnFinalTarget: true) as FileInfo;
            return target is { Exists: true } ? target : file;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return file;
        }
    }

    // How many symbolic links a path may pass through before the kernel gives up on it (ELOOP): 32 on macOS, 40 on Linux.
    internal static int MaxLinks { get; } = OperatingSystem.IsMacOS() ? 32 : 40;

    // The path the kernel opens for an absolute path, without opening it: walked a component at a time from its root,
    // each symbolic link replaced by its target, a relative target resolved against the part already resolved, and '..'
    // applied to that part, not to the text as written. Null where the kernel stops: past MaxLinks links (ELOOP), or at
    // a component before the last that is missing (ENOENT) or not a folder (ENOTDIR), a trailing separator or '.'
    // included, so 'missing/../x', 'file.txt/../x' and 'file.txt/' lead nowhere.
    internal static string? PhysicalPathOf(string path)
    {
        char[] separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
        // A trailing separator asks for a folder, as '.' after the last name does.
        IEnumerable<string> PartsOf(string text) =>
            text.Split(separators, StringSplitOptions.RemoveEmptyEntries).Concat(text.Length > 0 && separators.Contains(text[^1]) ? ["."] : []);

        var root = Path.GetPathRoot(path) ?? "";
        var pending = new Stack<string>(PartsOf(path[root.Length..]).Reverse());
        var resolved = root;
        var links = 0;
        while (pending.TryPop(out var part))
        {
            if (part == ".")
                continue;
            if (part == "..")
            {
                resolved = Path.GetDirectoryName(resolved) ?? resolved;
                continue;
            }
            var next = Path.Join(resolved, part);
            var entry = new FileInfo(next);
            // Attributes are -1 when nothing is there; a link reports itself, whatever it leads to.
            if ((int)entry.Attributes != -1 && (entry.Attributes & FileAttributes.ReparsePoint) != 0 && entry.LinkTarget is { } target)
            {
                if (++links > MaxLinks)
                    return null;
                var targetRoot = Path.GetPathRoot(target);
                if (!string.IsNullOrEmpty(targetRoot))
                    resolved = targetRoot;
                foreach (var targetPart in PartsOf(target[(targetRoot?.Length ?? 0)..]).Reverse())
                    pending.Push(targetPart);
                continue;
            }
            // The kernel looks the next component up in this one, so it must be a folder.
            if (pending.Count > 0 && !Directory.Exists(next))
                return null;
            resolved = next;
        }
        return resolved;
    }

    private static string VersionOf(SafeFileHandle handle) => ETag(File.GetLastWriteTimeUtc(handle), RandomAccess.GetLength(handle));

    // Exactly the name a write gives its temp file, ".wfp-" and a GUID's 32 lowercase hex digits and ".tmp": any other
    // name, ".wfp-notes.tmp" included, is a file of the caller's and is listed.
    internal static bool IsTempFile(string name) =>
        name.Length == TempPrefix.Length + 32 + TempSuffix.Length
        && name.StartsWith(TempPrefix, StringComparison.Ordinal)
        && name.EndsWith(TempSuffix, StringComparison.Ordinal)
        && name.AsSpan(TempPrefix.Length, 32).IndexOfAnyExcept(_tempDigits) < 0;

    // The physical path is the entry's own, under the root, even when it is a link to a file elsewhere.
    private WriteableFileInfo FileEntry(ProviderPath path, FileInfo file)
    {
        var described = Described(file);
        return new(this, path, true, false, described.Length, described.LastWriteTimeUtc, file.FullName, ETag(described.LastWriteTimeUtc, described.Length));
    }

    private WriteableFileInfo FolderEntry(ProviderPath path, FileSystemInfo folder) =>
        new(this, path, true, true, -1, folder.LastWriteTimeUtc, folder.FullName);

    // Opens a file to read, shared with writers and deleters; a missing file or a folder is FileNotFoundException.
    private static SafeFileHandle OpenRead(ResolvedPath path, string fullPath)
    {
        try
        {
            return File.OpenHandle(fullPath, FileMode.Open, FileAccess.Read, ReadShare, FileOptions.Asynchronous);
        }
        catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && !File.Exists(fullPath))
        {
            throw NotFound(path, e);
        }
    }

    // IfNotExists is checked before, on the stat, and by the atomic create.
    private static void CheckPreconditions(ResolvedPath path, UpdateFileOptions options, string? version)
    {
        if (options.IfMatch is not null && options.IfMatch != version)
            throw Changed(path, options.IfMatch);
    }

    private static void DeleteQuietly(string fullPath)
    {
        try
        {
            File.Delete(fullPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The failure that brought us here is the one to report; a temp file left behind stays out of listings.
        }
    }

    // A folder that took the name during the write is what a write without conditions finds there, not a file.
    private static IOException IsAFolder(ResolvedPath path, Exception innerException) =>
        new($"The path '{path}' is a folder.", innerException);

    // The caller's bytes of an append, copied once into a hidden temp file next to the target, before the file is opened:
    // a retry appends them again without reading the caller's stream, they never leave the root, and a crash leaves a
    // temp file DeleteTempFilesAsync finds. On Linux and macOS only the process's user may read it, from its creation.
    private sealed class AppendSpool : IAsyncDisposable
    {
        private FileStream? _file;

        public async Task FillAsync(ResolvedPath path, string target, Stream contents, CancellationToken cancellationToken)
        {
            if (_file is not null)
                return;
            var folder = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(folder);
            var create = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                BufferSize = BufferSize,
                Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose,
            };
            if (!OperatingSystem.IsWindows())
                create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            FileStream file;
            try
            {
                file = new FileStream(Path.Join(folder, TempPrefix + Guid.NewGuid().ToString("N") + TempSuffix), create);
            }
            catch (UnauthorizedAccessException e)
            {
                // The message would name the temp file.
                throw new UnauthorizedAccessException($"The file '{path}' cannot be written: the process may not create files in its folder.", e);
            }
            try
            {
                await contents.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await file.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            _file = file;
        }

        public async Task CopyFromStartAsync(Stream output, CancellationToken cancellationToken)
        {
            _file!.Position = 0;
            await _file.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => _file?.DisposeAsync() ?? default;
    }

    private static FileAlreadyExistsException AlreadyExists(ResolvedPath path, Exception? innerException = null) =>
        new($"The file '{path}' already exists.", innerException);

    private static PreconditionFailedException Changed(ResolvedPath path, string expected) =>
        new($"The file '{path}' changed: its version is no longer '{expected}'.");
}
