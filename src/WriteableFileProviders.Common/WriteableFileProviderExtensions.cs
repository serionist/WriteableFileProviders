using Microsoft.Extensions.FileProviders;

namespace WriteableFileProviders;

/// <summary>
/// The synchronous forms of <see cref="IWriteableFileProvider"/>'s members, the overloads that take an
/// <see cref="IFileInfo"/>, and conveniences built on the interface.
/// </summary>
/// <remarks>
/// The synchronous forms block a thread on the asynchronous member. The library never captures the caller's
/// synchronization context, so they do not deadlock, but in server code prefer the asynchronous members.
/// </remarks>
public static class WriteableFileProviderExtensions
{
    /// <summary>Gets a file, a folder or a missing path; see <see cref="IWriteableFileProvider.GetFileInfoAsync"/>.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="path">The path.</param>
    public static IWriteableFileInfo GetFileInfo(this IWriteableFileProvider provider, ProviderPath path) =>
        Wait(Required(provider).GetFileInfoAsync(path));

    /// <summary>Lists one level of a folder; see <see cref="IWriteableFileProvider.GetDirectoryContentsAsync"/>.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="path">The folder.</param>
    public static IDirectoryContents GetDirectoryContents(this IWriteableFileProvider provider, ProviderPath path) =>
        Wait(Required(provider).GetDirectoryContentsAsync(path));

    /// <summary>Opens a file for reading; see <see cref="IWriteableFileProvider.ReadFileAsync"/>.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="path">The file.</param>
    public static Stream ReadFile(this IWriteableFileProvider provider, ProviderPath path) =>
        Wait(Required(provider).ReadFileAsync(path));

    /// <summary>Writes a file; see <see cref="IWriteableFileProvider.UpdateFileAsync"/>.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="path">The file.</param>
    /// <param name="contents">The content, read from its position to its end, and left open.</param>
    /// <param name="options">How to write.</param>
    public static void UpdateFile(this IWriteableFileProvider provider, ProviderPath path, Stream contents, UpdateFileOptions? options = null) =>
        Wait(Required(provider).UpdateFileAsync(path, contents, options));

    /// <summary>Overwrites or appends to a file; see <see cref="IWriteableFileProvider.UpdateFileAsync"/>.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="path">The file.</param>
    /// <param name="contents">The content, read from its position to its end, and left open.</param>
    /// <param name="mode">Overwrite or append.</param>
    public static void UpdateFile(this IWriteableFileProvider provider, ProviderPath path, Stream contents, UpdateFileMode mode) =>
        Wait(Required(provider).UpdateFileAsync(path, contents, OptionsFor(mode)));

    /// <summary>Overwrites or appends to a file; see <see cref="IWriteableFileProvider.UpdateFileAsync"/>.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="path">The file.</param>
    /// <param name="contents">The content, read from its position to its end, and left open.</param>
    /// <param name="mode">Overwrite or append.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public static Task UpdateFileAsync(this IWriteableFileProvider provider, ProviderPath path, Stream contents, UpdateFileMode mode, CancellationToken cancellationToken = default) =>
        Required(provider).UpdateFileAsync(path, contents, OptionsFor(mode), cancellationToken);

    /// <summary>Deletes a file or a folder; see <see cref="IWriteableFileProvider.DeleteAsync"/>.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="path">The file or folder.</param>
    public static bool Delete(this IWriteableFileProvider provider, ProviderPath path) =>
        Wait(Required(provider).DeleteAsync(path));

    /// <summary>Moves a file; see <see cref="IWriteableFileProvider.MoveAsync"/>.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="source">The file to move.</param>
    /// <param name="destination">Where to move it.</param>
    public static void Move(this IWriteableFileProvider provider, ProviderPath source, ProviderPath destination) =>
        Wait(Required(provider).MoveAsync(source, destination));

    /// <summary>Opens a local copy of a file; see <see cref="IWriteableFileProvider.CreateLocalFileStreamAsync"/>.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="path">The file.</param>
    /// <param name="readOnly">Whether the stream is read-only.</param>
    public static LocalFileStream CreateLocalFileStream(this IWriteableFileProvider provider, ProviderPath path, bool readOnly) =>
        Wait(Required(provider).CreateLocalFileStreamAsync(path, readOnly));

    /// <summary>
    /// Issues a URL to download a file for exactly <paramref name="validity"/>; see
    /// <see cref="IWriteableFileProvider.GetSignedUrlAsync"/>.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="path">The file.</param>
    /// <param name="validity">How long the URL lives.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public static async Task<Uri> GetDownloadUrlAsync(this IWriteableFileProvider provider, ProviderPath path, TimeSpan validity, CancellationToken cancellationToken = default) =>
        (await Required(provider).GetSignedUrlAsync(path, new SignedUrlOptions { Validity = validity }, cancellationToken).ConfigureAwait(false)).Url;

    /// <summary>Issues a URL to download a file; see <see cref="GetDownloadUrlAsync(IWriteableFileProvider, ProviderPath, TimeSpan, CancellationToken)"/>.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="path">The file.</param>
    /// <param name="validity">How long the URL lives.</param>
    public static Uri GetDownloadUrl(this IWriteableFileProvider provider, ProviderPath path, TimeSpan validity) =>
        Wait(provider.GetDownloadUrlAsync(path, validity));

