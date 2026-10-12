using System.Collections.Concurrent;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace WriteableFileProviders.AspNetCore.Tests;

/// <summary>
/// A provider that hands every call to another one, but signs no URLs natively (as Physical), counts the reads the
/// gateway makes and checks it disposes every stream they return, and can act as a store that keeps no version
/// (<see cref="CanPin"/>), fails, or loses the file.
/// </summary>
/// <param name="inner">The provider that does the work.</param>
internal sealed class DelegatingFileProvider(IWriteableFileProvider inner) : IWriteableFileProvider
{
    private int _openStreams;
    private int _fileReads;

    /// <summary>When false, every file reports no ETag, as a store that keeps no version a read could pin.</summary>
    public bool CanPin { get; init; } = true;

    public int SignedUrlCalls { get; private set; }

    /// <summary>The ranges the gateway read, as (offset, length).</summary>
    public ConcurrentQueue<(long Offset, long? Length)> RangeReads { get; } = new();

    /// <summary>How many whole-file reads the gateway made.</summary>
    public int FileReads => _fileReads;

    /// <summary>How many streams this provider returned that are not disposed yet.</summary>
    public int OpenStreams => Volatile.Read(ref _openStreams);

    /// <summary>When set, <see cref="GetFileInfoAsync"/> throws it, as a store that fails.</summary>
    public Exception? GetFileInfoFailure { get; set; }

    /// <summary>When set, <see cref="GetFileInfoAsync"/> reports every path missing, as after a concurrent delete.</summary>
    public bool ReportMissing { get; set; }

    /// <summary>Runs after <see cref="GetFileInfoAsync"/> has read the file's info, as a concurrent writer would.</summary>
    public Func<Task>? AfterGetFileInfo { get; set; }

    /// <summary>When set, the reads of a file's bytes throw it, as a store that came to refuse the path
    /// meanwhile.</summary>
    public Exception? ReadFailure { get; set; }

    public FileProviderOptions Options => inner.Options;

    public IFileInfo GetFileInfo(string subpath) => inner.GetFileInfo(subpath);

    public IDirectoryContents GetDirectoryContents(string subpath) => inner.GetDirectoryContents(subpath);

    public IChangeToken Watch(string filter) => inner.Watch(filter);

    public async Task<IWriteableFileInfo> GetFileInfoAsync(ProviderPath path, CancellationToken cancellationToken = default)
    {
        if (GetFileInfoFailure is { } failure)
            throw failure;
        if (ReportMissing)
            return WriteableFileInfo.Missing(this, path);
        var info = await inner.GetFileInfoAsync(path, cancellationToken);
        if (AfterGetFileInfo is { } after)
            await after();
        return CanPin || !info.Exists || info.IsDirectory
            ? info
            : new WriteableFileInfo(this, info.Path, true, false, info.Length, info.LastModified, contentType: info.ContentType);
    }

    public Task<IDirectoryContents> GetDirectoryContentsAsync(ProviderPath path, CancellationToken cancellationToken = default) =>
        inner.GetDirectoryContentsAsync(path, cancellationToken);

    public IAsyncEnumerable<IWriteableFileInfo> EnumerateAsync(ProviderPath path, bool recursive = false, CancellationToken cancellationToken = default) =>
        inner.EnumerateAsync(path, recursive, cancellationToken);

    public async Task<Stream> ReadFileAsync(ProviderPath path, CancellationToken cancellationToken = default)
    {
        if (ReadFailure is { } failure)
            throw failure;
        Interlocked.Increment(ref _fileReads);
        return Track(await inner.ReadFileAsync(path, cancellationToken));
    }

    public async Task<Stream> ReadRangeAsync(ProviderPath path, long offset, long? length, ReadOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (ReadFailure is { } failure)
            throw failure;
        RangeReads.Enqueue((offset, length));
        return Track(await inner.ReadRangeAsync(path, offset, length, options, cancellationToken));
    }

    public Task<Stream> OpenReadSeekableAsync(ProviderPath path, SeekableReadOptions? options = null, CancellationToken cancellationToken = default) =>
        inner.OpenReadSeekableAsync(path, options, cancellationToken);

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
        throw new NotSupportedException("This provider signs no URLs natively.");
    }

    /// <summary>Waits until every stream this provider returned is disposed: the server disposes them as a response ends.</summary>
    public async Task<bool> AllStreamsDisposedAsync()
    {
        for (var i = 0; i < 100 && OpenStreams > 0; i++)
            await Task.Delay(20);
        return OpenStreams == 0;
    }

    public void Dispose() => inner.Dispose();

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    private TrackedStream Track(Stream stream)
    {
        Interlocked.Increment(ref _openStreams);
        return new TrackedStream(stream, () => Interlocked.Decrement(ref _openStreams));
    }

    /// <summary>A stream that reports its first dispose.</summary>
    private sealed class TrackedStream(Stream inner, Action disposed) : Stream
    {
        private int _disposed;

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                disposed();
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                disposed();
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
