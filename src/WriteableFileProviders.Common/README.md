# WriteableFileProviders.Common

The core of [WriteableFileProviders](https://github.com/serionist/WriteableFileProviders#readme):
`IWriteableFileProvider`, a writeable `IFileProvider` that means the same thing on every storage backend; the base
class providers build on; and construction from configuration or JSON, with dependency injection. It holds no provider
of its own: install a provider package, which brings this one along.

## Install

```
dotnet add package WriteableFileProviders.Physical
```

Or `.AzureBlob`, `.AmazonS3`, `.Minio`, `.GoogleCloudStorage`, `.Box`, `.SharePoint`. It needs .NET 10. Every
WriteableFileProviders package in an application must be on the same major version: a `2026.10.10.x` package does not
load against `WriteableFileProviders.Common` 2027.

## Use

```csharp
using WriteableFileProviders;

IWriteableFileProvider files = WriteableFileProviderSerializer.FromConfiguration(configuration.GetSection("Files"));

await files.UpdateFileAsync("reports/2026.csv", stream);                      // atomic overwrite, parents created
await files.UpdateFileAsync("log.txt", more, UpdateFileMode.Append);           // atomic append
await using var slice = await files.ReadRangeAsync("big.bin", 1_000_000, 4096); // bytes 1,000,000 to 1,004,095
await using var seekable = await files.OpenReadSeekableAsync("big.bin");      // seeks, fetches ranges as read
await foreach (var entry in files.EnumerateAsync("reports", recursive: true)) { }
await files.MoveAsync("reports/2026.csv", "archive/2026.csv");                // FileExistsException if taken
var url = await files.GetSignedUrlAsync("archive/2026.csv", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(15) });
```

Every path is a `ProviderPath` relative to the provider's root, which a string or a string array converts to; nothing
outside the root can be reached. Every async member takes a `CancellationToken`. Synchronous forms of the common
operations are extension methods (`ReadFile`, `UpdateFile`, `Delete`, `Move`, `CreateLocalFileStream`,
`GetDownloadUrl`), beside the three members `IFileProvider` itself has (`GetFileInfo`, `GetDirectoryContents`,
`Watch`).

## Configuration

```json
{ "Files": { "Type": "WriteableAzureBlobFileProvider", "AccountName": "...", "AccountKey": "...", "ContainerName": "files", "RootDirectoryPath": "tenants/acme" } }
```

`Type` is the provider's class name, matched without regard to case; the other keys are its options' property names.
With dependency injection, as a keyed singleton the container creates on first use and disposes:

```csharp
services.AddWriteableFileProvider("files", configuration.GetSection("Files"));
// then: [FromKeyedServices("files")] IWriteableFileProvider files
```

`WriteableFileProviderSerializer` also builds a provider from JSON (`FromJson`) and writes one back (`ToJson`,
`ToJsonString`); the JSON holds the options' secrets, so never log it. It finds this library's providers by their class
name; a provider from elsewhere is registered with `WriteableFileProviderSerializer.Register<TProvider>()`, or its
assembly passed in. Configurations stored by the 2026 versions read unchanged.

## How it behaves

Every provider runs the same conformance suite, the written contract, declaring what its store cannot do:

- **Paths:** a segment that is empty, only dots and spaces, holds a control character, or becomes one of those when
  percent-decoded throws `ArgumentException`. The root is a folder: it cannot be written, moved or deleted
  (`InvalidOperationException`), nor read as a file.
- **Writes:** atomic, so a write that fails leaves the old content. They create parent folders, read the caller's
  stream from its position, and leave it open. `UpdateFileOptions.IfMatch` and `IfNotExists` throw
  `PreconditionFailedException` when they do not hold; each provider's README says whether its store checks them
  atomically.
- **Missing things:** a read of a missing file, or of a folder, throws `FileNotFoundException`; `GetFileInfoAsync`
  returns `Exists` false; a missing folder lists as not existing and empty; `DeleteAsync` returns false. A missing
  bucket or container throws `InvalidOperationException`, naming the option that creates it.
- **Moves and copies** refuse an existing destination with `FileExistsException` and leave it unchanged.
- **Listings** show only names a path can address, so everything listed can be read back.
- **Signed URLs** never live longer than asked: what a provider cannot sign throws `NotSupportedException`, which lets
  `WriteableFileProviders.AspNetCore` serve a gateway URL instead.
- **Unsupported operations** throw `NotSupportedException`.

`IWriteableFileProvider`'s documentation says what each member does.

## Writing a provider

Derive from `WriteableFileProvider<TOptions>`: it validates and resolves every path, protects the root, checks moves,
copies and conditions, and calls the protected `...CoreAsync` members you implement. Its protected helpers:
`AppendByRewriteAsync` (an append for a store without one, conditional on the version read), `SpoolToTempFileAsync`,
`NotFound`, and `IsAddressable(name)`, true when the provider's own path validation (`IsSegmentAllowed` included) would
accept the name as one path segment: leave the names it refuses out of your listings, so everything listed can be read
back. Run your provider against `ProviderConformanceTests` (in the repository's `tests/WriteableFileProviders.Testing`)
to check it meets the contract.

## More

- [WriteableFileProviders](https://github.com/serionist/WriteableFileProviders#readme): every package, the provider
  matrix, versioning.
- [Moving from 2026](https://github.com/serionist/WriteableFileProviders/blob/main/docs/migration-2027.md): what
  2027.0.0 changed, starting with the steps every application takes.
