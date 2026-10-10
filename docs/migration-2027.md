# Moving to WriteableFileProviders 2027.0.0

2027.0.0 is a major release: every package targets .NET 10, the API is rebuilt around one path type and cancellation,
and every provider follows one written contract. This guide is the ledger of what breaks and how to move over. It grows
with each change of the release; a provider's own section lands with that provider's rewrite.

Consumers on .NET 8 or 9 stay on the `2026.10.10.x` packages.

## Version numbers

The packages move from date versions (`2026.10.10.4`) to semantic versions that continue above them: the first is
`2027.0.0`, compatible releases are `2027.x.y`, and the next breaking release is `2028.0.0`, whatever the year. All
packages share one version.

## Common: the API

| 2026 | 2027 | How to move |
|---|---|---|
| `string subpath` and `params string[] pathSegments` overloads | One `ProviderPath` parameter, with implicit conversions from `string` (`"a/b"`) and `string[]` | Most calls compile unchanged. Calls with several string arguments, such as `ReadFileAsync("a", "b.txt")`, compile through an `[Obsolete]` shim: pass `"a/b.txt"` or `new[] { "a", "b.txt" }`. |
| No `CancellationToken` | Every async member takes one, last | Add it where you have one. |
| `UpdateFileAsync(Stream, UpdateFileMode, params string[])`, `UpdateFileAsync(string, UpdateFileMode, Stream)` | `UpdateFileAsync(path, contents, UpdateFileOptions?)` and the extension `UpdateFileAsync(path, contents, UpdateFileMode)` | The old orders compile through `[Obsolete]` shims. |
| `CreateLocalFileStreamAsync(bool readOnly, ...)` | `CreateLocalFileStreamAsync(path, bool readOnly)` | The path comes first; the old order compiles through a shim. |
| Sync members on the interface (`ReadFile`, `UpdateFile`, `Delete`, `Move`, `CreateLocalFileStream`, `GetDownloadUrl`) | Extension methods in `WriteableFileProviderExtensions` | Add `using WriteableFileProviders;` if you call them on the interface. `GetFileInfo(string)`, `GetDirectoryContents(string)` and `Watch(string)` stay on the interface (`IFileProvider`). |
| `GetDownloadUrl(...)` returns `string` | `GetSignedUrlAsync(path, SignedUrlOptions)` returns a `SignedUrl`; the extension `GetDownloadUrlAsync(path, validity)` returns a `Uri` | `string url = ...` becomes `var url = ...` or `.ToString()`. |
| `WatchAsync(string)` | `Watch(string)` | The async form compiles through a shim. |
| `FileInfo` (it clashed with `System.IO.FileInfo`) | `WriteableFileInfo`, and the interface `IWriteableFileInfo` with `Path`, `ETag`, `ContentType` | Replace `(FileInfo)x` with `(WriteableFileInfo)x`; `PathSegments` becomes `Path.Segments`. |
| Overloads that take an `IFileInfo` accepted any entry, falling back to its `PhysicalPath` | They take only entries a writeable provider returned (`IWriteableFileInfo`), and throw `ArgumentException` for any other | For an entry from elsewhere, pass its path instead. |
| `DirectoryContents`: a `public class` built from `(bool, IFileInfo[])`, null entries allowed | A `public sealed class` built from `(bool, IEnumerable<IFileInfo>)`; null entries throw `ArgumentNullException` | Stop deriving from it; pass an empty sequence instead of null. |
| `GetFileInfoAsync` returns `IFileInfo` | It returns `IWriteableFileInfo` | Nothing to do; it is an `IFileInfo`. |
| `IWriteableFileProvider.ToJson()` returns Newtonsoft's `JObject` | `WriteableFileProvider<T>.ToJson()` and `WriteableFileProviderSerializer.ToJson(provider)` return `System.Text.Json.Nodes.JsonObject`; `ToJsonString` returns text | Newtonsoft.Json is no longer a dependency of any package. |
| `WriteableFileProviderSerializer.FromJson(JObject, ...)` | `FromJson(string | JsonElement | JsonObject, ...)` | Pass the JSON text. |
| `OnLocalFileStreamDisposingAsync` on the interface | Gone: the base class commits a local stream through the path it resolved when the stream was opened | Nothing to do unless you implemented it. |
| `LocalFileStream`'s public constructor, `TargetFilePathSegments` | Only providers create it; `Path` replaces `TargetFilePathSegments` | Create local streams with `CreateLocalFileStreamAsync`. |
| `LocalFileStream.DisposeAsync()` returns `Task` and hid `Stream.DisposeAsync()` | A real `ValueTask DisposeAsync()` override; `CommitAsync()` uploads explicitly | `await using` works the same; disposing through `Stream` no longer blocks. |
| Providers were not disposable | `IWriteableFileProvider` is `IDisposable` and `IAsyncDisposable` | Dispose providers you create (the DI container disposes the ones it creates). |

