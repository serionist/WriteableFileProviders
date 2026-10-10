namespace WriteableFileProviders.Common.Tests.Ambiguous.Second;

/// <summary>One of two providers that share a class name, for the serializer's ambiguity check.</summary>
public sealed class DuplicateProvider(StubOptions options) : WriteableFileProvider<StubOptions>(options)
{
    protected override Task<IWriteableFileInfo> GetFileInfoCoreAsync(ResolvedPath path, CancellationToken cancellationToken) => throw new NotSupportedException();
    protected override IAsyncEnumerable<IWriteableFileInfo> EnumerateCoreAsync(ResolvedPath path, CancellationToken cancellationToken) => throw new NotSupportedException();
    protected override Task<Stream> ReadFileCoreAsync(ResolvedPath path, CancellationToken cancellationToken) => throw new NotSupportedException();
    protected override Task UpdateFileCoreAsync(ResolvedPath path, Stream contents, UpdateFileOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();
    protected override Task<bool> DeleteCoreAsync(ResolvedPath path, CancellationToken cancellationToken) => throw new NotSupportedException();
}
