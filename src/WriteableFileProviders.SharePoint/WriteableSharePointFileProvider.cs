using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Drives.Item.Items.Item;
using Microsoft.Graph.Drives.Item.Items.Item.Children;
using Microsoft.Graph.Drives.Item.Items.Item.Content;
using Microsoft.Graph.Drives.Item.Items.Item.Copy;
using Microsoft.Graph.Drives.Item.Items.Item.CreateUploadSession;
using Microsoft.Graph.Models;

namespace WriteableFileProviders.SharePoint;

/// <summary>
/// A writeable file provider over a SharePoint document library (a OneDrive for Business drive), through Microsoft Graph.
/// </summary>
/// <remarks>
/// <para>
/// Folders are real: an empty folder exists, and writes create missing parents. Names are compared without regard to
/// case, so moving <c>a.txt</c> to <c>A.txt</c> renames it. Deleted items go to the site's recycle bin. Path segments may
/// not contain <c>:</c>, Graph's path syntax; a <c>%</c> is sent escaped, so <c>a%41</c> names an item literally called
/// <c>a%41</c>. Listings leave out items whose names the provider cannot address, such as one that decodes to
/// <c>..</c>. A file whose size Graph does not report is measured (its download URL, then a request for its first
/// byte), so its length is real.
/// </para>
/// <para>
/// Preconditions are checked, not atomic: <see cref="UpdateFileOptions.IfMatch"/> and
/// <see cref="UpdateFileOptions.IfNotExists"/> are checked just before the write, and also sent to the service
/// (<c>If-Match</c>, and the conflict behaviour <c>fail</c>), which the service enforces on uploads it documents them for.
/// An append rewrites the file with <c>If-Match</c> on the version it read, so a concurrent change it sees throws
/// <see cref="PreconditionFailedException"/>. Reads pinned to a version check the ETag before each fetch, which is not
/// atomic either.
/// </para>
/// <para>
/// Signed URLs are read-only: the service's own pre-authenticated download URLs, which live up to an hour. A validity
/// under an hour throws <see cref="NotSupportedException"/>, because the URL could outlive it, and a longer one needs
/// <see cref="SignedUrlOptions.AllowShorterValidity"/>.
/// </para>
/// </remarks>
public sealed partial class WriteableSharePointFileProvider : WriteableFileProvider<SharePointFileProviderOptions>
{
    private const string GraphScope = "https://graph.microsoft.com/.default";
    private const string ConflictBehaviorTemplate = "{?%40microsoft.graph.conflictBehavior}";
    private const string ConflictBehaviorParameter = "%40microsoft.graph.conflictBehavior";
    private const string ConflictBehaviorAnnotation = "@microsoft.graph.conflictBehavior";
    private const string DownloadUrlAnnotation = "@microsoft.graph.downloadUrl";
    private const int PageSize = 200;
    private static readonly string[] _itemFields = ["id", "name", "size", "eTag", "file", "folder", "lastModifiedDateTime"];
    private static readonly string[] _downloadFields = ["id", "size", "eTag", "file", "folder", DownloadUrlAnnotation];
    private static readonly string[] _idField = ["id"];
    private static readonly string[] _folderFields = ["id", "folder"];
    private static readonly string[] _driveFields = ["id", "name"];
    private static readonly DateTimeOffset _unknownTime = new(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly GraphServiceClient _graph;
    private readonly HttpClient? _ownedHttpClient;
    private readonly X509Certificate2? _ownedCertificate;
    private readonly bool _ownsGraph;
    private readonly string[] _rootSegments;
    private readonly Uri _siteUrl;
    private readonly SemaphoreSlim _driveLock = new(1, 1);
    private string? _driveId;

    /// <summary>
    /// Creates the provider from its options, signing in as configured (see <see cref="SharePointFileProviderOptions"/>).
    /// No request is sent until the first call; the site and library are looked up then.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// A required option is missing or invalid: the site URL, the tenant and client ids, a secret or a certificate that
    /// loads with its password, or a root that is a strict prefix of the work folder.
    /// </exception>
    public WriteableSharePointFileProvider(SharePointFileProviderOptions options)
        : this(options, finalHandler: null, baseUrl: null)
    {
    }

    /// <summary>
    /// Creates the provider over a Graph client the caller built and owns (managed identity, Aspire, custom retries or
    /// handlers, tests): only <see cref="SharePointFileProviderOptions.SiteUrl"/>, the library, the work folder and the
    /// root are read from the options. The provider never disposes <paramref name="client"/>.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <param name="client">The Graph client. Downloads, upload fragments and copy monitoring also go through its
    /// request adapter, to hosts other than Graph's; its authentication provider must not send a Graph token there (the
    /// SDK's Azure Identity provider does not).</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="client"/> is null.</exception>
    /// <exception cref="ArgumentException">The site URL is missing or invalid, or the root is a strict prefix of the work folder.</exception>
    public WriteableSharePointFileProvider(SharePointFileProviderOptions options, GraphServiceClient client)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(client);
        _siteUrl = ValidateSiteUrl(Options);
        _rootSegments = ResolveRoot(Options);
        _graph = client;
    }