New in Common: `ReadRangeAsync` (byte ranges), `OpenReadSeekableAsync` (a seekable stream that reads ranges on
demand), `EnumerateAsync` (one level or recursive, as `IAsyncEnumerable`), `CopyAsync`, `UpdateFileOptions` (content
type, `IfMatch`, `IfNotExists`), `GetSignedUrlAsync` (read, create and write URLs), `RangeReadStream`, and DI
registration (`services.AddWriteableFileProvider(name, configuration)`).

## Common: writing your own provider

`WriteableFileProvider<TOptions>`'s protected members are renamed to the .NET convention and take a validated
`ResolvedPath` and a `CancellationToken`:

| 2026 | 2027 |
|---|---|
| `getFileInfoAsync(params string[] absolutePathSegments)` | `GetFileInfoCoreAsync(ResolvedPath, CancellationToken)` |
| `getDirectoryContentsAsync(...)` returning `IDirectoryContents` | `EnumerateCoreAsync(ResolvedPath, CancellationToken)` returning `IAsyncEnumerable<IWriteableFileInfo>`; the base builds the listing |
| `readFileAsync(...)` | `ReadFileCoreAsync(...)` |
| `updateFileAsync(Stream, UpdateFileMode, ...)` | `UpdateFileCoreAsync(ResolvedPath, Stream, UpdateFileOptions, CancellationToken)` |
| `deleteAsync(...)` | `DeleteCoreAsync(...)` |
| `move(string[], string[])` | `MoveCoreAsync(ResolvedPath, ResolvedPath, CancellationToken)`; optional, the default copies then deletes |
| `getDownloadUrl(string[], TimeSpan)` | `GetSignedUrlCoreAsync(ResolvedPath, SignedUrlOptions, CancellationToken)` with `SupportsSignedUrl(access)` |
| `watchAsync(string)` (abstract) | `WatchCore(string)`, optional |
| `createLocalFileStreamAsync(bool, params string[])` (protected virtual), and overriding the public virtual `CreateLocalFileStreamAsync(bool, params string[])` | `CreateLocalFileStreamCoreAsync(ResolvedPath, bool, CancellationToken)`, optional. The public `CreateLocalFileStreamAsync(ProviderPath, bool, CancellationToken)` is no longer virtual, so an override of it no longer compiles |
| `isSegmentAllowed`, `validateSegments` | `IsSegmentAllowed`, `ValidateSegments` |
| `getRelativeSegments`, `isRootPath` | Gone by 2027.0.0 (`[Obsolete]` until then): `ResolvedPath.RelativeSegments` and `ResolvedPath.IsRoot` |
| `normalizePem` | Gone by 2027.0.0 (`[Obsolete]` until then): keep your own helper |
| `normalizePath(string)` | `ProviderPath.Parse(string)` |
| `fileSegments(IFileInfo)` | `IWriteableFileInfo.Path` |
| `RunTaskSerial(...)`, both overloads | Gone: await the task |

Optional overrides with shared defaults: `ReadRangeCoreAsync`, `OpenReadSeekableCoreAsync`, `CopyCoreAsync`,
`MoveCoreAsync`, `DeleteFileCoreAsync`, `FolderExistsCoreAsync`, `EnumerateRecursiveCoreAsync`,
`CreateLocalFileStreamCoreAsync`, `IsNotFound`, `Dispose(bool)`, `DisposeAsyncCore`. Helpers: `AppendByRewriteAsync`,
`SpoolToTempFileAsync`, `NotFound`.

What the store can do is declared through these, and each default makes the base class refuse or weaken an operation
until the provider says otherwise:

| Member | Default | What the default does |
|---|---|---|
| `SupportsAtomicPreconditions` | false | The base checks `IfMatch` and `IfNotExists` just before the write: checked, not atomic. |
| `IsCaseInsensitive` | false | Moving `a.txt` to `A.txt` is an ordinary move. When true it is a rename: the destination check is skipped, and the default `MoveCoreAsync` goes through a temporary name. |
| `SupportsSignedUrl(access)` | false | `GetSignedUrlAsync` throws `NotSupportedException`. |
| `SupportsSignedUrlContentHeaders(access)` | false | A signed URL request that sets `DownloadFileName` or `ContentType` throws `NotSupportedException`. |
| `MaxSignedUrlValidity` | 7 days | A longer validity throws `NotSupportedException`. |
| `TimeProvider` (property) | `TimeProvider.System` | The clock for expiry times; a test can set another. |

Every file needs an `ETag`: the default seekable read pins it, and `AppendByRewriteAsync` writes with `IfMatch` on it.
Without one, a pinned seekable read and an append by rewrite throw `NotSupportedException`.

Run your provider against `ProviderConformanceTests` (tests/WriteableFileProviders.Testing) to check it meets the
contract.

## Common: behaviour

The base class now enforces these on every provider:

- **Missing source:** `MoveAsync` and `CopyAsync` throw `FileNotFoundException` when the source is missing or is a folder.
- **Existing destination:** they throw `FileExistsException` when the destination exists, and leave it unchanged.
- **The root is a folder:** `GetFileInfoAsync` on the root returns an existing folder, and a listing of the root always
  exists.
