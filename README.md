# WriteableFileProviders

Writeable `IFileProvider`s for .NET: one interface, `IWriteableFileProvider`, over the local disk, Azure Blob Storage,
Amazon S3 and S3-compatible servers, MinIO, Google Cloud Storage, Box and SharePoint. Read, write, append, list, copy,
move and delete files, read byte ranges, write on conditions, and hand out signed URLs, with the same code and the same
meaning on every store. Each provider passes one shared conformance suite, the written contract of what every member
does; where a store cannot do something, the provider says so with the same exception as the others.

Every provider is its own NuGet package, and `WriteableFileProviders.AspNetCore` adds signed URLs for every provider
through a gateway in your ASP.NET Core app.

## Packages

| Package | What it is |
|---|---|
| [WriteableFileProviders.Common](src/WriteableFileProviders.Common/README.md) | `IWriteableFileProvider`, the base class providers build on, construction from configuration, DI. Every provider brings it. |
| [WriteableFileProviders.Physical](src/WriteableFileProviders.Physical/README.md) | A folder on the local disk. |
| [WriteableFileProviders.AzureBlob](src/WriteableFileProviders.AzureBlob/README.md) | An Azure Blob Storage container. |
| [WriteableFileProviders.AmazonS3](src/WriteableFileProviders.AmazonS3/README.md) | An Amazon S3 bucket, or a bucket on any S3-compatible server. |
| [WriteableFileProviders.Minio](src/WriteableFileProviders.Minio/README.md) | A MinIO bucket. |
| [WriteableFileProviders.GoogleCloudStorage](src/WriteableFileProviders.GoogleCloudStorage/README.md) | A Google Cloud Storage bucket. |
| [WriteableFileProviders.Box](src/WriteableFileProviders.Box/README.md) | A Box folder. |
| [WriteableFileProviders.SharePoint](src/WriteableFileProviders.SharePoint/README.md) | A SharePoint document library, through Microsoft Graph. |
| [WriteableFileProviders.AspNetCore](src/WriteableFileProviders.AspNetCore/README.md) | Signed download and upload URLs for every provider: the provider's own where it can sign them, else your app's gateway. |

Each package's README covers its configuration keys, sign-in, behaviour and limits.

## Install

```
dotnet add package WriteableFileProviders.Physical
```

