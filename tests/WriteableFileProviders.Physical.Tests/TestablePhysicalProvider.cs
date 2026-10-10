using System.Reflection;

namespace WriteableFileProviders.Physical.Tests;

/// <summary>
/// The provider with its protected members in reach: the core members called directly, past the base class's checks,
/// reach the races the public API only meets under concurrency.
/// </summary>
/// <param name="root">The root folder.</param>
internal sealed class TestablePhysicalProvider(string root) : WriteablePhysicalFileProvider(root)
{
    /// <summary>Overrides whether names are matched without regard to case; null keeps the system's answer.</summary>
    public bool? CaseInsensitive { get; init; }

    /// <summary>The clock the provider stamps writes with.</summary>
    public TimeProvider Clock
    {
        set => TimeProvider = value;
    }

    protected override bool IsCaseInsensitive => CaseInsensitive ?? base.IsCaseInsensitive;

    public ResolvedPath Resolved(ProviderPath path) => Resolve(path);

    /// <summary>A path the base class would refuse, such as one with "..": only the provider's own check stands.</summary>
    public ResolvedPath Unvalidated(params string[] relativeSegments) =>
        (ResolvedPath)typeof(ResolvedPath).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single()
            .Invoke([Options.RootDirectorySegments, relativeSegments]);

    public Task<IWriteableFileInfo> GetFileInfoCore(ResolvedPath path) => GetFileInfoCoreAsync(path, default);

    public Task<Stream> ReadCore(ResolvedPath path) => ReadFileCoreAsync(path, default);

    public Task<Stream> ReadRangeCore(ResolvedPath path) => ReadRangeCoreAsync(path, 0, null, null, default);

    public Task UpdateCore(ResolvedPath path, Stream contents, UpdateFileOptions options) => UpdateFileCoreAsync(path, contents, options, default);

    public Task<bool> DeleteCore(ResolvedPath path) => DeleteCoreAsync(path, default);

    public Task<bool> FolderExistsCore(ResolvedPath path) => FolderExistsCoreAsync(path, default);

    public async Task<int> ListCore(ResolvedPath path)
    {
        var count = 0;
        await foreach (var _ in EnumerateCoreAsync(path, default))
            count++;
        return count;
    }

    public Task MoveCore(ProviderPath source, ProviderPath destination) => MoveCoreAsync(Resolve(source), Resolve(destination), default);

    public Task MoveCore(ResolvedPath source, ResolvedPath destination) => MoveCoreAsync(source, destination, default);

    public Task CopyCore(ProviderPath source, ProviderPath destination) => CopyCoreAsync(Resolve(source), Resolve(destination), default);

    /// <summary>What a finalizer would call: it must leave the managed watcher alone.</summary>
    public void DisposeFromFinalizer() => Dispose(false);
}

/// <summary>A clock that stands still.</summary>
/// <param name="now">The time it always tells.</param>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
