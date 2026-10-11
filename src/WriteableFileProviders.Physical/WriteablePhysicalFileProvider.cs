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
/// <c>.wfp-&lt;guid&gt;.tmp</c> in the target's folder, then rename it over the target: the file has its old content or
/// its new content, never a part. A write that fails removes its temp file; listings never show one. An append copies
/// the file and the new bytes into the temp file.
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
/// symbolic link at the path, a link to nothing included, is replaced by a file, its target left as it was.
/// </para>
/// <para>
/// <b>Versions.</b> A file's <see cref="IWriteableFileInfo.ETag"/> is made from its last-write time and its length.
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
/// case renames the file, and with regard to case elsewhere. A <c>:</c> is not allowed in a name. Listings leave out
/// the files and folders whose names a path cannot name (on Linux and macOS, a name holding <c>:</c>, <c>\</c> or a
/// control character, or only dots and spaces; on any system, a percent-encoded form of those, such as
/// <c>%2e%2e</c>), and never walk into such a folder, so everything listed can be read back. Containment in the root is
/// checked on the path: symbolic links and junctions inside the root are followed, except that a recursive listing lists
/// a linked folder without walking into it.
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
    /// <exception cref="ArgumentException">The path is empty or not valid.</exception>
    public WriteablePhysicalFileProvider(string rootPath)
        : this(new PhysicalFileProviderOptions { RootDirectoryPath = rootPath })
    {
    }

    /// <summary>
    /// Creates a provider from its options. A relative root is resolved against the current directory here, once, and the
    /// absolute path is written back to <paramref name="options"/>, so its JSON names the same folder wherever it is read.
    /// The folder need not exist: the first write creates it. Nothing on disk is touched here.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The root is empty or not a valid path.</exception>
    public WriteablePhysicalFileProvider(PhysicalFileProviderOptions options) : base(options)
    {
        if (string.IsNullOrEmpty(Options.RootDirectoryPath))
            throw new ArgumentException("The root folder, RootDirectoryPath, is not set.", nameof(options));
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Options.RootDirectoryPath));
        Options.RootDirectoryPath = _root;
        _rootPrefix = Path.EndsInDirectorySeparator(_root) ? _root : _root + Path.DirectorySeparatorChar;
    }

    // The choices that differ by system, settable so the tests run both on each.
    internal bool IgnoresCase { get; init; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    internal bool RenamesWithoutReplacing { get; init; } = OperatingSystem.IsWindows();

    internal TimeProvider Clock
    {
        init => TimeProvider = value;
    }

    // Called with the temp file's path as soon as it is created, before anything else touches it: the tests read its mode.
    internal Action<string>? TempCreated { get; init; }

    /// <summary>True on Windows and macOS, whose file systems match names without regard to case by default.</summary>
    protected override bool IsCaseInsensitive => IgnoresCase;

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

    /// <inheritdoc />
    protected override bool IsSegmentAllowed(string part) => !part.Contains(':', StringComparison.Ordinal);

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
        var fullPath = Path.GetFullPath(Path.Join(_rootPrefix, string.Join(Path.DirectorySeparatorChar, path.RelativeSegments)));
        return fullPath.StartsWith(_rootPrefix, StringComparison.Ordinal)
            ? fullPath
            : throw new ArgumentException($"The path '{path}' resolves outside the provider root.", nameof(path));
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
            // A name a path cannot name again (on Linux, one with ':' or '\', a control character or only dots; anywhere,
            // a percent-encoded form of those) is left out, and never walked into: everything listed can be read back.
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

    // A listed name a path can name again: one segment that passes the same checks as a caller's path.
    private bool IsAddressable(string name)
    {
        try
        {
            return Resolve(new ProviderPath(name)).RelativeSegments.Length == 1;
        }
        catch (ArgumentException)
        {
            return false;
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
            Stamp(current.LastWriteTimeUtc, TimeProvider.GetUtcNow().UtcDateTime, time =>
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
    private static bool IsReplaceable(ResolvedPath path, string target, FileInfo replaced)
    {
        try
        {
            var access = replaced.Length == 0 ? FileAccess.ReadWrite : FileAccess.Write;
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

    private static string? VersionOf(FileInfo file) => file.Exists ? ETag(file.LastWriteTimeUtc, file.Length) : null;

    private static string VersionOf(SafeFileHandle handle) => ETag(File.GetLastWriteTimeUtc(handle), RandomAccess.GetLength(handle));

    private static bool IsTempFile(string name) =>
        name.StartsWith(TempPrefix, StringComparison.Ordinal) && name.EndsWith(TempSuffix, StringComparison.Ordinal);

    private WriteableFileInfo FileEntry(ProviderPath path, FileInfo file) =>
        new(this, path, true, false, file.Length, file.LastWriteTimeUtc, file.FullName, ETag(file.LastWriteTimeUtc, file.Length));

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
