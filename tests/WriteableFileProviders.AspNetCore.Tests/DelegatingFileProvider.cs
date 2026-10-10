using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace WriteableFileProviders.AspNetCore.Tests;

/// <summary>
/// A provider that hands every call to another one, except that it signs no URLs natively (as Physical) unless told to,
/// and, when <see cref="CanPin"/> is false, keeps no version a seekable read could pin (as a store without ETags).
/// </summary>
/// <param name="inner">The provider that does the work.</param>
internal sealed class DelegatingFileProvider(IWriteableFileProvider inner) : IWriteableFileProvider
{
    public bool NativeUrls { get; init; }

    public bool CanPin { get; init; } = true;

    public int SignedUrlCalls { get; private set; }

    /// <summary>When set, <see cref="GetFileInfoAsync"/> throws it, as a store that fails.</summary>
    public Exception? GetFileInfoFailure { get; set; }

    /// <summary>When set, <see cref="GetFileInfoAsync"/> reports every path missing, as after a concurrent delete.</summary>
    public bool ReportMissing { get; set; }

    public FileProviderOptions Options => inner.Options;

    public IFileInfo GetFileInfo(string subpath) => inner.GetFileInfo(subpath);

    public IDirectoryContents GetDirectoryContents(string subpath) => inner.GetDirectoryContents(subpath);

    public IChangeToken Watch(string filter) => inner.Watch(filter);

    public Task<IWriteableFileInfo> GetFileInfoAsync(ProviderPath path, CancellationToken cancellationToken = default)
    {
        if (GetFileInfoFailure is { } failure)
            return Task.FromException<IWriteableFileInfo>(failure);
        return ReportMissing
            ? Task.FromResult<IWriteableFileInfo>(WriteableFileInfo.Missing(this, path))
            : inner.GetFileInfoAsync(path, cancellationToken);
    }

    public Task<IDirectoryContents> GetDirectoryContentsAsync(ProviderPath path, CancellationToken cancellationToken = default) =>
        inner.GetDirectoryContentsAsync(path, cancellationToken);

    public IAsyncEnumerable<IWriteableFileInfo> EnumerateAsync(ProviderPath path, bool recursive = false, CancellationToken cancellationToken = default) =>
        inner.EnumerateAsync(path, recursive, cancellationToken);

    public Task<Stream> ReadFileAsync(ProviderPath path, CancellationToken cancellationToken = default) =>
        inner.ReadFileAsync(path, cancellationToken);

    public Task<Stream> ReadRangeAsync(ProviderPath path, long offset, long? length, ReadOptions? options = null, CancellationToken cancellationToken = default) =>
        inner.ReadRangeAsync(path, offset, length, options, cancellationToken);

    public async Task<Stream> OpenReadSeekableAsync(ProviderPath path, SeekableReadOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (CanPin || options?.AllowModifications == true)
            return await inner.OpenReadSeekableAsync(path, options, cancellationToken);
        var info = await inner.GetFileInfoAsync(path, cancellationToken);
        throw info.Exists && !info.IsDirectory
            ? new NotSupportedException("This store keeps no version to pin.")
            : new FileNotFoundException("Missing.", path.ToString());
    }

    public Task UpdateFileAsync(ProviderPath path, Stream contents, UpdateFileOptions? options = null, CancellationToken cancellationToken = default) =>
        inner.UpdateFileAsync(path, contents, options, cancellationToken);

    public Task<bool> DeleteAsync(ProviderPath path, CancellationToken cancellationToken = default) => inner.DeleteAsync(path, cancellationToken);

    public Task MoveAsync(ProviderPath source, ProviderPath destination, CancellationToken cancellationToken = default) =>
        inner.MoveAsync(source, destination, cancellationToken);

    public Task CopyAsync(ProviderPath source, ProviderPath destination, CancellationToken cancellationToken = default) =>
        inner.CopyAsync(source, destination, cancellationToken);

    public Task<LocalFileStream> CreateLocalFileStreamAsync(ProviderPath path, bool readOnly, CancellationToken cancellationToken = default) =>
        inner.CreateLocalFileStreamAsync(path, readOnly, cancellationToken);

    public Task<SignedUrl> GetSignedUrlAsync(ProviderPath path, SignedUrlOptions options, CancellationToken cancellationToken = default)
    {
        SignedUrlCalls++;
        return NativeUrls
            ? inner.GetSignedUrlAsync(path, options, cancellationToken)
            : throw new NotSupportedException("This provider signs no URLs natively.");
    }

    public void Dispose() => inner.Dispose();

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
