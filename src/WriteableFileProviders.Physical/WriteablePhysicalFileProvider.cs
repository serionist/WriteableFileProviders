using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
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
/// into the temp file.
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
/// atomic: another process can change the file between the check and the rename. An append that finds the file changed
/// since it read it throws <see cref="PreconditionFailedException"/>, with the same window.
/// </para>
/// <para>
/// <b>New names are taken atomically:</b> <see cref="UpdateFileOptions.IfNotExists"/>, an append to a missing file, and
/// the destination of a move or a copy never replace a file that is there. Windows renames without replacing. Linux and
/// macOS, where .NET checks the name and then renames, take the name with an exclusive create and rename onto it, so
/// the file exists empty for that moment: a watcher sees it, and a process that dies there leaves an empty file.
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
/// that keeps them. Listings leave out the files and folders whose names a path cannot name
/// (those names on Windows; on Linux and macOS, a name holding <c>:</c>, <c>\</c> or a control character, or only dots
/// and spaces; on any system, a percent-encoded form of those, such as <c>%2e%2e</c>), and never walk into such a
/// folder, so everything listed can be read back. Containment in the root is checked on the path: symbolic links and
/// junctions inside the root are followed, except that a recursive listing lists a linked folder without walking into
/// it.
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
    private readonly Lock _watchLock = new();
    private PhysicalFileProvider? _watcher;
    private bool _disposed;

    /// <summary>Creates a provider over a folder.</summary>
    /// <param name="rootPath">The folder: an absolute path, or a path relative to the current directory.</param>
    /// <exception cref="ArgumentException">
    /// The path is empty or not valid, or, on Windows, an extended or device path (<c>\\?\</c>, <c>\\.\</c>) that has
    /// no plain form of at most 259 characters.
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
    /// The root is empty or not a valid path, or, on Windows, an extended or device path that has no plain form (a
    /// volume GUID, a device) or whose plain form is longer than 259 characters.
    /// </exception>
    public WriteablePhysicalFileProvider(PhysicalFileProviderOptions options) : base(options)
    {
        if (string.IsNullOrEmpty(Options.RootDirectoryPath))
            throw new ArgumentException("The root folder, RootDirectoryPath, is not set.", nameof(options));
        var root = !OperatingSystem.IsWindows() ? Options.RootDirectoryPath : PlainRootOf(Options.RootDirectoryPath)
            ?? throw new ArgumentException(
                "RootDirectoryPath is an extended or device path with no plain form of at most 259 characters (a volume GUID, a device, or a longer path): give the folder as a drive or share path.",
                nameof(options));
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        Options.RootDirectoryPath = _root;
        _rootPrefix = Path.EndsInDirectorySeparator(_root) ? _root : _root + Path.DirectorySeparatorChar;
    }

    // The choices that differ by system, settable so the tests run both on each.
    internal bool IgnoresCase { get; init; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    internal bool RenamesWithoutReplacing { get; init; } = OperatingSystem.IsWindows();

    // Whether names Windows reads as another name, or cannot hold, are refused (IsSegmentAllowed).
    internal bool RefusesWindowsNames { get; init; } = OperatingSystem.IsWindows();

    // How a link is followed to describe it: by walking the path as the kernel does (Linux, macOS), or by asking the system
    // for the final target (Windows).
    internal bool ResolvesLinksAsTheKernel { get; init; } = !OperatingSystem.IsWindows();

    internal TimeProvider Clock
    {
        init => TimeProvider = value;
    }

    // Called with the temp file's path as soon as it is created, before anything else touches it: the tests read its mode.
    internal Action<string>? TempCreated { get; init; }

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
        if (Directory.Exists(target))
            throw new IOException($"The path '{path}' is a folder.");
        var replaced = new FileInfo(target);
        if (options.IfNotExists && replaced.Exists)
            throw AlreadyExists(path);
        // Before anything else opens it: a file the write must not replace is refused, and the new file takes the mode of
        // the one it replaces.
        var like = replaced.Exists && IsReplaceable(path, target, replaced) ? replaced : null;
        if (options.Mode == UpdateFileMode.Append && like is not null)
        {
            // A file deleted since the probe is reported as missing.
            var existing = OpenRead(path, target);
            // The file as this handle opened it, then the new bytes; the rename happens only if the file is still that version.
            var source = new FileStream(existing, FileAccess.Read, BufferSize, isAsync: true);
            await using (source.ConfigureAwait(false))
            {
                var version = VersionOf(existing);
                CheckPreconditions(path, options, version);
                await CommitAsync(path, target, version, mustBeNew: false, like, async (output, token) =>
                {
                    await source.CopyToAsync(output, token).ConfigureAwait(false);
                    // Closed before the rename: Windows does not rename over a file that is open, by this handle or any.
                    await source.DisposeAsync().ConfigureAwait(false);
                    await contents.CopyToAsync(output, token).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);
            }
            return;
        }
        CheckPreconditions(path, options, VersionOf(replaced));
        // An append to a missing file creates it, and fails rather than replace one that appears meanwhile; over a link to
        // nothing, it replaces the link, as an overwrite does.
        var mustBeNew = options.IfNotExists || (options.Mode == UpdateFileMode.Append && !replaced.Exists);
        await CommitAsync(path, target, options.IfMatch, mustBeNew, like, contents.CopyToAsync, cancellationToken).ConfigureAwait(false);
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
            try
            {
                await CommitAsync(destination, FullPath(destination), expected: null, mustBeNew: true, new FileInfo(from), input.CopyToAsync, cancellationToken).ConfigureAwait(false);
            }
            catch (PreconditionFailedException e)
            {
                throw new FileExistsException($"The destination '{destination}' already exists.", e);
            }
        }
    }

    /// <summary>
    /// Watches the root as ASP.NET Core's <see cref="PhysicalFileProvider"/> does, through <see cref="PhysicalFilesWatcher"/>:
    /// the filter is a glob relative to the root, and leading <c>/</c> and <c>\</c> are ignored, so <c>/a.txt</c> is
    /// <c>a.txt</c>. A filter that is null, holds a character no file name can, names a drive or leaves the root never
    /// fires. Dot-prefixed, hidden and system files are not watched, which keeps the provider's own temp files out; a
    /// write fires when its rename puts the new content in place. <c>DOTNET_USE_POLLING_FILE_WATCHER</c> set to
    /// <c>1</c> or <c>true</c> polls instead of relying on file system events. The first call creates the root folder if
    /// it is missing.
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
    // (\\.\C:\files) is taken the same way. A form with no plain equivalent (a volume GUID, a device), or one too long
    // for a plain path (MAX_PATH, 260 characters with its terminator), is null. Any other path is returned as it is.
    internal static string? PlainRootOf(string root)
    {
        const int MaxPlainLength = 259;
        var text = root.Replace('/', '\\');
        if (!text.StartsWith(@"\\?\", StringComparison.Ordinal) && !text.StartsWith(@"\\.\", StringComparison.Ordinal))
            return root;
        var rest = text[4..];
        string plain;
        if (rest.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
            plain = @"\\" + rest[4..];
        else if (rest.Length >= 2 && char.IsAsciiLetter(rest[0]) && rest[1] == ':' && (rest.Length == 2 || rest[2] == '\\'))
            plain = rest.Length == 2 ? rest + @"\" : rest;
        else
            return null;
        return plain.Length <= MaxPlainLength ? plain : null;
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
        return fullPath;
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
    // target must still have just before the rename (checked, not atomic); 'mustBeNew' renames only onto a free name
    // (atomic); the new file takes the permission bits of 'like', the file it replaces or copies, else the default mode.
    private async Task CommitAsync(ResolvedPath path, string target, string? expected, bool mustBeNew, FileInfo? like,
        Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken)
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
            var current = new FileInfo(target);
            if (expected is not null && VersionOf(current) != expected)
                throw Changed(path, expected);
            Stamp(Described(current).LastWriteTimeUtc, TimeProvider.GetUtcNow().UtcDateTime, time =>
            {
                File.SetLastWriteTimeUtc(temp, time);
                return File.GetLastWriteTimeUtc(temp);
            });
            if (mustBeNew)
                RenameToNew(temp, target, e => AlreadyExists(path, e));
            else
                File.Move(temp, target, overwrite: true);
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

    private static PreconditionFailedException AlreadyExists(ResolvedPath path, Exception? innerException = null) =>
        new($"The file '{path}' already exists.", innerException);

    private static PreconditionFailedException Changed(ResolvedPath path, string expected) =>
        new($"The file '{path}' changed: its version is no longer '{expected}'.");
}