    internal static ProviderPath PathOf(this IWriteableFileProvider provider, IFileInfo file)
    {
        Required(provider);
        return file is IWriteableFileInfo info
            ? info.Path
            : throw new ArgumentException("The entry does not come from a writeable file provider.", nameof(file));
    }

    /// <summary>Opens a file for reading.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="file">The file, as the provider returned it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public static Task<Stream> ReadFileAsync(this IWriteableFileProvider provider, IFileInfo file, CancellationToken cancellationToken = default) =>
        Required(provider).ReadFileAsync(provider.PathOf(file), cancellationToken);

    /// <summary>Opens a file for reading.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="file">The file, as the provider returned it.</param>
    public static Stream ReadFile(this IWriteableFileProvider provider, IFileInfo file) =>
        Wait(provider.ReadFileAsync(file));

    /// <summary>Writes a file.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="file">The file, as the provider returned it.</param>
    /// <param name="contents">The content, read from its position to its end, and left open.</param>
    /// <param name="options">How to write.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public static Task UpdateFileAsync(this IWriteableFileProvider provider, IFileInfo file, Stream contents, UpdateFileOptions? options = null, CancellationToken cancellationToken = default) =>
        Required(provider).UpdateFileAsync(provider.PathOf(file), contents, options, cancellationToken);

    /// <summary>Deletes a file or a folder.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="fileOrFolder">The entry, as the provider returned it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public static Task<bool> DeleteAsync(this IWriteableFileProvider provider, IFileInfo fileOrFolder, CancellationToken cancellationToken = default) =>
        Required(provider).DeleteAsync(provider.PathOf(fileOrFolder), cancellationToken);

    /// <summary>Deletes a file or a folder.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="fileOrFolder">The entry, as the provider returned it.</param>
    public static bool Delete(this IWriteableFileProvider provider, IFileInfo fileOrFolder) =>
        Wait(provider.DeleteAsync(fileOrFolder));

    /// <summary>Moves a file.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="file">The file, as the provider returned it.</param>
    /// <param name="destination">Where to move it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public static Task MoveAsync(this IWriteableFileProvider provider, IFileInfo file, ProviderPath destination, CancellationToken cancellationToken = default) =>
        Required(provider).MoveAsync(provider.PathOf(file), destination, cancellationToken);

    /// <summary>Moves a file.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="file">The file, as the provider returned it.</param>
    /// <param name="destination">Where to move it.</param>
    public static void Move(this IWriteableFileProvider provider, IFileInfo file, ProviderPath destination) =>
        Wait(provider.MoveAsync(file, destination));

    /// <summary>Opens a local copy of a file.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="file">The file, as the provider returned it.</param>
    /// <param name="readOnly">Whether the stream is read-only.</param>
    public static LocalFileStream CreateLocalFileStream(this IWriteableFileProvider provider, IFileInfo file, bool readOnly) =>
        Wait(Required(provider).CreateLocalFileStreamAsync(provider.PathOf(file), readOnly));

    /// <summary>Writes a file.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="file">The file, as the provider returned it.</param>
    /// <param name="contents">The content, read from its position to its end, and left open.</param>
    /// <param name="options">How to write.</param>
    public static void UpdateFile(this IWriteableFileProvider provider, IFileInfo file, Stream contents, UpdateFileOptions? options = null) =>
        Wait(Required(provider).UpdateFileAsync(provider.PathOf(file), contents, options));

    /// <summary>Issues a URL to download a file.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="file">The file, as the provider returned it.</param>
    /// <param name="validity">How long the URL lives.</param>
    public static Uri GetDownloadUrl(this IWriteableFileProvider provider, IFileInfo file, TimeSpan validity) =>
        Wait(provider.GetDownloadUrlAsync(file, validity));

    /// <summary>Opens a local copy of a file.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="file">The file, as the provider returned it.</param>
    /// <param name="readOnly">Whether the stream is read-only.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public static Task<LocalFileStream> CreateLocalFileStreamAsync(this IWriteableFileProvider provider, IFileInfo file, bool readOnly, CancellationToken cancellationToken = default) =>
        Required(provider).CreateLocalFileStreamAsync(provider.PathOf(file), readOnly, cancellationToken);

    /// <summary>Issues a URL to download a file.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="file">The file, as the provider returned it.</param>
    /// <param name="validity">How long the URL lives.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public static Task<Uri> GetDownloadUrlAsync(this IWriteableFileProvider provider, IFileInfo file, TimeSpan validity, CancellationToken cancellationToken = default) =>
        provider.GetDownloadUrlAsync(provider.PathOf(file), validity, cancellationToken);

    internal static UpdateFileOptions OptionsFor(UpdateFileMode mode) =>
        mode == UpdateFileMode.Append ? UpdateFileOptions.Append : UpdateFileOptions.Overwrite;

    internal static IWriteableFileProvider Required(IWriteableFileProvider provider) =>
        provider ?? throw new ArgumentNullException(nameof(provider));

    internal static T Wait<T>(Task<T> task) => task.GetAwaiter().GetResult();

    internal static void Wait(Task task) => task.GetAwaiter().GetResult();
}