- **Case-only renames:** on a store that ignores case (Physical on Windows and macOS, Box, SharePoint), moving `a.txt`
  to `A.txt` renames the file.
- **Local streams:** a read-only local stream of a missing file or of a folder throws `FileNotFoundException` on every
  provider; a writeable one of a folder throws `IOException`. Every local stream is a temp copy, Physical included, so
  nothing reaches the file before the commit.
- **Signed URL validity:**
  - a validity that is not positive throws `ArgumentOutOfRangeException`;
  - one longer than the provider can sign throws `NotSupportedException` (Azure no longer caps it silently at 7 days);
  - a read URL for a missing file throws `FileNotFoundException`;
  - `DownloadFileName` and `ContentType` are carried by the URL, or the call throws `NotSupportedException`: a URL never
    silently serves the stored name and type instead.
- **Pinned reads and appends:** a pinned seekable read (the default) or an append by rewrite of a file the store keeps
  no `ETag` for throws `NotSupportedException`; `SeekableReadOptions.AllowModifications` reads it unpinned.
- **Content type kept:** an append by rewrite, and the commit of a local stream, keep the file's content type when the
  caller gives none (2026 stored the store's default, such as `application/octet-stream`, after an append or an edit).
  An overwrite without one still stores the default.
- **Unsupported operations:** they throw `NotSupportedException`, never `PlatformNotSupportedException`.
- **Invalid path segments:** an `ArgumentException` for one names the parameter `path`.

## Amazon S3 and MinIO

Both providers now run on one implementation of the S3 protocol, on the AWS SDK for .NET v4 (AWSSDK.S3 4.x).
WriteableFileProviders.Minio no longer depends on the Minio SDK, so Minio, Microsoft.Extensions 9 and System.Reactive
leave your dependency graph; reference the Minio SDK yourself if you used its types. If you use AWSSDK.S3 directly,
follow AWS's v3-to-v4 migration guide: response collections can be null, value types are nullable, checksums are on by
default, and the obsolete `S3Region` short names (`US`, `EU`, `SFO`…) are gone.

### Amazon S3

| 2026 | 2027 | How to move |
|---|---|---|
| `ReadFileAsync(string versionId, params string[] absolutePathSegments)`, `EnumerateObjects(string[], bool)` (obsolete since the 2026.10 security patch) | Removed | `ReadFileVersionAsync(versionId, path)` and `EnumerateAsync(path, recursive: true)`, both relative to the root. |
| `ReadFileVersionAsync(string versionId, params string[] pathSegments)` | `ReadFileVersionAsync(string? versionId, ProviderPath path, CancellationToken)` | A call with one path string compiles unchanged; pass several segments as `"a/b.txt"` or an array. |
| `ReadFileAsync(string subpath)` on the concrete type | Removed | `ReadFileAsync(path)` of the interface, which a string converts to. |
| `WriteableAmazonS3FileProvider` could be derived from | It is `sealed` (D25) | Derive a custom provider from `WriteableFileProvider<TOptions>`, or wrap this one. |
| Missing file: `AmazonS3Exception` (NoSuchKey) | `FileNotFoundException`, with the SDK's error inside | Catch `FileNotFoundException`. |
| A folder: `Exists` false, `IsDirectory` true; `PhysicalPath` the key | `Exists` and `IsDirectory` true; `PhysicalPath` null | Use `IWriteableFileInfo.Path`. |
| Move overwrote an existing destination | `FileExistsException`; it deletes exactly the source key | Delete the destination first if you meant to replace it. |
| A missing bucket failed with the SDK's error | `InvalidOperationException` naming the bucket, unless `CreateBucketIfMissing` is set | Create the bucket, or set the option. |
| One PUT per write, at most 5 GB, and the caller's stream was disposed | Multipart above 8 MiB (up to 5 TiB); the stream is left open | Dispose your stream yourself. |
| A failed append could upload a half-appended file (F4) | An append is a rewrite with `If-Match`: it fails whole, and a concurrent write makes it throw `PreconditionFailedException` | Retry on `PreconditionFailedException`. |
| Content type inferred from the key's extension | The `ContentType` you give, else `application/octet-stream`; an append keeps the file's type | Pass `UpdateFileOptions.ContentType` where you relied on the inference. |
| Download URLs forced `Content-Type: application/octet-stream` | Read URLs serve the stored type unless `SignedUrlOptions.ContentType` says otherwise; create and write URLs are new | Set `ContentType` on the URL to keep the old header. |
| A presigned URL lived as asked, whatever the credential | A credential that expires sooner, or a session token, needs `AllowShorterValidity` (D18) | Use long-term keys, or accept the shorter URL. |
| The S3Region constructors stored `""` for us-east-1 and `"EU"` for eu-west-1 | They store `us-east-1` and `eu-west-1`, and the old values read as those regions | Nothing to do. |
| A key with an empty segment, `..` or a backslash was listed | Left out of listings, since no path can name it | Rename such keys with another tool. |

New: `ServiceUrl` and `ForcePathStyle` for S3-compatible servers, `UseDefaultCredentials` for the SDK's default
credential chain, `CreateBucketIfMissing`, and a constructor that takes your own `IAmazonS3`. The new options are written
to JSON only when set (D26), so a configuration that uses none of them keeps its 2026 shape.

### MinIO

| 2026 | 2027 | How to move |
|---|---|---|
| `WriteableMinioFileProvider` could be derived from | It is `sealed` (D25) | Derive a custom provider from `WriteableFileProvider<TOptions>`, or wrap this one. |
| `UseSSL` false sent everything over plain HTTP to any host | Plain HTTP only toward a loopback host; any other host throws `ArgumentException` when the provider is built (D7) | Set `UseSSL`, or `AllowInsecureHttp` for a server you reach over plain HTTP on purpose. |
| A missing bucket was created on the first write | `InvalidOperationException` naming the bucket, unless `CreateBucketIfMissing` is set | Set `CreateBucketIfMissing: true` to keep the old behaviour. |
| Missing file: `ObjectNotFoundException`; missing bucket: `BucketNotFoundException` | `FileNotFoundException`; `InvalidOperationException` | Catch those. |
| `ReadFileAsync` downloaded the whole file to a seekable temp file | A forward-only network stream | Use `OpenReadSeekableAsync` (ranged reads) or `CreateLocalFileStreamAsync` (a local copy) where you seek. |
| Download URLs threw `NotSupportedException` | Presigned read, create and write URLs, up to 7 days | Nothing to do. |
| Every file was stored as `application/octet-stream` | The `ContentType` you give is stored; without one, still `application/octet-stream` | Nothing to do. |
| Move overwrote an existing destination | `FileExistsException`, checked before the copy (MinIO ignores `If-None-Match` on copies, so the check is not atomic) | Delete the destination first if you meant to replace it. |
| The Minio SDK asked the server for the bucket's region | The provider asks once (`GetBucketLocation`), or takes the new `Region` option | Set `Region` for a server with a site region whose bucket does not exist yet. |

**Finding the configurations D7 breaks:** search your configuration files and stores for `WriteableMinioFileProvider`
entries whose `UseSSL` is `false` or absent and whose `HostAndPort` is not `localhost`, a `127.x.x.x` address or `[::1]`.
Each needs `"UseSSL": true`, or `"AllowInsecureHttp": true` if plain HTTP is intended. `HostAndPort` still takes
`host:port` only: a scheme, a path or a query is refused, as the Minio SDK refused it.

`IfMatch`, `IfNotExists` and the protection of concurrent appends need a MinIO release with conditional writes; older
servers ignore the headers. New: `AllowInsecureHttp`, `Region`, `CreateBucketIfMissing`, and a constructor that takes your
own `IAmazonS3`. The new options are written to JSON only when set (D26).

## Stored configuration

The shape is unchanged:

- a `Type` key holding the provider's class name, matched case-insensitively;
- the options' property names, read case-insensitively;
- both root properties.

Everything a 2026 version stored reads the same, with one deliberate difference: S3's `StorageClassForNewFiles`, which
2026 wrote as `{"Value":"STANDARD_IA"}` but read back as `STANDARD`, now reads correctly in both forms and is written as
the string `"STANDARD_IA"`.

**JSON written by 2027.0.0 is not promised to read back in 2026.10.10.x**, so do not share stored configuration between
the two versions once 2027 has written it.

A `Type` is found among the assemblies passed in, then this library's own providers (their assembly is loaded when
needed), then types registered with `WriteableFileProviderSerializer.Register<TProvider>()`. Loaded assemblies are no
longer searched, so a third-party provider must be registered or its assembly passed in.

