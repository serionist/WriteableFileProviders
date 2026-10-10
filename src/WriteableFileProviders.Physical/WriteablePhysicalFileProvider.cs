using System.Globalization;
using Microsoft.Extensions.FileProviders.Physical;
using Microsoft.Extensions.Primitives;
using Microsoft.Win32.SafeHandles;

namespace WriteableFileProviders.Physical;

/// <summary>A writeable file provider over a folder on the local disk.</summary>
/// <remarks>
/// <para>
/// <b>Writes are atomic.</b> An overwrite, an append, a copy and a local stream's commit write a temp file named
/// <c>.wfp-&lt;guid&gt;.tmp</c> in the target's folder, then rename it over the target: the file has its old content or
/// its new content, never a part. A write that fails removes its temp file; listings never show one. An append copies the
/// file and the new bytes into the temp file. A replaced file keeps its Unix permissions, and its temp file is never
/// readable by more users than the file it replaces; on Windows it takes the permissions the folder gives a new file.
/// </para>
/// <para>
/// <b>Versions.</b> A file's <see cref="IWriteableFileInfo.ETag"/> is made from its last-write time and its length. Each
/// write through the provider sets a last-write time later than the one it replaces, so it changes the ETag; a change by
/// another program that keeps the length within the file system's time resolution may not.
/// <see cref="UpdateFileOptions.IfMatch"/> is checked just before the rename, which is not atomic: another process can
/// change the file between the check and the rename. <see cref="UpdateFileOptions.IfNotExists"/> is atomic, and so is the
/// destination of a move or a copy: the name is taken with an exclusive create, which fails when anything is there, and a
/// rename then fills it, so the file exists empty for that moment. An append that finds the file changed since it read it
/// throws <see cref="PreconditionFailedException"/>, with the same window as <see cref="UpdateFileOptions.IfMatch"/>.
/// </para>
/// <para>
/// <b>Reads</b> share the file with writers and deleters, and keep the content they opened. A delete goes ahead while a
/// file is read. So does a write on Linux and macOS; on Windows, which does not rename over a file that is open, a write
/// to a file that is being read fails with <see cref="UnauthorizedAccessException"/> and the file keeps its content. A
/// range read checks <see cref="ReadOptions.IfMatch"/> on the handle it reads through; a seekable read re-checks the ETag
/// on each fetch.
/// </para>
/// <para>
/// <b>Names</b> are matched without regard to case on Windows and macOS, so a move between names that differ only in
/// case renames the file, and with regard to case elsewhere. A <c>:</c> is not allowed in a name. Containment in the root
/// is checked on the path: symbolic links and junctions inside the root are followed.
/// </para>
/// <para>
/// <b>Not native:</b> signed URLs (WriteableFileProviders.AspNetCore issues gateway URLs) and content types (a given
/// <see cref="UpdateFileOptions.ContentType"/> is not kept).
/// </para>
/// </remarks>
public class WriteablePhysicalFileProvider : WriteableFileProvider<PhysicalFileProviderOptions>
{
    private const string TempPrefix = ".wfp-";
    private const string TempSuffix = ".tmp";
    private const int BufferSize = 4096;

    // Readers let writers replace and delete the file they read.
    private const FileShare ReadShare = FileShare.ReadWrite | FileShare.Delete;

    // Every entry, hidden and system ones included, as 2026 listed them.
    private static readonly EnumerationOptions _listing = new() { AttributesToSkip = 0, IgnoreInaccessible = false };

    private readonly string _root;
    private readonly string _rootPrefix;
    private readonly Lock _watchLock = new();
    private PhysicalFilesWatcher? _watcher;
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