    /// <summary>The options constructor, with the HTTP handler and base URL a test points at an in-memory Graph.</summary>
    internal WriteableSharePointFileProvider(SharePointFileProviderOptions options, HttpMessageHandler? finalHandler, string? baseUrl)
        : base(options)
    {
        _siteUrl = ValidateSiteUrl(Options);
        _rootSegments = ResolveRoot(Options);
        X509Certificate2? certificate = null;
        HttpClient? httpClient = null;
        try
        {
            var credential = CreateCredential(Options, out certificate);
            // The SDK's default pipeline (retries with Retry-After, redirects) in front of the handler. GraphServiceClient
            // never disposes an HttpClient it is given, so the provider keeps and disposes its own.
            httpClient = GraphClientFactory.Create(finalHandler: finalHandler);
            _graph = new GraphServiceClient(httpClient, credential, [GraphScope], baseUrl);
        }
        catch
        {
            httpClient?.Dispose();
            certificate?.Dispose();
            throw;
        }
        _ownedHttpClient = httpClient;
        _ownedCertificate = certificate;
        _ownsGraph = true;
    }

    /// <summary>The largest file written in one request; larger files go through an upload session.</summary>
    internal long SimpleUploadLimit { get; set; } = 4L * 1024 * 1024;

    /// <summary>The size of an upload session's fragments: a multiple of 320 KiB, as Graph requires.</summary>
    internal int UploadFragmentSize { get; set; } = 16 * 320 * 1024;

    /// <summary>The first wait between two polls of a copy's monitor; it doubles up to <see cref="MaxCopyPollDelay"/>.</summary>
    internal TimeSpan CopyPollDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The longest wait between two polls of a copy's monitor.</summary>
    internal TimeSpan MaxCopyPollDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The longest a server-side copy is waited for before the copy throws <see cref="IOException"/>.</summary>
    internal TimeSpan CopyTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The longest a download URL lives, as Graph documents it.</summary>
    internal static TimeSpan DownloadUrlLifetime { get; } = TimeSpan.FromHours(1);

    /// <inheritdoc />
    /// <remarks>
    /// False: the base class checks the conditions before the write, and the provider also sends them to the service.
    /// Graph documents <c>If-Match</c> when an upload session is created (not again at its commit), and does not document
    /// it on a simple upload, so the check is not promised to be atomic until a live run shows a stale <c>If-Match</c>
    /// on a simple upload refused.
    /// </remarks>
    protected override bool SupportsAtomicPreconditions => false;

    /// <inheritdoc />
    protected override bool IsCaseInsensitive => true;

    /// <inheritdoc />
    protected override bool SupportsSignedUrl(SignedUrlAccess permission) => permission == SignedUrlAccess.Read;

    /// <inheritdoc />
    /// <remarks>Refuses <c>:</c>, Graph's path syntax.</remarks>
    protected override bool IsSegmentAllowed(string part) => part is not null && !part.Contains(':', StringComparison.Ordinal);

    /// <inheritdoc />
    protected override bool IsNotFound(Exception exception) => exception is not null && GraphErrors.IsNotFound(exception);