## New package: WriteableFileProviders.AspNetCore

2027.0.0 adds `WriteableFileProviders.AspNetCore`, for ASP.NET Core apps on .NET 10. Nothing in 2026 maps onto it, so
there is nothing to migrate; it is the way to keep handing out download links where a provider's own URLs changed:

- **`IWriteableFileUrlService.GetSignedUrlAsync(providerName, path, options)`** asks the keyed provider for its native
  URL and, when the provider throws `NotSupportedException`, issues a URL of the app's own gateway instead. A provider
  throws it when it cannot sign the URL asked for: Physical signs none, and Box and SharePoint sign only download URLs
  whose lifetime their service sets. So the service gives a 2026 caller of `GetDownloadUrl` a URL that does what was
  asked on every provider.
- **The gateway** (`AddWriteableFileProviderGateway`, `MapWriteableFileProviderGateway`) serves `GET`, `HEAD` (with
  HTTP ranges) and `PUT` on `{BasePath}/{token}/{fileName?}`. The token is an ASP.NET Core Data Protection payload, so
  an app on several instances shares its key ring, and one-time upload URLs need a shared `IGatewayReplayStore`.
- **Defaults fail closed:** `PublicBaseUri` is required and gateway URLs never use the request's `Host`; gateway
  download URLs live at most 7 days and gateway upload URLs 1 hour by default; gateway upload URLs work once; uploads
  are capped at 100 MiB; a download is served as the content type its read URL names, else
  `application/octet-stream`, under a sandboxing `Content-Security-Policy`. These rules bind gateway URLs only: a
  provider's native upload URL can be reused until it expires and lives up to the provider's maximum, so an app that
  needs the same rules everywhere sets `PreferGateway`.

