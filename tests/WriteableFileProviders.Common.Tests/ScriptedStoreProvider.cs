using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace WriteableFileProviders.Common.Tests;

/// <summary>
/// A store of text files whose reads and writes a test scripts, attempt by attempt, for the base class's append by
/// rewrite: a hook before each write sees its options and may change the store, as a concurrent writer would; a hook
/// before each read of a file may do the same, or throw. The store checks the conditions as it writes (atomically), and
/// its own "not found" is <see cref="KeyNotFoundException"/>, which <see cref="IsNotFound"/> recognises.
/// </summary>
public sealed class ScriptedStoreProvider() : WriteableFileProvider<StubOptions>(new StubOptions())
{
    private readonly Dictionary<string, (string Text, string ETag)> _files = new(StringComparer.Ordinal);
    private int _version;

    /// <summary>The options of every write that reached the store, in order.</summary>
    public List<UpdateFileOptions> Writes { get; } = [];

    /// <summary>Called with the write's options and its number (from 1) before the store checks them.</summary>
    public Action<UpdateFileOptions, int>? BeforeWrite { get; set; }

    /// <summary>Called with the read's number (from 1) before the store reads the file.</summary>
    public Action<int>? BeforeRead { get; set; }

    private int Reads { get; set; }

    protected override bool SupportsAtomicPreconditions => true;

    public void Put(string name, string text) =>
        _files[name] = (text, "v" + (++_version).ToString(CultureInfo.InvariantCulture));

    public void Remove(string name) => _files.Remove(name);

    public string? Text(string name) => _files.TryGetValue(name, out var file) ? file.Text : null;

    public string? ETagOf(string name) => _files.TryGetValue(name, out var file) ? file.ETag : null;

    public Task Append(string path, Stream contents, UpdateFileOptions? options = null) =>
        AppendByRewriteAsync(Resolve(path), contents, options ?? UpdateFileOptions.Append, default);

    protected override Task<IWriteableFileInfo> GetFileInfoCoreAsync(ResolvedPath path, CancellationToken cancellationToken) =>
        Task.FromResult<IWriteableFileInfo>(_files.TryGetValue(path.Name, out var file)
            ? new WriteableFileInfo(this, path.Path, true, false, file.Text.Length, DateTimeOffset.UtcNow, etag: file.ETag)
            : WriteableFileInfo.Missing(this, path.Path));

    protected override async IAsyncEnumerable<IWriteableFileInfo> EnumerateCoreAsync(ResolvedPath path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield break;
    }

    protected override Task<Stream> ReadFileCoreAsync(ResolvedPath path, CancellationToken cancellationToken)
    {
        BeforeRead?.Invoke(++Reads);
        return _files.TryGetValue(path.Name, out var file)
            ? Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(file.Text)))
            : throw new KeyNotFoundException("The store has no such key.");
    }

    protected override async Task UpdateFileCoreAsync(ResolvedPath path, Stream contents, UpdateFileOptions options, CancellationToken cancellationToken)
    {
        Writes.Add(options);
        BeforeWrite?.Invoke(options, Writes.Count);
        var exists = _files.TryGetValue(path.Name, out var current);
        if (options.IfNotExists && exists)
            throw new FileAlreadyExistsException($"The file '{path}' already exists.");
        if (options.IfMatch is not null && (!exists || current.ETag != options.IfMatch))
            throw new PreconditionFailedException($"The file '{path}' changed.");
        using var reader = new StreamReader(contents, leaveOpen: true);
        Put(path.Name, await reader.ReadToEndAsync(cancellationToken));
    }

    protected override Task<bool> DeleteCoreAsync(ResolvedPath path, CancellationToken cancellationToken) =>
        Task.FromResult(_files.Remove(path.Name));

    protected override bool IsNotFound(Exception exception) => exception is KeyNotFoundException;
}