    /// <inheritdoc />
    /// <remarks>
    /// Graph's 401 and 403, and Microsoft Entra ID refusing the credential (Azure.Identity's
    /// <see cref="AuthenticationFailedException"/>, which the SDK raises inside each request, as it asks for the token).
    /// The provider maps the errors it reads itself; these are what reaches the base class untyped. A sign-in that finds no
    /// credential at all (<see cref="CredentialUnavailableException"/>) is not a refusal: the provider throws
    /// <see cref="InvalidOperationException"/> for it.
    /// </remarks>
    protected override bool IsAccessDenied(Exception exception) =>
        exception is AuthenticationFailedException and not CredentialUnavailableException
        || (exception is not null && GraphErrors.Status(exception) is 401 or 403);

    /// <inheritdoc />
    protected override async Task<IWriteableFileInfo> GetFileInfoCoreAsync(ResolvedPath path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        var driveId = await DriveIdAsync(cancellationToken).ConfigureAwait(false);
        var item = await TryGetItemAsync(driveId, LibrarySegments(path), _itemFields, $"Reading '{path}'", cancellationToken).ConfigureAwait(false);
        return (item is null ? null : await ToInfoAsync(driveId, path, item, cancellationToken).ConfigureAwait(false))
            ?? WriteableFileInfo.Missing(this, path.Path);
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<IWriteableFileInfo> EnumerateCoreAsync(ResolvedPath path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        var driveId = await DriveIdAsync(cancellationToken).ConfigureAwait(false);
        var request = ChildrenRequest(driveId, LibrarySegments(path));
        var page = await GetPageAsync(request, null, path, cancellationToken).ConfigureAwait(false);
        while (page is not null)
        {
            foreach (var item in page.Value ?? [])
            {
                // An entry measured as gone (deleted while listed) is left out.
                if (item.Name is { Length: > 0 } name && IsAddressable(name)
                    && await ToInfoAsync(driveId, path.Child(name), item, cancellationToken).ConfigureAwait(false) is { } entry)
                {
                    yield return entry;
                }
            }
            page = page.OdataNextLink is { } next ? await GetPageAsync(request, next, path, cancellationToken).ConfigureAwait(false) : null;
        }
    }

    /// <inheritdoc />
    protected override async Task<bool> DeleteCoreAsync(ResolvedPath path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        var driveId = await DriveIdAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ItemRequest(driveId, LibrarySegments(path)).DeleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (GraphErrors.IsNotFound(e))
        {
            return false;
        }
        catch (Exception e) when (GraphErrors.IsFailure(e))
        {
            throw GraphErrors.Wrap(e, $"Deleting '{path}'");
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_ownsGraph)
                _graph.Dispose();
            _ownedHttpClient?.Dispose();
            _ownedCertificate?.Dispose();
            _driveLock.Dispose();
        }
        base.Dispose(disposing);
    }

    // ----- Construction -----

