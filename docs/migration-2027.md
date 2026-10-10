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
| Deleting a blob with snapshots fails | Delete, move and the append-blob conversion delete the blob's snapshots too | Copy snapshots you need elsewhere first. |
| Options: shared key only | `UseDefaultAzureCredential` (Entra ID) and a constructor that takes a `BlobContainerClient` (D17), which the provider never disposes. `AccountKey` must be base64, and setting it together with `UseDefaultAzureCredential` throws | Fix a key that is not base64; it never worked. |

**Append blobs already stored (P22, P30).** They stay readable. The first write or append to one converts it to a
block blob: the new content goes to a temporary blob under `.wfp-tmp/` in the provider root, the append blob is deleted
if it has not changed, and the temporary blob is copied to its name. The file is missing for a moment in between; if
the copy fails, the exception names the temporary blob holding the content. A 2026.10.10.x provider cannot append to a
block blob (`InvalidBlobType`), so a fleet that mixes the two lines must not append to shared files during the switch:
upgrade every writer together. An upload SAS cannot replace an append blob either; write such a file once through the
provider first.
