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
await files.UpdateFileAsync("log.txt", more, UpdateFileOptions.Append);       // atomic append
await using var slice = await files.ReadRangeAsync("big.bin", 1_000_000, 4096); // bytes 1,000,000 to 1,004,095
await using var seekable = await files.OpenReadSeekableAsync("big.bin");      // seeks, fetches ranges as read
await foreach (var entry in files.EnumerateAsync("reports", recursive: true)) { }
await files.MoveAsync("reports/2026.csv", "archive/2026.csv");                // FileExistsException if taken
var url = await files.GetSignedUrlAsync("archive/2026.csv", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(15) });
```

Every path is a `ProviderPath` relative to the provider's root, which a string or a string array converts to; nothing
outside the root can be reached by a path. Every member is async and takes a `CancellationToken`, except the three
`IFileProvider` itself has (`GetFileInfo`, `GetDirectoryContents`, `Watch`). The 2026 synchronous forms (`ReadFile`,
`UpdateFile`, `Delete`, `Move`, `CreateLocalFileStream`), the `IFileInfo` overloads and `GetDownloadUrl` still compile,
with a warning that names the async member to call, and go in 2028.0.0.

## Configuration

```json
{ "Files": { "Type": "WriteableAzureBlobFileProvider", "AccountName": "...", "AccountKey": "...", "ContainerName": "files", "RootDirectoryPath": "tenants/acme" } }
```

`Type` is the provider's class name, matched without regard to case; the other keys are its options' property names.
With dependency injection, as a singleton `IWriteableFileProvider` the container creates on first use and disposes:

```csharp
services.AddWriteableFileProvider(configuration.GetSection("Files"));
// then inject IWriteableFileProvider; for several, use keyed services, GetServices or types of your own
```

`WriteableFileProviderSerializer` also builds a provider from JSON (`FromJson`) and writes one back (`ToJson`,
`ToJsonString`); the JSON holds the options' secrets, so never log it. A provider from elsewhere is registered with
`WriteableFileProviderSerializer.Register<TProvider>()`, or its assembly passed in. Configurations stored by the 2026
versions read unchanged.

## Learn more

The guide, on GitHub:

- [Paths and listings](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/paths-and-listings.md):
  the root, folders, case, and what a listing shows.
- [Reading and writing](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/reading-and-writing.md):
  conditions, appends, ranges, seekable reads, local streams.
- [Signed URLs](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/signed-urls.md), and the
  gateway that issues them for every provider.
- [Dependency injection and configuration](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/dependency-injection.md):
  one provider or several, and the serializer.
- [Errors](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/errors.md): the exceptions every
  member of every provider throws.
- [Writing a provider](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/writing-a-provider.md):
  `WriteableFileProvider<TOptions>`, its members and its conformance suite.
- [Providers compared](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/providers.md), and
  [moving from 2026](https://github.com/serionist/WriteableFileProviders/blob/main/docs/migration-2027.md).
