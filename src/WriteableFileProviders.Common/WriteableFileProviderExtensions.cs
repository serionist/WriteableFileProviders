using Microsoft.Extensions.FileProviders;

namespace WriteableFileProviders;

/// <summary>
/// Helpers that Common's synchronous members (<see cref="IFileProvider"/>'s, <see cref="LocalFileStream"/>'s) and the
/// 2026 compatibility forms (<see cref="WriteableFileProviderCompatibilityExtensions"/>) share.
/// </summary>
/// <remarks>
/// A synchronous member blocks a thread on the asynchronous one. The library never captures the caller's synchronization
/// context, so it does not deadlock.
/// </remarks>
internal static class WriteableFileProviderExtensions
{
    internal static ProviderPath PathOf(this IWriteableFileProvider provider, IFileInfo file)
    {
        Required(provider);
        return file is IWriteableFileInfo info
            ? info.Path
            : throw new ArgumentException("The entry does not come from a writeable file provider.", nameof(file));
    }

    internal static UpdateFileOptions OptionsFor(UpdateFileMode mode) =>
        mode == UpdateFileMode.Append ? UpdateFileOptions.Append : UpdateFileOptions.Overwrite;

    internal static IWriteableFileProvider Required(IWriteableFileProvider provider) =>
        provider ?? throw new ArgumentNullException(nameof(provider));

    internal static T Wait<T>(Task<T> task) => task.GetAwaiter().GetResult();

    internal static void Wait(Task task) => task.GetAwaiter().GetResult();
}
