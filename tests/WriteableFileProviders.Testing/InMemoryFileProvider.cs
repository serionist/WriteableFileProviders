using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace WriteableFileProviders.Testing;

/// <summary>The options of <see cref="InMemoryFileProvider"/>.</summary>
public sealed class InMemoryFileProviderOptions : FileProviderOptions
{
    /// <summary>The store to use: providers with the same name share their files.</summary>
    public string StoreName { get; set; } = "default";

    /// <summary>
    /// Whether names that differ only in case are the same name, as on Box, SharePoint and Windows. Set by the provider
    /// that first opens the store.
    /// </summary>
    public bool CaseInsensitive { get; set; }

    /// <summary>
    /// A lifetime the store gives every signed URL whatever was asked, as Box and SharePoint do; null to honour the
    /// validity asked.
    /// </summary>
    public TimeSpan? FixedSignedUrlLifetime { get; set; }
}

/// <summary>
/// The reference provider: files in memory, with real folders, ETags, content types and signed URLs (with their download
/// file name and content type), built on the base class's defaults (range reads, seekable reads, copy, move, append by
/// rewrite). It passes the conformance suite, and shows what each <c>...CoreAsync</c> member owes the contract.
/// </summary>
/// <param name="options">The options.</param>
public sealed class InMemoryFileProvider(InMemoryFileProviderOptions options) : WriteableFileProvider<InMemoryFileProviderOptions>(options)
{
    private static readonly ConcurrentDictionary<string, Store> _stores = new(StringComparer.Ordinal);

    private Store Files => _stores.GetOrAdd(Options.StoreName, _ => new Store(Options.CaseInsensitive));

    /// <summary>Deletes every file of a store.</summary>
    /// <param name="storeName">The store.</param>
    public static void ClearStore(string storeName) => _stores.TryRemove(storeName, out _);

    /// <summary>
    /// Stores a file under the provider's root without the path rules, with its folders, as another client of a real
    /// store could: names the provider refuses, which its listings must leave out.
    /// </summary>
    /// <param name="names">The file's names under the root, one per level; none holds a <c>/</c>.</param>
    /// <param name="content">The content.</param>
    public void StoreRaw(IReadOnlyList<string> names, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(names);
        // The root the provider was built with, which a later change to the options does not move.
        string[] segments = [.. Resolve(ProviderPath.Root).AbsoluteSegments, .. names];
        lock (Files)
        {
            for (var i = 1; i < segments.Length; i++)
                Files.Folders.Add(string.Join('/', segments[..i]));
            Files.Entries[string.Join('/', segments)] = new Entry(content, TimeProvider.GetUtcNow(), Guid.NewGuid().ToString("N"), null);
        }
    }

    /// <summary>
    /// When set, every call to the store throws it, as a store or its SDK might (a network failure, an answer it cannot
    /// read, a timeout): how the error-policy tests see what reaches the caller.
    /// </summary>
    public Exception? Failure { get; set; }

    /// <inheritdoc />
    protected override bool IsCaseInsensitive => Files.Comparison == StringComparison.OrdinalIgnoreCase;

    /// <inheritdoc />
    protected override bool SupportsSignedUrl(SignedUrlAccess access) => true;

    /// <inheritdoc />
    protected override bool SupportsSignedUrlContentHeaders(SignedUrlAccess access) => true;

