# Moving to WriteableFileProviders 2027.0.0

Every breaking change from `2026.10.10.x`, and what to do about it. Work through the checklist, then read the sections
for Common and for each provider you use.

## Start here

- [ ] **Target `net10.0`.** Apps on .NET 8 or 9 stay on `2026.10.10.x`, which gets security fixes until a date
  announced in [SECURITY.md](../SECURITY.md).
- [ ] **Update every WriteableFileProviders package to 2027.0.0 in one change.** A 2026 provider does not load against
  Common 2027. Each provider now depends on Common `[2027.0.0, 2028.0.0-0)`, so NuGet warns (NU1608) on a mix.
- [ ] **Move to the async API.** Each operation is one async member that takes a `CancellationToken`. The sync forms,
  the `IFileInfo` and `UpdateFileMode` overloads and `GetDownloadUrl` still compile, with an `[Obsolete]` warning that
  names the replacement. They go in 2028.0.0.
- [ ] **Pass one path.** Every member takes a `ProviderPath`, which a string or a string array converts to. A call on
  the root needs `ProviderPath.Root`. See [Common: the API](#common-the-api).
- [ ] **Dispose the providers you create** (`await using`).
- [ ] **Move JSON code to System.Text.Json.** Newtonsoft.Json is gone, and `ToJson` returns a `JsonObject`. Stored
  configuration reads the same, except a root with a dot segment. See [Stored configuration](#stored-configuration).
- [ ] **Stop deriving from provider classes.** They are sealed. Wrap one (implement `IWriteableFileProvider` and
  delegate), or derive from `WriteableFileProvider<TOptions>`.
- [ ] **Catch the contract's exceptions**, not the SDK's. See [What a member throws](#what-a-member-throws).
- [ ] **Expect `NotSupportedException` from a signed URL** the provider can't sign as asked. `IWriteableFileUrlService`
  in [WriteableFileProviders.AspNetCore](#new-package-writeablefileprovidersaspnetcore) falls back to your app's
  gateway.
- [ ] **Read your provider's section.** The biggest changes:

| Provider | Biggest changes |
|---|---|
| [Physical](#physical) | Writes go through a temp file and a rename. Readers no longer block deletes, or writes on Linux and macOS. |
| [Azure Blob Storage](#azure-blob-storage) | Block blobs; 2026's append blobs convert on their first write. A missing container is created only with `CreateContainerIfMissing`. |
| [Amazon S3](#amazon-s3) | The content type is no longer guessed from the name. Moves refuse an existing destination. The absolute-path members are gone. |
| [MinIO](#minio) | Runs on the AWS SDK. Plain HTTP only to a loopback host unless `AllowInsecureHttp`. A missing bucket is created only with `CreateBucketIfMissing`. |
| [Google Cloud Storage](#google-cloud-storage) | No network I/O in the constructor. Reads stream. A missing bucket throws instead of reading as a missing file. |
| [Box](#box) | Box.V2.Core 10 (`Box.Sdk.Gen`). Signs in on the first call. Moves create folders and refuse an existing destination. |
| [SharePoint](#sharepoint) | Microsoft Graph 6. `WorkFolderPath` is the start of the root, and caller paths are no longer rewritten. |

The same calls in 2026 and 2027. The 2026 forms compile on 2027, with warnings, until 2028.0.0:

```csharp
// 2026
Stream report = provider.ReadFile("reports", "2026.csv");
provider.UpdateFile(contents, UpdateFileMode.Append, "logs", "app.log");
string url = provider.GetDownloadUrl(new[] { "reports", "2026.csv" }, TimeSpan.FromHours(1));
```

```csharp
// 2027
await using Stream report = await provider.ReadFileAsync("reports/2026.csv", ct);
await provider.UpdateFileAsync("logs/app.log", contents, UpdateFileOptions.Append, ct);
SignedUrl url = await provider.GetSignedUrlAsync(
    "reports/2026.csv", new SignedUrlOptions { Validity = TimeSpan.FromHours(1) }, ct);
```

## Version numbers

- 2026 used date versions (`2026.10.10.4`). 2027 uses semantic versions above them, so NuGet orders every 2027 release
  after every earlier one.
- The major is a sequence number, not a date: `2027.x.y` releases are compatible, and the next break is `2028.0.0`.
- All packages share one version. See the [versioning policy](guide/versioning.md).

## Common: the API

| 2026 | 2027 | What to do |
|---|---|---|
| `string subpath` and `params string[] pathSegments` overloads | One `ProviderPath`, converted from a `string` (`"a/b"`) or a `string[]` | Nothing for one string. Several string arguments (`ReadFileAsync("a", "b.txt")`) compile through an `[Obsolete]` shim: pass `"a/b.txt"` or `new[] { "a", "b.txt" }`. |
| A call with no path meant the root: `GetDirectoryContentsAsync()`, `GetFileInfoAsync()` | Doesn't compile | Pass `ProviderPath.Root` or `""`. `CreateLocalFileStream(Async)(readOnly)` and `UpdateFile(Async)(contents, mode)` with no path still compile, through the shims, and throw at run time. |
| `IFileProvider` members | `GetFileInfo(string)`, `GetDirectoryContents(string)` and `Watch(string)` still take a string | Nothing. |
| No `CancellationToken` | Every async member takes one, last | Pass yours. |
| `UpdateFileAsync(Stream, UpdateFileMode, params string[])`, `UpdateFileAsync(string, UpdateFileMode, Stream)` | `UpdateFileAsync(path, contents, UpdateFileOptions?)`; `UpdateFileOptions.Append` or `.Overwrite` sets the mode | Put the path first and pass `UpdateFileOptions.Append`. The old orders, and `UpdateFileAsync(path, contents, UpdateFileMode)`, compile through shims. |
| `CreateLocalFileStreamAsync(bool readOnly, ...)` | `CreateLocalFileStreamAsync(path, bool readOnly)` | Put the path first. The old order compiles through a shim. |
| Sync members on the interface: `GetFileInfo` and `GetDirectoryContents` with segments, `ReadFile`, `UpdateFile`, `Delete`, `Move`, `CreateLocalFileStream` | `[Obsolete]` extension methods in the `WriteableFileProviders` namespace; they go in 2028.0.0 | Call the async member: `await ReadFileAsync(path)` for `ReadFile(path)`. The old calls compile with `using WriteableFileProviders;`. |
| `GetDownloadUrl(...)` and `GetDownloadUrlAsync(...)` return a `string` | `GetSignedUrlAsync(path, SignedUrlOptions)` returns a `SignedUrl`; its `Url` is the URL. The old names are `[Obsolete]` shims that return its `AbsoluteUri`; they go in 2028.0.0 | `(await provider.GetSignedUrlAsync(path, new SignedUrlOptions { Validity = validity })).Url`, or `IWriteableFileUrlService` from the AspNetCore package. |
| `WatchAsync(string)` | `Watch(string)` | The async form compiles through a shim. |
| `FileInfo` | `WriteableFileInfo`, sealed; the interface `IWriteableFileInfo` adds `Path`, `ETag` and `ContentType` | Replace `(FileInfo)x` with `(WriteableFileInfo)x`. `PathSegments` becomes `Path.Segments` (an `IReadOnlyList<string>`). |
| `GetFileInfoAsync` returns `IFileInfo` | It returns `IWriteableFileInfo` | Nothing: it is an `IFileInfo`. |
| Overloads that take an `IFileInfo` accepted any entry, falling back to its `PhysicalPath` | `[Obsolete]`, and they go in 2028.0.0. They accept only an `IWriteableFileInfo`, and throw `ArgumentException` for any other | Pass the entry's path: `MoveAsync(entry.Path, destination)`. Entries from `EnumerateAsync` are `IWriteableFileInfo`; cast those from `GetDirectoryContentsAsync`. |
| `DirectoryContents`: a `public class` built from `(bool, IFileInfo[])`, null entries allowed | A `public sealed class` built from `(bool, IEnumerable<IFileInfo>)`; a null sequence throws `ArgumentNullException` | Stop deriving from it. Pass an empty sequence, not null. |
| `IWriteableFileProvider.ToJson()` returns Newtonsoft's `JObject` | Off the interface. `WriteableFileProviderSerializer.ToJson(provider)` and `WriteableFileProvider<T>.ToJson()` return a `System.Text.Json.Nodes.JsonObject`; `ToJsonString` returns text | Call `WriteableFileProviderSerializer.ToJson(provider)` on an `IWriteableFileProvider`. |
| `WriteableFileProviderSerializer.FromJson(JObject, ...)` | `FromJson` takes a `string`, a `JsonElement` or a `JsonObject` | Pass the JSON text. |
| `OnLocalFileStreamDisposingAsync` on the interface | Gone: the base class commits a local stream through the path it resolved when the stream opened | Nothing, unless you implemented it. |
| `LocalFileStream`: public constructor, `TargetFilePathSegments` | Sealed, with an internal constructor: only Common creates one. `Path` replaces `TargetFilePathSegments` | Create local streams with `CreateLocalFileStreamAsync`. |
| `LocalFileStream.DisposeAsync()` returned `Task` and hid `Stream.DisposeAsync()` | A real `ValueTask DisposeAsync()` override; `CommitAsync()` uploads explicitly | Nothing: `await using` works the same, and disposing through `Stream` no longer blocks. |
| Providers were not disposable | `IWriteableFileProvider` is `IDisposable` and `IAsyncDisposable` | Dispose the providers you create. The DI container disposes the ones it creates. |
| Implementing `IWriteableFileProvider` directly | The interface is new: `Options` and every member above | Derive from `WriteableFileProvider<TOptions>`, or implement every member (a wrapper delegates them all). |
| Common brought in Microsoft.Extensions.Configuration, Configuration.Binder and Newtonsoft.Json | It depends only on the `.Abstractions` packages of Configuration, DependencyInjection and FileProviders | If you used `ConfigurationBuilder`, `Bind` or `Get<T>` through this dependency, reference those packages yourself. |

The `[Obsolete]` shims live in `WriteableFileProviderCompatibilityExtensions`, hidden from IntelliSense. Each names the
call that replaces it, and they all go in 2028.0.0.

New in Common:

- `ReadRangeAsync`: a byte range.
- `OpenReadSeekableAsync`: a seekable stream that reads ranges on demand.
- `EnumerateAsync`: one level or recursive, as an `IAsyncEnumerable`.
- `CopyAsync`.
- `UpdateFileOptions`: content type, `IfMatch`, `IfNotExists`.
- `GetSignedUrlAsync`: read, create and write URLs, with `SignedUrlOptions`.
- `services.AddWriteableFileProvider(configuration)` or `AddWriteableFileProvider(factory)`, in the
  `Microsoft.Extensions.DependencyInjection` namespace: a singleton `IWriteableFileProvider` that the container builds
  and disposes. Each call adds one, and `GetServices` sees them all. To tell several apart, use keyed services
  (`AddKeyedSingleton<IWriteableFileProvider>(key, ...)`).

## Common: writing your own provider

`WriteableFileProvider<TOptions>`'s protected members follow .NET naming and take a validated `ResolvedPath` and a
`CancellationToken`. The base class now does more for every provider: it validates paths, the root and signed URL
requests, and checks moves, copies and preconditions.

| 2026 | 2027 | What to do |
|---|---|---|
| `getFileInfoAsync(params string[] absolutePathSegments)` | `GetFileInfoCoreAsync(ResolvedPath, CancellationToken)` | Rename; take the resolved path. |
| `getDirectoryContentsAsync(...)` returning `IDirectoryContents` | `EnumerateCoreAsync(ResolvedPath, CancellationToken)` returning `IAsyncEnumerable<IWriteableFileInfo>` | Yield the entries; the base builds the listing. |
| `readFileAsync(...)` | `ReadFileCoreAsync(...)` | Rename. |
| `updateFileAsync(Stream, UpdateFileMode, ...)` | `UpdateFileCoreAsync(ResolvedPath, Stream, UpdateFileOptions, CancellationToken)` | Honour the options' mode, content type and conditions. |
| `deleteAsync(...)` | `DeleteCoreAsync(...)` | Rename. |
| `move(string[], string[])` | `MoveCoreAsync(ResolvedPath, ResolvedPath, CancellationToken)`, optional | The default copies, then deletes exactly the source. |
| `getDownloadUrl(string[], TimeSpan)` | `GetSignedUrlCoreAsync(ResolvedPath, SignedUrlOptions, CancellationToken)` with `SupportsSignedUrl(access)` | Override both, or neither (the call then throws `NotSupportedException`). |
| `watchAsync(string)`, abstract | `WatchCore(string)`, optional | Override it only if the store can watch. |
| `createLocalFileStreamAsync(bool, params string[])` (protected virtual), or an override of the public virtual `CreateLocalFileStreamAsync(bool, params string[])` | Removed. The public `CreateLocalFileStreamAsync(ProviderPath, bool, CancellationToken)` is not virtual: every local stream is a temp copy, read through `ReadFileCoreAsync` and committed through `UpdateFileCoreAsync` | Drop the override; move what it did into those two members. |
| `new FileInfo(provider, exists, length, physicalPath, name, lastModified, isDirectory, pathSegments)` | `new WriteableFileInfo(provider, path, exists, isDirectory, length, lastModified, physicalPath?, etag?, contentType?)`; `Name` comes from the path. `WriteableFileInfo.Missing` and `WriteableFileInfo.Folder` build the common cases | Reorder the arguments. |
| Store exceptions reached callers as the provider let them out | The base maps what escapes a `...CoreAsync` member, or a stream or listing it returns: `IsNotFound` → `FileNotFoundException`, the new `IsAccessDenied` → `UnauthorizedAccessException`, a contract type as it is, anything else → `IOException`, the original inside | Override `IsNotFound` and `IsAccessDenied` for your SDK. Throw a contract type yourself where you know better. Route reads of your own public members through `ReadWithContractAsync`. |
| `RootDirectorySegments` used as given | The base constructor checks the root as a path, and keeps a copy that `Resolve` uses: a later change to the options does not move the provider | Read the root through `Resolve(ProviderPath.Root)`, not `Options`. Override `ValidatesRootDirectory` to return false when the root is not a path in your store (such as a folder of the disk). |
| `isSegmentAllowed`, `validateSegments` | `IsSegmentAllowed`, `ValidateSegments` | Rename. |
| `getRelativeSegments`, `isRootPath` | Removed | Use `ResolvedPath.RelativeSegments` and `ResolvedPath.IsRoot`. |
| `normalizePem` | Removed | Keep your own helper. |
| `normalizePath(string)` | `ProviderPath.Parse(string)` | Rename. |
| `fileSegments(IFileInfo)` | `IWriteableFileInfo.Path` | Read the entry's path. |
| `RunTaskSerial(...)`, both overloads | Removed | Await the task. |

- **Optional overrides**, with shared defaults: `ReadRangeCoreAsync`, `OpenReadSeekableCoreAsync`, `CopyCoreAsync`,
  `MoveCoreAsync`, `DeleteFileCoreAsync`, `FolderExistsCoreAsync`, `EnumerateRecursiveCoreAsync`, `IsNotFound`,
  `IsAccessDenied`, `ValidatesRootDirectory`, `Dispose(bool)`, `DisposeAsyncCore`.
- **Helpers:** `AppendByRewriteAsync`, `SpoolToTempFileAsync`, `NotFound`, `ReadWithContractAsync` (a read under the
  same error contract as `ReadFileAsync`), and `IsAddressable(name)`, true when your path validation accepts the name
  as one segment.
- The base constructor calls `IsSegmentAllowed` and `ValidatesRootDirectory` before your constructor runs: they must
  not depend on your provider's state.

Declare what the store can do. Each default makes the base refuse or weaken an operation:

| Member | Default | What the default does |
|---|---|---|
| `SupportsAtomicPreconditions` | false | The base checks `IfMatch` and `IfNotExists` just before the write: checked, not atomic. |
| `IsCaseInsensitive` | false | Moving `a.txt` to `A.txt` is an ordinary move. When true it is a rename: the destination check is skipped, and the default `MoveCoreAsync` goes through a temporary name. |
| `SupportsSignedUrl(access)` | false | `GetSignedUrlAsync` throws `NotSupportedException`. |
| `SupportsSignedUrlContentHeaders(access)` | false | A request that sets `DownloadFileName` or `ContentType` throws `NotSupportedException`. |
| `MaxSignedUrlValidity` | 7 days | A longer validity throws `NotSupportedException`. |
| `TimeProvider` (property) | `TimeProvider.System` | The clock for expiry times; a test can set another. |

- **Give every file an `ETag`.** The default seekable read pins it, and `AppendByRewriteAsync` writes with `IfMatch` on
  it. Without one, both throw `NotSupportedException`.
- **Leave unaddressable names out of listings**, as every built-in provider does: skip a name `IsAddressable`
  refuses, and don't walk into such a folder.
- **Test it** against `ProviderConformanceTests` (`tests/WriteableFileProviders.Testing` in the repository).
- **Register it** with `WriteableFileProviderSerializer.Register<TProvider>()` so the serializer finds it (see
  [Stored configuration](#stored-configuration)).

## Common: behaviour

The base class enforces these on every provider.

| 2026 | 2027 | What to do |
|---|---|---|
| Moving or copying a missing file, or a folder, failed differently per provider | `MoveAsync` and `CopyAsync` throw `FileNotFoundException` when the source is missing or is a folder | Catch `FileNotFoundException`. |
| Moving onto an existing file replaced it on some providers | `MoveAsync` and `CopyAsync` throw `FileExistsException` (an `IOException`) and leave the destination unchanged | Delete the destination first if you meant to replace it. |
| The root was not always a folder | `GetFileInfoAsync` of the root returns an existing folder, and a listing of the root always exists | Nothing. |
| A move between names that differ only in case failed on stores that ignore case | On a store that ignores case (Physical on Windows and macOS, Box, SharePoint), moving `a.txt` to `A.txt` renames the file | Nothing. |
| Local streams differed per provider; Physical's wrote to the file in place | Every local stream is a temp copy, committed once and atomically. A read-only one of a missing file or a folder throws `FileNotFoundException`; a writeable one of a folder throws `IOException` | Nothing, unless you relied on in-place writes. |
| Signed URL validities were ignored, clamped or refused per provider | A validity that isn't positive: `ArgumentOutOfRangeException`. Longer than the provider can sign: `NotSupportedException` (Azure no longer cuts it to 7 days). A read URL for a missing file: `FileNotFoundException`. `DownloadFileName` and `ContentType` are carried by the URL, or the call throws `NotSupportedException` | Catch `NotSupportedException`, or use the AspNetCore package's URL service, which falls back to its gateway. |
| Files had no ETags | A pinned seekable read (the default) or an append by rewrite of a file the store keeps no `ETag` for throws `NotSupportedException` | Set `SeekableReadOptions.AllowModifications` to read such a file unpinned. |
| An append or a local stream's commit stored the store's default content type, such as `application/octet-stream` | Both keep the file's content type when you give none. An overwrite without one still stores the default | Nothing. |
| Some providers threw `PlatformNotSupportedException` for an unsupported operation | `NotSupportedException` | Catch `NotSupportedException`. |
| An invalid path segment's `ArgumentException` named various parameters | It names the parameter `path` | Nothing. |
| Listings could show names no path could read back | Left out: an empty or whitespace name, only dots and spaces, a control character, a backslash, a name that percent-decodes to an invalid segment (`%2e%2e`), what a provider refuses itself, Physical's `.wfp-<guid>.tmp` files and Azure's `.wfp-tmp` folder at the root. Your own `.wfp-` names are listed | Rename such objects with another tool if you need them. Each provider's section has its cases. |
| Writing a file at a folder's path, or a folder under a file's path, did whatever the store did | **S3, MinIO, Azure, Google Cloud Storage** (folders are name prefixes): both writes succeed, both read back, and a listing shows both (MinIO lists only the file, and hides the folder's contents while the file exists). A delete removes the file, a second the folder. A writeable local stream of a folder's path still throws `IOException`. **Physical, Box, SharePoint**: either write throws `IOException` | On an object store, check `IsDirectory` on each listed entry. Delete the file to show a folder MinIO hides. |

## What a member throws

2026 let each SDK's exceptions out. In 2027 every member of every provider throws only these types:

| Type | When |
|---|---|
| `ArgumentException` (`ArgumentNullException`, `ArgumentOutOfRangeException`) | A path or an option the provider refuses. |
| `FileNotFoundException` | A file the member needs doesn't exist, or is a folder. |
| `FileExistsException` | The destination of a copy or a move exists. |
| `PreconditionFailedException` | `IfMatch` or `IfNotExists` didn't hold, or a pinned read's file changed. |
| `NotSupportedException` | A feature this provider or its store can't give. |
| `InvalidOperationException` | The store isn't set up as configured (a missing container, bucket, library or root folder, or no credentials to find), or the call would modify the root. |
| `UnauthorizedAccessException` | The store refused the credential or the permission: HTTP 401 or 403, or a refused sign-in. |
| `IOException` | Any other failure of the store, the service or the network, timeouts included. |
| `ObjectDisposedException` | The provider is disposed. |
| `OperationCanceledException` | Only when your cancellation token cancelled the call. |

- The SDK's exception is the `InnerException`, where there is one. An exception that already is one of these types
  passes as it is (a network `IOException`, say). `OutOfMemoryException` passes unmapped.
- The same holds for reads of a stream a member returned, each step of `EnumerateAsync`, and a local stream's commit.
- A failure of the stream you pass to `UpdateFileAsync` reaches you as it is when it is one of these types, and inside
  an `IOException` otherwise.

| 2026 | 2027 | What to do |
|---|---|---|
| The SDK's exception: `RequestFailedException`, `AmazonS3Exception`, `GoogleApiException`, `BoxException`, `HttpRequestException` | One of the types above, the SDK's exception inside | Catch the contract's type. Read a status or error code from `InnerException` where you need one. |
| A timeout as `TaskCanceledException` or `OperationCanceledException`, or inside an `AggregateException` | `IOException`, the timeout inside. `OperationCanceledException` only for your token | Handle `IOException` as you handled timeouts (retry, report). |
| A refused credential or permission, each SDK its own way | `UnauthorizedAccessException`, on every provider | Catch `UnauthorizedAccessException`. |

The guide's [Errors](guide/errors.md) page says which of each store's answers becomes which type.

## Stored configuration

The shape is unchanged. A stored configuration has, as before:

- a `Type` key holding the provider's class name, matched case-insensitively;
- the options' property names, read case-insensitively;
- both root properties.

| 2026 | 2027 | What to do |
|---|---|---|
| S3's `StorageClassForNewFiles` was written as `{"Value":"STANDARD_IA"}` and read back as `STANDARD` | From JSON, both forms read correctly, and it is written as the string `"STANDARD_IA"`. Configuration (`FromConfiguration`) reads the string form, which 2026 ignored; it still ignores the object form | In a configuration file, give the class as a string. |
| A `Type` was searched for in the loaded assemblies | It is looked up in the assemblies you pass, then this library's own providers (loading their assembly when needed), then types registered with `WriteableFileProviderSerializer.Register<TProvider>()`. Loaded assemblies are no longer searched | Register a third-party provider, or pass its assembly. |
| 2026 read what it wrote | **JSON written by 2027.0.0 is not promised to read back in 2026.10.10.x** | Don't share stored configuration between the two once 2027 has written it. |
| A `RootDirectoryPath` (or `RootDirectorySegments`) was used as written: on Azure, `tenants/../other` reached blobs outside the root, and `../secrets` another container | Every provider but Physical checks the root when it is built. A segment of only dots and spaces, with a control character, or that decodes to one (`..`, `.`, `%2e%2e`) throws `ArgumentException` naming `RootDirectoryPath` in its message. **A stored configuration with such a root no longer builds** | Name the folder itself (`other`, not `tenants/../other`). Physical's root is a folder of the disk, which the system resolves, as before. |

New options are written to JSON only when they differ from their defaults, so a configuration that uses none keeps its
2026 shape. Some new options change what an existing configuration does: MinIO's plain HTTP, Azure's and MinIO's
missing container or bucket, SharePoint's work folder. Each provider's section lists them.

## Physical

| 2026 | 2027 | What to do |
|---|---|---|
| An overwrite truncated the file first, so a failed write left part of the new content | A write goes to a `.wfp-<guid>.tmp` file next to the target, then a rename puts it in place: old content or new, never part. A failed write removes its temp file. Listings hide exactly `.wfp-` + 32 lowercase hex digits + `.tmp`; any other name, `.wfp-notes.tmp` included, is listed | Delete a `.wfp-<guid>.tmp` left by a process that died mid-write. A folder watcher sees a rename, not a write in place. |
| An append wrote in place | An append copies the file and the new bytes into the temp file, then renames: atomic, at the cost of a copy | Append in batches to files you append to often, such as logs. |
| A replaced file stayed the same file | A new file at the path, owned by the process. Linux and macOS: it takes the replaced file's permission bits (a link's target's), a copy its source's, whatever the umask; not setuid, setgid, sticky, ACLs or extended attributes. Windows: the folder's permissions for a new file. Other hard links keep the old content; a symbolic link at the path becomes a file, its target unchanged | Set permissions on the folder (inherited or default ACLs), not single files. Write to a link's target, not the link. Don't rely on hard links seeing writes. |
| A file the process couldn't write was refused (`UnauthorizedAccessException`) | Still refused, content kept, on every system. Also refused, content kept: a file in a folder the process can't write (`UnauthorizedAccessException`); a FIFO, socket or non-seekable device (`IOException`; 2026 opened it, and hung on a FIFO); a mount point, such as a single-file Docker bind mount or a Kubernetes `subPath` (`IOException`; 2026 wrote in place) | Write such files in place yourself, or mount their folder, not the file. |
| A local stream wrote in place, so `SkipReUploadOnDispose` did nothing | A temp copy committed atomically, as on every provider | Nothing. |
| Readers opened files with `FileShare.Read`, so a write or delete failed while a file was read | Readers share with writers and deleters, and keep the content they opened. A delete goes ahead, and so does a write on Linux and macOS. On Windows the write fails, content kept: `UnauthorizedAccessException` when the reader shares writing (this provider's readers do), `IOException` ("being used by another process") when it doesn't | On Windows, retry a write that meets an open reader, or dispose read streams sooner. |
| `GetFileInfo` of a folder reported a missing path; a missing path had a `PhysicalPath` | A folder exists: `IsDirectory`, `Length` -1, and below the root its `PhysicalPath`. A missing path has none | Check `IsDirectory`. |
| A missing file threw the BCL's `FileNotFoundException`, naming the path on disk | `FileNotFoundException` naming the path under the root | Read `FileName` as the provider path. |
| Moving onto an existing file threw a plain `IOException` | `FileExistsException` (an `IOException`); the destination is taken atomically | Catch `FileExistsException` if you told the cases apart. |
| No ETags, conditions or ranges; `Watch` threw `PlatformNotSupportedException` | An `ETag` from the last-write time and the length. `IfMatch`, checked just before the rename, not atomic. `ReadRangeAsync` through `RandomAccess`. `Watch` as `PhysicalFileProvider` does it (`DOTNET_USE_POLLING_FILE_WATCHER` polls) | Nothing. |
| — | Every write sets the file's last-write time: now, or later than the replaced file's time when the clock or the file system's resolution hasn't passed it, so each write changes the `ETag` | On FAT, exFAT or HFS+, fast writes push a file's time ahead of the clock, without bound. |
| No `IfNotExists`; an append to a missing file created it whatever appeared meanwhile | `IfNotExists`, an append to a missing file and the destination of a move or copy are taken atomically. Windows renames without replacing. Linux and macOS take the name with an exclusive create, then rename onto it: the file exists, empty, for that moment, and a process that dies there leaves it | If you watch the folder on Linux or macOS, expect that empty file. |
| A symbolic link to a file was described by `FileInfo` (on Windows, the link itself: length 0, the link's time) | Described by its target: length, last-write time and `ETag`, on every system. `PhysicalPath` stays the link's path. A link to nothing, or in a loop, is described as itself | Nothing. |
| A recursive walk followed linked folders | `EnumerateAsync(recursive: true)` lists a linked folder (symbolic link, junction) but doesn't walk into it | List a linked folder to walk it. |
| On Windows, some names named another file or failed late (see below) | On Windows they throw `ArgumentException`. Linux and macOS take them as they are | Pass the name Windows stores (`report.txt`, not `report.txt.`). Rename files whose names Windows reserves. |
| Listings showed every name on disk | Listings leave out names no path can address: on Windows, the names below; on Linux and macOS, a name with `:` or `\`, a control character, or only dots and spaces (`...`); anywhere, a name that decodes to one of these (`%2e%2e`) | Rename such files with another tool if you need them. |
| `UnauthorizedAccessException` was documented for writes only | Thrown wherever the system refuses the process access to a file or folder a member opens, lists, writes, moves or deletes. A path inside a folder the process can't enter reads as missing | Catch it where you read or list files the process may not reach. |
| `WriteablePhysicalFileProvider` could be derived from | Sealed | Wrap it, or derive your own provider from `WriteableFileProvider<TOptions>`. |
| Linux and macOS: `Options.RootDirectoryPath`, and so `ToJson`, gave `root:/srv/files` | `/srv/files`. The `root:` form still reads, from JSON and configuration, and `RootDirectorySegments` keeps its 2026 form | Stop stripping `root:`. |
| A root of `C:\` read back as `C:` | `C:\` | Nothing. |
| An extended or device root (`\\?\C:\files`, `\\?\UNC\server\share`) was used as given | On Windows it is taken, and written back, in plain form (`C:\files`, `\\server\share`). One with no plain form throws `ArgumentException`: a volume GUID, a device, or a name the plain form would change (ending in a dot or a space, `.` or `..`) | Give the root as a drive or share path. |

The Windows names that now throw `ArgumentException`:

- a name that ends in `.` or a space (2026 wrote `report.txt.` to `report.txt`, and a delete of `logs ` removed `logs`);
- a name with `"`, `<`, `>`, `|`, `?` or `*` (2026 read it as missing and failed late);
- a reserved device name, in any case, alone or with an extension: `CON`, `PRN`, `AUX`, `NUL`, `COM1` to `COM9`,
  `LPT1` to `LPT9`, and `COM` or `LPT` with `¹`, `²` or `³`. Recent Windows builds don't always enforce the
  reservation, so a file such as `con.txt` that 2026 served is now refused and left out of listings;
- an 8.3 short name (`REPORT~1.TXT`) of a file or folder whose long name differs (2026 wrote it to `report.txt`).

New: `IfMatch`, `IfNotExists`, ranged and seekable reads, copies and moves that never replace a file, and `Watch`.
Physical signs no URLs: the AspNetCore package's gateway serves them.

## Azure Blob Storage

Azure.Storage.Blobs moves from 12.23 to 12.30, and the package also references Azure.Identity 1.21.0.
`WriteableAzureBlobFileProvider` is sealed. The configuration shape is unchanged; the two new options are written to
JSON only when `true`.

| 2026 | 2027 | What to do |
|---|---|---|
| `AzureFileProviderOptions` | `AzureBlobFileProviderOptions`, same properties. The old name is an `[Obsolete]` subclass, so code that builds a provider with it compiles until 2028.0.0. `provider.Options` is an `AzureBlobFileProviderOptions`. Stored configurations read unchanged | Rename the type, including where you type `provider.Options` as `AzureFileProviderOptions` or name `WriteableFileProvider<AzureFileProviderOptions>`. |
| Files were append blobs; an overwrite emptied the blob, then appended, so a failed write truncated the file | Block blobs, staged and committed in one request: a failed write keeps the old content, and readers never see half of it | Nothing. 2026's append blobs convert on their first write (below). |
| A missing container was created by the first write or move; reads reported missing files and empty listings | A missing container makes every member throw `InvalidOperationException` naming it | Create the container, or set `CreateContainerIfMissing: true` to create it on the first write. Reads never create it. |
| A missing file threw `RequestFailedException` (404) | `FileNotFoundException` | Catch `FileNotFoundException`. |
| `ReadFileAsync` returned `AzureFileStream`, a public type with a `Length` | The SDK's download stream, read forward only. `AzureFileStream` is gone | Take the length from `GetFileInfoAsync`, or use `OpenReadSeekableAsync`, which knows its length and reads ranges. |
| `MoveAsync` onto an existing file replaced it | `FileExistsException`, destination unchanged. A source that changes while it moves is kept, the copy removed, and `PreconditionFailedException` thrown | Delete the destination first if you meant to replace it. |
| `GetFileInfo` of a folder: not existing; of a missing path: `Length` 0; `PhysicalPath` was the blob name | A folder exists and is a directory; a missing path has `Length` -1; `PhysicalPath` is null; files carry `ETag` and `ContentType` | Use `IWriteableFileInfo.Path` where you used `PhysicalPath`. |
| A download URL started now, and a validity over 7 days was cut to 7 days silently | It starts 15 minutes in the past and ends at the validity. Over 7 days throws `NotSupportedException`, and so does a user delegation URL that would outlive its key (at most 7 days less 5 minutes) unless `AllowShorterValidity` is set. A URL for a missing file throws `FileNotFoundException` | Ask for at most 7 days (less 5 minutes with Microsoft Entra ID), or use the gateway. |
| Read URLs only, signed with the account key | Read, create and write URLs; with Microsoft Entra ID, user delegation SAS | See [Signed URLs](guide/signed-urls.md). |
| Deleting a blob with snapshots failed (`RequestFailedException`) | It still fails, now with `IOException`, and so does a move of such a blob. Snapshots are never deleted, except by the append blob conversion | Delete the snapshots first. |
| Listings showed every blob | Blobs whose names no path can address, and the provider's temporary folder `.wfp-tmp` at the provider root, are left out. Your own names starting with `.wfp-` are listed | Rename such blobs with another tool if you need them. |
| `WriteableAzureBlobFileProvider` could be derived from | Sealed | Wrap it, or derive from `WriteableFileProvider<AzureBlobFileProviderOptions>`. |
| A root with a dot segment, such as `tenants/../other`, reached blobs outside the root, and `../secrets` another container | Building the provider throws `ArgumentException` (see [Stored configuration](#stored-configuration)) | Name the folder itself. |
| Two overwrites of one file at once both committed, one after the other | The one that commits second finds its blocks dropped by the first. A one-block write is staged again (up to 5 attempts), so the last writer still wins; a longer one throws `IOException` and stores nothing. With `IfMatch` or `IfNotExists`, it throws `PreconditionFailedException` | Write it again, or serialize the writers. |
| Shared key only | `UseDefaultAzureCredential` (Microsoft Entra ID), and a constructor that takes a `BlobContainerClient`: `new WriteableAzureBlobFileProvider(options, container)` (`new()` for options at the container's root), which the provider never disposes. With the options constructor, `AccountKey` must be base64, and setting it with `UseDefaultAzureCredential` throws | Fix a key that isn't base64; it never worked. |

**Append blobs already stored** stay readable. The first write or append to one converts it to a block blob:

1. The new content goes to a temporary blob under `.wfp-tmp/` in the provider root.
2. The append blob is deleted, if it hasn't changed.
3. The temporary blob is copied to its name.

Meanwhile the file is missing, or empty while the server copy runs. If the copy fails, the exception names the
temporary blob that holds the content.

- A 2026.10.10.x provider can't append to a block blob (`InvalidBlobType`). If you run both versions, don't append to
  shared files during the switch: upgrade every writer together.
- An upload SAS can't replace an append blob. Write such a file once through the provider first.

New: `CreateContainerIfMissing`, `UseDefaultAzureCredential`, the `BlobContainerClient` constructor, atomic `IfMatch`
and `IfNotExists`, appends that never lose a concurrent write, native ranges, server-side copies, and create and write
SAS URLs.

## Amazon S3 and MinIO

Both run on one S3 implementation on the AWS SDK for .NET v4 (AWSSDK.S3 4.x):

- WriteableFileProviders.Minio no longer depends on the Minio SDK, so Minio, Microsoft.Extensions 9 and System.Reactive
  leave your dependency graph. Reference the Minio SDK yourself if you used its types.
- If you use AWSSDK.S3 directly, follow AWS's v3-to-v4 migration guide: response collections can be null, value types
  are nullable, checksums are on by default, and the obsolete `S3Region` short names (`US`, `EU`, `SFO`…) are gone.
- Both provider classes are sealed. Their new options are written to JSON only when set.

### Amazon S3

| 2026 | 2027 | What to do |
|---|---|---|
| `ReadFileAsync(string versionId, params string[] absolutePathSegments)`, `EnumerateObjects(string[], bool)` (obsolete since the 2026.10 security patch) | Removed. **A call such as `ReadFileAsync(versionId, "a", "b.txt")` still compiles**, through the multi-segment shim, and reads the path `<versionId>/a/b.txt` | Replace every such call: `ReadFileVersionAsync(versionId, path)` and `EnumerateAsync(path, recursive: true)`, both relative to the root. |
| `ReadFileVersionAsync(string versionId, params string[] pathSegments)` | `ReadFileVersionAsync(string? versionId, ProviderPath path, CancellationToken)` | A call with one path string compiles unchanged. Pass several segments as `"a/b.txt"` or an array. |
| `ReadFileAsync(string subpath)` on the concrete type | Removed | Call the interface's `ReadFileAsync(path)`; a string converts. |
| `WriteableAmazonS3FileProvider` could be derived from | Sealed | Wrap it, or derive from `WriteableFileProvider<TOptions>`. |
| A missing file threw `AmazonS3Exception` (NoSuchKey) | `FileNotFoundException` | Catch `FileNotFoundException`. |
| A folder: `Exists` false, `IsDirectory` true; `PhysicalPath` the key | `Exists` and `IsDirectory` true; `PhysicalPath` null | Use `IWriteableFileInfo.Path`. |
| A move overwrote an existing destination | `FileExistsException`. It deletes exactly the source key, with `If-Match` on the version it copied: a source deleted meanwhile completes the move, and one changed meanwhile undoes the copy with `PreconditionFailedException` | Delete the destination first if you meant to replace it. Retry on `PreconditionFailedException`. |
| A missing bucket failed with the SDK's error | `InvalidOperationException` naming the bucket, unless `CreateBucketIfMissing` is set | Create the bucket, or set the option. |
| One PUT per write, at most 5 GB, and the caller's stream was disposed | Multipart above 8 MiB (up to 5 TiB from a stream that knows its length, about 430 GiB from one that doesn't). Your stream is left open | Dispose your stream yourself. |
| A failed append could upload a half-appended file | An append is a rewrite with `If-Match`: it fails whole, and a concurrent write makes it throw `PreconditionFailedException` | Retry on `PreconditionFailedException`. |
| The content type was inferred from the key's extension | The `ContentType` you give, else `application/octet-stream`. An append keeps the file's type | Pass `UpdateFileOptions.ContentType` where you relied on the inference. |
| Download URLs forced `Content-Type: application/octet-stream` | Read URLs serve the stored type, unless `SignedUrlOptions.ContentType` says otherwise. Write URLs are new, and so are create URLs on AWS. Toward an endpoint named by `ServiceUrl` or a client's `ServiceURL`, a create URL throws `NotSupportedException` | Set `ContentType` on the URL to keep the old header. For create URLs toward other servers, use the AspNetCore package's URL service, which issues a gateway URL. |
| A presigned URL lived as asked, whatever the credential | `NotSupportedException` unless `AllowShorterValidity` is set, when the credential expires before the URL would, is a session token with no known expiry, or comes with a client you passed in | Use long-term keys, or set `AllowShorterValidity` and accept the shorter URL. |
| The `S3Region` constructors stored `""` for us-east-1 and `"EU"` for eu-west-1 | They store `us-east-1` and `eu-west-1`, and the old values read as those regions | Nothing. |
| A key with an empty segment, `..`, a backslash or a control character was listed | Left out of listings: no path can name it | Rename such keys with another tool. |
| No check on the endpoint's scheme | Plain `http` toward a host that isn't loopback throws `ArgumentException` when the provider is built, unless `AllowInsecureHttp` is set. This covers `ServiceUrl`, a client's own endpoint, and the SDK's configured endpoint (`AWS_ENDPOINT_URL_S3`, `AWS_ENDPOINT_URL`, a profile's `endpoint_url`) | Use `https`, or set `AllowInsecureHttp` for a server you reach over plain HTTP on purpose. |

New: `ServiceUrl` and `ForcePathStyle` for S3-compatible servers, `UseDefaultCredentials` for the SDK's default
credential chain, `CreateBucketIfMissing`, `AllowInsecureHttp`, and a constructor that takes your own `IAmazonS3`.
Folders are key prefixes, so a file and a folder may share a name. `IfMatch` and `IfNotExists` are atomic on AWS.
Toward a named endpoint (`ServiceUrl`, a client's `ServiceURL`, or the SDK's configured endpoint), the provider also
checks them first, which is not atomic.

### MinIO

| 2026 | 2027 | What to do |
|---|---|---|
| `WriteableMinioFileProvider` could be derived from | Sealed | Wrap it, or derive from `WriteableFileProvider<TOptions>`. |
| `UseSSL` false sent everything over plain HTTP, to any host | Plain HTTP only toward a loopback host. Any other host throws `ArgumentException` when the provider is built | Set `UseSSL`, or `AllowInsecureHttp` for a server you reach over plain HTTP on purpose (below). |
| A missing bucket was created on the first write | `InvalidOperationException` naming the bucket, unless `CreateBucketIfMissing` is set | Set `CreateBucketIfMissing: true` to keep the old behaviour. |
| A missing file threw `ObjectNotFoundException`; a missing bucket `BucketNotFoundException` | `FileNotFoundException`; `InvalidOperationException` | Catch those. |
| `ReadFileAsync` downloaded the whole file to a seekable temp file | A forward-only network stream | Where you seek, use `OpenReadSeekableAsync` (ranged reads) or `CreateLocalFileStreamAsync` (a local copy). |
| Download URLs threw `NotSupportedException` | Presigned read and write URLs, up to 7 days. Create URLs throw `NotSupportedException`: MinIO releases without conditional writes ignore the `If-None-Match` that would guard them | Get create URLs from the AspNetCore package's URL service, which issues a gateway URL. |
| Every file was stored as `application/octet-stream` | The `ContentType` you give is stored; without one, still `application/octet-stream` | Nothing. |
| A move overwrote an existing destination | `FileExistsException`, checked before the copy. The source's version is checked before its delete: a source deleted meanwhile completes the move, and a change undoes the copy with `PreconditionFailedException`. Neither check is atomic (MinIO ignores `If-None-Match` on copies and `If-Match` on deletes) | Delete the destination first if you meant to replace it. Retry on `PreconditionFailedException`. |
| The Minio SDK asked the server for the bucket's region | The provider asks once (`GetBucketLocation`), or takes the new `Region` option | Set `Region` for a server with a site region whose bucket doesn't exist yet. |
| Every key was listed | Keys no path can name (an empty segment, `..`, a backslash, a control character) are left out | Rename such keys with another tool. |

**Find the configurations the plain-HTTP rule breaks:** `WriteableMinioFileProvider` entries whose `UseSSL` is `false`
or absent and whose `HostAndPort` is not `localhost`, a `127.x.x.x` address or `[::1]`. Each needs `"UseSSL": true`, or
`"AllowInsecureHttp": true` if plain HTTP is intended. `HostAndPort` still takes `host:port` only: a scheme, a path or
a query is refused, as the Minio SDK refused it.

New: `AllowInsecureHttp`, `Region`, `CreateBucketIfMissing`, and a constructor that takes your own `IAmazonS3`. The
provider checks `IfMatch` and `IfNotExists` first, which is not atomic, and sends them to the server, which enforces
them where it has conditional writes; older MinIO releases ignore them.

## Google Cloud Storage

`Google.Cloud.Storage.V1` moves from 4.15 to 5.0. If you use that SDK directly: `UploadValidationException` and
`UploadValidationMode.ThrowOnly` are gone, and the server validates uploads (`RejectAndThrow`) by default.
`WriteableGoogleCloudStorageFileProvider` is sealed. The new options (`Endpoint`, `AllowInsecureHttp`,
`CreateBucketIfMissing`) are written to JSON only when set.

| 2026 | 2027 | What to do |
|---|---|---|
| The constructor looked up Application Default Credentials synchronously: network I/O, and a throw where there were none | The constructor reads the key only. The client is built, and Application Default Credentials looked up, on the first call, which throws `InvalidOperationException` where there are none | Expect a configuration without credentials to fail on its first call, not at construction. |
| A key it couldn't read threw `FormatException`; a missing bucket name threw `ArgumentException` for `BucketName` | Both throw `ArgumentException` for `options`, as does an `Endpoint` that isn't a root URL | Catch `ArgumentException`. |
| Only the options constructor | Also `new WriteableGoogleCloudStorageFileProvider(options, StorageClient)`, which never disposes the client | Use it for your own credentials, retries, HTTP handling or encryption key. |
| `WriteableGoogleCloudStorageFileProvider` could be derived from | Sealed | Wrap it, or derive from `WriteableFileProvider<TOptions>`. |
| No way to reach another endpoint | `Endpoint`: a root URL, such as fake-gcs-server's `http://localhost:4443` or a Private Service Connect endpoint. Plain `http` toward a host that isn't loopback throws `ArgumentException` unless `AllowInsecureHttp` is set | Nothing; it is new. |
| A missing bucket: listings threw `GoogleApiException`, `GetFileInfo` said "not found", writes threw `GoogleApiException` | Every member that reaches the bucket throws `InvalidOperationException` naming it. `CreateBucketIfMissing` (default false) creates it on the first call instead | Create the bucket, or set the option (with `ProjectId` when the key doesn't name the project). |
| A missing file threw `GoogleApiException` (404) | `FileNotFoundException` | Catch `FileNotFoundException`. |
| `GetFileInfo` of a folder said it didn't exist | A folder that holds objects exists and is a directory, `Length` -1 | Check `IsDirectory`. |
| `GetFileInfo` of a path that isn't a file read one object | It also lists objects, to tell a folder. So do listings, copies, appends to a new file, and telling a missing bucket from a missing file. Without `storage.objects.list` these throw `UnauthorizedAccessException`, and a missing bucket reads as a missing file | Grant `storage.objects.list` (`roles/storage.objectViewer` and `roles/storage.objectAdmin` have it). |
| `PhysicalPath` held the object's name | `PhysicalPath` is null | Use `IWriteableFileInfo.Path`. |
| `ReadFile` downloaded the whole object to a seekable temp file | It returns the download as it arrives: read forward, with `Length` when the server sends it | Use `OpenReadSeekableAsync` to seek, or `ReadRangeAsync` for a slice. Both download only what is read. |
| Move and copy overwrote an existing destination | They throw `FileExistsException` and leave it unchanged. A move deletes exactly the generation it copied, and throws `PreconditionFailedException` if the source was written meanwhile, keeping that write | Delete the destination first if you meant to replace it. |
| The SDK retried a copy or move | Not retried: when one fails with a server error or a timeout, the copy may still have landed | Check the destination before you try again. |
| An append whose source stream failed uploaded a half-appended file; a concurrent write could be lost | An append that fails leaves the file unchanged, and throws `PreconditionFailedException` instead of losing a concurrent write | Retry the append on `PreconditionFailedException`. |
| The caller's stream was disposed after a write | It is left open | Dispose it yourself. |
| Every object was listed | Objects whose names no path can address, such as `a//b` or `x/../y`, are left out | Rename such objects with another tool if you need them. |
| Read URLs only, with the stored name and type | Read, create (only if absent) and write URLs. A read URL can carry `DownloadFileName` and `ContentType` | See [Signed URLs](guide/signed-urls.md) for the headers an upload must send. |
| Over seven days threw the SDK's `ArgumentOutOfRangeException`; a credential that couldn't sign threw `PlatformNotSupportedException`; a URL was signed for a missing file | `NotSupportedException` (so the gateway can take over) for: a validity over seven days or under one second; a credential that can't sign; IAM `signBlob` refused (403), failing with another error than 401, or unreachable. A refused credential and a timeout fail as on every member (`UnauthorizedAccessException`, `IOException`). The validity is rounded down to whole seconds. A read URL for a missing file throws `FileNotFoundException` | Catch `NotSupportedException`. |

New: the `ETag` is the object's generation, and `IfMatch` and `IfNotExists` are atomic. `ContentType` is stored and
reported: `application/octet-stream` when a write gives none, as in 2026; an append or a local stream keeps the file's.

## Box

The provider runs on `Box.V2.Core` 10 (the generated SDK, namespace `Box.Sdk.Gen`) and signs in on its first call.
`WriteableBoxFileProvider` is sealed. Box adds no options, so its configuration is stored as before.

| 2026 | 2027 | What to do |
|---|---|---|
| `Box.V2.Core` 5.8 (namespace `Box.V2`) | `Box.V2.Core` 10.19 (namespace `Box.Sdk.Gen`); v10 has no `Box.V2` namespace | Move your own Box SDK code to `Box.Sdk.Gen` (Box's guide: `migration-guides/from-v5-to-v10.md` in `box/box-windows-sdk-v2`). Newtonsoft.Json and Portable.BouncyCastle leave the graph; the SDK brings BouncyCastle.Cryptography 2, System.IdentityModel.Tokens.Jwt 8, and Microsoft.Extensions.Http and DependencyInjection 8. |
| The constructor signed in to Box, blocking on the network, and threw `AggregateException` on a bad credential | No network I/O in the constructor. It checks that every credential is set and that the private key opens with the passphrase (`ArgumentException`), and signs in on the first call | Expect a wrong client id or secret to fail on the first call, with `UnauthorizedAccessException`. `WriteableFileProviderSerializer` builds a Box provider offline. |
| `WriteableBoxFileProvider` could be derived from | Sealed | Wrap it, or derive from `WriteableFileProvider<TOptions>`. |
| Only the options constructor | Also `WriteableBoxFileProvider(BoxFileProviderOptions, IBoxClient)`: a ready client (another authentication, as-user, a proxy, custom retries), which the provider never disposes. Only `RootFolderId` and the root folder are read from the options | Optional. |
| `GetFileInfo` of a folder: `Exists` false, `IsDirectory` true | `Exists` and `IsDirectory` true, `Length` -1. A missing path has `Length` -1 (was 0) | Test `IsDirectory` and `Exists` as on every provider. |
| `PhysicalPath`: the joined path from `GetFileInfo`, the bare name in listings | Always null | Use `IWriteableFileInfo.Path`. |
| An overwrite rewound a seekable stream and uploaded it from byte 0 | Every write uploads from the stream's current position | Rewind the stream yourself if you meant to upload all of it. |
| A move created no folders (`DirectoryNotFoundException`), moved folders, and wrapped Box's 409 in `BoxException` | Moves files only, creates the destination's folders, and throws `FileExistsException` when the destination exists | Move a folder's files one by one, or use the Box SDK. |
| Errors were rethrown as `BoxException`, the path in the message | The contract's exceptions, with the SDK's `BoxApiException` inside (status in `ResponseInfo.StatusCode`). `ArgumentException` for a name Box refuses; `InvalidOperationException` for a `RootFolderId` that doesn't exist or the app can't see; `UnauthorizedAccessException` for 401, 403 and a refused sign-in; `IOException` for the rest | Catch the contract's exceptions. Read Box's status and code from the `BoxApiException` inside. |
| — | A failed download throws `FileNotFoundException` or `IOException` with its status only: the SDK's error names the pre-authenticated URL | Nothing. |
| — | Wrapped `BoxApiException`s are redacted when the provider built its client or got exactly a `BoxClient`: they keep Box's status, code and message, and of the request only its method, its URL without the query, and allow-listed headers and parameters. Any other `IBoxClient`, a `BoxClient` subclass included, gives its own errors, which may carry its credentials (the access token, a token request's client secret) | With a client of your own, mind what you log from its errors. |
| Writing under a file threw `InvalidOperationException` | `IOException` | Catch `IOException`. |
| Folder listings paged by offset, 500 a page | Marker paging, 1000 a page | Nothing. |
| Listings showed every item | Items whose names no path can address are left out: only dots and spaces (`...`), or a name such as `%2e%2e` that decodes to one | Rename such items in Box if you need them. |
| `ReadFile` downloaded the whole file to a temp file first | It streams the download. Ranged and seekable reads fetch only their bytes, pinned to the version they opened | Nothing. |
| `GetDownloadUrl(validity)` ignored the validity: Box's URL lives about 15 minutes | `GetSignedUrlAsync` refuses a validity under 15 minutes (`NotSupportedException`, so the gateway serves it). From 15 minutes to 7 days it returns Box's URL only with `AllowShorterValidity`, with `ExpiresOn` set. `GetDownloadUrl(path, validity)` always throws `NotSupportedException` for Box | Ask for at least 15 minutes with `AllowShorterValidity = true`, or use the gateway. |
| Chunked uploads from 20,000,000 bytes, through the SDK's helper | From 20 MiB, through the provider's own upload session: exact part sizes, part and whole-file digests, a bounded wait while Box processes the commit, the session aborted on failure | Nothing. |

New: a file's `ETag` is the id of its current Box file version, not Box's `etag`, which restarts at `0` for a file
deleted and created again. `IfNotExists` is atomic. `IfMatch` is checked just before the write, not atomic, and so are
appends. Box keeps no content type: `ContentType` is ignored on writes and null on reads.

## SharePoint

The provider moves to Microsoft.Graph 6 and Microsoft.Graph.Core 4 (Kiota 2); your own code on those SDKs moves with
it. `WriteableSharePointFileProvider` is sealed: wrap it, or derive from `WriteableFileProvider<TOptions>`. The public
`UploadResult<T>` class is gone.

### The work folder is the start of the root

The root is `WorkFolderPath` followed by `RootDirectoryPath`. When `RootDirectoryPath` starts with the work folder
(ignoring case), that leading copy is dropped, so a configuration that set both to the same folder keeps its files.
Caller paths are no longer rewritten: 2026 removed a leading copy of the work folder from every path. These
configurations now place files elsewhere:

| Configuration | Path | 2026 | 2027 |
|---|---|---|---|
| `WorkFolderPath` `Reports`, no root | `Reports/2026.csv` (any case) | `Reports/2026.csv` | `Reports/Reports/2026.csv` |
| `WorkFolderPath` `Main/Work`, no root | `Main/Work/a.txt` | `Main/Work/a.txt` | `Main/Work/Main/Work/a.txt` |
| `WorkFolderPath` `Main/Work`, no root | `Main/Work` (the work folder itself) | the root: refused for writes and deletes | a folder `Main/Work` inside the root |
| `WorkFolderPath` `Main/Work`, `RootDirectoryPath` `Main` | `Work/a.txt` / `a.txt` | `Main/Work/a.txt` / `Main/Work/Main/a.txt` | refused at construction: `ArgumentException` |
| `WorkFolderPath` or `RootDirectoryPath` with a percent-escape, such as `Team%20Files` copied from a browser | any | under the decoded name, `Team Files` | refused at construction: `ArgumentException` asking to decode it |

Every other shape places files where 2026 did: no work folder; a root inside the work folder; a root that repeats the
work folder, in any case, with more after it; a root that shares only part of the work folder.

- **Find what moves:** search your code and stored paths for paths that start with the work folder's name. Drop that
  prefix, or move the files.
- **A root that is the start of the work folder** has no equivalent: set `RootDirectoryPath` to a folder inside the
  work folder (or leave it empty), and move the files 2026 wrote under the second copy.
- **The work folder is created on the first write**, not in the constructor. Reading under a missing one finds nothing.

### Behaviour

| 2026 | 2027 | What to do |
|---|---|---|
| The constructor signed in and looked up the site, library and work folder, blocking on the network. A failure threw `InvalidOperationException("Failed to initialize SharePoint connection")` | No I/O in the constructor. The site and library are looked up on the first call. A missing one throws `InvalidOperationException` naming `SiteUrl` or `DocumentLibraryName` from every member that reaches the library; any other failure is retried on the next call | Expect configuration errors on the first call, not at construction. |
| A site URL with an escaped character (`Team%20Site`) was escaped twice and not found | It is found | Nothing. |
| Every Graph error while reading file info meant "missing" or `FileNotFoundException` | Only a 404 means missing. 401 and 403 throw `UnauthorizedAccessException`; throttling after the SDK's retries, and every other error, `IOException` | Catch `UnauthorizedAccessException` and `IOException` where you caught `FileNotFoundException` for them. |
| An empty folder listed as `Exists = false` | It exists | Nothing. |
| A folder's `Length` was the size of its contents, a missing path's 0, and `PhysicalPath` held the path or the name | -1, -1 and null | Use `IWriteableFileInfo.Path`. |
| Writes rewound the caller's stream, a simple upload disposed it, and a missing parent retried without end | Writes read from the stream's position, leave it open, and create missing parents once | Rewind the stream yourself if you meant to upload all of it, and dispose it. |
| Upload sessions of 3.2 MB slices, with no retry, ending silently on a short read | 5 MiB fragments from a temp copy. A transient failure is resent once; any other cancels the session and throws | Nothing. |
| Appends combined in memory or a temp file, two ways | One append by rewrite, with `If-Match` on the version read | Retry an append that throws `PreconditionFailedException`. |
| Reads downloaded the whole file to a temp file first; a 0-byte file threw `FileNotFoundException` | Reads stream, ranges are native, and a 0-byte file reads empty | Use `OpenReadSeekableAsync` where you seek. |
| A move was a lookup and a `PATCH` | One `PATCH` with the conflict behaviour `fail`: a taken destination throws `FileExistsException`. A copy runs on the server, waited for at most 10 minutes | Delete the destination first if you meant to replace it. |
| A path segment with a percent-escape (`a%41`) reached the service unescaped, which decoded it (`aA`) | The `%` is escaped, so `a%41` names an item called `a%41`. `WorkFolderPath` and `RootDirectoryPath` with an escape are refused instead. Listings leave out names the provider can't address, such as one that decodes to `..` | Files 2026 wrote through an escaped path are under the decoded name: use that. |
| `UpdateFileOptions` did not exist | `IfMatch` and `IfNotExists` are checked before the write and sent to the service; not promised atomic. `ContentType` is ignored: SharePoint names the type from the file name | Nothing. |
| `GetDownloadUrl` returned the download URL whatever validity was asked | A read URL (`SignedUrlKind.PreAuthenticated`, `ExpiresOn` null), only for a validity of an hour to 7 days, and only with `AllowShorterValidity`. A shorter validity throws `NotSupportedException`, so the gateway can serve it. `GetDownloadUrl(path, validity)` always throws `NotSupportedException` for SharePoint | Ask for at least an hour with `AllowShorterValidity = true`, or use the gateway. |

New sign-in options, all optional:

- `ClientCertificateBase64` and `ClientCertificatePassword`: a base64 PKCS#12 file.
- `UseDefaultAzureCredential`: managed identity, workload identity, environment, developer sign-in.

The first one configured wins: `UseDefaultAzureCredential`, then the certificate, then `ClientSecret`. The new
constructor `WriteableSharePointFileProvider(options, GraphServiceClient)` takes a client you build and own; the
provider never disposes it. Stored 2026 configurations read unchanged, and `ToJson` writes the three new keys only when
they are set.

## New package: WriteableFileProviders.AspNetCore

New in 2027.0.0, for ASP.NET Core apps on .NET 10. Nothing in 2026 maps onto it. Use it to keep handing out download
links where a provider's own URLs changed:

```csharp
builder.Services.AddWriteableFileProviderGateway(new Uri("https://files.example.com"));
var app = builder.Build();
app.MapWriteableFileProviderGateway();
```

```csharp
SignedUrl url = await urls.GetSignedUrlAsync(
    "reports/2026.csv", new SignedUrlOptions { Validity = TimeSpan.FromHours(1) });
```

- `IWriteableFileUrlService.GetSignedUrlAsync(path, options)` asks your `IWriteableFileProvider` for its native URL.
  When the provider can't sign the URL asked for (it throws `NotSupportedException`), the service issues a URL of your
  app's own gateway instead. So a 2026 caller of `GetDownloadUrl` gets a URL that does what was asked on every
  provider, the local disk included. `GetSignedUrlAsync(path, options, providerKey)` does the same for a keyed provider.
- Every provider it serves must be a singleton, or resolve to the same store in every scope (see
  [Signed URLs](guide/signed-urls.md)).
- `AddWriteableFileProviderGateway(options => ...)` sets the other options.
- The gateway serves `GET`, `HEAD` (with HTTP ranges) and `PUT` on `{BasePath}/{token}/{fileName?}`. The token is an
  ASP.NET Core Data Protection payload: an app on several instances shares its key ring, and one-time upload URLs need
  a shared `IGatewayReplayStore`.
- Defaults fail closed:
  - `PublicBaseUri` is required; gateway URLs never use the request's `Host`.
  - Gateway download URLs live at most 7 days, and upload URLs 1 hour by default.
  - Gateway upload URLs work once, and uploads are capped at 100 MiB.
  - A download is served as the content type its read URL names, else `application/octet-stream`, under a sandboxing
    `Content-Security-Policy`.
  - These bind gateway URLs only. A provider's native upload URL can be reused until it expires, and lives up to the
    provider's maximum: set `PreferGateway` to apply the gateway's rules everywhere.

The [package README](../src/WriteableFileProviders.AspNetCore/README.md) covers the options, and the guide's
[Signed URLs](guide/signed-urls.md) the endpoints' status codes and headers, and how to keep tokens out of logs.