    private static Uri ValidateSiteUrl(SharePointFileProviderOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SiteUrl))
            throw new ArgumentException("SiteUrl is required: the site's URL, such as https://contoso.sharepoint.com/sites/Finance.", nameof(options));
        // Only the host and the path are used, so http is taken as 2026 took it.
        if (!Uri.TryCreate(options.SiteUrl, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException($"SiteUrl '{options.SiteUrl}' is not an absolute http or https URL.", nameof(options));
        return url;
    }

    private string[] ResolveRoot(SharePointFileProviderOptions options)
    {
        var workFolder = SharePointPaths.WorkFolderSegments(options.WorkFolderPath);
        // A value copied from a browser holds escapes such as %20. 2026 sent them on and the service decoded them; 2027
        // sends every '%' as itself, so the same value would name another folder. Refused rather than reinterpreted.
        foreach (var (name, segments) in new[] { ("WorkFolderPath", workFolder), ("RootDirectoryPath", options.RootDirectorySegments) })
        {
            if (segments.FirstOrDefault(HasPercentEscape) is { } escaped)
            {
                throw new ArgumentException(
                    $"{name} holds the percent-escape in '{escaped}': decode it (write '%20' as a space), because the provider sends '%' as itself.",
                    nameof(options));
            }
        }
        var root = SharePointPaths.RootSegments(workFolder, options.RootDirectorySegments)
            ?? throw new ArgumentException(
                $"RootDirectoryPath '{string.Join('/', options.RootDirectorySegments)}' is the start of WorkFolderPath '{string.Join('/', workFolder)}'. "
                + "Since 2027.0.0 the root is a folder inside the work folder: set RootDirectoryPath to a folder inside it, or leave it "
                + "empty (docs/migration-2027.md, SharePoint).",
                nameof(options));
        try
        {
            var valid = ValidateSegments(root);
            foreach (var part in valid)
            {
                if (!IsSegmentAllowed(part))
                    throw new ArgumentException($"The path segment '{part}' is not allowed.", nameof(options));
            }
            return valid;
        }
        catch (ArgumentException e)
        {
            throw new ArgumentException($"WorkFolderPath and RootDirectoryPath must name a valid folder: {e.Message}", nameof(options), e);
        }
    }

    private static TokenCredential CreateCredential(SharePointFileProviderOptions options, out X509Certificate2? certificate)
    {
        certificate = null;
        if (options.UseDefaultAzureCredential)
        {
            var defaults = new DefaultAzureCredentialOptions();
            if (!string.IsNullOrWhiteSpace(options.TenantId))
                defaults.TenantId = options.TenantId;
            if (!string.IsNullOrWhiteSpace(options.ClientId))
                defaults.ManagedIdentityClientId = options.ClientId;
            return new DefaultAzureCredential(defaults);
        }
        if (string.IsNullOrWhiteSpace(options.TenantId) || string.IsNullOrWhiteSpace(options.ClientId))
            throw new ArgumentException("TenantId and ClientId are required to sign in with a client secret or a certificate.", nameof(options));
        var (tenantId, clientId) = (options.TenantId, options.ClientId);
        if (!string.IsNullOrWhiteSpace(options.ClientCertificateBase64))
        {
            certificate = LoadCertificate(options);
            return new ClientCertificateCredential(tenantId, clientId, certificate);
        }
        if (string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            throw new ArgumentException(
                "No sign-in is configured: set ClientSecret, or ClientCertificateBase64 (and ClientCertificatePassword), or UseDefaultAzureCredential.",
                nameof(options));
        }
        return new ClientSecretCredential(tenantId, clientId, options.ClientSecret);
    }

    private static X509Certificate2 LoadCertificate(SharePointFileProviderOptions options)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(options.ClientCertificateBase64!);
        }
        catch (FormatException e)
        {
            throw new ArgumentException("ClientCertificateBase64 is not base64.", nameof(options), e);
        }
        X509Certificate2 certificate;
        try
        {
            // An ephemeral key never touches the disk; macOS cannot hold one, so it keeps the key the default way.
            certificate = X509CertificateLoader.LoadPkcs12(bytes, options.ClientCertificatePassword,
                OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException e)
        {
            throw new ArgumentException("ClientCertificateBase64 is not a PKCS#12 (.pfx) file that ClientCertificatePassword opens.", nameof(options), e);
        }
        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new ArgumentException("ClientCertificateBase64 holds no private key: export the certificate with its key.", nameof(options));
        }
        return certificate;
    }

    // ----- The site and the library -----

    private async ValueTask<string> DriveIdAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _driveId) is { } cached)
            return cached;
        await _driveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Only a success is kept: a failure is retried on the next call.
            if (_driveId is null)
                Volatile.Write(ref _driveId, await ResolveDriveIdAsync(cancellationToken).ConfigureAwait(false));
            return _driveId!;
        }
        finally
        {
            _driveLock.Release();
        }
    }

    private async Task<string> ResolveDriveIdAsync(CancellationToken cancellationToken)
    {
        // The site is addressed as {host}:{server-relative path}. The path must be unescaped: the SDK escapes the key
        // again, so "Team%20Site" would reach Graph as "Team%2520Site".
        var sitePath = Uri.UnescapeDataString(_siteUrl.AbsolutePath).TrimEnd('/');
        var siteKey = $"{_siteUrl.Host}:{(sitePath.Length == 0 ? "/" : sitePath)}";
        Site? site;
        try
        {
            site = await _graph.Sites[siteKey].GetAsync(c => c.QueryParameters.Select = _idField, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (GraphErrors.IsNotFound(e))
        {
            throw new InvalidOperationException($"The SharePoint site '{Options.SiteUrl}' does not exist, or the app cannot see it (SiteUrl).", e);
        }
        catch (Exception e) when (GraphErrors.IsFailure(e))
        {
            throw GraphErrors.Wrap(e, $"Finding the site '{Options.SiteUrl}'");
        }
        // An answer without an id is the service's failure, not a site the options name wrongly: it is not cached, and the
        // next call asks again.
        var siteId = site?.Id ?? throw new IOException($"Finding the site '{Options.SiteUrl}' failed: Microsoft Graph returned no id for it.");

        var wanted = Options.DocumentLibraryName;
        var drives = _graph.Sites[siteId].Drives;
        try
        {
            var page = await drives.GetAsync(c => c.QueryParameters.Select = _driveFields, cancellationToken).ConfigureAwait(false);
            while (page is not null)
            {
                var match = (page.Value ?? []).FirstOrDefault(d => string.IsNullOrEmpty(wanted) || string.Equals(d.Name, wanted, StringComparison.OrdinalIgnoreCase));
                if (match?.Id is { } id)
                    return id;
                page = page.OdataNextLink is { } next ? await drives.WithUrl(next).GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false) : null;
            }
        }
        catch (Exception e) when (GraphErrors.IsFailure(e))
        {
            throw GraphErrors.Wrap(e, $"Listing the document libraries of '{Options.SiteUrl}'");
        }
        throw new InvalidOperationException(string.IsNullOrEmpty(wanted)
            ? $"The SharePoint site '{Options.SiteUrl}' has no document library."
            : $"The document library '{wanted}' does not exist in the SharePoint site '{Options.SiteUrl}' (DocumentLibraryName).");
    }

    // ----- Addressing -----
    //
    // ItemWithPath returns a CustomDriveItemItemRequestBuilder, which re-declares Children, Content, Copy and
    // CreateUploadSession. Held as the base DriveItemItemRequestBuilder, those would address the parent item, so each
    // sub-request has a helper of its own and only ItemRequest returns the base type (GetAsync, PatchAsync and DeleteAsync
    // bind correctly on it). The library's top level has no path: ItemWithPath("") throws, so it is items/root.
    //
    // ItemWithPath escapes what a URL path cannot hold but leaves a valid escape such as %41 as it is, which the service
    // then decodes. Every '%' is escaped first, so each segment reaches Graph as the name it is.

    private string[] LibrarySegments(ResolvedPath path) => [.. _rootSegments, .. path.RelativeSegments];

    private static string GraphPath(string[] segments) =>
        string.Join('/', segments.Select(segment => segment.Replace("%", "%25", StringComparison.Ordinal)));

    private DriveItemItemRequestBuilder ItemRequest(string driveId, string[] segments) =>
        segments.Length == 0 ? _graph.Drives[driveId].Items["root"] : _graph.Drives[driveId].Root.ItemWithPath(GraphPath(segments));

    private ChildrenRequestBuilder ChildrenRequest(string driveId, string[] segments) =>
        segments.Length == 0 ? _graph.Drives[driveId].Items["root"].Children : _graph.Drives[driveId].Root.ItemWithPath(GraphPath(segments)).Children;

    private ContentRequestBuilder ContentRequest(string driveId, string[] file) => _graph.Drives[driveId].Root.ItemWithPath(GraphPath(file)).Content;

    private CopyRequestBuilder CopyRequest(string driveId, string[] file) => _graph.Drives[driveId].Root.ItemWithPath(GraphPath(file)).Copy;

    private CreateUploadSessionRequestBuilder UploadSessionRequest(string driveId, string[] file) =>
        _graph.Drives[driveId].Root.ItemWithPath(GraphPath(file)).CreateUploadSession;

    // ----- Items -----

    private async Task<DriveItem?> TryGetItemAsync(string driveId, string[] segments, string[] fields, string what, CancellationToken cancellationToken)
    {
        try
        {
            return await ItemRequest(driveId, segments).GetAsync(c => c.QueryParameters.Select = fields, cancellationToken).ConfigureAwait(false)
                ?? throw new IOException($"{what} failed: Microsoft Graph answered with no item.");
        }
        catch (Exception e) when (GraphErrors.IsNotFound(e))
        {
            return null;
        }
        catch (Exception e) when (GraphErrors.IsFailure(e))
        {
            throw GraphErrors.Wrap(e, what);
        }
    }

    private static async Task<DriveItemCollectionResponse?> GetPageAsync(ChildrenRequestBuilder request, string? nextLink, ResolvedPath path, CancellationToken cancellationToken)
    {
        try
        {
            return nextLink is null
                ? await request.GetAsync(c =>
                {
                    c.QueryParameters.Select = _itemFields;
                    c.QueryParameters.Top = PageSize;
                }, cancellationToken).ConfigureAwait(false)
                : await request.WithUrl(nextLink).GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (nextLink is null && GraphErrors.IsNotFound(e))
        {
            return null;
        }
        catch (Exception e) when (GraphErrors.IsFailure(e))
        {
            throw GraphErrors.Wrap(e, $"Listing '{path}'");
        }
    }

    /// <summary>
    /// The entry for an item: a folder, or a file with its real length, measured when Graph does not report its size.
    /// Null when the file is gone by the time it is measured.
    /// </summary>
    private async ValueTask<WriteableFileInfo?> ToInfoAsync(string driveId, ResolvedPath path, DriveItem item, CancellationToken cancellationToken)
    {
        if (item.Folder is not null)
            return WriteableFileInfo.Folder(this, path.Path, item.LastModifiedDateTime);
        var length = item.Size ?? await MeasureAsync(driveId, path, cancellationToken).ConfigureAwait(false);
        return length is { } known
            ? new WriteableFileInfo(this, path.Path, true, false, known, item.LastModifiedDateTime ?? _unknownTime,
                physicalPath: null, etag: item.ETag, contentType: item.File?.MimeType)
            : null;
    }

    /// <summary>
    /// Makes sure the folder exists, creating what is missing one level at a time, and returns its id. No request is
    /// retried, so a folder that vanishes while it is created ends in an exception, never a loop.
    /// </summary>
    private async Task<string> EnsureFolderAsync(string driveId, string[] relativeSegments, string what, CancellationToken cancellationToken)
    {
        string[] segments = [.. _rootSegments, .. relativeSegments];
        var existing = await TryGetItemAsync(driveId, segments, _folderFields, what, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return FolderId(existing, segments, what);

        string? id = null;
        var creating = false;
        for (var depth = 1; depth <= segments.Length; depth++)
        {
            if (!creating)
            {
                var item = await TryGetItemAsync(driveId, segments[..depth], _folderFields, what, cancellationToken).ConfigureAwait(false);
                if (item is not null)
                {
                    id = FolderId(item, segments[..depth], what);
                    continue;
                }
                creating = true;
            }
            id = await CreateFolderAsync(driveId, segments[..(depth - 1)], segments[depth - 1], what, cancellationToken).ConfigureAwait(false);
        }
        return id ?? throw new IOException($"{what} failed: the folder '{string.Join('/', segments)}' could not be created.");
    }

    private async Task<string> CreateFolderAsync(string driveId, string[] parent, string name, string what, CancellationToken cancellationToken)
    {
        var folder = new DriveItem
        {
            Name = name,
            Folder = new Folder(),
            AdditionalData = new Dictionary<string, object> { [ConflictBehaviorAnnotation] = "fail" },
        };
        try
        {
            var created = await ChildrenRequest(driveId, parent).PostAsync(folder, cancellationToken: cancellationToken).ConfigureAwait(false);
            return created?.Id ?? throw new IOException($"{what} failed: Microsoft Graph returned no id for the new folder '{name}'.");
        }
        catch (Exception e) when (GraphErrors.Status(e) == 409)
        {
            // Another writer created it first.
            string[] path = [.. parent, name];
            var item = await TryGetItemAsync(driveId, path, _folderFields, what, cancellationToken).ConfigureAwait(false)
                ?? throw GraphErrors.Wrap(e, what);
            return FolderId(item, path, what);
        }
        catch (Exception e) when (GraphErrors.IsFailure(e))
        {
            throw GraphErrors.Wrap(e, what);
        }
    }

    private static string FolderId(DriveItem item, string[] segments, string what) =>
        item.Folder is null
            ? throw new IOException($"{what} failed: '{string.Join('/', segments)}' is a file, so it cannot hold a folder or a file.")
            : item.Id ?? throw new IOException($"{what} failed: Microsoft Graph returned no id for '{string.Join('/', segments)}'.");

    private static bool HasPercentEscape(string segment)
    {
        for (var i = segment.IndexOf('%', StringComparison.Ordinal); i >= 0; i = segment.IndexOf('%', i + 1))
        {
            if (i + 2 < segment.Length && Uri.IsHexDigit(segment[i + 1]) && Uri.IsHexDigit(segment[i + 2]))
                return true;
        }
        return false;
    }
}
