using System.Runtime.CompilerServices;
using Microsoft.Extensions.Primitives;

namespace WriteableFileProviders.Common.Tests;

/// <summary>A constant-class-like type: converts from a string, carries it in Value (as SDK constant classes do).</summary>
public sealed class Tier
{
    private Tier(string value) => Value = value;

    public string Value { get; }

    public static Tier Hot { get; } = new("HOT");

    public static implicit operator Tier(string value) => new(value);

    public override string ToString() => Value;
}

/// <summary>A type that converts from a string through a public constructor only.</summary>
public sealed class Region(string name)
{
    public string Name { get; } = name;

    public override string ToString() => Name;
}

public sealed class StubOptions : FileProviderOptions
{
    public string? Name { get; set; }

    public Tier? Tier { get; set; }

    public Region? Region { get; set; }

    public bool Flag { get; set; }

    public bool? Maybe { get; set; }

    public int Count { get; set; }

    public long? Size { get; set; }

    public DayOfWeek Day { get; set; }

    public Uri? Endpoint { get; set; }

    public TimeSpan Timeout { get; set; }

    public Guid Id { get; set; }

    public DateTimeOffset When { get; set; }

    public decimal Price { get; set; }

    public string[]? Tags { get; set; }

    public string ReadOnlyValue { get; } = "computed";
}

/// <summary>
/// A provider with every hook the tests want to see: a store error that means "not found", atomic preconditions, Watch,
/// and a missing core for paths named "missing".
/// </summary>
public sealed class StubProvider(StubOptions options) : WriteableFileProvider<StubOptions>(options)
{
    public bool Disposed { get; private set; }

    public UpdateFileOptions? LastUpdate { get; private set; }

    public bool AtomicPreconditions { get; set; }

    public bool SignedUrls { get; set; }

    public string? ETag { get; set; } = "e1";

    public string? ContentType { get; set; }

    /// <summary>A character the provider's own segment rule refuses, as Physical and SharePoint refuse ':'.</summary>
    public char? RefusedCharacter { get; set; }

    protected override bool SupportsAtomicPreconditions => AtomicPreconditions;

    protected override bool SupportsSignedUrl(SignedUrlAccess access) => SignedUrls;

    protected override bool IsSegmentAllowed(string part) => RefusedCharacter is not { } refused || !part.Contains(refused, StringComparison.Ordinal);

    public ProviderPath ResolveForTest(ProviderPath path) => Resolve(path).Path;

    public bool Addressable(string name) => IsAddressable(name);

    public Task AppendByRewrite(string path, Stream contents, UpdateFileOptions? options = null) =>
        AppendByRewriteAsync(Resolve(path), contents, options ?? UpdateFileOptions.Append, default);

    public static Task<FileStream> Spool(Stream source) => SpoolToTempFileAsync(source, default);

    public Task DeleteFile(string path) => DeleteFileCoreAsync(Resolve(path), default);

    public Task<bool> FolderExists(string path) => FolderExistsCoreAsync(Resolve(path), default);

    protected override Task<IWriteableFileInfo> GetFileInfoCoreAsync(ResolvedPath path, CancellationToken cancellationToken) =>
        Task.FromResult<IWriteableFileInfo>(path.Name == "file.txt"
            ? new WriteableFileInfo(this, path.Path, true, false, 4, DateTimeOffset.UtcNow, etag: ETag, contentType: ContentType)
            : WriteableFileInfo.Missing(this, path.Path));

    protected override async IAsyncEnumerable<IWriteableFileInfo> EnumerateCoreAsync(ResolvedPath path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield break;
    }

    protected override Task<Stream> ReadFileCoreAsync(ResolvedPath path, CancellationToken cancellationToken) =>
        path.Name == "file.txt"
            ? Task.FromResult<Stream>(new MemoryStream("data"u8.ToArray()))
            : throw new KeyNotFoundException("The store has no such key.");

    protected override Task UpdateFileCoreAsync(ResolvedPath path, Stream contents, UpdateFileOptions options, CancellationToken cancellationToken)
    {
        LastUpdate = options;
        return Task.CompletedTask;
    }

