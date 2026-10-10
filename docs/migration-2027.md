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
- **Defaults fail closed:** `PublicBaseUri` is required and gateway URLs never use the request's `Host`; download URLs
  live at most 7 days and upload URLs 1 hour by default; upload URLs work once; uploads are capped at 100 MiB.

The package README covers the options, the endpoints' status codes and headers, and how to keep tokens out of logs.
