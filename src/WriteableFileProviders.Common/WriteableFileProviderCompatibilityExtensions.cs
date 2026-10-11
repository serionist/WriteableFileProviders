using System.ComponentModel;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using static WriteableFileProviders.WriteableFileProviderExtensions;

namespace WriteableFileProviders;

/// <summary>
/// The 2026 API's call shapes and conveniences that 2027 keeps only to ease the move: the synchronous forms, the forms
/// that take an <see cref="IFileInfo"/>, a segment list or an <see cref="UpdateFileMode"/>, the 2026 parameter orders, and
/// <c>GetDownloadUrl</c>. Each compiles with a warning that names the asynchronous <see cref="ProviderPath"/> member to
/// use instead, and goes in 2028.0.0. See the migration guide.
/// </summary>
/// <remarks>
/// A synchronous form blocks a thread on the asynchronous member. The library never captures the caller's
/// synchronization context, so it does not deadlock, but it holds a thread for the whole call.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class WriteableFileProviderCompatibilityExtensions
{
    private const string Goes = " It goes in 2028.0.0.";
    private const string OnePath = ", with the segments as one path: a string such as \"a/b\", a string[], or a ProviderPath." + Goes;
    private const string EntryPath = ", with the entry's Path (IWriteableFileInfo.Path; EnumerateAsync lists IWriteableFileInfo)." + Goes;
    private const string Append = "Use UpdateFileAsync(path, contents, UpdateFileOptions.Append), or UpdateFileOptions.Overwrite";
    private const string DownloadUrl = "Use GetSignedUrlAsync(path, new SignedUrlOptions { Validity = validity }) and its Url, or IWriteableFileUrlService (WriteableFileProviders.AspNetCore), which falls back to a gateway URL where a provider cannot sign one.";

    // ---- The synchronous forms, on a ProviderPath ----

    /// <summary>Gets a file, a folder or a missing path; see <see cref="IWriteableFileProvider.GetFileInfoAsync"/>.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="path">The path.</param>
    [Obsolete("Use GetFileInfoAsync(path)." + Goes)]
    public static IWriteableFileInfo GetFileInfo(this IWriteableFileProvider provider, ProviderPath path) =>
        Wait(Required(provider).GetFileInfoAsync(path));

    /// <summary>Lists one level of a folder; see <see cref="IWriteableFileProvider.GetDirectoryContentsAsync"/>.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="path">The folder.</param>
    [Obsolete("Use GetDirectoryContentsAsync(path) or EnumerateAsync(path)." + Goes)]
    public static IDirectoryContents GetDirectoryContents(this IWriteableFileProvider provider, ProviderPath path) =>
        Wait(Required(provider).GetDirectoryContentsAsync(path));

    /// <summary>Opens a file for reading; see <see cref="IWriteableFileProvider.ReadFileAsync"/>.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="path">The file.</param>
    [Obsolete("Use ReadFileAsync(path)." + Goes)]
    public static Stream ReadFile(this IWriteableFileProvider provider, ProviderPath path) =>
        Wait(Required(provider).ReadFileAsync(path));

    /// <summary>Writes a file; see <see cref="IWriteableFileProvider.UpdateFileAsync"/>.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="path">The file.</param>
    /// <param name="contents">The content, read from its position to its end, and left open.</param>
    /// <param name="options">How to write.</param>
    [Obsolete("Use UpdateFileAsync(path, contents, options)." + Goes)]
    public static void UpdateFile(this IWriteableFileProvider provider, ProviderPath path, Stream contents, UpdateFileOptions? options = null) =>
        Wait(Required(provider).UpdateFileAsync(path, contents, options));

    /// <summary>Deletes a file or a folder; see <see cref="IWriteableFileProvider.DeleteAsync"/>.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="path">The file or folder.</param>
    [Obsolete("Use DeleteAsync(path)." + Goes)]
    public static bool Delete(this IWriteableFileProvider provider, ProviderPath path) =>
        Wait(Required(provider).DeleteAsync(path));

    /// <summary>Moves a file; see <see cref="IWriteableFileProvider.MoveAsync"/>.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="source">The file to move.</param>
    /// <param name="destination">Where to move it.</param>
    [Obsolete("Use MoveAsync(source, destination)." + Goes)]
    public static void Move(this IWriteableFileProvider provider, ProviderPath source, ProviderPath destination) =>
        Wait(Required(provider).MoveAsync(source, destination));

    /// <summary>Opens a local copy of a file; see <see cref="IWriteableFileProvider.CreateLocalFileStreamAsync"/>.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="path">The file.</param>
    /// <param name="readOnly">Whether the stream is read-only.</param>
    [Obsolete("Use CreateLocalFileStreamAsync(path, readOnly)." + Goes)]
    public static LocalFileStream CreateLocalFileStream(this IWriteableFileProvider provider, ProviderPath path, bool readOnly) =>
        Wait(Required(provider).CreateLocalFileStreamAsync(path, readOnly));

    // ---- An UpdateFileMode instead of UpdateFileOptions ----

    /// <summary>Overwrites or appends to a file; see <see cref="IWriteableFileProvider.UpdateFileAsync"/>.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="path">The file.</param>
    /// <param name="contents">The content, read from its position to its end, and left open.</param>
    /// <param name="mode">Overwrite or append.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    [Obsolete(Append + "." + Goes)]
    public static Task UpdateFileAsync(this IWriteableFileProvider provider, ProviderPath path, Stream contents, UpdateFileMode mode, CancellationToken cancellationToken = default) =>
        Required(provider).UpdateFileAsync(path, contents, OptionsFor(mode), cancellationToken);

    /// <summary>Overwrites or appends to a file; see <see cref="IWriteableFileProvider.UpdateFileAsync"/>.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="path">The file.</param>
    /// <param name="contents">The content, read from its position to its end, and left open.</param>
    /// <param name="mode">Overwrite or append.</param>
    [Obsolete(Append + "." + Goes)]
    public static void UpdateFile(this IWriteableFileProvider provider, ProviderPath path, Stream contents, UpdateFileMode mode) =>
        Wait(Required(provider).UpdateFileAsync(path, contents, OptionsFor(mode)));

    // ---- GetDownloadUrl ----

    /// <summary>
    /// Issues a URL to download a file for exactly <paramref name="validity"/>, as the string 2026 returned (the URL's
    /// <see cref="Uri.AbsoluteUri"/>); see <see cref="IWriteableFileProvider.GetSignedUrlAsync"/>.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="path">The file.</param>
    /// <param name="validity">How long the URL lives.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    [Obsolete(DownloadUrl + Goes)]
    public static async Task<string> GetDownloadUrlAsync(this IWriteableFileProvider provider, ProviderPath path, TimeSpan validity, CancellationToken cancellationToken = default) =>
        (await Required(provider).GetSignedUrlAsync(path, new SignedUrlOptions { Validity = validity }, cancellationToken).ConfigureAwait(false)).Url.AbsoluteUri;

    /// <summary>
    /// Issues a URL to download a file for exactly <paramref name="validity"/>, as the string 2026 returned (the URL's
    /// <see cref="Uri.AbsoluteUri"/>).
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="path">The file.</param>
    /// <param name="validity">How long the URL lives.</param>
    [Obsolete(DownloadUrl + Goes)]
    public static string GetDownloadUrl(this IWriteableFileProvider provider, ProviderPath path, TimeSpan validity) =>
        Wait(provider.GetDownloadUrlAsync(path, validity));

    // ---- An IFileInfo instead of a path ----

    /// <summary>Opens a file for reading.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="file">The file, as the provider returned it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    [Obsolete("Use ReadFileAsync(path)" + EntryPath)]
    public static Task<Stream> ReadFileAsync(this IWriteableFileProvider provider, IFileInfo file, CancellationToken cancellationToken = default) =>
        Required(provider).ReadFileAsync(provider.PathOf(file), cancellationToken);

    /// <summary>Opens a file for reading.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="file">The file, as the provider returned it.</param>
    [Obsolete("Use ReadFileAsync(path)" + EntryPath)]
    public static Stream ReadFile(this IWriteableFileProvider provider, IFileInfo file) =>
        Wait(Required(provider).ReadFileAsync(provider.PathOf(file)));

    /// <summary>Writes a file.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="file">The file, as the provider returned it.</param>
    /// <param name="contents">The content, read from its position to its end, and left open.</param>
    /// <param name="options">How to write.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    [Obsolete("Use UpdateFileAsync(path, contents, options)" + EntryPath)]
    public static Task UpdateFileAsync(this IWriteableFileProvider provider, IFileInfo file, Stream contents, UpdateFileOptions? options = null, CancellationToken cancellationToken = default) =>
        Required(provider).UpdateFileAsync(provider.PathOf(file), contents, options, cancellationToken);

    /// <summary>Writes a file.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="file">The file, as the provider returned it.</param>
    /// <param name="contents">The content, read from its position to its end, and left open.</param>
    /// <param name="options">How to write.</param>
    [Obsolete("Use UpdateFileAsync(path, contents, options)" + EntryPath)]
    public static void UpdateFile(this IWriteableFileProvider provider, IFileInfo file, Stream contents, UpdateFileOptions? options = null) =>
        Wait(Required(provider).UpdateFileAsync(provider.PathOf(file), contents, options));

    /// <summary>Deletes a file or a folder.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="fileOrFolder">The entry, as the provider returned it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    [Obsolete("Use DeleteAsync(path)" + EntryPath)]
    public static Task<bool> DeleteAsync(this IWriteableFileProvider provider, IFileInfo fileOrFolder, CancellationToken cancellationToken = default) =>
        Required(provider).DeleteAsync(provider.PathOf(fileOrFolder), cancellationToken);

    /// <summary>Deletes a file or a folder.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="fileOrFolder">The entry, as the provider returned it.</param>
    [Obsolete("Use DeleteAsync(path)" + EntryPath)]
    public static bool Delete(this IWriteableFileProvider provider, IFileInfo fileOrFolder) =>
        Wait(Required(provider).DeleteAsync(provider.PathOf(fileOrFolder)));

    /// <summary>Moves a file.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="file">The file, as the provider returned it.</param>
    /// <param name="destination">Where to move it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    [Obsolete("Use MoveAsync(source, destination)" + EntryPath)]
    public static Task MoveAsync(this IWriteableFileProvider provider, IFileInfo file, ProviderPath destination, CancellationToken cancellationToken = default) =>
        Required(provider).MoveAsync(provider.PathOf(file), destination, cancellationToken);

    /// <summary>Moves a file.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="file">The file, as the provider returned it.</param>
    /// <param name="destination">Where to move it.</param>
    [Obsolete("Use MoveAsync(source, destination)" + EntryPath)]
    public static void Move(this IWriteableFileProvider provider, IFileInfo file, ProviderPath destination) =>
        Wait(Required(provider).MoveAsync(provider.PathOf(file), destination));

    /// <summary>Opens a local copy of a file.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="file">The file, as the provider returned it.</param>
    /// <param name="readOnly">Whether the stream is read-only.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    [Obsolete("Use CreateLocalFileStreamAsync(path, readOnly)" + EntryPath)]
    public static Task<LocalFileStream> CreateLocalFileStreamAsync(this IWriteableFileProvider provider, IFileInfo file, bool readOnly, CancellationToken cancellationToken = default) =>
        Required(provider).CreateLocalFileStreamAsync(provider.PathOf(file), readOnly, cancellationToken);

    /// <summary>Opens a local copy of a file.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="file">The file, as the provider returned it.</param>
    /// <param name="readOnly">Whether the stream is read-only.</param>
    [Obsolete("Use CreateLocalFileStreamAsync(path, readOnly)" + EntryPath)]
    public static LocalFileStream CreateLocalFileStream(this IWriteableFileProvider provider, IFileInfo file, bool readOnly) =>
        Wait(Required(provider).CreateLocalFileStreamAsync(provider.PathOf(file), readOnly));

    /// <summary>
    /// Issues a URL to download a file for exactly <paramref name="validity"/>, as the string 2026 returned (the URL's
    /// <see cref="Uri.AbsoluteUri"/>).
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="file">The file, as the provider returned it.</param>
    /// <param name="validity">How long the URL lives.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    [Obsolete(DownloadUrl + Goes)]
    public static Task<string> GetDownloadUrlAsync(this IWriteableFileProvider provider, IFileInfo file, TimeSpan validity, CancellationToken cancellationToken = default) =>
        provider.GetDownloadUrlAsync(provider.PathOf(file), validity, cancellationToken);

    /// <summary>
    /// Issues a URL to download a file for exactly <paramref name="validity"/>, as the string 2026 returned (the URL's
    /// <see cref="Uri.AbsoluteUri"/>).
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="file">The file, as the provider returned it.</param>
    /// <param name="validity">How long the URL lives.</param>
    [Obsolete(DownloadUrl + Goes)]
    public static string GetDownloadUrl(this IWriteableFileProvider provider, IFileInfo file, TimeSpan validity) =>
        Wait(provider.GetDownloadUrlAsync(provider.PathOf(file), validity));

    // ---- Segment lists ----

    /// <summary>Gets a file by its segments.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="segment1">The first segment.</param>
    /// <param name="segment2">The second segment.</param>
    /// <param name="more">Further segments.</param>
    [Obsolete("Use GetFileInfoAsync(path)" + OnePath)]
    public static Task<IWriteableFileInfo> GetFileInfoAsync(this IWriteableFileProvider provider, string segment1, string segment2, params string[] more) =>
        Required(provider).GetFileInfoAsync(Join(segment1, segment2, more));

    /// <summary>Gets a file by its segments.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="segment1">The first segment.</param>
    /// <param name="segment2">The second segment.</param>
    /// <param name="more">Further segments.</param>
    [Obsolete("Use GetFileInfoAsync(path)" + OnePath)]
    public static IWriteableFileInfo GetFileInfo(this IWriteableFileProvider provider, string segment1, string segment2, params string[] more) =>
        Wait(Required(provider).GetFileInfoAsync(Join(segment1, segment2, more)));

    /// <summary>Lists a folder by its segments.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="segment1">The first segment.</param>
    /// <param name="segment2">The second segment.</param>
    /// <param name="more">Further segments.</param>
    [Obsolete("Use GetDirectoryContentsAsync(path)" + OnePath)]
    public static Task<IDirectoryContents> GetDirectoryContentsAsync(this IWriteableFileProvider provider, string segment1, string segment2, params string[] more) =>
        Required(provider).GetDirectoryContentsAsync(Join(segment1, segment2, more));

    /// <summary>Lists a folder by its segments.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="segment1">The first segment.</param>
    /// <param name="segment2">The second segment.</param>
    /// <param name="more">Further segments.</param>
    [Obsolete("Use GetDirectoryContentsAsync(path)" + OnePath)]
    public static IDirectoryContents GetDirectoryContents(this IWriteableFileProvider provider, string segment1, string segment2, params string[] more) =>
        Wait(Required(provider).GetDirectoryContentsAsync(Join(segment1, segment2, more)));

    /// <summary>Opens a file by its segments.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="segment1">The first segment.</param>
    /// <param name="segment2">The second segment.</param>
    /// <param name="more">Further segments.</param>
    [Obsolete("Use ReadFileAsync(path)" + OnePath)]
    public static Task<Stream> ReadFileAsync(this IWriteableFileProvider provider, string segment1, string segment2, params string[] more) =>
        Required(provider).ReadFileAsync(Join(segment1, segment2, more));

    /// <summary>Opens a file by its segments.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="segment1">The first segment.</param>
    /// <param name="segment2">The second segment.</param>
    /// <param name="more">Further segments.</param>
    [Obsolete("Use ReadFileAsync(path)" + OnePath)]
    public static Stream ReadFile(this IWriteableFileProvider provider, string segment1, string segment2, params string[] more) =>
        Wait(Required(provider).ReadFileAsync(Join(segment1, segment2, more)));

    /// <summary>Deletes by segments.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="segment1">The first segment.</param>
    /// <param name="segment2">The second segment.</param>
    /// <param name="more">Further segments.</param>
    [Obsolete("Use DeleteAsync(path)" + OnePath)]
    public static Task<bool> DeleteAsync(this IWriteableFileProvider provider, string segment1, string segment2, params string[] more) =>
        Required(provider).DeleteAsync(Join(segment1, segment2, more));

    /// <summary>Deletes by segments.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="segment1">The first segment.</param>
    /// <param name="segment2">The second segment.</param>
    /// <param name="more">Further segments.</param>
    [Obsolete("Use DeleteAsync(path)" + OnePath)]
    public static bool Delete(this IWriteableFileProvider provider, string segment1, string segment2, params string[] more) =>
        Wait(Required(provider).DeleteAsync(Join(segment1, segment2, more)));

    // ---- The 2026 parameter orders ----

    /// <summary>Writes a file: the 2026 parameter order.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="subpath">The file.</param>
    /// <param name="mode">Overwrite or append.</param>
    /// <param name="contents">The content.</param>
    [Obsolete(Append + ": the path comes first, then the content." + Goes)]
    public static Task UpdateFileAsync(this IWriteableFileProvider provider, string subpath, UpdateFileMode mode, Stream contents) =>
        Required(provider).UpdateFileAsync(subpath, contents, OptionsFor(mode));

    /// <summary>Writes a file: the 2026 parameter order.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="subpath">The file.</param>
    /// <param name="mode">Overwrite or append.</param>
    /// <param name="contents">The content.</param>
    [Obsolete(Append + ": the path comes first, then the content." + Goes)]
    public static void UpdateFile(this IWriteableFileProvider provider, string subpath, UpdateFileMode mode, Stream contents) =>
        Wait(Required(provider).UpdateFileAsync(subpath, contents, OptionsFor(mode)));

    /// <summary>Writes a file: the 2026 parameter order.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="contents">The content.</param>
    /// <param name="mode">Overwrite or append.</param>
    /// <param name="pathSegments">The file's segments.</param>
    [Obsolete(Append + ": the path comes first, then the content." + Goes)]
    public static Task UpdateFileAsync(this IWriteableFileProvider provider, Stream contents, UpdateFileMode mode, params string[] pathSegments) =>
        Required(provider).UpdateFileAsync(pathSegments, contents, OptionsFor(mode));

    /// <summary>Writes a file: the 2026 parameter order.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="contents">The content.</param>
    /// <param name="mode">Overwrite or append.</param>
    /// <param name="pathSegments">The file's segments.</param>
    [Obsolete(Append + ": the path comes first, then the content." + Goes)]
    public static void UpdateFile(this IWriteableFileProvider provider, Stream contents, UpdateFileMode mode, params string[] pathSegments) =>
        Wait(Required(provider).UpdateFileAsync(pathSegments, contents, OptionsFor(mode)));

    /// <summary>Writes a file: the 2026 parameter order.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="contents">The content.</param>
    /// <param name="mode">Overwrite or append.</param>
    /// <param name="file">The file, as the provider returned it.</param>
    [Obsolete(Append + ", with the entry's Path first, then the content." + Goes)]
    public static Task UpdateFileAsync(this IWriteableFileProvider provider, Stream contents, UpdateFileMode mode, IFileInfo file) =>
        Required(provider).UpdateFileAsync(provider.PathOf(file), contents, OptionsFor(mode));

    /// <summary>Writes a file: the 2026 parameter order.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="contents">The content.</param>
    /// <param name="mode">Overwrite or append.</param>
    /// <param name="file">The file, as the provider returned it.</param>
    [Obsolete(Append + ", with the entry's Path first, then the content." + Goes)]
    public static void UpdateFile(this IWriteableFileProvider provider, Stream contents, UpdateFileMode mode, IFileInfo file) =>
        Wait(Required(provider).UpdateFileAsync(provider.PathOf(file), contents, OptionsFor(mode)));

    /// <summary>Opens a local copy: the 2026 parameter order.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="readOnly">Whether the stream is read-only.</param>
    /// <param name="subpath">The file, as a string path.</param>
    [Obsolete("Use CreateLocalFileStreamAsync(path, readOnly): the path comes first." + Goes)]
    public static Task<LocalFileStream> CreateLocalFileStreamAsync(this IWriteableFileProvider provider, bool readOnly, string subpath) =>
        Required(provider).CreateLocalFileStreamAsync(subpath, readOnly);

    /// <summary>Opens a local copy: the 2026 parameter order.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="readOnly">Whether the stream is read-only.</param>
    /// <param name="subpath">The file, as a string path.</param>
    [Obsolete("Use CreateLocalFileStreamAsync(path, readOnly): the path comes first." + Goes)]
    public static LocalFileStream CreateLocalFileStream(this IWriteableFileProvider provider, bool readOnly, string subpath) =>
        Wait(Required(provider).CreateLocalFileStreamAsync(subpath, readOnly));

    /// <summary>Opens a local copy: the 2026 parameter order.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="readOnly">Whether the stream is read-only.</param>
    /// <param name="pathSegments">The file's segments.</param>
    [Obsolete("Use CreateLocalFileStreamAsync(path, readOnly): the path comes first." + Goes)]
    public static Task<LocalFileStream> CreateLocalFileStreamAsync(this IWriteableFileProvider provider, bool readOnly, params string[] pathSegments) =>
        Required(provider).CreateLocalFileStreamAsync(pathSegments, readOnly);

    /// <summary>Opens a local copy: the 2026 parameter order.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="readOnly">Whether the stream is read-only.</param>
    /// <param name="pathSegments">The file's segments.</param>
    [Obsolete("Use CreateLocalFileStreamAsync(path, readOnly): the path comes first." + Goes)]
    public static LocalFileStream CreateLocalFileStream(this IWriteableFileProvider provider, bool readOnly, params string[] pathSegments) =>
        Wait(Required(provider).CreateLocalFileStreamAsync(pathSegments, readOnly));

    /// <summary>Opens a local copy: the 2026 parameter order.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="readOnly">Whether the stream is read-only.</param>
    /// <param name="file">The file, as the provider returned it.</param>
    [Obsolete("Use CreateLocalFileStreamAsync(path, readOnly), with the entry's Path first." + Goes)]
    public static Task<LocalFileStream> CreateLocalFileStreamAsync(this IWriteableFileProvider provider, bool readOnly, IFileInfo file) =>
        Required(provider).CreateLocalFileStreamAsync(provider.PathOf(file), readOnly);

    /// <summary>Opens a local copy: the 2026 parameter order.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="readOnly">Whether the stream is read-only.</param>
    /// <param name="file">The file, as the provider returned it.</param>
    [Obsolete("Use CreateLocalFileStreamAsync(path, readOnly), with the entry's Path first." + Goes)]
    public static LocalFileStream CreateLocalFileStream(this IWriteableFileProvider provider, bool readOnly, IFileInfo file) =>
        Wait(Required(provider).CreateLocalFileStreamAsync(provider.PathOf(file), readOnly));

    /// <summary>Watches for changes, asynchronously in name only.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="filter">The files to watch.</param>
    [Obsolete("Use Watch(filter)." + Goes)]
    public static Task<IChangeToken> WatchAsync(this IWriteableFileProvider provider, string filter) =>
        Task.FromResult(Required(provider).Watch(filter));

    private static ProviderPath Join(string segment1, string segment2, string[] more) => new([segment1, segment2, .. more ?? []]);
}