    /// <summary>True on Windows and macOS, whose file systems match names without regard to case by default.</summary>
    protected override bool IsCaseInsensitive => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

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
        Enumerate(path, cancellationToken).ToAsyncEnumerable();

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
        if (options.Mode == UpdateFileMode.Append && TryOpenRead(path, target) is { } existing)
        {
            // The file as this handle opened it, then the new bytes; the rename happens only if the file is still that version.
            var source = new FileStream(existing, FileAccess.Read, BufferSize, isAsync: true);
            await using (source.ConfigureAwait(false))
            {
                var version = VersionOf(existing);
                CheckPreconditions(path, options, version);
                await CommitAsync(path, target, version, mustBeNew: false, async (output, token) =>
                {
                    await source.CopyToAsync(output, token).ConfigureAwait(false);
                    // Closed before the rename: Windows does not rename over a file that is open, by this handle or any.
                    await source.DisposeAsync().ConfigureAwait(false);
                    await contents.CopyToAsync(output, token).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);
            }
            return;
        }
        CheckPreconditions(path, options, VersionOf(new FileInfo(target)));
        // An append to a missing file creates it, and fails rather than replace one that appears meanwhile.
        var mustBeNew = options.IfNotExists || options.Mode == UpdateFileMode.Append;
        await CommitAsync(path, target, options.IfMatch, mustBeNew, contents.CopyToAsync, cancellationToken).ConfigureAwait(false);
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
            if (IsCaseInsensitive && string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
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

    /// <summary>Copies through the same atomic create as a write with <see cref="UpdateFileOptions.IfNotExists"/>.</summary>
    /// <inheritdoc />
    protected override async Task CopyCoreAsync(ResolvedPath source, ResolvedPath destination, CancellationToken cancellationToken)
    {
        try
        {
            await base.CopyCoreAsync(source, destination, cancellationToken).ConfigureAwait(false);
        }
        catch (PreconditionFailedException e)
        {
            throw new FileExistsException($"The destination '{destination}' already exists.", e);
        }
    }

    /// <summary>
    /// Watches the root through <see cref="PhysicalFilesWatcher"/>, as <c>PhysicalFileProvider</c> does: the filter is a
    /// glob relative to the root, and an absolute filter or one that leaves the root never fires. Dot-prefixed, hidden and
    /// system files are not watched, which keeps the provider's own temp files out; a write fires the token when its
    /// rename puts the new content in place. The first call creates the root folder if it is missing.
    /// </summary>
    /// <inheritdoc />
    protected override IChangeToken WatchCore(string filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        lock (_watchLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_watcher is null)
            {
                Directory.CreateDirectory(_root);
                _watcher = new PhysicalFilesWatcher(_rootPrefix, new FileSystemWatcher(_root), pollForChanges: false, ExclusionFilters.Sensitive);
            }
            return _watcher.CreateFileChangeToken(filter);
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

    private IEnumerable<IWriteableFileInfo> Enumerate(ResolvedPath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var folder = new DirectoryInfo(FullPath(path));
        if (!folder.Exists)
            yield break;
        foreach (var entry in folder.EnumerateFileSystemInfos("*", _listing))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is FileInfo file)
            {
                if (!IsTempFile(file.Name))
                    yield return FileEntry(path.Child(file.Name).Path, file);
            }
            else
            {
                yield return FolderEntry(path.Child(entry.Name).Path, entry);
            }
        }
    }

    // Writes the content to a temp file next to the target, then renames it over the target. 'expected' is the version the
    // target must still have just before the rename (checked, not atomic); 'mustBeNew' renames only onto a free name (atomic).
    private async Task CommitAsync(ResolvedPath path, string target, string? expected, bool mustBeNew,
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
        // While it is written, the new content is readable by no more users than the file it replaces (the umask can only
        // narrow the mode); the exact mode follows before the rename.
        var replaced = new FileInfo(target);
        if (replaced.Exists && !OperatingSystem.IsWindows())
            create.UnixCreateMode = replaced.UnixFileMode;
        try
        {
            var output = new FileStream(temp, create);
            await using (output.ConfigureAwait(false))
                await write(output, cancellationToken).ConfigureAwait(false);
            var current = new FileInfo(target);
            if (expected is not null && VersionOf(current) != expected)
                throw Changed(path, expected);
            File.SetLastWriteTimeUtc(temp, NextWriteTime(current.LastWriteTimeUtc));
            if (current.Exists && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, current.UnixFileMode);
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

    // The current time, or a tick after the replaced file's when the clock has not passed it: every write changes the ETag.
    private DateTime NextWriteTime(DateTime replaced)
    {
        var now = TimeProvider.GetUtcNow().UtcDateTime;
        return replaced < now ? now : replaced.AddTicks(1);
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

    private static SafeFileHandle? TryOpenRead(ResolvedPath path, string fullPath)
    {
        try
        {
            return OpenRead(path, fullPath);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static void CheckPreconditions(ResolvedPath path, UpdateFileOptions options, string? version)
    {
        if (options.IfNotExists && version is not null)
            throw AlreadyExists(path);
        if (options.IfMatch is not null && options.IfMatch != version)
            throw Changed(path, options.IfMatch);
    }

    // Puts 'source' at 'target' only if nothing is there. An exclusive create takes the name, failing when anything has it,
    // and the rename replaces that empty file. File.Move without overwrite would not do: on Linux and macOS, .NET checks the
    // name and then renames, and a file created in between is replaced.
    private static void RenameToNew(string source, string target, Func<IOException, IOException> exists)
    {
        try
        {
            new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None).Dispose();
        }
        catch (IOException e) when (Path.Exists(target))
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
