using System.Reflection;

namespace WriteableFileProviders.Physical.Tests;

/// <summary>
/// The provider's protected members, in reach of the tests through reflection (the provider is sealed): the core members
/// called directly, past the base class's checks, reach the races the public API meets only under concurrency.
/// </summary>
internal static class ProviderCore
{
    public static ResolvedPath Resolved(this WriteablePhysicalFileProvider provider, ProviderPath path) =>
        (ResolvedPath)Call(provider, "Resolve", path)!;

    /// <summary>A path the base class would refuse, such as one with "..": only the provider's own check stands.</summary>
    public static ResolvedPath Unvalidated(this WriteablePhysicalFileProvider provider, params string[] relativeSegments) =>
        (ResolvedPath)typeof(ResolvedPath).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single()
            .Invoke([provider.Options.RootDirectorySegments, relativeSegments]);

    public static Task<IWriteableFileInfo> GetFileInfoCore(this WriteablePhysicalFileProvider provider, ResolvedPath path) =>
        (Task<IWriteableFileInfo>)Call(provider, "GetFileInfoCoreAsync", path, CancellationToken.None)!;

    public static Task<Stream> ReadCore(this WriteablePhysicalFileProvider provider, ResolvedPath path) =>
        (Task<Stream>)Call(provider, "ReadFileCoreAsync", path, CancellationToken.None)!;

    public static Task<Stream> ReadRangeCore(this WriteablePhysicalFileProvider provider, ResolvedPath path) =>
        (Task<Stream>)Call(provider, "ReadRangeCoreAsync", path, 0L, null, null, CancellationToken.None)!;

    public static Task UpdateCore(this WriteablePhysicalFileProvider provider, ResolvedPath path, Stream contents, UpdateFileOptions options) =>
        (Task)Call(provider, "UpdateFileCoreAsync", path, contents, options, CancellationToken.None)!;

    public static Task<bool> DeleteCore(this WriteablePhysicalFileProvider provider, ResolvedPath path) =>
        (Task<bool>)Call(provider, "DeleteCoreAsync", path, CancellationToken.None)!;

    public static Task<bool> FolderExistsCore(this WriteablePhysicalFileProvider provider, ResolvedPath path) =>
        (Task<bool>)Call(provider, "FolderExistsCoreAsync", path, CancellationToken.None)!;

    public static async Task<int> ListCore(this WriteablePhysicalFileProvider provider, ResolvedPath path)
    {
        var count = 0;
        await foreach (var _ in (IAsyncEnumerable<IWriteableFileInfo>)Call(provider, "EnumerateCoreAsync", path, CancellationToken.None)!)
            count++;
        return count;
    }

    public static Task MoveCore(this WriteablePhysicalFileProvider provider, ResolvedPath source, ResolvedPath destination) =>
        (Task)Call(provider, "MoveCoreAsync", source, destination, CancellationToken.None)!;

    public static Task MoveCore(this WriteablePhysicalFileProvider provider, ProviderPath source, ProviderPath destination) =>
        provider.MoveCore(provider.Resolved(source), provider.Resolved(destination));

    public static Task CopyCore(this WriteablePhysicalFileProvider provider, ProviderPath source, ProviderPath destination) =>
        (Task)Call(provider, "CopyCoreAsync", provider.Resolved(source), provider.Resolved(destination), CancellationToken.None)!;

    /// <summary>What a finalizer would call: it must leave the managed watcher alone.</summary>
    public static void DisposeFromFinalizer(this WriteablePhysicalFileProvider provider) => Call(provider, "Dispose", false);

    // The first method of that name and arity up the hierarchy; its exceptions are thrown as they are, not wrapped.
    private static object? Call(object target, string name, params object?[] arguments)
    {
        for (var type = target.GetType(); type is not null; type = type.BaseType)
        {
            var method = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == arguments.Length);
            if (method is not null)
                return method.Invoke(target, BindingFlags.DoNotWrapExceptions, null, arguments, null);
        }
        throw new MissingMethodException(target.GetType().Name, name);
    }
}

/// <summary>A clock that stands still.</summary>
/// <param name="now">The time it always tells.</param>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