Every package targets .NET 10 and brings `WriteableFileProviders.Common`. All the WriteableFileProviders packages of an
application must be on the same major version (see [Versioning](#versioning)). On .NET 8, use `2026.10.10.x`.

## Quick start

### The local disk

```csharp
using WriteableFileProviders;
using WriteableFileProviders.Physical;

await using var files = new WriteablePhysicalFileProvider("/srv/files");

await using (var content = File.OpenRead("report.csv"))
    await files.UpdateFileAsync("reports/2026.csv", content);           // atomic; parent folders are created

await foreach (var entry in files.EnumerateAsync("reports"))
    Console.WriteLine($"{entry.Name}: {entry.Length} bytes");

await using var read = await files.ReadFileAsync("reports/2026.csv");
```

### A cloud store

The same calls, on another provider. Here Azure Blob Storage, signed in with Microsoft Entra ID:

```csharp
using WriteableFileProviders;
using WriteableFileProviders.AzureBlob;

await using var files = new WriteableAzureBlobFileProvider(new AzureFileProviderOptions
{
    AccountName = "contoso",
    ContainerName = "files",
    UseDefaultAzureCredential = true,                                   // or AccountKey
    RootDirectoryPath = "tenants/acme",                                 // nothing outside it can be reached
});

await files.UpdateFileAsync("reports/2026.csv", stream, new UpdateFileOptions { ContentType = "text/csv" });
var url = await files.GetSignedUrlAsync("reports/2026.csv",
    new SignedUrlOptions { Validity = TimeSpan.FromMinutes(15) });
```

### Configuration, dependency injection and the serializer

A provider is described by its `Type`, the provider's class name, and its options' property names, so it can live in
configuration:

```json
{
  "Files": {
    "Type": "WriteableAmazonS3FileProvider",
    "BucketName": "my-bucket",
    "BucketRegion": "eu-west-1",
    "UseDefaultCredentials": true,
    "RootDirectoryPath": "tenants/acme"
  }
}
```

Register it as a keyed singleton, under a name of your choice:

```csharp
builder.Services.AddWriteableFileProvider("files", builder.Configuration.GetSection("Files"));

app.MapGet("/reports/{year}", async (int year, [FromKeyedServices("files")] IWriteableFileProvider files) =>
    Results.Stream(await files.ReadFileAsync($"reports/{year}.csv"), "text/csv"));
```

Or build it yourself, and keep it as JSON:

```csharp
IWriteableFileProvider files = WriteableFileProviderSerializer.FromConfiguration(configuration.GetSection("Files"));
string json = WriteableFileProviderSerializer.ToJsonString(files);     // holds the options' secrets: never log it
IWriteableFileProvider same = WriteableFileProviderSerializer.FromJson(json);

WriteableFileProviderSerializer.Register<MyOwnProvider>();             // a provider from outside this library
```

Configurations stored by the 2026 versions read unchanged.

## The API in one screen

```csharp
// Paths are relative to the provider's root. Strings and string arrays convert: "a/b.txt", "a\\b.txt" and
// new[] { "a", "b.txt" } are one path.
// A segment that is empty, only dots and spaces, holds a control character, or decodes to one throws ArgumentException.
ProviderPath path = "reports/2026.csv";

// Every operation is an async member that takes a CancellationToken. GetFileInfo, GetDirectoryContents and Watch
// are also sync members of IFileProvider; ReadFile, UpdateFile, Delete, Move, CreateLocalFileStream and
// GetDownloadUrl have sync forms as extension methods; copy, ranges, listings and signed URLs have none.
IWriteableFileInfo info = await files.GetFileInfoAsync(path, ct);      // Exists, IsDirectory, Length, ETag, ContentType
await foreach (var entry in files.EnumerateAsync("reports", recursive: true, ct)) { }

// Reads: whole, a byte range, or a seekable stream that fetches ranges as it is read.
await using var whole = await files.ReadFileAsync(path, ct);
await using var range = await files.ReadRangeAsync(path, 1024, 4096, new ReadOptions { IfMatch = info.ETag }, ct);
await using var seekable = await files.OpenReadSeekableAsync(path, cancellationToken: ct);   // pinned to its version

// Writes are atomic, create parent folders, and read the stream from its position, leaving it open.
await files.UpdateFileAsync(path, stream, new UpdateFileOptions { ContentType = "text/csv", IfMatch = info.ETag }, ct);
await files.UpdateFileAsync("logs/today.log", line, UpdateFileOptions.Append, ct);
await files.UpdateFileAsync("once.txt", stream, new UpdateFileOptions { IfNotExists = true }, ct);
// A condition that fails throws PreconditionFailedException.

// Copy and move refuse an existing destination (FileExistsException); delete takes a file or a whole folder.
await files.CopyAsync(path, "archive/2026.csv", ct);
await files.MoveAsync("archive/2026.csv", "archive/2026-final.csv", ct);
bool deleted = await files.DeleteAsync("tmp", ct);

// A local temp copy to edit, committed once and atomically when disposed.
await using (var local = await files.CreateLocalFileStreamAsync(path, readOnly: false, ct)) { /* edit it */ }

// Signed URLs: read, create (only if absent) or write, never living longer than asked.
SignedUrl url = await files.GetSignedUrlAsync(path, new SignedUrlOptions
{
    Validity = TimeSpan.FromMinutes(15),
    Permission = SignedUrlAccess.Read,
    DownloadFileName = "Report 2026.csv",
}, ct);
```

What every provider promises:

- **Paths** never reach outside the provider's root; on Physical, symbolic links and junctions inside the root are
  followed wherever they point ([its README](src/WriteableFileProviders.Physical/README.md)). The root is a folder: it
  cannot be written, moved or deleted (`InvalidOperationException`), nor read as a file (`ArgumentException`).
- **Missing things:** a read of a missing file, or of a folder, throws `FileNotFoundException`; `GetFileInfoAsync`
  returns `Exists` false; a missing folder lists as not existing and empty; `DeleteAsync` returns false. A missing
  bucket or container throws `InvalidOperationException` from every member that reaches it, naming the option that
  creates it.
- **Writes** are atomic: a write that fails leaves the old content. An append never silently loses a concurrent write,
  except where the matrix below says the check is not atomic.
- **Listings** show only names a path can address, so everything listed can be read back.
- **Signed URLs** never live longer than asked: a provider that cannot sign the URL asked for throws
  `NotSupportedException`, and so does any operation a provider cannot do.

## Providers

What differs is what each store can do. Every cell below comes from the provider's code; its README says more.

**The stores**

| Provider | Package | Store, SDK | Folders | Names | Tested on |
|---|---|---|---|---|---|
| Physical | `WriteableFileProviders.Physical` | A folder on disk, .NET's file APIs | Real | Case ignored on Windows and macOS, kept elsewhere | A temp folder |
| Azure Blob | `WriteableFileProviders.AzureBlob` | Azure Blob Storage, Azure.Storage.Blobs 12 (block blobs) | Virtual (name prefixes) | Case kept | Azurite |
| Amazon S3 | `WriteableFileProviders.AmazonS3` | Amazon S3 or an S3-compatible server, AWSSDK.S3 4 | Virtual | Case kept | S3Mock and MinIO |
| MinIO | `WriteableFileProviders.Minio` | MinIO, AWSSDK.S3 4 (no Minio SDK) | Virtual | Case kept | S3Mock and MinIO |
| Google Cloud Storage | `WriteableFileProviders.GoogleCloudStorage` | Cloud Storage, Google.Cloud.Storage.V1 5 | Virtual | Case kept | fake-gcs-server |
| Box | `WriteableFileProviders.Box` | Box, Box.V2.Core 10 (`Box.Sdk.Gen`) | Real | Case ignored | An in-memory Box API fake |
| SharePoint | `WriteableFileProviders.SharePoint` | A document library, Microsoft Graph 6 | Real | Case ignored | An in-memory Graph fake |

**Reads and writes**

| Provider | Overwrite | `IfMatch` / `IfNotExists` | Append | Ranges | Seekable reads | Content type |
|---|---|---|---|---|---|---|
| Physical | Atomic: temp file, then rename | Checked / atomic | Rewrite through a temp file; a concurrent change is checked, not atomically | Native | Ranges, the ETag checked on each fetch | Not kept |
| Azure Blob | Atomic: staged blocks, one commit | Atomic / atomic | Native: the block list recommitted on the ETag read; atomic | Native | The SDK's stream, pinned to the ETag | Kept |
| Amazon S3 | Atomic: one PUT, or multipart above 8 MiB | Atomic on AWS (no `ServiceUrl`); checked, then sent, toward any named endpoint | Rewrite with `If-Match`; atomic on AWS (no `ServiceUrl`) | Native | Ranged GETs, pinned with `If-Match` | Kept |
| MinIO | Atomic: one PUT, or multipart above 8 MiB | Checked, then sent for the server to enforce | Rewrite with `If-Match`, checked first | Native | Ranged GETs, pinned with `If-Match` | Kept |
| Google Cloud Storage | Atomic: resumable upload | Atomic / atomic (generation) | Rewrite on the generation read; atomic | Native | Ranged GETs, pinned to the generation | Kept |
| Box | Atomic: Box keeps a new version once the upload completes | Checked / atomic | Rewrite on the version read; checked, not atomically | Native, on the version's download URL | Ranges of the pinned file version | Not kept |
| SharePoint | Atomic: one upload up to 4 MiB, or an upload session | Checked, and sent to the service | Rewrite with `If-Match`; checked, not atomically | Native, on the download URL | Ranges, the ETag checked before each fetch | SharePoint's own type; the one given is ignored |

**URLs, copies, notifications**

| Provider | Native signed URLs | Through the gateway | Copy | Move | Watch |
|---|---|---|---|---|---|
| Physical | None | Every URL | A copy into a temp file, then an atomic rename | Native rename | Yes |
| Azure Blob | Read, create, write: SAS (service, or user delegation), up to 7 days | Upload URLs with a content type or file name; a user delegation key that ends sooner, without `AllowShorterValidity`; a client that cannot sign | On the server | Server copy, then a conditional delete | No |
| Amazon S3 | Read, create, write: presigned (SigV4), up to 7 days | Anonymous access; credentials that expire sooner, a session token or a client of yours, without `AllowShorterValidity`; upload URLs that ask for a file name | On the server | Server copy, then a conditional delete | No |
| MinIO | Read, create, write: presigned, up to 7 days | Anonymous access; a client of yours, without `AllowShorterValidity`; upload URLs that ask for a file name | On the server | Server copy, then a checked delete | No |
| Google Cloud Storage | Read, create, write: V4, 1 second to 7 days | A credential that cannot sign; upload URLs that ask for a file name; a validity under 1 second | On the server | Server copy, then a conditional delete | No |
| Box | Read only: Box's download URL, about 15 minutes, with `AllowShorterValidity` | Create and write; a read without `AllowShorterValidity`, under 15 minutes, or with a file name or content type | On the server | On the server | No |
| SharePoint | Read only: Graph's download URL, up to an hour, with `AllowShorterValidity` | Create and write; a read without `AllowShorterValidity`, under an hour, or with a file name or content type | On the server | On the server | No |

Notes:

- **Atomic** means the store itself refuses a write whose condition fails; **checked** means the provider checks the
  condition just before the write, so a change that lands in between is not seen. Box's `IfMatch` is checked, not
  atomic; a later release may make it atomic once a live run confirms Box honours the ETag form its SDK sends.
- **Folders:** a real folder exists while empty; a virtual one exists while something is under it.
- **Listings** leave out names the provider cannot address on every provider, such as `..`, a name holding `\`, or a
  percent-encoded form of one, so everything listed can be read back.
- **Signed URLs** never outlive the validity asked. A URL whose lifetime the service sets (Box, SharePoint) is returned
  only with `SignedUrlOptions.AllowShorterValidity`; otherwise, and wherever a provider cannot sign, the call throws
  `NotSupportedException`, which is what lets the gateway take over.
- **Tested on:** every provider runs the conformance suite there, in CI, declaring what its store cannot do; the
  emulators start through Testcontainers. Each provider also has a live suite against a real account (for Physical, a
  folder you name), which the maintainers run.

## Signed URLs and the gateway

`WriteableFileProviders.AspNetCore` gives every provider download and upload URLs. `IWriteableFileUrlService` asks the
provider for its native URL and, where the provider throws `NotSupportedException`, issues a URL of a gateway in your
app instead, which serves `GET`, `HEAD` (with ranges) and `PUT` on ASP.NET Core Data Protection tokens:

```csharp
builder.Services.AddWriteableFileProviderGateway(options =>
    options.PublicBaseUri = new Uri("https://files.example.com/"));
// ...
app.MapWriteableFileProviderGateway();

// In an endpoint that has checked the caller may read the file:
SignedUrl url = await urls.GetSignedUrlAsync("files", "reports/2026.pdf",
    new SignedUrlOptions { Validity = TimeSpan.FromMinutes(15) });
```

Its [README](src/WriteableFileProviders.AspNetCore/README.md) covers the options, the one-time upload tokens, serving
from a host of its own, and keeping tokens out of your logs.

## Moving from 2026

2027.0.0 is a major release: .NET 10, one async API on `ProviderPath`, System.Text.Json, sealed provider classes, and
behaviour changes on every provider. [docs/migration-2027.md](docs/migration-2027.md) starts with the steps every
application takes, then lists each change with what to do. Applications on .NET 8 stay on `2026.10.10.x`.

## Versioning

- **SemVer, from git tags, every package in lockstep.** Each release tags `v<version>` on `main`, and MinVer gives
  every package that version. Use the same version of every WriteableFileProviders package: a provider of one major
  does not load against the Common of another. Each provider's package depends on Common of its own major
  (`[2027.0.0, 2028.0.0-0)` for 2027.0.0, which also keeps out the next major's prereleases), so NuGet warns (NU1608)
  or fails the restore (NU1107) when packages of different majors from 2027 on meet; the 2026.10.10.x providers carry
  no upper bound, so nothing warns there: update every package together.
- **The major is a sequence number, not a date.** It continues above the 2018–2026 date-stamped versions
  (`yyyy.M.d.n`, such as `2026.10.10.4`), so NuGet orders every new release after them. `2027.0.0` is the first
  SemVer release; `2027.1.0` adds features and `2027.0.1` fixes bugs; the next breaking release is `2028.0.0`, whenever
  it ships, even years later. The number does not date the release.
- **Breaking means a new major:** any change to what consumers depend on (the package ids and namespaces, the Common
  API, the stored configuration shape, the gateway's URLs, one meaning for every member on every provider), dropping a
  target framework, or a dependency change that breaks consumers' builds (such as a cloud SDK major whose types are in
  a public signature). Adding a target framework or a feature is a minor; a fix is a patch.
- **Target frameworks:** the current .NET LTS, `net10.0` today. A new LTS is added in a minor; an out-of-support one is
  dropped only in a major.
- **Support:** the latest major gets every fix. The previous major gets security fixes for 6 months after the next
  major ships. `2026.10.10.x` (.NET 8) gets security fixes until a date the maintainers announce in
  [SECURITY.md](SECURITY.md).
- **Prereleases:** a build from an untagged commit is `-alpha.0.<height>` (such as `2027.0.0-alpha.0.42`); a major is
  preceded by `-rc.N` tags.

## Contributing

Issues and pull requests are welcome: [CONTRIBUTING.md](CONTRIBUTING.md) is the short path, and
[AGENTS.md](AGENTS.md) holds the rules every change follows, for people and agents alike: where things are, how to
build and test, how a change lands, and what consumers depend on.

## Security

Report a vulnerability privately, never in an issue: [SECURITY.md](SECURITY.md) says how, and which versions get fixes.

## Licence

To be agreed: the maintainers are settling the licence with the packages' co-owners, and until then the repository
has no licence file.