The package README covers the options, the endpoints' status codes and headers, and how to keep tokens out of logs.

## Google Cloud Storage

`WriteableFileProviders.GoogleCloudStorage` moves to `Google.Cloud.Storage.V1` 5.0 (from 4.15). If you use that SDK
directly, its 5.0 break is small: `UploadValidationException` and `UploadValidationMode.ThrowOnly` are gone, and uploads
are validated by the server (`RejectAndThrow`) by default.

| 2026 | 2027 | How to move |
|---|---|---|
| The constructor looked up Application Default Credentials synchronously, so it did network I/O and threw where there were none | The constructor reads the key only; the client is built, and Application Default Credentials looked up, on the first call | Nothing to do; a configuration without credentials fails on its first call instead of at construction. |
| A key it could not read threw `FormatException`; a missing bucket name threw `ArgumentException` for `BucketName` | Both throw `ArgumentException` for `options`, as does an `Endpoint` that is not a root URL | Catch `ArgumentException`. |
| Only the options constructor | Also `new WriteableGoogleCloudStorageFileProvider(StorageClient, options)`, which never disposes the client | For your own credentials, retries, HTTP handling or encryption key. |
| `WriteableGoogleCloudStorageFileProvider` could be derived from | It is `sealed` (D25) | Derive a custom provider from `WriteableFileProvider<TOptions>`, or wrap this one. |
| No way to reach another endpoint | `Endpoint`: a root URL, such as fake-gcs-server's `http://localhost:4443` or a Private Service Connect endpoint. Plain `http` toward a host that is not loopback throws `ArgumentException` unless `AllowInsecureHttp` is set (D7) | Additive. |
| A missing bucket: listings threw `GoogleApiException`, `GetFileInfo` said "not found", writes threw `GoogleApiException` | Every member throws `InvalidOperationException` naming the bucket; `CreateBucketIfMissing` (default false) creates it on the first call instead | Create the bucket, or set the option (with `ProjectId` when the key does not name the project). |
| A missing file threw `GoogleApiException` 404 | `FileNotFoundException`, with the `GoogleApiException` as `InnerException` | Catch `FileNotFoundException`. |
| `GetFileInfo` on a folder said it did not exist | A folder that holds objects exists and is a directory, with `Length` -1 | Check `IsDirectory`. |
| `PhysicalPath` held the object's name | `PhysicalPath` is null; `IWriteableFileInfo.Path` is the path | Use `Path`. |
| `ReadFile` downloaded the whole object to a temp file and returned it, seekable | It returns the download as it arrives: read forward, with `Length` when the server sends it | Use `OpenReadSeekableAsync` to seek, or `ReadRangeAsync` for a slice; both download only what is read. |
| Move and copy overwrote an existing destination | They throw `FileExistsException` and leave it unchanged; a move deletes exactly the generation it copied, and throws `PreconditionFailedException` if the source was written meanwhile, keeping that write | Delete the destination first if you meant to replace it. |
| An append whose source stream failed uploaded a half-appended file; a concurrent write could be lost | An append leaves the file unchanged when it fails, and throws `PreconditionFailedException` instead of losing a concurrent write | Retry the append on `PreconditionFailedException`. |
| The caller's stream was disposed after a write | It is left open | Dispose it yourself. |
| Read URLs only, with the stored name and type | Read, create (only if absent) and write URLs; a read URL can carry `DownloadFileName` and `ContentType` | See the package README for the headers an upload must send. |
| A validity over seven days threw the SDK's `ArgumentOutOfRangeException`; a credential that could not sign threw `PlatformNotSupportedException`; a URL was signed for a missing file | Over seven days, or under one second, throws `NotSupportedException` (so the gateway can take over), and the validity is rounded down to whole seconds; a credential that cannot sign throws `NotSupportedException`; a read URL for a missing file throws `FileNotFoundException` | Catch `NotSupportedException`. |

