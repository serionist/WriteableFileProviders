using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace WriteableFileProviders.Testing;

/// <summary>The options of <see cref="InMemoryFileProvider"/>.</summary>
public sealed class InMemoryFileProviderOptions : FileProviderOptions
{
    /// <summary>The store to use: providers with the same name share their files.</summary>
    public string StoreName { get; set; } = "default";
}

/// <summary>
/// The reference provider: files in memory, with real folders, ETags, content types and signed URLs, built on the base
/// class's defaults (range reads, seekable reads, copy, move, append by rewrite). It passes the conformance suite, and
/// shows what each <c>...CoreAsync</c> member owes the contract.
/// </summary>
/// <param name="options">The options.</param>
public sealed class InMemoryFileProvider(InMemoryFileProviderOptions options) : WriteableFileProvider<InMemoryFileProviderOptions>(options)
{
    private static readonly ConcurrentDictionary<string, Store> _stores = new(StringComparer.Ordinal);

    private Store Files => _stores.GetOrAdd(Options.StoreName, _ => new Store());

    /// <summary>Deletes every file of a store.</summary>
    /// <param name="storeName">The store.</param>
    public static void ClearStore(string storeName) => _stores.TryRemove(storeName, out _);

    /// <inheritdoc />
    protected override bool SupportsSignedUrl(SignedUrlAccess permission) => true;

    /// <inheritdoc />
    protected override Task<IWriteableFileInfo> GetFileInfoCoreAsync(ResolvedPath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = Key(path);
        lock (Files)
        {
            if (Files.Entries.TryGetValue(key, out var file))
                return Task.FromResult<IWriteableFileInfo>(Info(path.Path, file));
            if (Files.Folders.Contains(key))
                return Task.FromResult<IWriteableFileInfo>(WriteableFileInfo.Folder(this, path.Path));
        }
        return Task.FromResult<IWriteableFileInfo>(WriteableFileInfo.Missing(this, path.Path));
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<IWriteableFileInfo> EnumerateCoreAsync(ResolvedPath path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = Key(path);
        List<IWriteableFileInfo> entries;
        lock (Files)
        {
            entries =
            [
                .. Files.Folders.Where(f => IsChild(key, f)).Order(StringComparer.Ordinal).Select(f => WriteableFileInfo.Folder(this, path.Child(Name(f)).Path)),
                .. Files.Entries.Where(e => IsChild(key, e.Key)).OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => Info(path.Child(Name(e.Key)).Path, e.Value)),
            ];
        }
        foreach (var entry in entries)
        {
            await Task.Yield();
            yield return entry;
        }
    }

    /// <inheritdoc />
    protected override Task<Stream> ReadFileCoreAsync(ResolvedPath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Files)
        {
            return Files.Entries.TryGetValue(Key(path), out var file)
                ? Task.FromResult<Stream>(new MemoryStream(file.Data, writable: false))
                : throw NotFound(path);
        }
    }

    /// <inheritdoc />
    protected override async Task UpdateFileCoreAsync(ResolvedPath path, Stream contents, UpdateFileOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (options.Mode == UpdateFileMode.Append)
        {
            await AppendByRewriteAsync(path, contents, options, cancellationToken).ConfigureAwait(false);
            return;
        }
        // Read the whole content first: a source that fails leaves the old file as it was.
        using var buffer = new MemoryStream();
        await contents.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var key = Key(path);
        lock (Files)
        {
            if (Files.Folders.Contains(key))
                throw new IOException($"The path '{path}' is a folder.");
            var segments = path.AbsoluteSegments;
            for (var i = 1; i < segments.Length; i++)
                Files.Folders.Add(string.Join('/', segments[..i]));
            Files.Entries[key] = new Entry(buffer.ToArray(), TimeProvider.GetUtcNow(), Guid.NewGuid().ToString("N"), options.ContentType);
        }
    }

    /// <inheritdoc />
    protected override Task<bool> DeleteCoreAsync(ResolvedPath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = Key(path);
        lock (Files)
        {
            if (Files.Entries.Remove(key))
                return Task.FromResult(true);
            if (!Files.Folders.Remove(key))
                return Task.FromResult(false);
            var prefix = key + "/";
            Files.Folders.RemoveWhere(f => f.StartsWith(prefix, StringComparison.Ordinal));
            foreach (var file in Files.Entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                Files.Entries.Remove(file);
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    protected override Task<SignedUrl> GetSignedUrlCoreAsync(ResolvedPath path, SignedUrlOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var expires = TimeProvider.GetUtcNow().Add(options.Validity);
        var url = new Uri($"memory://{Uri.EscapeDataString(Options.StoreName)}/{Key(path)}?access={options.Permission}&expires={expires.ToUnixTimeSeconds()}");
        return Task.FromResult(new SignedUrl
        {
            Url = url,
            Method = options.Permission == SignedUrlAccess.Read ? "GET" : "PUT",
            ExpiresOn = expires,
        });
    }

    private static string Key(ResolvedPath path) => string.Join('/', path.AbsoluteSegments);

    private static string Name(string key) => key[(key.LastIndexOf('/') + 1)..];

    private static bool IsChild(string folderKey, string key) =>
        folderKey.Length == 0
            ? !key.Contains('/', StringComparison.Ordinal)
            : key.Length > folderKey.Length + 1 && key.StartsWith(folderKey + "/", StringComparison.Ordinal)
              && !key.AsSpan(folderKey.Length + 1).Contains('/');

    private WriteableFileInfo Info(ProviderPath path, Entry file) =>
        new(this, path, true, false, file.Data.Length, file.Modified, etag: file.ETag, contentType: file.ContentType);

    private sealed class Store
    {
        public Dictionary<string, Entry> Entries { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Folders { get; } = new(StringComparer.Ordinal);
    }

    private sealed record Entry(byte[] Data, DateTimeOffset Modified, string ETag, string? ContentType);
}
