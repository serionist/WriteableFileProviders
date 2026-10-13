# Moving to WriteableFileProviders 2027.0.0

Every breaking change from `2026.10.10.x`, and what to do about it. What's new is in the [changelog](../CHANGELOG.md).

## Start here

- [ ] **Target `net10.0`.** .NET 8 and 9 apps stay on `2026.10.10.x`, which gets security fixes only
  ([SECURITY.md](../SECURITY.md)). It is unlisted on nuget.org: reference it by version.
- [ ] **Update every WriteableFileProviders package to 2027.0.0 at once.** NuGet warns (NU1608) on a mix of majors.
- [ ] **Move to the async API**, with a `CancellationToken`. See [Common: the API](#common-the-api).
- [ ] **Pass one path** (`"a/b.txt"`), and `ProviderPath.Root` for the root.
- [ ] **Dispose the providers you create** (`await using`).
- [ ] **Move JSON code to System.Text.Json.** See [Stored configuration](#stored-configuration).
- [ ] **Stop deriving from provider classes.** They are sealed: wrap one, or derive from
  `WriteableFileProvider<TOptions>`.
- [ ] **Catch the contract's exceptions**, not the SDK's. See [What a member throws](#what-a-member-throws).
- [ ] **Expect `NotSupportedException` from `GetSignedUrlAsync`**, or use
  [WriteableFileProviders.AspNetCore](#new-package-writeablefileprovidersaspnetcore).
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

- 2026 used date versions (`2026.10.10.4`); 2027 uses semantic versions, ordered after them.
- `2027.x.y` releases are compatible; the next break is `2028.0.0`. All packages share one version
  ([versioning policy](guide/versioning.md)).

## Common: the API

Every 2026 form in this table still compiles, with an `[Obsolete]` warning, until 2028.0.0:

| 2026 | 2027 | What to do |
|---|---|---|
| `ReadFileAsync("a", "b.txt")` | `ReadFileAsync("a/b.txt")` | One path, or a `string[]`. |
| `ReadFile`, `UpdateFile`, `Delete`, `Move`, `CreateLocalFileStream` | The `...Async` member | – |
| `GetFileInfo(segments)`, `GetDirectoryContents(segments)` | `GetFileInfoAsync(path)`, `GetDirectoryContentsAsync(path)` | – |
| `UpdateFileAsync(stream, mode, segments)` | `UpdateFileAsync(path, stream, UpdateFileOptions.Append)` | Path first. |
| `CreateLocalFileStreamAsync(readOnly, path)` | `CreateLocalFileStreamAsync(path, readOnly)` | Path first. |
| `GetDownloadUrl(path, validity)` → `string` | `GetSignedUrlAsync(path, new SignedUrlOptions { Validity = validity })` | Read `.Url`, or use `IWriteableFileUrlService`. |
| `WatchAsync(filter)` | `Watch(filter)` | – |
| `MoveAsync(IFileInfo, ...)` and other `IFileInfo` overloads | `MoveAsync(entry.Path, ...)` | Pass the entry's path. |

- The sync forms are extension methods now: they need `using WriteableFileProviders;`.
- The `IFileInfo` overloads take only an `IWriteableFileInfo`, and throw `ArgumentException` for any other.
- `CreateLocalFileStream(Async)(readOnly)` and `UpdateFile(Async)(contents, mode)` with no path still compile, and
  throw at run time.

These no longer compile:

| 2026 | 2027 | What to do |
|---|---|---|
| `GetDirectoryContentsAsync()`, `GetFileInfoAsync()` | `GetDirectoryContentsAsync(ProviderPath.Root)` | Pass the root. |
| `FileInfo`, `PathSegments` | `WriteableFileInfo` (sealed), `Path.Segments` | – |
| `new DirectoryContents(exists, IFileInfo[])`, derivable | Sealed; takes `IEnumerable<IFileInfo>`, never null | Pass an empty sequence, not null. |
| `IWriteableFileProvider.ToJson()` → `JObject` | `WriteableFileProviderSerializer.ToJson(provider)` → `JsonObject` | `ToJsonString` for text. |
| `FromJson(JObject)` | `FromJson(string)`, or a `JsonElement` or `JsonObject` | Pass the JSON text. |
| `OnLocalFileStreamDisposingAsync` | Gone | Drop your implementation. |
| `new LocalFileStream(...)`, `TargetFilePathSegments` | `CreateLocalFileStreamAsync(path, readOnly)`, `Path` | – |
| `LocalFileStream.DisposeAsync()` → `Task` | → `ValueTask`; `CommitAsync()` uploads | – |
| Your own `IWriteableFileProvider` | `Options` and every async member | Derive from `WriteableFileProvider<TOptions>` instead. |
| Common brought in Configuration, Configuration.Binder, Newtonsoft.Json | `.Abstractions` packages only | Reference `Microsoft.Extensions.Configuration` yourself. |

## Common: writing your own provider

| 2026 | 2027 | What to do |
|---|---|---|
| `getFileInfoAsync`, `readFileAsync`, `deleteAsync` | `GetFileInfoCoreAsync`, `ReadFileCoreAsync`, `DeleteCoreAsync` | Take `ResolvedPath` and `CancellationToken`. |
| `getDirectoryContentsAsync` | `EnumerateCoreAsync` → `IAsyncEnumerable<IWriteableFileInfo>` | Yield the entries. |
| `updateFileAsync(Stream, UpdateFileMode, ...)` | `UpdateFileCoreAsync(path, stream, UpdateFileOptions, ct)` | Honour mode, content type and conditions. |
| `move(string[], string[])` | `MoveCoreAsync`, optional | Default: copy, then delete the source. |
| `getDownloadUrl` | `GetSignedUrlCoreAsync` with `SupportsSignedUrl(access)` | Override both, or neither. |
| `watchAsync`, abstract | `WatchCore`, optional | – |
| A `createLocalFileStreamAsync` override | Removed: local streams aren't customisable | Move its logic into `ReadFileCoreAsync`, `UpdateFileCoreAsync`. |
| `new FileInfo(provider, exists, length, physicalPath, name, ...)` | `new WriteableFileInfo(provider, path, exists, isDirectory, length, ...)` | Reorder the arguments. |
| Store exceptions escaped | Mapped to the contract's types | Override `IsNotFound`, `IsAccessDenied`. |
| `Options.RootDirectorySegments` | `Resolve(ProviderPath.Root)` | Read the validated root. |
| `isSegmentAllowed`, `validateSegments`, `normalizePath` | `IsSegmentAllowed`, `ValidateSegments`, `ProviderPath.Parse` | – |
| `getRelativeSegments`, `isRootPath`, `fileSegments` | `ResolvedPath.RelativeSegments`, `ResolvedPath.IsRoot`, `IWriteableFileInfo.Path` | – |
| `normalizePem`, `RunTaskSerial` | Removed | Keep your own helper; await the task. |

- The base maps what escapes a `...CoreAsync` member, stream or listing: `IsNotFound` → `FileNotFoundException`,
  `IsAccessDenied` → `UnauthorizedAccessException`, contract types as they are, the rest → `IOException`.
- The base constructor validates the root, calling `IsSegmentAllowed` and `ValidatesRootDirectory` before yours runs.
  Neither may use your provider's state. Return false from `ValidatesRootDirectory` if the root isn't a store path.
- Give every file an `ETag`: pinned seekable reads and `AppendByRewriteAsync` need one.
- [Writing a provider](guide/writing-a-provider.md) has the optional overrides, the helpers and the capability members.

## Common: behaviour

| 2026 | 2027 | What to do |
|---|---|---|
| Moving or copying a missing file or a folder: per provider | `FileNotFoundException` | Catch it. |
| Moving onto an existing file replaced it on some providers | `FileExistsException`; the destination is kept | Delete the destination first. |
| A folder: `Exists` false on some providers | Exists, `IsDirectory`, `Length` -1; a missing path has `Length` -1 | Check `IsDirectory`. |
| `PhysicalPath`: a key or name on cloud providers | Null on cloud providers | Use `IWriteableFileInfo.Path`. |
| Signed URL validity ignored or clamped | Too long: `NotSupportedException`; not positive: `ArgumentOutOfRangeException` | Catch it, or use the URL service. |
| A read URL for a missing file was signed | `FileNotFoundException` | – |
| `DownloadFileName`, `ContentType` ignored by some | Honoured, or `NotSupportedException` | – |
| No ETags | A pinned seekable read of a file without one: `NotSupportedException` | Set `SeekableReadOptions.AllowModifications`. |
| `PlatformNotSupportedException` | `NotSupportedException` | Catch `NotSupportedException`. |
| Listings showed every name | Names no path can address are left out | Rename them with another tool. |
| A file and a folder of one name: per store | Object stores keep both; Physical, Box, SharePoint throw `IOException` | Check `IsDirectory` on each listed entry. |

- Left out of listings: empty or whitespace names, only dots and spaces, control characters, backslashes, names that
  percent-decode to an invalid segment (`%2e%2e`), Physical's `.wfp-<guid>.tmp` files, Azure's root `.wfp-tmp` folder.
- On object stores, a delete of a shared name removes the file, a second the folder. MinIO lists only the file, and
  hides the folder while the file exists.

## What a member throws

Every member of every provider throws only these types:

| Type | When |
|---|---|
| `ArgumentException` | A path or an option the provider refuses. |
| `FileNotFoundException` | A file the member needs is missing, or is a folder. |
| `FileExistsException` | The destination of a copy or a move exists. |
| `PreconditionFailedException` | `IfMatch` or `IfNotExists` failed, or a pinned read's file changed. |
| `NotSupportedException` | The provider or its store can't do it. |
| `InvalidOperationException` | A missing container, bucket, library or credential, or a change to the root. |
| `UnauthorizedAccessException` | The store refused the credential or the permission (401, 403). |
| `IOException` | Any other failure of the store or the network, timeouts included. |
| `ObjectDisposedException` | The provider is disposed. |
| `OperationCanceledException` | Only when your token cancelled the call. |

| 2026 | 2027 | What to do |
|---|---|---|
| `RequestFailedException`, `AmazonS3Exception`, `GoogleApiException`, `BoxException`, `HttpRequestException` | A type above; the SDK's exception in `InnerException` | Catch the contract's type. |
| A timeout as `TaskCanceledException` or in an `AggregateException` | `IOException` | Retry or report as before. |
| A refused credential, per SDK | `UnauthorizedAccessException` | Catch it. |

- The same holds for reading a returned stream, each step of `EnumerateAsync`, and a local stream's commit.
- A failure of the stream you pass to `UpdateFileAsync` arrives as it is if it's one of these types, else inside an
  `IOException`. [Errors](guide/errors.md) lists each store's answers.

## Stored configuration

The shape is unchanged: a `Type` key and the options' property names, both case-insensitive.

| 2026 | 2027 | What to do |
|---|---|---|
| A root such as `tenants/../other` was accepted | `ArgumentException` when built (every provider but Physical) | Name the folder itself. |
| A `Type` was found in the loaded assemblies | Assemblies you pass, built-in providers, then `Register<TProvider>()` | Register a third-party provider, or pass its assembly. |
| S3's `StorageClassForNewFiles` written as `{"Value":"STANDARD_IA"}` | Written as `"STANDARD_IA"`; JSON reads both | Use the string form in configuration files. |
| – | 2027's JSON is not promised to read in 2026 | Don't share stored configuration across the two. |

- A root is refused for a segment of only dots and spaces, a control character, or one that decodes to one (`%2e%2e`).
- New options are written only when they differ from their defaults, so an unchanged configuration keeps its shape.

## Physical

| 2026 | 2027 | What to do |
|---|---|---|
| An overwrite truncated the file first | Writes a `.wfp-<guid>.tmp` file, then renames it | Delete those a crashed process left. |
| An append wrote in place | Copies the file, then renames | Batch frequent appends, such as logs. |
| A write kept the same file | A new file: the old one's permission bits (Linux, macOS), the folder's (Windows) | Set permissions on the folder; don't rely on hard links. |
| A file in a read-only folder was written | `UnauthorizedAccessException`, file kept | Make the folder writable. |
| FIFOs, sockets, devices, mount points were opened | `IOException` | Mount the folder, not the file. |
| Readers blocked writes and deletes | Deletes go ahead; Windows writes still fail while a reader is open | On Windows, retry, or dispose readers sooner. |
| `FileNotFoundException.FileName`: the disk path | The provider path | – |
| The system set the last-write time | Every write sets it, past the old one, so the `ETag` changes | On FAT, exFAT, HFS+, times can run ahead of the clock. |
| – | `IfNotExists`, append to a missing file, move destination: atomic | On Linux and macOS, expect a brief empty file (from 2027.1, only at a move's destination, or where the file system refuses hard links). |
| A recursive walk followed linked folders | Lists them, doesn't enter them | List a linked folder yourself. |
| Windows names that aliased another file (below) | `ArgumentException`, and left out of listings | Use the stored name; rename reserved names. |
| `UnauthorizedAccessException` on writes only | Also on reads and listings | Catch it there too. |
| `root:/srv/files` as the root (Linux, macOS) | `/srv/files`; `root:` still reads | Stop stripping `root:`. |
| A `\\?\C:\files` root used as given | `C:\files`; no plain form: `ArgumentException` | Give a drive or share path. |

Refused on Windows:

- a name ending in `.` or a space (2026 wrote `report.txt.` to `report.txt`), or with `"`, `<`, `>`, `|`, `?` or `*`;
- a device name in any case, with or without extension: `CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9`,
  `COM`/`LPT` with `¹²³` (such as `con.txt`, which 2026 served);
- an 8.3 short name (`REPORT~1.TXT`) whose long name differs.

## Azure Blob Storage

Azure.Storage.Blobs moves from 12.23 to 12.30.

| 2026 | 2027 | What to do |
|---|---|---|
| `AzureFileProviderOptions` | `AzureBlobFileProviderOptions` | Rename; the old name compiles until 2028.0.0. |
| Append blobs | Block blobs | Upgrade every writer together (below). |
| A missing container was created on the first write | `InvalidOperationException` | Create it, or set `CreateContainerIfMissing`. |
| `ReadFileAsync` returned `AzureFileStream`, with `Length` | A forward-only stream | Use `GetFileInfoAsync`, or `OpenReadSeekableAsync`. |
| A URL over 7 days was cut to 7 days | `NotSupportedException` | Ask for 7 days or less, or use the gateway. |
| Two concurrent overwrites both committed | The second may throw `IOException` | Write again, or serialize the writers. |
| An `AccountKey` that isn't base64 never worked | `ArgumentException` when built | Fix the key. |

- With Microsoft Entra ID, a URL can't outlive its key (7 days less 5 minutes) without `AllowShorterValidity`.
- The first write to a 2026 append blob converts it through `.wfp-tmp/` in the root. Meanwhile the file is missing or
  empty.
- 2026.10.10.x can't append to a block blob (`InvalidBlobType`): don't append to shared files while you mix versions.
- An upload URL can't replace an append blob: write the file once through the provider first.

## Amazon S3 and MinIO

- Both run on the AWS SDK for .NET v4. MinIO no longer brings in the Minio SDK or System.Reactive.
- Using AWSSDK.S3 directly? Follow AWS's v3-to-v4 guide: nullable collections and value types, default checksums, no
  `S3Region` short names.

### Amazon S3

| 2026 | 2027 | What to do |
|---|---|---|
| `ReadFileAsync(versionId, segments)` | `ReadFileVersionAsync(versionId, path)` | Replace every call (below). |
| `EnumerateObjects(segments, deep)` | `EnumerateAsync(path, recursive: true)` | Paths are relative to the root. |
| `ReadFileVersionAsync(versionId, "a", "b.txt")` | `ReadFileVersionAsync(versionId, "a/b.txt")` | One path. |
| `ReadFileAsync(string)` on the concrete type | `IWriteableFileProvider.ReadFileAsync(path)` | – |
| A missing bucket: the SDK's error | `InvalidOperationException` | Create it, or set `CreateBucketIfMissing`. |
| The content type came from the extension | Yours, else `application/octet-stream` | Pass `UpdateFileOptions.ContentType`. |
| Read URLs forced `application/octet-stream` | The stored type | Set `SignedUrlOptions.ContentType` to keep it. |
| A URL lived as asked | Short-lived credentials need `AllowShorterValidity` | Use long-term keys, or set it. |
| Any endpoint scheme | Plain `http` to a non-loopback host: `ArgumentException` | Use `https`, or set `AllowInsecureHttp`. |
| Writes disposed your stream | Left open | Dispose it yourself. |
| A racing append lost a write | The append reads the file again and lands after the other write; `PreconditionFailedException` after five attempts (2027.1.1) | Nothing; retry the rare failure. |
| A racing move went through | `PreconditionFailedException` | Retry. |

- **`ReadFileAsync(versionId, "a", "b.txt")` still compiles**: it reads the path `<versionId>/a/b.txt`.
- `AllowShorterValidity` is needed for a credential that expires first, a session token, or a client you pass in.
- The plain-HTTP rule covers `ServiceUrl`, a client's endpoint, `AWS_ENDPOINT_URL_S3`, `AWS_ENDPOINT_URL`, and a
  profile's `endpoint_url`.

### MinIO

| 2026 | 2027 | What to do |
|---|---|---|
| `UseSSL: false` to any host | Only to a loopback host | Set `UseSSL`, or `AllowInsecureHttp`. |
| A missing bucket was created | `InvalidOperationException` | Set `CreateBucketIfMissing: true`. |
| `ReadFileAsync` returned a seekable temp file | A forward-only stream | Use `OpenReadSeekableAsync`, or a local stream. |
| The Minio SDK found the bucket's region | `GetBucketLocation` once, or `Region` | Set `Region` if the bucket doesn't exist yet. |
| A racing move went through | `PreconditionFailedException` (checked, not atomic) | Retry. |

- To find what the plain-HTTP rule breaks: `UseSSL` false or absent, and `HostAndPort` not `localhost`, `127.x.x.x`
  or `[::1]`.

## Google Cloud Storage

`Google.Cloud.Storage.V1` moves from 4.15 to 5.0. Using it directly? `UploadValidationException` is gone.

| 2026 | 2027 | What to do |
|---|---|---|
| The constructor looked up credentials | The first call does; none: `InvalidOperationException` | Expect it on the first call. |
| An unreadable key: `FormatException` | `ArgumentException` | Catch `ArgumentException`. |
| A missing bucket: varied | `InvalidOperationException` | Create it, or set `CreateBucketIfMissing` (and `ProjectId`). |
| `GetFileInfo` read one object | It also lists | Grant `storage.objects.list` (`roles/storage.objectViewer`). |
| `ReadFile` returned a seekable temp file | A forward-only stream | Use `OpenReadSeekableAsync` or `ReadRangeAsync`. |
| The SDK retried a failed copy | Not retried | Check the destination before you retry. |
| A racing append lost a write | The append reads the file again and lands after the other write; `PreconditionFailedException` after five attempts (2027.1.1) | Nothing; retry the rare failure. |
| Writes disposed your stream | Left open | Dispose it yourself. |
| Over 7 days: `ArgumentOutOfRangeException`; can't sign: `PlatformNotSupportedException` | `NotSupportedException` | Catch it, or use the gateway. |

- Without `storage.objects.list`, a missing bucket reads as a missing file.

## Box

The provider runs on Box.V2.Core 10.19, namespace `Box.Sdk.Gen`. Newtonsoft.Json and Portable.BouncyCastle leave
your dependency graph.

| 2026 | 2027 | What to do |
|---|---|---|
| `Box.V2` (Box.V2.Core 5.8) | `Box.Sdk.Gen` | Move your Box SDK code (Box's v5-to-v10 migration guide). |
| The constructor signed in | The first call signs in | Expect a bad client id or secret on the first call. |
| `ReadFile` returned a seekable temp file | A forward-only download | Use `OpenReadSeekableAsync` to seek. |
| Overwrites rewound your stream | Upload from its current position | Rewind it yourself. |
| A move created no folders, and moved folders | Creates folders; moves files only | Move a folder's files one by one. |
| `BoxException` | Contract types, `BoxApiException` inside | Read the status from `ResponseInfo.StatusCode`. |
| Writing under a file: `InvalidOperationException` | `IOException` | Catch `IOException`. |
| Download URLs ignored the validity | Under 15 minutes: `NotSupportedException`; else `AllowShorterValidity` | Set it, or use the gateway. |

- `GetDownloadUrl(path, validity)` always throws `NotSupportedException` for Box.
- Errors from an `IBoxClient` you pass in, other than a plain `BoxClient`, aren't redacted and may hold credentials.

## SharePoint

The provider moves to Microsoft.Graph 6 and Microsoft.Graph.Core 4. `UploadResult<T>` is gone.

### The work folder is the start of the root

The root is `WorkFolderPath` then `RootDirectoryPath`, and caller paths are no longer rewritten. These configurations
now place files elsewhere:

| Configuration | Path | 2026 | 2027 |
|---|---|---|---|
| `WorkFolderPath` `Reports`, no root | `Reports/2026.csv` (any case) | `Reports/2026.csv` | `Reports/Reports/2026.csv` |
| `WorkFolderPath` `Main/Work`, no root | `Main/Work/a.txt` | `Main/Work/a.txt` | `Main/Work/Main/Work/a.txt` |
| `WorkFolderPath` `Main/Work`, no root | `Main/Work` | the root | a folder `Main/Work` in the root |
| `WorkFolderPath` `Main/Work`, `RootDirectoryPath` `Main` | any | under `Main/Work` | `ArgumentException` when built |
| A percent-escape, such as `Team%20Files` | any | under `Team Files` | `ArgumentException` when built |

- A `RootDirectoryPath` that starts with the work folder keeps its files: the repeated part is dropped.
- To find what moves, search your paths for ones starting with the work folder's name. Drop that prefix, or move them.
- The work folder is created on the first write.

### Behaviour

| 2026 | 2027 | What to do |
|---|---|---|
| The constructor looked up the site and library | The first call does | Expect configuration errors on the first call. |
| Any Graph error read as "missing" | Only 404; 401, 403: `UnauthorizedAccessException`; else `IOException` | Catch those too. |
| `ReadFile` returned a seekable temp file | A forward-only download | Use `OpenReadSeekableAsync` to seek. |
| Writes rewound your stream, some disposed it | Read from its position, leave it open | Rewind and dispose it yourself. |
| A racing append lost a write | The append reads the file again and lands after the other write; `PreconditionFailedException` after five attempts (2027.1.1) | Nothing; retry the rare failure. |
| `a%41` in a path reached the service as `aA` | Names `a%41` | Read 2026's files by the decoded name. |
| Download URLs ignored the validity | 1 hour to 7 days, with `AllowShorterValidity` | Set it, or use the gateway. |

- `GetDownloadUrl(path, validity)` always throws `NotSupportedException` for SharePoint.

## New package: WriteableFileProviders.AspNetCore

Signed URLs on every provider, through a gateway in your app where the provider can't sign them:

```csharp
builder.Services.AddWriteableFileProviderGateway(new Uri("https://files.example.com"));
var app = builder.Build();
app.MapWriteableFileProviderGateway();

// Where you hand out links, with IWriteableFileUrlService urls:
SignedUrl url = await urls.GetSignedUrlAsync(
    "reports/2026.csv", new SignedUrlOptions { Validity = TimeSpan.FromHours(1) });
```

- `IWriteableFileUrlService` returns the provider's URL, or a gateway URL when the provider throws
  `NotSupportedException`.
- Every provider it serves must be a singleton, or resolve to the same store in every scope.
- [Signed URLs](guide/signed-urls.md) and the [package README](../src/WriteableFileProviders.AspNetCore/README.md) have
  the rules and options.
