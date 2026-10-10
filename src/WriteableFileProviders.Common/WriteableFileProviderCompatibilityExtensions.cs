using System.ComponentModel;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace WriteableFileProviders;

/// <summary>
/// The call shapes of the 2026 API that the 2027 API cannot take as they are. Each compiles with a warning that names the
/// call to use instead, and goes in the next major release. See docs/migration-2027.md.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class WriteableFileProviderCompatibilityExtensions
{
    private const string PathMessage = "Pass the segments as one path: a string such as \"a/b\", a string[], or a ProviderPath.";

    /// <summary>Gets a file by its segments.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="segment1">The first segment.</param>
    /// <param name="segment2">The second segment.</param>
    /// <param name="more">Further segments.</param>
    [Obsolete(PathMessage)]
    public static Task<IWriteableFileInfo> GetFileInfoAsync(this IWriteableFileProvider provider, string segment1, string segment2, params string[] more) =>
        Required(provider).GetFileInfoAsync(Join(segment1, segment2, more));

    /// <summary>Gets a file by its segments.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="segment1">The first segment.</param>
    /// <param name="segment2">The second segment.</param>
    /// <param name="more">Further segments.</param>
    [Obsolete(PathMessage)]
    public static IWriteableFileInfo GetFileInfo(this IWriteableFileProvider provider, string segment1, string segment2, params string[] more) =>
        Required(provider).GetFileInfo(Join(segment1, segment2, more));

    /// <summary>Lists a folder by its segments.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="segment1">The first segment.</param>
    /// <param name="segment2">The second segment.</param>
    /// <param name="more">Further segments.</param>
    [Obsolete(PathMessage)]
    public static Task<IDirectoryContents> GetDirectoryContentsAsync(this IWriteableFileProvider provider, string segment1, string segment2, params string[] more) =>
        Required(provider).GetDirectoryContentsAsync(Join(segment1, segment2, more));

    /// <summary>Lists a folder by its segments.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="segment1">The first segment.</param>
    /// <param name="segment2">The second segment.</param>
    /// <param name="more">Further segments.</param>
    [Obsolete(PathMessage)]
    public static IDirectoryContents GetDirectoryContents(this IWriteableFileProvider provider, string segment1, string segment2, params string[] more) =>
        Required(provider).GetDirectoryContents(Join(segment1, segment2, more));

    /// <summary>Opens a file by its segments.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="segment1">The first segment.</param>
    /// <param name="segment2">The second segment.</param>
    /// <param name="more">Further segments.</param>
    [Obsolete(PathMessage)]
    public static Task<Stream> ReadFileAsync(this IWriteableFileProvider provider, string segment1, string segment2, params string[] more) =>
        Required(provider).ReadFileAsync(Join(segment1, segment2, more));

    /// <summary>Opens a file by its segments.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="segment1">The first segment.</param>
    /// <param name="segment2">The second segment.</param>
    /// <param name="more">Further segments.</param>
    [Obsolete(PathMessage)]
    public static Stream ReadFile(this IWriteableFileProvider provider, string segment1, string segment2, params string[] more) =>
        Required(provider).ReadFile(Join(segment1, segment2, more));

    /// <summary>Writes a file: the 2026 parameter order.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="subpath">The file.</param>
    /// <param name="mode">Overwrite or append.</param>
    /// <param name="contents">The content.</param>
    [Obsolete("Use UpdateFileAsync(path, contents, mode): the path comes first, then the content.")]
    public static Task UpdateFileAsync(this IWriteableFileProvider provider, string subpath, UpdateFileMode mode, Stream contents) =>
        Required(provider).UpdateFileAsync(subpath, contents, WriteableFileProviderExtensions.OptionsFor(mode));

    /// <summary>Writes a file: the 2026 parameter order.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="subpath">The file.</param>
    /// <param name="mode">Overwrite or append.</param>
    /// <param name="contents">The content.</param>
    [Obsolete("Use UpdateFile(path, contents, mode): the path comes first, then the content.")]
    public static void UpdateFile(this IWriteableFileProvider provider, string subpath, UpdateFileMode mode, Stream contents) =>
        Required(provider).UpdateFile(subpath, contents, mode);

    /// <summary>Writes a file: the 2026 parameter order.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="contents">The content.</param>
    /// <param name="mode">Overwrite or append.</param>
    /// <param name="pathSegments">The file's segments.</param>
    [Obsolete("Use UpdateFileAsync(path, contents, mode): the path comes first, then the content.")]
    public static Task UpdateFileAsync(this IWriteableFileProvider provider, Stream contents, UpdateFileMode mode, params string[] pathSegments) =>
        Required(provider).UpdateFileAsync(pathSegments, contents, WriteableFileProviderExtensions.OptionsFor(mode));

    /// <summary>Writes a file: the 2026 parameter order.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="contents">The content.</param>
    /// <param name="mode">Overwrite or append.</param>
    /// <param name="pathSegments">The file's segments.</param>
    [Obsolete("Use UpdateFile(path, contents, mode): the path comes first, then the content.")]
    public static void UpdateFile(this IWriteableFileProvider provider, Stream contents, UpdateFileMode mode, params string[] pathSegments) =>
        Required(provider).UpdateFile(pathSegments, contents, mode);

    /// <summary>Writes a file: the 2026 parameter order.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="contents">The content.</param>
    /// <param name="mode">Overwrite or append.</param>
    /// <param name="file">The file, as the provider returned it.</param>
    [Obsolete("Use UpdateFileAsync(file, contents, options): the file comes first, then the content.")]
    public static Task UpdateFileAsync(this IWriteableFileProvider provider, Stream contents, UpdateFileMode mode, IFileInfo file) =>
        Required(provider).UpdateFileAsync(provider.PathOf(file), contents, WriteableFileProviderExtensions.OptionsFor(mode));

    /// <summary>Writes a file: the 2026 parameter order.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="contents">The content.</param>
    /// <param name="mode">Overwrite or append.</param>
    /// <param name="file">The file, as the provider returned it.</param>
    [Obsolete("Use UpdateFile(path, contents, mode): the path comes first, then the content.")]
    public static void UpdateFile(this IWriteableFileProvider provider, Stream contents, UpdateFileMode mode, IFileInfo file) =>
        Required(provider).UpdateFile(provider.PathOf(file), contents, mode);

    /// <summary>Deletes by segments.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="segment1">The first segment.</param>
    /// <param name="segment2">The second segment.</param>
    /// <param name="more">Further segments.</param>
    [Obsolete(PathMessage)]
    public static Task<bool> DeleteAsync(this IWriteableFileProvider provider, string segment1, string segment2, params string[] more) =>
        Required(provider).DeleteAsync(Join(segment1, segment2, more));

    /// <summary>Deletes by segments.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="segment1">The first segment.</param>
    /// <param name="segment2">The second segment.</param>
    /// <param name="more">Further segments.</param>
    [Obsolete(PathMessage)]
    public static bool Delete(this IWriteableFileProvider provider, string segment1, string segment2, params string[] more) =>
        Required(provider).Delete(Join(segment1, segment2, more));

    /// <summary>Opens a local copy: the 2026 parameter order.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="readOnly">Whether the stream is read-only.</param>
    /// <param name="pathSegments">The file's segments.</param>
    [Obsolete("Use CreateLocalFileStreamAsync(path, readOnly): the path comes first.")]
    public static Task<LocalFileStream> CreateLocalFileStreamAsync(this IWriteableFileProvider provider, bool readOnly, params string[] pathSegments) =>
        Required(provider).CreateLocalFileStreamAsync(pathSegments, readOnly);

    /// <summary>Opens a local copy: the 2026 parameter order.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="readOnly">Whether the stream is read-only.</param>
    /// <param name="pathSegments">The file's segments.</param>
    [Obsolete("Use CreateLocalFileStream(path, readOnly): the path comes first.")]
    public static LocalFileStream CreateLocalFileStream(this IWriteableFileProvider provider, bool readOnly, params string[] pathSegments) =>
        Required(provider).CreateLocalFileStream(pathSegments, readOnly);

    /// <summary>Opens a local copy: the 2026 parameter order.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="readOnly">Whether the stream is read-only.</param>
    /// <param name="file">The file, as the provider returned it.</param>
    [Obsolete("Use CreateLocalFileStreamAsync(file, readOnly): the file comes first.")]
    public static Task<LocalFileStream> CreateLocalFileStreamAsync(this IWriteableFileProvider provider, bool readOnly, IFileInfo file) =>
        Required(provider).CreateLocalFileStreamAsync(provider.PathOf(file), readOnly);

    /// <summary>Opens a local copy: the 2026 parameter order.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="readOnly">Whether the stream is read-only.</param>
    /// <param name="file">The file, as the provider returned it.</param>
    [Obsolete("Use CreateLocalFileStream(file, readOnly): the file comes first.")]
    public static LocalFileStream CreateLocalFileStream(this IWriteableFileProvider provider, bool readOnly, IFileInfo file) =>
        Required(provider).CreateLocalFileStream(file, readOnly);

    /// <summary>Watches for changes, asynchronously in name only.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="filter">The files to watch.</param>
    [Obsolete("Use Watch(filter).")]
    public static Task<IChangeToken> WatchAsync(this IWriteableFileProvider provider, string filter) =>
        Task.FromResult(Required(provider).Watch(filter));

    private static IWriteableFileProvider Required(IWriteableFileProvider provider) => WriteableFileProviderExtensions.Required(provider);

    private static ProviderPath Join(string segment1, string segment2, string[] more) => new([segment1, segment2, .. more ?? []]);
}