    /// <inheritdoc />
    protected override Task<IWriteableFileInfo> GetFileInfoCoreAsync(ResolvedPath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfFailing();
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
        ThrowIfFailing();
        var key = Key(path);
        List<IWriteableFileInfo> entries;
        lock (Files)
        {
            entries =
            [
                .. Files.Folders.Where(f => IsChild(key, f, Files.Comparison)).Order(StringComparer.Ordinal).Select(f => WriteableFileInfo.Folder(this, path.Child(Name(f)).Path)),
                .. Files.Entries.Where(e => IsChild(key, e.Key, Files.Comparison)).OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => Info(path.Child(Name(e.Key)).Path, e.Value)),
            ];
        }
        foreach (var entry in entries)
        {
            await Task.Yield();
            // A name the path rules refuse cannot be read back, so it is never listed or walked into.
            if (IsAddressable(entry.Name))
                yield return entry;
        }
    }

    /// <inheritdoc />
    protected override Task<Stream> ReadFileCoreAsync(ResolvedPath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfFailing();
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
        ThrowIfFailing();
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
            // The store checks the conditions again under its lock, as a real store does on the write: the base class's
            // check before this call is not atomic, so writers racing past it are settled here.
            Files.Entries.TryGetValue(key, out var current);
            if (options.IfNotExists && current is not null)
                throw new FileAlreadyExistsException($"The file '{path}' already exists.");
            if (options.IfMatch is { } ifMatch && current?.ETag != ifMatch)
                throw new PreconditionFailedException($"The file '{path}' changed: its version is no longer '{ifMatch}'.");
            var segments = path.AbsoluteSegments;
            // Folders are real here, as on a disk: a file cannot hold one.
            for (var i = 1; i < segments.Length; i++)
            {
                if (Files.Entries.ContainsKey(string.Join('/', segments[..i])))
                    throw new IOException($"The path '{path}' is under a file.");
            }
            for (var i = 1; i < segments.Length; i++)
                Files.Folders.Add(string.Join('/', segments[..i]));
            Files.Entries[key] = new Entry(buffer.ToArray(), TimeProvider.GetUtcNow(), Guid.NewGuid().ToString("N"), options.ContentType);
        }
    }

    /// <inheritdoc />
    protected override Task<bool> DeleteCoreAsync(ResolvedPath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfFailing();
        var key = Key(path);
        lock (Files)
        {
            if (Files.Entries.Remove(key))
                return Task.FromResult(true);
            if (!Files.Folders.Remove(key))
                return Task.FromResult(false);
            var prefix = key + "/";
            Files.Folders.RemoveWhere(f => f.StartsWith(prefix, Files.Comparison));
            foreach (var file in Files.Entries.Keys.Where(k => k.StartsWith(prefix, Files.Comparison)).ToList())
                Files.Entries.Remove(file);
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    protected override Task<SignedUrl> GetSignedUrlCoreAsync(ResolvedPath path, SignedUrlOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfFailing();
        var lifetime = options.Validity;
        if (Options.FixedSignedUrlLifetime is { } fixedLifetime)
        {
            // A URL never outlives what was asked, and lives shorter only when the caller accepts it.
            if (options.Validity < fixedLifetime)
                throw new NotSupportedException($"The store's URLs live {fixedLifetime}, longer than the {options.Validity} asked.");
            if (!options.AllowShorterValidity)
                throw new NotSupportedException($"The store's URLs live {fixedLifetime}, shorter than the {options.Validity} asked.");
            lifetime = fixedLifetime;
        }
        var expires = TimeProvider.GetUtcNow().Add(lifetime);
        var query = $"access={options.Access}&expires={expires.ToUnixTimeSeconds()}";
        if (options.DownloadFileName is not null)
            query += "&filename=" + Uri.EscapeDataString(options.DownloadFileName);
        if (options.ContentType is not null)
            query += "&contentType=" + Uri.EscapeDataString(options.ContentType);
        return Task.FromResult(new SignedUrl
        {
            Url = new Uri($"memory://{Uri.EscapeDataString(Options.StoreName)}/{Key(path)}?{query}"),
            Method = options.Access == SignedUrlAccess.Read ? "GET" : "PUT",
            ExpiresOn = expires,
        });
    }

    private static string Key(ResolvedPath path) => string.Join('/', path.AbsoluteSegments);

    private void ThrowIfFailing()
    {
        if (Failure is { } failure)
            throw failure;
    }

    private static string Name(string key) => key[(key.LastIndexOf('/') + 1)..];

    private static bool IsChild(string folderKey, string key, StringComparison comparison) =>
        folderKey.Length == 0
            ? !key.Contains('/', StringComparison.Ordinal)
            : key.Length > folderKey.Length + 1 && key.StartsWith(folderKey + "/", comparison)
              && !key.AsSpan(folderKey.Length + 1).Contains('/');

    private WriteableFileInfo Info(ProviderPath path, Entry file) =>
        new(this, path, true, false, file.Data.Length, file.Modified, etag: file.ETag, contentType: file.ContentType);

    private sealed class Store(bool caseInsensitive)
    {
        public StringComparison Comparison { get; } = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        public Dictionary<string, Entry> Entries { get; } = new(caseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        public HashSet<string> Folders { get; } = new(caseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }

    private sealed record Entry(byte[] Data, DateTimeOffset Modified, string ETag, string? ContentType);
}
