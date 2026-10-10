# WriteableFileProviders.Common

The core of [WriteableFileProviders](https://github.com/serionist/WriteableFileProviders): `IWriteableFileProvider`, a
writeable `IFileProvider` that means the same thing on every storage backend, the base class providers build on, and
construction from configuration. Install a provider package (`WriteableFileProviders.Physical`, `.AzureBlob`,
`.AmazonS3`, `.Minio`, `.GoogleCloudStorage`, `.Box`, `.SharePoint`); it brings this one along.

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

Every path is relative to the provider's root, and nothing outside it can be reached. Every async member takes a
`CancellationToken`. The synchronous forms are extension methods.

## Configuration

```json
{ "Files": { "Type": "WriteableAzureBlobFileProvider", "AccountName": "...", "AccountKey": "...", "ContainerName": "files", "RootDirectoryPath": "tenants/acme" } }
```

`Type` is the provider's class name; the other keys are its options' property names. With dependency injection:

```csharp
services.AddWriteableFileProvider("files", configuration.GetSection("Files"));
// then: [FromKeyedServices("files")] IWriteableFileProvider files
```

## The contract

Every provider passes the same conformance suite:

- **Writes:** atomic. They create parent folders, read the caller's stream from its position, and leave it open.
- **Missing files:** `FileNotFoundException`.
- **Moves:** refuse an existing destination with `FileExistsException`.
- **Unsupported operations:** `NotSupportedException`.

See `IWriteableFileProvider`'s documentation for each member, and
[docs/migration-2027.md](https://github.com/serionist/WriteableFileProviders/blob/main/docs/migration-2027.md) to move
from 2026.