    protected override Task<bool> DeleteCoreAsync(ResolvedPath path, CancellationToken cancellationToken) => Task.FromResult(path.Name == "file.txt");

    protected override bool IsNotFound(Exception exception) => exception is KeyNotFoundException;

    protected override Task<SignedUrl> GetSignedUrlCoreAsync(ResolvedPath path, SignedUrlOptions options, CancellationToken cancellationToken) =>
        Task.FromResult(new SignedUrl { Url = new Uri("https://stub.test/" + path) });

    protected override IChangeToken WatchCore(string filter) => new CancellationChangeToken(CancellationToken.None);

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}

/// <summary>A provider that is not built on WriteableFileProvider: the serializer cannot know its options.</summary>
public sealed class ForeignProvider : IWriteableFileProvider
{
    public FileProviderOptions Options { get; } = new();

    public Task<IWriteableFileInfo> GetFileInfoAsync(ProviderPath path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Microsoft.Extensions.FileProviders.IDirectoryContents> GetDirectoryContentsAsync(ProviderPath path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IWriteableFileInfo> EnumerateAsync(ProviderPath path, bool recursive = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Stream> ReadFileAsync(ProviderPath path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Stream> ReadRangeAsync(ProviderPath path, long offset, long? length, ReadOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Stream> OpenReadSeekableAsync(ProviderPath path, SeekableReadOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task UpdateFileAsync(ProviderPath path, Stream contents, UpdateFileOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<bool> DeleteAsync(ProviderPath path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task MoveAsync(ProviderPath source, ProviderPath destination, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task CopyAsync(ProviderPath source, ProviderPath destination, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<LocalFileStream> CreateLocalFileStreamAsync(ProviderPath path, bool readOnly, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SignedUrl> GetSignedUrlAsync(ProviderPath path, SignedUrlOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Microsoft.Extensions.FileProviders.IFileInfo GetFileInfo(string subpath) => throw new NotSupportedException();
    public Microsoft.Extensions.FileProviders.IDirectoryContents GetDirectoryContents(string subpath) => throw new NotSupportedException();
    public IChangeToken Watch(string filter) => throw new NotSupportedException();
    public void Dispose() { }
    public ValueTask DisposeAsync() => default;
}

/// <summary>A provider without the single-options constructor the serializer needs.</summary>
public sealed class NoOptionsConstructorProvider(StubOptions options, string extra) : WriteableFileProvider<StubOptions>(options)
{
    public string Extra { get; } = extra;

    protected override Task<IWriteableFileInfo> GetFileInfoCoreAsync(ResolvedPath path, CancellationToken cancellationToken) => throw new NotSupportedException();
    protected override IAsyncEnumerable<IWriteableFileInfo> EnumerateCoreAsync(ResolvedPath path, CancellationToken cancellationToken) => throw new NotSupportedException();
    protected override Task<Stream> ReadFileCoreAsync(ResolvedPath path, CancellationToken cancellationToken) => throw new NotSupportedException();
    protected override Task UpdateFileCoreAsync(ResolvedPath path, Stream contents, UpdateFileOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();
    protected override Task<bool> DeleteCoreAsync(ResolvedPath path, CancellationToken cancellationToken) => throw new NotSupportedException();
}

/// <summary>A provider whose constructor refuses its options.</summary>
public sealed class RefusingProvider : WriteableFileProvider<StubOptions>
{
    public RefusingProvider(StubOptions options) : base(options) =>
        throw new ArgumentException("Name is required.", nameof(options));

    protected override Task<IWriteableFileInfo> GetFileInfoCoreAsync(ResolvedPath path, CancellationToken cancellationToken) => throw new NotSupportedException();
    protected override IAsyncEnumerable<IWriteableFileInfo> EnumerateCoreAsync(ResolvedPath path, CancellationToken cancellationToken) => throw new NotSupportedException();
    protected override Task<Stream> ReadFileCoreAsync(ResolvedPath path, CancellationToken cancellationToken) => throw new NotSupportedException();
    protected override Task UpdateFileCoreAsync(ResolvedPath path, Stream contents, UpdateFileOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();
    protected override Task<bool> DeleteCoreAsync(ResolvedPath path, CancellationToken cancellationToken) => throw new NotSupportedException();
}
