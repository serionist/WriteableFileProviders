# Moving to WriteableFileProviders 2027.0.0

2027.0.0 is a major release: every package targets .NET 10, the API is rebuilt around one path type and cancellation,
and every provider follows one written contract, which a shared conformance suite checks on each of them. This guide is
the ledger of what breaks and how to move over: first the steps every consumer takes, then Common, the stored
configuration, each provider, and the new ASP.NET Core package.

Every section has the same shape: what changed, then a table of each change with what to do, then what is new.

## Start here

1. **Target .NET 10.** Every 2027 package targets `net10.0` only. An application on .NET 8 or 9 stays on the
   `2026.10.10.x` packages, which get security fixes until a date the maintainers announce in
   [SECURITY.md](../SECURITY.md).
2. **Move every package together.** All WriteableFileProviders packages of an application must be on the same major
   version: a `2026.10.10.x` provider does not load against `WriteableFileProviders.Common` 2027. Update them all to
   `2027.0.0` in one change. From 2027, each provider's package depends on Common of its own major
   (`[2027.0.0, 2028.0.0-0)`, which also keeps out 2028's prereleases), so NuGet warns (NU1608) when another major of
   Common is restored with it.
3. **Paths are one `ProviderPath`.** Every member takes a `ProviderPath`, which a string (`"reports/2026.csv"`) or a
   string array converts to, so most calls compile unchanged; calls with several string arguments compile through
   `[Obsolete]` shims that name the new call (see [Common: the API](#common-the-api)). Every path is relative to the
   provider's root, as before.
4. **The API is async, with cancellation.** Each operation is one async member that takes a `CancellationToken`; the
   synchronous forms are extension methods in `WriteableFileProviderExtensions` (add `using WriteableFileProviders;`).
   Providers are disposable: `await using` the ones you create.
5. **The serializer is System.Text.Json.** Newtonsoft.Json leaves every package. `ToJson` returns a `JsonObject`, and
   `FromJson` takes text, a `JsonElement` or a `JsonObject`. Every configuration stored by 2026 reads the same, except
   a root with a dot segment (`tenants/../other`), which no longer builds; JSON that 2027 writes is not promised to read
   back on 2026 (see [Stored configuration](#stored-configuration)).
6. **Provider classes are sealed.** To change what a provider does, wrap it (implement `IWriteableFileProvider` and
   delegate), or derive your own provider from `WriteableFileProvider<TOptions>`.
7. **Each provider changed behaviour** to follow the contract. Every provider but Physical checks its root as a path
   when it is built (`ArgumentException` for a root such as `tenants/../other`). The short list; its section has the
   rest:
   - [Physical](#physical): writes and appends go through a temp file and a rename, so the file at the path is a new
     file after each write; readers no longer block writers and deletes.
   - [Azure Blob Storage](#azure-blob-storage): files are block blobs; a missing container is no longer created unless
     `CreateContainerIfMissing` is set; 2026's append blobs are converted on their first write.
   - [Amazon S3](#amazon-s3): the content type is no longer guessed from the name; a move refuses an existing
     destination; the obsolete absolute-path members are gone.
   - [MinIO](#minio): it runs on the AWS SDK, without the Minio SDK; plain HTTP only toward a loopback host unless
     `AllowInsecureHttp` is set; a missing bucket is no longer created unless `CreateBucketIfMissing` is set.
   - [Google Cloud Storage](#google-cloud-storage): no network I/O in the constructor; reads stream instead of
     downloading to a temp file; a missing bucket is reported, not taken for a missing file.
   - [Box](#box): Box.V2.Core 10 (namespace `Box.Sdk.Gen`); sign-in on the first call; moves create folders and refuse
     an existing destination; download URLs honour the validity asked.
   - [SharePoint](#sharepoint): Microsoft Graph 6; `WorkFolderPath` is the start of the root, and caller paths are no
     longer rewritten; download URLs honour the validity asked.
8. **Failures have one set of types.** Every member of every provider throws only the contract's exceptions, with the
   SDK's own exception inside: catch `UnauthorizedAccessException`, `IOException` and the others where you caught
   `RequestFailedException`, `AmazonS3Exception`, `GoogleApiException` or `BoxException` (see
   [What a member throws](#what-a-member-throws)).
9. **Download URLs mean one thing.** A signed URL never lives longer than asked: where a provider cannot sign the URL
   asked for, `GetSignedUrlAsync` throws `NotSupportedException` instead of returning another one. For a download or
   upload URL on every provider, use `IWriteableFileUrlService` from the new
   [WriteableFileProviders.AspNetCore](#new-package-writeablefileprovidersaspnetcore) package, which falls back to a
   URL of your app's own gateway.

## Version numbers

The packages move from date versions (`2026.10.10.4`) to semantic versions that continue above them, so NuGet orders
every 2027 release after every earlier one. The major is a sequence number, not a date: `2027.0.0` is the first,
compatible releases are `2027.x.y`, and the next breaking release is `2028.0.0`, whenever it ships. All packages share
one version. The [README](../README.md#versioning) has the whole policy.

## Common: the API

What changed: one member per operation on `ProviderPath`, every async member with a `CancellationToken`, the sync forms
and conveniences as extension methods, and System.Text.Json instead of Newtonsoft.Json.

| 2026 | 2027 | What to do |
|---|---|---|
| `string subpath` and `params string[] pathSegments` overloads | One `ProviderPath` parameter, with implicit conversions from `string` (`"a/b"`) and `string[]` | Most calls compile unchanged. Calls with several string arguments, such as `ReadFileAsync("a", "b.txt")`, compile through an `[Obsolete]` shim: pass `"a/b.txt"` or `new[] { "a", "b.txt" }`. A call with no path, which meant the root, such as `GetDirectoryContentsAsync()` or `GetFileInfoAsync()`, no longer compiles: pass `ProviderPath.Root` or `""`. |
| No `CancellationToken` | Every async member takes one, last | Pass yours where you have one. |
| `UpdateFileAsync(Stream, UpdateFileMode, params string[])`, `UpdateFileAsync(string, UpdateFileMode, Stream)` | `UpdateFileAsync(path, contents, UpdateFileOptions?)` and the extension `UpdateFileAsync(path, contents, UpdateFileMode)` | The old orders compile through `[Obsolete]` shims; move the path first. |
| `CreateLocalFileStreamAsync(bool readOnly, ...)` | `CreateLocalFileStreamAsync(path, bool readOnly)` | The path comes first; the old order compiles through a shim. |
| Sync members on the interface (`ReadFile`, `UpdateFile`, `Delete`, `Move`, `CreateLocalFileStream`, `GetDownloadUrl`) | Extension methods in `WriteableFileProviderExtensions` | Add `using WriteableFileProviders;` if you call them on the interface. `GetFileInfo(string)`, `GetDirectoryContents(string)` and `Watch(string)` stay on the interface (`IFileProvider`). |
| `GetDownloadUrl(...)` returns `string` | `GetSignedUrlAsync(path, SignedUrlOptions)` returns a `SignedUrl`; the extension `GetDownloadUrlAsync(path, validity)` returns a `Uri` | `string url = ...` becomes `var url = ...` or `.ToString()`. |
| `WatchAsync(string)` | `Watch(string)` | The async form compiles through a shim. |
| `FileInfo` (it clashed with `System.IO.FileInfo`) | `WriteableFileInfo`, and the interface `IWriteableFileInfo` with `Path`, `ETag`, `ContentType` | Replace `(FileInfo)x` with `(WriteableFileInfo)x`; `PathSegments` becomes `Path.Segments`. |
| `GetFileInfoAsync` returns `IFileInfo` | It returns `IWriteableFileInfo` | Nothing; it is an `IFileInfo`. |
| Overloads that take an `IFileInfo` accepted any entry, falling back to its `PhysicalPath` | They take only entries a writeable provider returned (`IWriteableFileInfo`), and throw `ArgumentException` for any other | For an entry from elsewhere, pass its path instead. |
| `DirectoryContents`: a `public class` built from `(bool, IFileInfo[])`, null entries allowed | A `public sealed class` built from `(bool, IEnumerable<IFileInfo>)`; a null sequence throws `ArgumentNullException` | Stop deriving from it; pass an empty sequence instead of null. |
| `IWriteableFileProvider.ToJson()` returns Newtonsoft's `JObject` | `WriteableFileProvider<T>.ToJson()` and `WriteableFileProviderSerializer.ToJson(provider)` return `System.Text.Json.Nodes.JsonObject`; `ToJsonString` returns text. `ToJson` is no longer on the interface | Call `WriteableFileProviderSerializer.ToJson(provider)` on an `IWriteableFileProvider`. |
| `WriteableFileProviderSerializer.FromJson(JObject, ...)` | `FromJson(string | JsonElement | JsonObject, ...)` | Pass the JSON text. |
| `OnLocalFileStreamDisposingAsync` on the interface | Gone: the base class commits a local stream through the path it resolved when the stream was opened | Nothing, unless you implemented it. |
| `LocalFileStream`'s public constructor, `TargetFilePathSegments` | Only Common creates it: its constructor is internal, so no provider outside Common, yours included, can construct one; `Path` replaces `TargetFilePathSegments` | Create local streams with `CreateLocalFileStreamAsync`. |
| `LocalFileStream.DisposeAsync()` returns `Task` and hid `Stream.DisposeAsync()` | A real `ValueTask DisposeAsync()` override; `CommitAsync()` uploads explicitly | `await using` works the same; disposing through `Stream` no longer blocks. |
| Providers were not disposable | `IWriteableFileProvider` is `IDisposable` and `IAsyncDisposable` | Dispose providers you create (the DI container disposes the ones it creates). |
| Common brought in Microsoft.Extensions.Configuration, Configuration.Binder and Newtonsoft.Json | It depends on the `.Abstractions` packages of Configuration, DependencyInjection and FileProviders only | If your code used `ConfigurationBuilder`, `Bind` or `Get<T>` through this dependency, reference those packages yourself. |

The `[Obsolete]` shims (`WriteableFileProviderCompatibilityExtensions`) keep the old call shapes compiling for 2027.x
and go in the next major; each names the call that replaces it.

New: `ReadRangeAsync` (byte ranges), `OpenReadSeekableAsync` (a seekable stream that reads ranges on demand),
`EnumerateAsync` (one level or recursive, as `IAsyncEnumerable`), `CopyAsync`, `UpdateFileOptions` (content type,
`IfMatch`, `IfNotExists`), `GetSignedUrlAsync` (read, create and write URLs), `RangeReadStream`, and DI registration
(`services.AddWriteableFileProvider(name, configuration)`, keyed by name).

## Common: writing your own provider

What changed: `WriteableFileProvider<TOptions>`'s protected members follow the .NET naming convention, take a validated
`ResolvedPath` and a `CancellationToken`, and the base class does more for every provider (path validation, the root,
the move and copy checks, preconditions, signed URL validity).

| 2026 | 2027 | What to do |
|---|---|---|
| `getFileInfoAsync(params string[] absolutePathSegments)` | `GetFileInfoCoreAsync(ResolvedPath, CancellationToken)` | Rename and take the resolved path. |
| `getDirectoryContentsAsync(...)` returning `IDirectoryContents` | `EnumerateCoreAsync(ResolvedPath, CancellationToken)` returning `IAsyncEnumerable<IWriteableFileInfo>` | Yield the entries; the base builds the listing. |
| `readFileAsync(...)` | `ReadFileCoreAsync(...)` | Rename. |
| `updateFileAsync(Stream, UpdateFileMode, ...)` | `UpdateFileCoreAsync(ResolvedPath, Stream, UpdateFileOptions, CancellationToken)` | Honour the options' mode, content type and conditions. |
| `deleteAsync(...)` | `DeleteCoreAsync(...)` | Rename. |
| `move(string[], string[])` | `MoveCoreAsync(ResolvedPath, ResolvedPath, CancellationToken)` | Optional: the default copies, then deletes exactly the source. |
| `getDownloadUrl(string[], TimeSpan)` | `GetSignedUrlCoreAsync(ResolvedPath, SignedUrlOptions, CancellationToken)` with `SupportsSignedUrl(access)` | Override both, or neither (the call then throws `NotSupportedException`). |
| `watchAsync(string)` (abstract) | `WatchCore(string)`, optional | Override it only if the store can watch. |
| `createLocalFileStreamAsync(bool, params string[])` (protected virtual), and overriding the public virtual `CreateLocalFileStreamAsync(bool, params string[])` | Removed: the public `CreateLocalFileStreamAsync(ProviderPath, bool, CancellationToken)` is not virtual, and how a local stream is made is not customisable in 2027.0.0. Every provider's local stream is a local temp copy, spooled through `ReadFileCoreAsync` and committed through `UpdateFileCoreAsync` | Drop the override; put what it did in those two members. |
| Exceptions of the store reached callers as the provider let them out | The base class maps every exception that escapes a `...CoreAsync` member, or a stream or a listing it returns, and is not one of the contract's types ([What a member throws](#what-a-member-throws)): what `IsNotFound` recognises becomes `FileNotFoundException`, what the new `IsAccessDenied` recognises `UnauthorizedAccessException`, anything else `IOException`, the original inside | Override `IsNotFound` and `IsAccessDenied` for your SDK's exceptions, and throw a contract type yourself where you know better: it passes through. A public member of your own that reads goes through `ReadWithContractAsync`. |
| `RootDirectorySegments` were used as given | The base constructor checks them as a path, unless `ValidatesRootDirectory` is overridden to false (a root that is not a path in your store, such as a folder of the disk), and keeps a copy that `Resolve` uses, so a later change to the options does not move the provider | Read the root through `Resolve(ProviderPath.Root)`, not from `Options`. |
| `isSegmentAllowed`, `validateSegments` | `IsSegmentAllowed`, `ValidateSegments` | Rename. |
| `getRelativeSegments`, `isRootPath` | Removed | `ResolvedPath.RelativeSegments` and `ResolvedPath.IsRoot`. |
| `normalizePem` | Removed | Keep your own helper. |
| `normalizePath(string)` | `ProviderPath.Parse(string)` | Rename. |
| `fileSegments(IFileInfo)` | `IWriteableFileInfo.Path` | Read the entry's path. |
| `RunTaskSerial(...)`, both overloads | Removed | Await the task. |

Optional overrides with shared defaults: `ReadRangeCoreAsync`, `OpenReadSeekableCoreAsync`, `CopyCoreAsync`,
`MoveCoreAsync`, `DeleteFileCoreAsync`, `FolderExistsCoreAsync`, `EnumerateRecursiveCoreAsync`, `IsNotFound`,
`IsAccessDenied`, `ValidatesRootDirectory`, `Dispose(bool)`, `DisposeAsyncCore`. Helpers: `AppendByRewriteAsync`,
`SpoolToTempFileAsync`, `NotFound`, `ReadWithContractAsync` (a read by a public member of your own, under the same error
contract as `ReadFileAsync`), and `IsAddressable(name)`, true when the provider's own path validation (`IsSegmentAllowed`
included) would accept the name as one path segment. The base constructor calls `IsSegmentAllowed` and
`ValidatesRootDirectory` to check the root before your constructor runs, so they must not depend on your provider's
state.

What the store can do is declared through these members, and each default makes the base class refuse or weaken an
operation until the provider says otherwise:

| Member | Default | What the default does |
|---|---|---|
| `SupportsAtomicPreconditions` | false | The base checks `IfMatch` and `IfNotExists` just before the write: checked, not atomic. |
| `IsCaseInsensitive` | false | Moving `a.txt` to `A.txt` is an ordinary move. When true it is a rename: the destination check is skipped, and the default `MoveCoreAsync` goes through a temporary name. |
| `SupportsSignedUrl(access)` | false | `GetSignedUrlAsync` throws `NotSupportedException`. |
| `SupportsSignedUrlContentHeaders(access)` | false | A signed URL request that sets `DownloadFileName` or `ContentType` throws `NotSupportedException`. |
| `MaxSignedUrlValidity` | 7 days | A longer validity throws `NotSupportedException`. |
| `TimeProvider` (property) | `TimeProvider.System` | The clock for expiry times; a test can set another. |

Every file needs an `ETag`: the default seekable read pins it, and `AppendByRewriteAsync` writes with `IfMatch` on it.
Without one, a pinned seekable read and an append by rewrite throw `NotSupportedException`. Listings should leave out
names the provider's own path validation would refuse, as every built-in provider does, so that everything listed can
be read back: skip an entry whose name `IsAddressable` refuses, and do not walk into such a folder.

New: run your provider against `ProviderConformanceTests` (`tests/WriteableFileProviders.Testing` in the repository) to
check it meets the contract, and register it with `WriteableFileProviderSerializer.Register<TProvider>()` so the
serializer finds it (see [Stored configuration](#stored-configuration)).

## Common: behaviour

What changed: the base class enforces the contract on every provider, so these hold whichever provider you use.

| 2026 | 2027 | What to do |
|---|---|---|
| Moving or copying a missing file, or a folder, failed differently per provider | `MoveAsync` and `CopyAsync` throw `FileNotFoundException` when the source is missing or is a folder | Catch `FileNotFoundException`. |
| Moving onto an existing file replaced it on some providers | `MoveAsync` and `CopyAsync` throw `FileExistsException`, an `IOException`, and leave the destination unchanged | Delete the destination first if you meant to replace it. |
| The root was not always a folder | `GetFileInfoAsync` on the root returns an existing folder, and a listing of the root always exists | Nothing. |
| A move between names that differ only in case failed on stores that ignore case | On a store that ignores case (Physical on Windows and macOS, Box, SharePoint), moving `a.txt` to `A.txt` renames the file | Nothing. |
| Local streams differed per provider; Physical's wrote to the file in place | Every local stream is a temp copy, committed once and atomically. A read-only local stream of a missing file or of a folder throws `FileNotFoundException`; a writeable one of a folder throws `IOException` | Nothing, unless you relied on in-place writes. |
| Signed URL validities were ignored, clamped or refused per provider | A validity that is not positive throws `ArgumentOutOfRangeException`; one longer than the provider can sign throws `NotSupportedException` (Azure no longer cuts it to 7 days); a read URL for a missing file throws `FileNotFoundException`; `DownloadFileName` and `ContentType` are carried by the URL, or the call throws `NotSupportedException`, never silently ignored | Catch `NotSupportedException`, or use the ASP.NET Core package's URL service, which falls back to its gateway. |
| Files had no ETags | A pinned seekable read (the default) or an append by rewrite of a file the store keeps no `ETag` for throws `NotSupportedException` | Set `SeekableReadOptions.AllowModifications` to read such a file unpinned. |
| An append or a local stream's commit stored the store's default content type, such as `application/octet-stream` | Both keep the file's content type when the caller gives none; an overwrite without one still stores the default | Nothing. |
| Unsupported operations threw `PlatformNotSupportedException` on some providers | They throw `NotSupportedException` | Catch `NotSupportedException`. |
| An invalid path segment's `ArgumentException` named various parameters | It names the parameter `path` | Nothing. |
| Listings could show names no path could read back | Listings leave out names the provider's path validation would refuse (an empty segment, a name of only dots and spaces, a control character, a backslash, a percent-encoded form of one of these, and what a provider refuses on its own), on every provider. They also leave out two kinds of temporary names the providers make: Physical's `.wfp-<guid>.tmp` write files and Azure's `.wfp-tmp` folder at the root. Every other name is listed, one of yours that starts with `.wfp-` included | Rename such objects with another tool if you need them. Each provider's section names its cases. |
| Writing a file at a folder's path, or a folder under a file's path, did whatever the store did | On S3, MinIO, Azure Blob Storage and Google Cloud Storage, where folders are name prefixes, both writes succeed: the file and the folder share the name, both read back by path, and a listing shows both (MinIO lists only the file, and hides what is under the folder while the file exists); deleting the name removes the file first, and a second delete the folder; a writeable local stream of a folder's path still throws `IOException`. On Physical, Box and SharePoint, where folders are real, either write throws `IOException` | On an object store, check `IsDirectory` on each listed entry rather than assume a name is only one of the two; delete the file to show a folder MinIO hides. |

## What a member throws

What changed: 2026 let each SDK's exceptions out, so code that handled a failure depended on the provider: Azure's
`RequestFailedException`, `AmazonS3Exception`, `GoogleApiException`, Box's `BoxException`, `HttpRequestException`, and a
timeout as `TaskCanceledException`. In 2027 every member of every provider throws only these types, and the SDK's
exception travels as the `InnerException`, unless it already derives from one of the contract's types, which passes as
it is (an `IOException` of the network, say); `OutOfMemoryException` passes unmapped:

| Type | When |
|---|---|
| `ArgumentException` (`ArgumentNullException`, `ArgumentOutOfRangeException`) | A path or an option the provider refuses. |
| `FileNotFoundException` | A file the member needs does not exist, or is a folder. |
| `FileExistsException` | The destination of a copy or a move exists. |
| `PreconditionFailedException` | `IfMatch` or `IfNotExists` did not hold, or a pinned read's file changed. |
| `NotSupportedException` | A feature this provider or its store cannot give. |
| `InvalidOperationException` | The store is not set up as configured: a missing container, bucket, library or root folder, or no credentials to find; or the root was to be modified. |
| `UnauthorizedAccessException` | The store refused the credential or the permission: an HTTP 401 or 403, or a sign-in its identity service refused. |
| `IOException` | Any other failure of the store, the service or the network, timeouts included. |
| `ObjectDisposedException` | The provider is disposed. |
| `OperationCanceledException` | Only when your cancellation token cancelled the call. |

The same holds for the reads of a stream a member returned, for each step of `EnumerateAsync`, and for a local stream's
commit. A failure of the stream you give `UpdateFileAsync` reaches you as it is when it is one of these types, as an
`IOException` usually is, and inside an `IOException` otherwise.

| 2026 | 2027 | What to do |
|---|---|---|
| An SDK's exception: `RequestFailedException`, `AmazonS3Exception`, `GoogleApiException`, `BoxException`, `HttpRequestException` | One of the types above, with the SDK's exception as `InnerException` | Catch the contract's type; read a status or an error code from `InnerException` where you need one. |
| A timeout as `TaskCanceledException` or `OperationCanceledException`, or inside an `AggregateException` | `IOException`, the timeout inside; `OperationCanceledException` only for your token | Handle `IOException` as you handled timeouts (retry, report). |
| A credential or a permission refused, each SDK its own way | `UnauthorizedAccessException`, on every provider | Catch `UnauthorizedAccessException`. |

Each provider's README says which of its store's answers become which type (its "Errors" item).

## Stored configuration

What changed: nothing in the shape. A stored configuration has, as before:

- a `Type` key holding the provider's class name, matched case-insensitively;
- the options' property names, read case-insensitively;
- both root properties.

| 2026 | 2027 | What to do |
|---|---|---|
| S3's `StorageClassForNewFiles` was written as `{"Value":"STANDARD_IA"}` and read back as `STANDARD` | From JSON, both forms read correctly; it is written as the string `"STANDARD_IA"`. Configuration (`FromConfiguration`) reads the string form, which 2026 ignored; the object form is still ignored there | In a configuration file, give the class as a string. |
| A `Type` was searched for in the loaded assemblies | It is found among the assemblies passed in, then this library's own providers (their assembly is loaded when needed), then types registered with `WriteableFileProviderSerializer.Register<TProvider>()`. Loaded assemblies are no longer searched | Register a third-party provider, or pass its assembly in. |
| 2026 read what it wrote | **JSON written by 2027.0.0 is not promised to read back in 2026.10.10.x** | Do not share stored configuration between the two versions once 2027 has written it. |
| A `RootDirectoryPath` (or `RootDirectorySegments`) was used as written, dot segments included: on Azure Blob Storage, `tenants/../other` reached blobs outside the root, and `../secrets` another container | Every provider but Physical checks the root with the rules of a path when it is built: a segment that is empty, only dots and spaces, holds a control character, or decodes to one (`..`, `.`, `%2e%2e`) throws `ArgumentException` naming `RootDirectoryPath`, so **a stored configuration with such a root no longer builds** | Name the folder itself (`other`, not `tenants/../other`). Physical's root is a folder of the disk, which the system resolves, as before. |

New options are written to JSON only when they differ from their defaults, so a configuration that uses none of them
keeps its 2026 shape. Each provider's section lists its new options; some change what an existing configuration does
(MinIO's plain HTTP, Azure's and MinIO's missing container or bucket, SharePoint's work folder).

## Physical

What changed: every write replaces the file through a temp file and a rename, readers share files with writers, and
the provider gains ETags, conditions, ranges and `Watch`. `WriteablePhysicalFileProvider` is sealed.

| 2026 | 2027 | What to do |
|---|---|---|
| An overwrite truncated the file first, so a write that failed left part of the new content | A write goes to a `.wfp-<guid>.tmp` file next to the target, then a rename puts it in place: the file has its old content or its new content, never a part. A failed write removes its temp file, and listings never show one: exactly `.wfp-`, 32 lowercase hex digits and `.tmp`. Any other name, `.wfp-notes.tmp` of yours included, is listed | Nothing, unless you look at the folder another way: a process that dies mid-write can leave a `.wfp-<guid>.tmp` file, which you can delete. A folder watcher sees a rename instead of a write in place. |
| An append wrote in place | An append copies the file and the new bytes into the temp file, then renames: atomic, at the cost of a copy of the file | Files appended to very often, such as logs, pay a copy per append: append in batches. |
| A replaced file stayed the same file, written in place | The new content arrives as a new file at the path. On Linux and macOS it takes the replaced file's permission bits (its target's, for a symbolic link), and a copy its source's exactly, whatever the umask (as `File.Copy` does), both set as the file is created; not the setuid, setgid or sticky bit, nor access control lists or extended attributes. On Windows it takes the permissions the folder gives a new file. Its owner is the process. Other hard links to the file keep the old content, and a symbolic link at the path is replaced by a file, its target left as it was | Give permissions through the folder (inherited ACLs, default ACLs) rather than to single files; write to a link's target, not to the link; do not rely on hard links seeing writes. |
| A file the process could not write was refused (`UnauthorizedAccessException`) | Still refused, with its content kept, on every system: Linux and macOS would allow the rename, so the provider checks first. Also refused, the file keeping its content: a file in a folder the process may not write (`UnauthorizedAccessException`, naming the file), since the new content is created there; a path that is not a regular file or a link to one, such as a FIFO, a socket or a device that cannot seek (`IOException`), which 2026 opened, and waited on forever for a FIFO; and a file that is a mount point, such as a single-file Docker bind mount or a Kubernetes `subPath` (`IOException`), which 2026 wrote in place | Write such files in place yourself, or mount their folder rather than the file. |
| A local stream wrote to the file in place, so `SkipReUploadOnDispose` did nothing | A local stream is a temp copy committed atomically, as on every provider | Nothing. |
| Readers opened files with `FileShare.Read`, so a write or a delete failed while a file was read | Readers share the file with writers and deleters, and keep the content they opened. A delete goes ahead, and so does a write on Linux and macOS. On Windows, which does not rename over a file that is open, the write fails and the file keeps its content: with `UnauthorizedAccessException` when the reader shares writing, as this provider's readers do, and with `IOException` ("being used by another process") when it does not | On Windows, retry a write that meets an open reader, or dispose read streams sooner. |
| `GetFileInfo` of a folder reported a missing path; a missing path had a `PhysicalPath` | A folder exists, `IsDirectory`, `Length` -1, with its `PhysicalPath`; a missing path has none | Check `IsDirectory`. |
| A missing file threw the BCL's `FileNotFoundException`, naming the path on disk | `FileNotFoundException` naming the path under the root, with the BCL's exception as `InnerException` | Read `FileName` as the provider path. |
| Moving onto an existing file threw a plain `IOException` | `FileExistsException`, an `IOException`, and the destination is taken atomically | Catch `FileExistsException` if you told the cases apart. |
| No ETags, conditions or ranges; `Watch` threw `PlatformNotSupportedException` | An `ETag` made from the last-write time and the length; `IfMatch` (checked just before the rename, not atomic); `ReadRangeAsync` through `RandomAccess`; `Watch` as ASP.NET Core's `PhysicalFileProvider` does it (`DOTNET_USE_POLLING_FILE_WATCHER` polls) | Every write now sets the file's last-write time itself: the current time, or later than the time of the file it replaces when the clock or the file system's time resolution has not passed it, so that each write changes the ETag. On a file system that keeps times coarsely (FAT, exFAT, HFS+), writes faster than its resolution push a file's time ahead of the clock, without bound. |
| There was no `IfNotExists`, and an append to a missing file created it whatever appeared meanwhile | `IfNotExists`, an append to a missing file, and the destination of a move or a copy are taken atomically: Windows renames without replacing; Linux and macOS take the name with an exclusive create and rename onto it, so the file exists empty for that moment, a watcher sees it, and a process that dies there leaves an empty file | Nothing, unless you watch the folder: expect that empty file on Linux and macOS. |
| A symbolic link to a file was described by `FileInfo`, which on Windows describes the link itself (length 0, the link's time) | A link to a file is described by its target, which reads open: length, last-write time and ETag, on every system; `PhysicalPath` stays the link's own path. A link to nothing, or in a loop, is described as itself | Nothing. |
| A recursive walk followed linked folders | `EnumerateAsync(recursive: true)` lists a linked folder (symbolic link, junction) but does not walk into it | Walk a linked folder by listing it. |
| On Windows, a name that ends in `.` or a space, or an 8.3 short name, named another file (`report.txt.` and `REPORT~1.TXT` wrote `report.txt`, a delete of `logs ` removed the folder `logs`); a name holding `"`, `<`, `>`, `\|`, `?` or `*` read as missing and failed late; a reserved device name such as `con.txt` was served as a file on the Windows builds that do not enforce the reservation | On Windows such a name is refused with `ArgumentException`: one that ends in `.` or a space, one that holds `"`, `<`, `>`, `\|`, `?` or `*`, and a reserved device name (`CON`, `PRN`, `AUX`, `NUL`, `COM1` to `COM9`, `LPT1` to `LPT9`, and `COM` or `LPT` with `¹`, `²` or `³`), in any case, alone or with an extension, as Windows documents the reservation (recent builds do not always enforce it, so a file such as `con.txt` that 2026 served is now refused and left out of listings); so is an 8.3 short name (`REPORT~1.TXT`) of a file or folder whose long name differs. Linux and macOS take these names as themselves | Pass the name Windows stores (`report.txt`, not `report.txt.`); rename files whose names Windows reserves. |
| Listings showed every name on disk | Listings leave out names no path can address: on Windows the names above; on Linux and macOS a name holding `:` or `\`, a control character, or only dots and spaces (`...`); on any system a name such as `%2e%2e` that decodes to one of these | Rename such files with another tool if you need them. |
| `UnauthorizedAccessException` was documented for writes only | Where the operating system refuses the process access to a file or a folder a member opens, lists, writes, moves or deletes, the member throws `UnauthorizedAccessException`, a read or a listing as well as a write; a path inside a folder the process may not enter reads as missing | Catch it where you read or list files the process may not reach. |
| `WriteablePhysicalFileProvider` could be derived from | It is sealed | Wrap it instead: implement `IWriteableFileProvider` and delegate, or derive your own provider from `WriteableFileProvider<TOptions>`. |
| On Linux and macOS, `Options.RootDirectoryPath`, and so `ToJson`, gave `root:/srv/files` | `/srv/files`. The `root:` form still reads, from JSON and from configuration, and `RootDirectorySegments` keeps its 2026 form | Code that stripped `root:` can stop. |
| A root of `C:\` read back as `C:` | `C:\` | Nothing. |
| A root in the extended or device form (`\\?\C:\files`, `\\?\UNC\server\share`) was used as given | On Windows it is taken, and written back, in its plain form (`C:\files`, `\\server\share`), so the name rules hold under it; one with no plain form throws `ArgumentException`: a volume GUID, a device, or a name the plain form would change (one ending in a dot or a space, `.` or `..`) | Give such a root as a drive or share path. |

New: `IfMatch`, `IfNotExists`, ranged and seekable reads, copies and moves that never replace a file, and `Watch`.
Physical signs no URLs: the ASP.NET Core package's gateway serves them.

## Azure Blob Storage

What changed: Azure.Storage.Blobs moves from 12.23 to 12.30, files are block blobs, and the provider gains Microsoft
Entra ID sign-in, conditions, ranges and upload URLs. The package also references Azure.Identity 1.21.0, whose types now
live in Azure.Core; the reference keeps an older Azure.Identity of yours from defining them twice.
`WriteableAzureBlobFileProvider` is sealed. The configuration shape is unchanged; the two new options are written to
JSON only when `true`.

| 2026 | 2027 | What to do |
|---|---|---|
| Files are append blobs; an overwrite empties the blob, then appends, so a failed write truncates the file | Files are block blobs, staged and committed in one request: a failed write keeps the old content, and readers never see half of it | Nothing. |
| A missing container is created by the first write or move; reads report it as missing files and empty listings | A missing container makes every member throw `InvalidOperationException`, naming the container | Create the container, or set `CreateContainerIfMissing: true` to keep the old creation on the first write. Reads never create it. |
| Reading a missing file throws `RequestFailedException` (404) | `FileNotFoundException`, with the SDK's error as `InnerException` | Catch `FileNotFoundException`. |
| `ReadFileAsync` returns `AzureFileStream`, a public type that reports `Length` | The SDK's download stream, read forward only; `AzureFileStream` is gone | Take the length from `GetFileInfoAsync`, or use `OpenReadSeekableAsync`, a seekable stream that knows its length and reads ranges. |
| `MoveAsync` onto an existing file replaces it | `FileExistsException`, destination unchanged. A source that changes while it is moved is kept, the copy removed, and `PreconditionFailedException` thrown | Delete the destination first if you meant to replace it. |
| `GetFileInfo` of a folder: not existing; of a missing path: `Length` 0; `PhysicalPath` is the blob name | A folder exists and is a directory; a missing path has `Length` -1; `PhysicalPath` is null; files carry `ETag` and `ContentType` | Use `IWriteableFileInfo.Path` where you used `PhysicalPath`. |
| A download URL starts now, and a validity over 7 days is cut to 7 days silently | It starts 15 minutes in the past and ends exactly at the validity; over 7 days throws `NotSupportedException`; a URL for a missing file throws `FileNotFoundException` | Ask for at most 7 days, or use the gateway. |
| Read URLs only, signed with the account key | Read, create and write URLs; with Microsoft Entra ID, user delegation SAS | See the README's "Signed URLs". |
| Deleting a blob with snapshots fails (`RequestFailedException`) | It still fails, now with `IOException`, and so does a move of such a blob; snapshots are never deleted, except by the append blob conversion below | Delete the snapshots first. |
| Listings showed every blob | Blobs whose names no path can address (an empty, `.` or `..` segment, a control character, a backslash) and the provider's own temporary folder, `.wfp-tmp` at the provider root, are left out. Every other name, one of yours that starts with `.wfp-` included, is listed | Rename such blobs with another tool if you need them. |
| `WriteableAzureBlobFileProvider` can be derived from | It is sealed | Wrap it instead, or derive from `WriteableFileProvider<AzureFileProviderOptions>`. |
| A root with a dot segment, such as `tenants/../other`, reached blobs outside the root, and `../secrets` another container | Building the provider throws `ArgumentException` naming `RootDirectoryPath` (see [Stored configuration](#stored-configuration)) | Name the folder itself. |
| Two overwrites of one file at once: both commit, one after the other | The one that commits second finds its blocks dropped by the first: one block's worth is staged again, so the last writer still wins; a longer one throws `IOException` and stores nothing | Write it again, or serialize the writers. |
| Options: shared key only | `UseDefaultAzureCredential` (Microsoft Entra ID) and a constructor that takes a `BlobContainerClient`, `new WriteableAzureBlobFileProvider(options, container)` (options may be null), which the provider never disposes. `AccountKey` must be base64, and setting it together with `UseDefaultAzureCredential` throws | Fix a key that is not base64; it never worked. |

**Append blobs already stored.** They stay readable. The first write or append to one converts it to a block blob: the
new content goes to a temporary blob under `.wfp-tmp/` in the provider root, the append blob is deleted if it has not
changed, and the temporary blob is copied to its name. Meanwhile the file is missing, or empty while the server copy
runs; if the copy fails, the exception names the temporary blob holding the content. A 2026.10.10.x provider cannot
append to a block blob (`InvalidBlobType`), so a fleet that mixes the two lines must not append to shared files during
the switch: upgrade every writer together. An upload SAS cannot replace an append blob either; write such a file once
through the provider first.

New: `CreateContainerIfMissing`, `UseDefaultAzureCredential`, the `BlobContainerClient` constructor, atomic `IfMatch`
and `IfNotExists`, appends that never lose a concurrent write, native ranges, server-side copies, and create and write
SAS URLs.

## Amazon S3 and MinIO

What changed for both: they run on one implementation of the S3 protocol, on the AWS SDK for .NET v4 (AWSSDK.S3 4.x).
WriteableFileProviders.Minio no longer depends on the Minio SDK, so Minio, Microsoft.Extensions 9 and System.Reactive
leave your dependency graph; reference the Minio SDK yourself if you used its types. If you use AWSSDK.S3 directly,
follow AWS's v3-to-v4 migration guide: response collections can be null, value types are nullable, checksums are on by
default, and the obsolete `S3Region` short names (`US`, `EU`, `SFO`…) are gone. Both provider classes are sealed, and
their new options are written to JSON only when set.

### Amazon S3

| 2026 | 2027 | What to do |
|---|---|---|
| `ReadFileAsync(string versionId, params string[] absolutePathSegments)`, `EnumerateObjects(string[], bool)` (obsolete since the 2026.10 security patch) | Removed | `ReadFileVersionAsync(versionId, path)` and `EnumerateAsync(path, recursive: true)`, both relative to the root. |
| `ReadFileVersionAsync(string versionId, params string[] pathSegments)` | `ReadFileVersionAsync(string? versionId, ProviderPath path, CancellationToken)` | A call with one path string compiles unchanged; pass several segments as `"a/b.txt"` or an array. |
| `ReadFileAsync(string subpath)` on the concrete type | Removed | `ReadFileAsync(path)` of the interface, which a string converts to. |
| `WriteableAmazonS3FileProvider` could be derived from | It is sealed | Derive a custom provider from `WriteableFileProvider<TOptions>`, or wrap this one. |
| Missing file: `AmazonS3Exception` (NoSuchKey) | `FileNotFoundException`, with the SDK's error inside | Catch `FileNotFoundException`. |
| A folder: `Exists` false, `IsDirectory` true; `PhysicalPath` the key | `Exists` and `IsDirectory` true; `PhysicalPath` null | Use `IWriteableFileInfo.Path`. |
| Move overwrote an existing destination | `FileExistsException`; it deletes exactly the source key, with `If-Match` on the version it copied; a source deleted meanwhile completes the move, and one changed meanwhile undoes the copy with `PreconditionFailedException` | Delete the destination first if you meant to replace it; retry on `PreconditionFailedException`. |
| A missing bucket failed with the SDK's error | `InvalidOperationException` naming the bucket, unless `CreateBucketIfMissing` is set | Create the bucket, or set the option. |
| One PUT per write, at most 5 GB, and the caller's stream was disposed | Multipart above 8 MiB (up to 5 TiB from a stream that knows its length, about 430 GiB from one that does not); the stream is left open | Dispose your stream yourself. |
| A failed append could upload a half-appended file | An append is a rewrite with `If-Match`: it fails whole, and a concurrent write makes it throw `PreconditionFailedException` | Retry on `PreconditionFailedException`. |
| Content type inferred from the key's extension | The `ContentType` you give, else `application/octet-stream`; an append keeps the file's type | Pass `UpdateFileOptions.ContentType` where you relied on the inference. |
| Download URLs forced `Content-Type: application/octet-stream` | Read URLs serve the stored type unless `SignedUrlOptions.ContentType` says otherwise; write URLs are new, and so are create URLs on AWS. Toward an endpoint named by `ServiceUrl` or a client's `ServiceURL`, a create URL throws `NotSupportedException`, since such a server may ignore the `If-None-Match` that guards it | Set `ContentType` on the URL to keep the old header. Get create URLs toward other servers from the ASP.NET Core package's URL service, which issues a gateway URL. |
| A presigned URL lived as asked, whatever the credential | A credential that expires sooner, or a session token, needs `AllowShorterValidity` | Use long-term keys, or accept the shorter URL. |
| The S3Region constructors stored `""` for us-east-1 and `"EU"` for eu-west-1 | They store `us-east-1` and `eu-west-1`, and the old values read as those regions | Nothing. |
| A key with an empty segment, `..`, a backslash or a control character was listed | Left out of listings, since no path can name it | Rename such keys with another tool. |

New: `ServiceUrl` and `ForcePathStyle` for S3-compatible servers, `UseDefaultCredentials` for the SDK's default
credential chain, `CreateBucketIfMissing`, and a constructor that takes your own `IAmazonS3`. `IfMatch` and
`IfNotExists` are atomic on AWS; toward an endpoint named by `ServiceUrl` or a client's `ServiceURL`, the provider also
checks them first, which is not atomic. An endpoint the AWS SDK takes from its own configuration (`AWS_ENDPOINT_URL_S3`,
`AWS_ENDPOINT_URL`, a profile's `endpoint_url`) counts as named too. A `ServiceUrl`, a client's own endpoint, or the
SDK's configured endpoint, with plain `http` toward a host that is not loopback throws `ArgumentException` when the
provider is built, unless the new `AllowInsecureHttp` option is set, as on MinIO. Folders are key prefixes, so a file
and a folder may share a name.

### MinIO

| 2026 | 2027 | What to do |
|---|---|---|
| `WriteableMinioFileProvider` could be derived from | It is sealed | Derive a custom provider from `WriteableFileProvider<TOptions>`, or wrap this one. |
| `UseSSL` false sent everything over plain HTTP to any host | Plain HTTP only toward a loopback host; any other host throws `ArgumentException` when the provider is built | Set `UseSSL`, or `AllowInsecureHttp` for a server you reach over plain HTTP on purpose (below). |
| A missing bucket was created on the first write | `InvalidOperationException` naming the bucket, unless `CreateBucketIfMissing` is set | Set `CreateBucketIfMissing: true` to keep the old behaviour. |
| Missing file: `ObjectNotFoundException`; missing bucket: `BucketNotFoundException` | `FileNotFoundException`; `InvalidOperationException` | Catch those. |
| `ReadFileAsync` downloaded the whole file to a seekable temp file | A forward-only network stream | Use `OpenReadSeekableAsync` (ranged reads) or `CreateLocalFileStreamAsync` (a local copy) where you seek. |
| Download URLs threw `NotSupportedException` | Presigned read and write URLs, up to 7 days. Create URLs throw `NotSupportedException`: their only guard would be an `If-None-Match` that MinIO releases without conditional writes ignore | Get create URLs from the ASP.NET Core package's URL service, which issues a gateway URL. |
| Every file was stored as `application/octet-stream` | The `ContentType` you give is stored; without one, still `application/octet-stream` | Nothing. |
| Move overwrote an existing destination | `FileExistsException`, checked before the copy; the source's version is checked before its delete: a source deleted meanwhile completes the move, and a change undoes the copy with `PreconditionFailedException` (MinIO ignores `If-None-Match` on copies and `If-Match` on deletes, so neither check is atomic) | Delete the destination first if you meant to replace it; retry on `PreconditionFailedException`. |
| The Minio SDK asked the server for the bucket's region | The provider asks once (`GetBucketLocation`), or takes the new `Region` option | Set `Region` for a server with a site region whose bucket does not exist yet. |
| Every key was listed | Keys no path can name (an empty segment, `..`, a backslash, a control character) are left out | Rename such keys with another tool. |

**Finding the configurations the plain-HTTP rule breaks:** search your configuration files and stores for
`WriteableMinioFileProvider` entries whose `UseSSL` is `false` or absent and whose `HostAndPort` is not `localhost`, a
`127.x.x.x` address or `[::1]`. Each needs `"UseSSL": true`, or `"AllowInsecureHttp": true` if plain HTTP is intended.
`HostAndPort` still takes `host:port` only: a scheme, a path or a query is refused, as the Minio SDK refused it.

New: `AllowInsecureHttp`, `Region`, `CreateBucketIfMissing`, and a constructor that takes your own `IAmazonS3`.
`IfMatch` and `IfNotExists` are checked by the provider first, which is not atomic, and sent to the server, which
enforces them where it has conditional writes; older MinIO releases ignore the headers.

## Google Cloud Storage

What changed: `WriteableFileProviders.GoogleCloudStorage` moves to `Google.Cloud.Storage.V1` 5.0 (from 4.15), builds its
client on the first call, and follows the contract. If you use that SDK directly, its 5.0 break is small:
`UploadValidationException` and `UploadValidationMode.ThrowOnly` are gone, and uploads are validated by the server
(`RejectAndThrow`) by default. `WriteableGoogleCloudStorageFileProvider` is sealed.

| 2026 | 2027 | What to do |
|---|---|---|
| The constructor looked up Application Default Credentials synchronously, so it did network I/O and threw where there were none | The constructor reads the key only; the client is built, and Application Default Credentials looked up, on the first call, which throws `InvalidOperationException` where there are none | Nothing; a configuration without credentials fails on its first call instead of at construction. |
| A key it could not read threw `FormatException`; a missing bucket name threw `ArgumentException` for `BucketName` | Both throw `ArgumentException` for `options`, as does an `Endpoint` that is not a root URL | Catch `ArgumentException`. |
| Only the options constructor | Also `new WriteableGoogleCloudStorageFileProvider(options, StorageClient)`, which never disposes the client | Use it for your own credentials, retries, HTTP handling or encryption key. |
| `WriteableGoogleCloudStorageFileProvider` could be derived from | It is sealed | Derive a custom provider from `WriteableFileProvider<TOptions>`, or wrap this one. |
| No way to reach another endpoint | `Endpoint`: a root URL, such as fake-gcs-server's `http://localhost:4443` or a Private Service Connect endpoint. Plain `http` toward a host that is not loopback throws `ArgumentException` unless `AllowInsecureHttp` is set | Nothing; it is additive. |
| A missing bucket: listings threw `GoogleApiException`, `GetFileInfo` said "not found", writes threw `GoogleApiException` | Every member throws `InvalidOperationException` naming the bucket; `CreateBucketIfMissing` (default false) creates it on the first call instead | Create the bucket, or set the option (with `ProjectId` when the key does not name the project). |
| A missing file threw `GoogleApiException` 404 | `FileNotFoundException`, with the `GoogleApiException` as `InnerException` | Catch `FileNotFoundException`. |
| `GetFileInfo` on a folder said it did not exist | A folder that holds objects exists and is a directory, with `Length` -1 | Check `IsDirectory`. |
| `GetFileInfo` of a path that is not a file read one object | It also lists objects, to tell a folder, so a credential needs `storage.objects.list` for it, as for listings, copies and appends to a new file (it gets `UnauthorizedAccessException`, Cloud Storage's 403, otherwise) | Grant `storage.objects.list` (the `roles/storage.objectViewer` and `roles/storage.objectAdmin` roles have it). |
| `PhysicalPath` held the object's name | `PhysicalPath` is null; `IWriteableFileInfo.Path` is the path | Use `Path`. |
| `ReadFile` downloaded the whole object to a temp file and returned it, seekable | It returns the download as it arrives: read forward, with `Length` when the server sends it | Use `OpenReadSeekableAsync` to seek, or `ReadRangeAsync` for a slice; both download only what is read. |
| Move and copy overwrote an existing destination | They throw `FileExistsException` and leave it unchanged; a move deletes exactly the generation it copied, and throws `PreconditionFailedException` if the source was written meanwhile, keeping that write | Delete the destination first if you meant to replace it. |
| A copy or move was retried by the SDK | It is not retried: when one fails with a server error or a timeout, the copy may still have landed | Check the destination before you try again. |
| An append whose source stream failed uploaded a half-appended file; a concurrent write could be lost | An append leaves the file unchanged when it fails, and throws `PreconditionFailedException` instead of losing a concurrent write | Retry the append on `PreconditionFailedException`. |
| The caller's stream was disposed after a write | It is left open | Dispose it yourself. |
| Every object was listed | Objects whose names no path can address, such as `a//b` or `x/../y`, are left out | Rename such objects with another tool if you need them. |
| Read URLs only, with the stored name and type | Read, create (only if absent) and write URLs; a read URL can carry `DownloadFileName` and `ContentType` | See the package README for the headers an upload must send. |
| A validity over seven days threw the SDK's `ArgumentOutOfRangeException`; a credential that could not sign threw `PlatformNotSupportedException`; a URL was signed for a missing file | Over seven days, or under one second, throws `NotSupportedException` (so the gateway can take over), and the validity is rounded down to whole seconds; a credential that cannot sign, or an identity whose signing through IAM's `signBlob` is refused (403), answers another error than 401 or cannot be reached, throws `NotSupportedException` with the cause inside (a refused credential and a timeout fail as on every other member); a read URL for a missing file throws `FileNotFoundException` | Catch `NotSupportedException`. |

New: `ETag` is the object's generation, and `IfMatch` and `IfNotExists` are atomic; `ContentType` is stored
(`application/octet-stream` when a write gives none, as in 2026; an append or a local stream keeps the file's) and
reported. The new options, `Endpoint`, `AllowInsecureHttp` and `CreateBucketIfMissing`, are written to JSON only when
set, so a configuration that uses none of them keeps its 2026 shape.

## Box

What changed: the provider is rebuilt on `Box.V2.Core` 10 (the generated SDK, namespace `Box.Sdk.Gen`), signs in on its
first call, and follows the contract. `WriteableBoxFileProvider` is sealed. Box adds no options, so its configuration
is stored as before.

| 2026 | 2027 | What to do |
|---|---|---|
| `Box.V2.Core` 5.8 (namespace `Box.V2`) | `Box.V2.Core` 10.19 (namespace `Box.Sdk.Gen`); v10 no longer has the `Box.V2` namespace | Code of yours that uses the Box SDK directly moves to `Box.Sdk.Gen` (Box's own guide: `migration-guides/from-v5-to-v10.md` in `box/box-windows-sdk-v2`). Newtonsoft.Json and Portable.BouncyCastle leave the graph; the SDK brings BouncyCastle.Cryptography 2, System.IdentityModel.Tokens.Jwt 8 and Microsoft.Extensions.Http and DependencyInjection 8. |
| The constructor signed in to Box, blocking on the network (and threw `AggregateException` on a bad credential) | No network I/O in the constructor: it checks that every credential is set and that the private key opens with the passphrase (`ArgumentException`), and signs in on the first call | A wrong client id or secret now fails on the first call, with `UnauthorizedAccessException` (the SDK's `BoxApiException` inside). `WriteableFileProviderSerializer` builds a Box provider offline. |
| `WriteableBoxFileProvider` could be derived from | It is sealed | Wrap it, or write a provider on `WriteableFileProvider<TOptions>`. |
| Only the options constructor | Also `WriteableBoxFileProvider(BoxFileProviderOptions, IBoxClient)`: a ready client (another authentication, as-user, a proxy, custom retries), which the provider never disposes | Optional. With a client, only `RootFolderId` and the root folder are read from the options. |
| `GetFileInfo` on a folder: `Exists` false, `IsDirectory` true | `Exists` true, `IsDirectory` true, `Length` -1; a missing path has `Length` -1 (was 0) | Test `IsDirectory` and `Exists` as for every provider. |
| `PhysicalPath`: the joined path from `GetFileInfo`, the bare name in listings | Always null | Use `IWriteableFileInfo.Path`. |
| Overwrite rewound a seekable stream and uploaded it from byte 0 | Every write uploads from the stream's current position | Rewind the stream yourself if you meant to upload it all. |
| Move created no folders (`DirectoryNotFoundException`), moved folders, and wrapped Box's 409 in `BoxException` | Moves files only, creates the destination's folders, and throws `FileExistsException` when the destination exists | Move a folder's files one by one, or use the Box SDK. |
| Errors were re-thrown as `BoxException` with the path in the message | The contract's exceptions ([What a member throws](#what-a-member-throws)): `FileNotFoundException`, `FileExistsException`, `PreconditionFailedException`, `ArgumentException` for a name Box refuses, `InvalidOperationException` for a `RootFolderId` that does not exist or the app cannot see, `UnauthorizedAccessException` for Box's 401 and 403 and a sign-in it refuses, `IOException` for the rest, with the SDK's `BoxApiException` (status in `ResponseInfo.StatusCode`) inside. A failed download throws `FileNotFoundException` or `IOException` with its status only, since the SDK's error names the pre-authenticated URL. When the provider built its client, or was given exactly a `BoxClient`, every `BoxApiException` it wraps keeps Box's status, code and message, and of the request only its method, its URL without the query, and an allow-list of headers and parameters (the SDK keeps the access token, extra headers, the query and the body, and a token request's body holds the client secret); over any other `IBoxClient`, a class derived from `BoxClient` included, the errors are the client's own and may carry its credentials | Catch the contract's exceptions; read Box's status and code from the `BoxApiException` inside. |
| Writing under a file threw `InvalidOperationException` | `IOException` | Catch `IOException`. |
| Folder listings paged by offset (500 a page) | Marker paging, 1000 a page | Nothing. |
| Listings showed every item | Items whose names no path can address are left out: a name of only dots and spaces (`...`), or one such as `%2e%2e` that decodes to one | Rename such items in Box if you need them. |
| `ReadFile` downloaded the whole file to a temp file first | It streams the download; ranged and seekable reads fetch only their bytes, pinned to the version they opened | Nothing. |
| `GetDownloadUrl(validity)` ignored the validity: Box's URL lives about 15 minutes | `GetSignedUrlAsync` refuses a validity under 15 minutes (`NotSupportedException`, so the gateway serves it), and returns Box's URL for 15 minutes to 7 days only with `AllowShorterValidity`, with `ExpiresOn` set | Ask for at least 15 minutes with `AllowShorterValidity = true`, or use the gateway. `GetDownloadUrl(path, validity)` throws `NotSupportedException` for Box, since it cannot accept a shorter URL. |
| Chunked uploads from 20,000,000 bytes through the SDK's helper | From 20 MiB, through the provider's own upload-session loop (exact part sizes, part and whole-file digests, a bounded wait while Box processes the commit, the session aborted on failure) | Nothing. |

New: a file's `ETag` is the id of its current Box file version, not Box's `etag`, which starts again at `0` for a file
deleted and created again. `IfNotExists` is atomic on Box. `IfMatch` is checked just before the write, not atomic, and
appends are checked the same way; a later release may make them atomic once a live run confirms that Box honours the
ETag the SDK sends. Box keeps no content type, so `ContentType` is ignored on writes and null on reads.

## SharePoint

What changed: the provider moves to Microsoft.Graph 6 and Microsoft.Graph.Core 4 (Kiota 2); code that uses those SDKs
directly moves with it. The work folder becomes the start of the root, and the provider follows the contract.
`WriteableSharePointFileProvider` is sealed: wrap an `IWriteableFileProvider` (or derive from
`WriteableFileProvider<TOptions>`) instead of deriving from the provider. The public `UploadResult<T>` class is gone.

### The work folder is the start of the root

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

| 2026 | 2027 | What to do |
|---|---|---|
| The constructor signed in and looked up the site, library and work folder, blocking on the network; a failure threw `InvalidOperationException("Failed to initialize SharePoint connection")` | No I/O in the constructor. The site and library are looked up on the first call; a missing one throws `InvalidOperationException` naming `SiteUrl` or `DocumentLibraryName` from every member that reaches the library, and any other failure is retried on the next call | Expect configuration errors on the first call, not at construction. |
| A site URL with an escaped character (`Team%20Site`) was escaped twice and not found | It is found | Nothing. |
| Every Graph error while reading file info meant "missing" or `FileNotFoundException` | Only a 404 means missing. 401 and 403 throw `UnauthorizedAccessException`; throttling after the SDK's retries, and every other error, `IOException`; each wraps Graph's error | Catch `UnauthorizedAccessException` and `IOException` where you caught `FileNotFoundException` for them. |
| An empty folder listed as `Exists = false` | It exists | Nothing. |
| A folder's `Length` was the size of its contents, a missing path's 0, and `PhysicalPath` held the path or the name | -1, -1 and null | Use `IWriteableFileInfo.Path`. |
| Writes rewound the caller's stream, a simple upload disposed it, and a missing parent retried without end | Writes read from the stream's position, leave it open, and create missing parents once | Rewind the stream yourself if you meant to upload it all, and dispose it. |
| Upload sessions of 3.2 MB slices with no retry, ending silently on a short read | 5 MiB fragments from a temp copy; a transient failure is resent once, any other cancels the session and throws | Nothing. |
| Appends combined in memory or a temp file in two ways | One append by rewrite, with `If-Match` on the version read | Retry an append that throws `PreconditionFailedException`. |
| Reads downloaded the whole file to a temp file first; a 0-byte file threw `FileNotFoundException` | Reads stream; ranges are native; a 0-byte file reads empty | Use `OpenReadSeekableAsync` where you seek. |
| Move was a lookup and a `PATCH` | One `PATCH` with the conflict behaviour `fail`: a taken destination throws `FileExistsException`. Copy runs on the server, waited for at most 10 minutes | Delete the destination first if you meant to replace it. |
| A path segment with a percent-escape (`a%41`) reached the service unescaped, which decoded it (`aA`) | The `%` is escaped, so `a%41` names an item called `a%41`. Files 2026 wrote through such a path are under the decoded name. `WorkFolderPath` and `RootDirectoryPath` with such an escape are refused instead, so a stored value never silently names another folder. Listings leave out names the provider cannot address, such as one that decodes to `..` | Use the decoded name for files 2026 wrote through an escaped path. |
| `UpdateFileOptions` did not exist | `IfMatch` and `IfNotExists` are checked before the write and sent to the service; not promised atomic. `ContentType` is ignored: SharePoint names the type from the file name | Nothing. |
| `GetDownloadUrl` returned the download URL whatever validity was asked | A read URL (`SignedUrlKind.PreAuthenticated`, `ExpiresOn` null) only for a validity of an hour to 7 days, and only with `AllowShorterValidity`; a shorter validity throws `NotSupportedException`, so the gateway can serve it | Ask for at least an hour with `AllowShorterValidity = true`, or use the gateway. `GetDownloadUrl(path, validity)` throws `NotSupportedException` for SharePoint, since it cannot accept a shorter URL. |

New, for sign-in, all optional: `ClientCertificateBase64` and `ClientCertificatePassword` (a base64 PKCS#12 file), and
`UseDefaultAzureCredential` (managed identity, workload identity, environment, developer sign-in). The first one
configured wins: `UseDefaultAzureCredential`, then the certificate, then `ClientSecret`. The new constructor
`WriteableSharePointFileProvider(options, GraphServiceClient)` takes a client you build and own; the provider never
disposes it. Stored 2026 configurations read unchanged. `ToJson` writes the three new keys only when they are set, so a
configuration that uses none of them is written exactly as 2026 wrote it.

## New package: WriteableFileProviders.AspNetCore

What changed: 2027.0.0 adds `WriteableFileProviders.AspNetCore`, for ASP.NET Core apps on .NET 10. Nothing in 2026 maps
onto it, so there is nothing to migrate; it is the way to keep handing out download links where a provider's own URLs
changed:

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

The [package README](../src/WriteableFileProviders.AspNetCore/README.md) covers the options, the endpoints' status
codes and headers, and how to keep tokens out of logs.