New: `ETag` is the object's generation, and `IfMatch` and `IfNotExists` are atomic; `ContentType` is stored
(`application/octet-stream` when a write gives none, as in 2026; an append or a local stream keeps the file's) and
reported. Listings leave out objects whose names no path can address, such as `a//b`. A copy or move is not retried by
the SDK: when one fails with a server error or a timeout, the copy may still have landed, so check the destination
before you try again. `GetFileInfoAsync` of a path that is not a file now lists objects to tell a folder, so a
credential needs `storage.objects.list` for it, as for listings, copies and appends to a new file (it gets a 403
otherwise). The new options, `Endpoint`, `AllowInsecureHttp` and `CreateBucketIfMissing`, are written to JSON only when
set (D26), so a configuration that uses none of them keeps its 2026 shape.

## Azure Blob Storage (WriteableFileProviders.AzureBlob)

Azure.Storage.Blobs moves from 12.23 to 12.30. The package also references Azure.Identity 1.21.0, whose types now live
in Azure.Core; the reference keeps an older Azure.Identity of yours from defining them twice. The configuration shape is
unchanged; the two new options are written to JSON only when `true`.

| 2026 | 2027 | How to move |
|---|---|---|
| Files are append blobs; an overwrite empties the blob, then appends, so a failed write truncates the file (F3) | Files are block blobs, staged and committed in one request: a failed write keeps the old content, and readers never see half of it | Nothing to do. |
| A missing container is created by the first write or move; reads report it as missing files and empty listings | A missing container makes every member throw `InvalidOperationException`, naming the container (F44, P18) | Create the container, or set `CreateContainerIfMissing: true` to keep the old creation on the first write. Reads never create it. |
| Reading a missing file throws `RequestFailedException` (404) | `FileNotFoundException`, with the SDK's error as `InnerException` (F18) | Catch `FileNotFoundException`. |
| `ReadFileAsync` returns `AzureFileStream`, a public type that reports `Length` | The SDK's download stream, read forward only; `AzureFileStream` is gone (F15, F64, F70) | Take the length from `GetFileInfoAsync`, or use `OpenReadSeekableAsync`, a seekable stream that knows its length and reads ranges. |
| `MoveAsync` onto an existing file replaces it | `FileExistsException`, destination unchanged (F19). A source that changes while it is moved is kept, the copy removed, and `PreconditionFailedException` thrown | Delete the destination first if you meant to replace it. |
| `GetFileInfo` of a folder: not existing; of a missing path: `Length` 0; `PhysicalPath` is the blob name | A folder exists and is a directory; a missing path has `Length` -1; `PhysicalPath` is null; files carry `ETag` and `ContentType` (F20, F21) | Use `IWriteableFileInfo.Path` where you used `PhysicalPath`. |
| A download URL starts now, and a validity over 7 days is cut to 7 days silently | It starts 15 minutes in the past (F40) and ends exactly at the validity; over 7 days throws `NotSupportedException` (F11); a URL for a missing file throws `FileNotFoundException` | Ask for at most 7 days, or use the gateway. |
| Read URLs only, signed with the account key | Read, create and write URLs; with Microsoft Entra ID, user delegation SAS | See the README's "Signed URLs". |
| Deleting a blob with snapshots fails (`RequestFailedException`) | It still fails, now with `IOException`, and so does a move of such a blob; snapshots are never deleted, except by the append blob conversion below | Delete the snapshots first. |
| `WriteableAzureBlobFileProvider` can be derived from | It is sealed (D25) | Wrap it instead, or derive from `WriteableFileProvider<AzureFileProviderOptions>`. |
| Two overwrites of one file at once: both commit, one after the other | The one that commits second finds its blocks dropped by the first: one block's worth is staged again, so the last writer still wins; a longer one throws `IOException` and stores nothing | Write it again, or serialize the writers. |
| Options: shared key only | `UseDefaultAzureCredential` (Entra ID) and a constructor that takes a `BlobContainerClient` (D17), which the provider never disposes. `AccountKey` must be base64, and setting it together with `UseDefaultAzureCredential` throws | Fix a key that is not base64; it never worked. |

**Append blobs already stored (P22, P30).** They stay readable. The first write or append to one converts it to a
block blob: the new content goes to a temporary blob under `.wfp-tmp/` in the provider root, the append blob is deleted
if it has not changed, and the temporary blob is copied to its name. Meanwhile the file is missing, or empty while the
server copy runs; if
the copy fails, the exception names the temporary blob holding the content. A 2026.10.10.x provider cannot append to a
block blob (`InvalidBlobType`), so a fleet that mixes the two lines must not append to shared files during the switch:
upgrade every writer together. An upload SAS cannot replace an append blob either; write such a file once through the
provider first.

## SharePoint

The SharePoint provider moves to Microsoft.Graph 6 and Microsoft.Graph.Core 4 (Kiota 2); code that uses those SDKs
directly moves with it. `WriteableSharePointFileProvider` is now `sealed`: wrap an `IWriteableFileProvider` (or derive
from `WriteableFileProvider<TOptions>`) instead of deriving from the provider. The public `UploadResult<T>` class is
gone (F70).

### The work folder is the start of the root (D7)

The provider's root is `WorkFolderPath` followed by `RootDirectoryPath`. When `RootDirectoryPath` starts with the work
folder (ignoring case), that one leading copy is dropped, so a configuration that set both to the same folder keeps its
files. Caller paths are never rewritten any more. 2026 removed a leading copy of the work folder from every path, so
these configurations place some files elsewhere now:

| Configuration | Path | 2026 | 2027 |
|---|---|---|---|
| `WorkFolderPath` `Reports`, no root | `Reports/2026.csv` (any case) | `Reports/2026.csv` | `Reports/Reports/2026.csv` |
| `WorkFolderPath` `Main/Work`, no root | `Main/Work/a.txt` | `Main/Work/a.txt` | `Main/Work/Main/Work/a.txt` |
| `WorkFolderPath` `Main/Work`, no root | `Main/Work` (the work folder itself) | the root: refused for writes and deletes | a folder `Main/Work` inside the root |
| `WorkFolderPath` `Main/Work`, `RootDirectoryPath` `Main` | `Work/a.txt` / `a.txt` | `Main/Work/a.txt` / `Main/Work/Main/a.txt` | refused at construction: `ArgumentException` |
| `WorkFolderPath` or `RootDirectoryPath` holding a percent-escape, such as `Team%20Files` copied from a browser | any | under the decoded name, `Team Files` | refused at construction: `ArgumentException` asking to decode it |

Every other shape places files where 2026 did: no work folder; a root inside the work folder; a root that repeats the
work folder, in any case, with more after it; a root that shares only part of the work folder. To find what moves,
search your code and stored paths for paths that start with the work folder's name, and drop that prefix, or move the
files. A root that is the start of the work folder has no equivalent: set `RootDirectoryPath` to a folder inside the
work folder (or leave it empty) and move the files that 2026 wrote under the second copy.

The work folder is created on the first write instead of in the constructor; reading under a missing one finds nothing.

### Behaviour

| 2026 | 2027 |
|---|---|
| The constructor signed in and looked up the site, library and work folder, blocking on the network; a failure threw `InvalidOperationException("Failed to initialize SharePoint connection")` | No I/O in the constructor (F13). The site and library are looked up on the first call; a missing one throws `InvalidOperationException` naming `SiteUrl` or `DocumentLibraryName` from every member, and any other failure is retried on the next call |
| A site URL with an escaped character (`Team%20Site`) was escaped twice and not found | It is found |
| Every Graph error while reading file info meant "missing" or `FileNotFoundException` (F66) | Only a 404 means missing. 401 and 403 throw `UnauthorizedAccessException`; throttling after the SDK's retries, and every other error, `IOException`; each wraps Graph's error |
| An empty folder listed as `Exists = false` (F43) | It exists |
| A folder's `Length` was the size of its contents, a missing path's 0, and `PhysicalPath` held the path or the name (F20, F21) | -1, -1 and null |
| Writes rewound the caller's stream, a simple upload disposed it, and a missing parent retried without end (F6, F8) | Writes read from the stream's position, leave it open, and create missing parents once |
| Upload sessions of 3.2 MB slices with no retry, ending silently on a short read (F39) | 5 MiB fragments from a temp copy; a transient failure is resent once, any other cancels the session and throws |
| Appends combined in memory or a temp file in two ways (F4) | One append by rewrite, with `If-Match` on the version read (D19) |
| Reads downloaded the whole file to a temp file first; a 0-byte file threw `FileNotFoundException` | Reads stream; ranges are native; a 0-byte file reads empty |
| Move was a lookup and a `PATCH` | One `PATCH` with the conflict behaviour `fail`: a taken destination throws `FileExistsException`. Copy runs on the server, waited for at most 10 minutes |
| A path segment with a percent-escape (`a%41`) reached the service unescaped, which decoded it (`aA`) | The `%` is escaped, so `a%41` names an item called `a%41`. Files 2026 wrote through such a path are under the decoded name. `WorkFolderPath` and `RootDirectoryPath` with such an escape are refused instead, so a stored value never silently names another folder. Listings leave out names the provider cannot address, such as one that decodes to `..` |
| `UpdateFileOptions` did not exist | `IfMatch` and `IfNotExists` are checked before the write and sent to the service; not promised atomic (D15). `ContentType` is ignored: SharePoint names the type from the file name |
| `GetDownloadUrl` returned the download URL whatever validity was asked (F11) | A read URL (`SignedUrlKind.PreAuthenticated`, `ExpiresOn` null) only for a validity of an hour or more, and only with `AllowShorterValidity`; a shorter validity throws `NotSupportedException`, so the gateway can serve it (D18) |

### Sign-in

New options, all optional (D17): `ClientCertificateBase64` and `ClientCertificatePassword` (a base64 PKCS#12 file),
and `UseDefaultAzureCredential` (managed identity, workload identity, environment, developer sign-in). The first one
configured wins: `UseDefaultAzureCredential`, then the certificate, then `ClientSecret`. The new constructor
`WriteableSharePointFileProvider(options, GraphServiceClient)` takes a client you build and own; the provider never
disposes it. Stored 2026 configurations read unchanged. `ToJson` writes the three new keys only when they are set, so a
configuration that uses none of them is written exactly as 2026 wrote it.

## Box

The provider is rebuilt on `Box.V2.Core` 10 (the generated SDK, namespace `Box.Sdk.Gen`), and follows the contract.

| 2026 | 2027 | How to move |
|---|---|---|
| `Box.V2.Core` 5.8 (namespace `Box.V2`) | `Box.V2.Core` 10.19 (namespace `Box.Sdk.Gen`); v10 no longer has the `Box.V2` namespace | Code of yours that uses the Box SDK directly moves to `Box.Sdk.Gen` (Box's own guide: `migration-guides/from-v5-to-v10.md` in `box/box-windows-sdk-v2`). Newtonsoft.Json and Portable.BouncyCastle leave the graph; the SDK brings BouncyCastle.Cryptography 2, System.IdentityModel.Tokens.Jwt 8 and Microsoft.Extensions.Http and DependencyInjection 8. |
| The constructor signed in to Box, blocking on the network (and threw `AggregateException` on a bad credential) | No network I/O in the constructor: it checks that every credential is set and that the private key opens with the passphrase (`ArgumentException`), and signs in on the first call | A wrong client id or secret now fails on the first call, with the SDK's `BoxApiException`. `WriteableFileProviderSerializer` builds a Box provider offline. |
| `WriteableBoxFileProvider` could be derived from | It is `sealed` (D25) | Wrap it, or write a provider on `WriteableFileProvider<TOptions>`. |
| Only the options constructor | Also `WriteableBoxFileProvider(BoxFileProviderOptions, IBoxClient)`: a ready client (another authentication, as-user, a proxy, custom retries), which the provider never disposes | Optional. With a client, only `RootFolderId` and the root folder are read from the options. |
| `GetFileInfo` on a folder: `Exists` false, `IsDirectory` true | `Exists` true, `IsDirectory` true, `Length` -1; a missing path has `Length` -1 (was 0) | Test `IsDirectory` and `Exists` as for every provider. |
| `PhysicalPath`: the joined path from `GetFileInfo`, the bare name in listings | Always null | Use `IWriteableFileInfo.Path`. |
| Overwrite rewound a seekable stream and uploaded it from byte 0 | Every write uploads from the stream's current position | Rewind the stream yourself if you meant to upload it all. |
| Move created no folders (`DirectoryNotFoundException`), moved folders, and wrapped Box's 409 in `BoxException` | Moves files only, creates the destination's folders, and throws `FileExistsException` when the destination exists | Move a folder's files one by one, or use the Box SDK. |
| Errors were re-thrown as `BoxException` with the path in the message | The SDK's `BoxApiException` (status in `ResponseInfo.StatusCode`), except where the contract names an exception (`FileNotFoundException`, `FileExistsException`, `PreconditionFailedException`, `ArgumentException` for a name Box refuses, `InvalidOperationException` for a `RootFolderId` that does not exist or the app cannot see). A failed download throws `FileNotFoundException` or `IOException` with its status only, since the SDK's error names the pre-authenticated URL. When the provider built its client, or was given exactly a `BoxClient`, every `BoxApiException` it throws or wraps keeps Box's status, code and message, and of the request only its method, its URL without the query, and an allow-list of headers and parameters (the SDK keeps the access token, extra headers, the query and the body, and a token request's body holds the client secret); over any other `IBoxClient`, a class derived from `BoxClient` included, the errors are the client's own and may carry its credentials | Catch the contract's exceptions, or `BoxApiException`. |
| Writing under a file threw `InvalidOperationException` | `IOException` | Catch `IOException`. |
| Folder listings paged by offset (500 a page) | Marker paging, 1000 a page | Nothing to do. |
| `ReadFile` downloaded the whole file to a temp file first | It streams the download; ranged and seekable reads fetch only their bytes, pinned to the version they opened | Nothing to do. |
| `GetDownloadUrl(validity)` ignored the validity: Box's URL lives about 15 minutes | `GetSignedUrlAsync` refuses a validity under 15 minutes (`NotSupportedException`, so the gateway serves it), and returns Box's URL for 15 minutes or more only with `AllowShorterValidity`, with `ExpiresOn` set (D18) | Ask for at least 15 minutes with `AllowShorterValidity = true`, or use the gateway. `GetDownloadUrl(path, validity)` throws `NotSupportedException` for Box, since it cannot accept a shorter URL. |
| Chunked uploads from 20,000,000 bytes through the SDK's helper | From 20 MiB, through the provider's own upload-session loop (exact part sizes, part and whole-file digests, a bounded wait while Box processes the commit, the session aborted on failure) | Nothing to do. |

A file's `ETag` (new in 2027) is the id of its current Box file version, not Box's `etag`, which starts again at `0`
for a file deleted and created again. `IfNotExists` is atomic on Box. `IfMatch` is checked just before the write, not atomically, until a live run confirms
that Box matches the quoted ETag the SDK sends (D15 as amended in S10); appends are checked the same way (D19). Box
keeps no content type, so `ContentType` is ignored on writes and null on reads.
