# WriteableFileProviders.Common

The core of [WriteableFileProviders](https://github.com/serionist/WriteableFileProviders#readme): the
`IWriteableFileProvider` interface, the base class every provider builds on, and building a provider from
configuration.

## Install

You rarely install it yourself: each provider package brings it.

```
dotnet add package WriteableFileProviders.Physical
```

Or `.AzureBlob`, `.AmazonS3`, `.Minio`, `.GoogleCloudStorage`, `.Box`, `.SharePoint`. Install Common alone to write a
provider of your own. Needs .NET 10. Keep every WriteableFileProviders package in an app on the same major version.

## Use

```csharp
using WriteableFileProviders;

IWriteableFileProvider files = WriteableFileProviderSerializer.FromConfiguration(configuration.GetSection("Files"));

await files.UpdateFileAsync("reports/2026.csv", stream);                         // creates reports/ if missing
await files.UpdateFileAsync("app.log", line, UpdateFileOptions.Append);
await using var slice = await files.ReadRangeAsync("big.bin", 1_000_000, 4096);   // 4,096 bytes from 1,000,000
await foreach (var entry in files.EnumerateAsync("reports", recursive: true)) { }
await files.MoveAsync("reports/2026.csv", "archive/2026.csv");                   // FileExistsException if taken
var url = await files.GetSignedUrlAsync("archive/2026.csv", new SignedUrlOptions { Validity = TimeSpan.FromMinutes(15) });
```

Paths are relative to the provider's root, and nothing outside it can be reached. Every member is async, except the
three `IFileProvider` ones (`GetFileInfo`, `GetDirectoryContents`, `Watch`).

## Configuration

```json
{ "Files": { "Type": "WriteableAzureBlobFileProvider", "AccountName": "contoso", "AccountKey": "...", "ContainerName": "files" } }
```

| Key | Default | Meaning |
|---|---|---|
| `Type` | required | The provider's class name, in any case. |
| `RootDirectoryPath` | the store's root | The folder the provider is confined to, such as `tenants/acme`. |
| `RootDirectorySegments` | | The same folder as an array of names. |
| any other | | The provider's options, by property name, in any case. Its README lists them. |

Register it with dependency injection as a singleton `IWriteableFileProvider`, which the container builds on first use
and disposes:

```csharp
services.AddWriteableFileProvider(configuration.GetSection("Files"));
```

For several providers, register each under a key: `services.AddKeyedSingleton<IWriteableFileProvider>(key, ...)`.

`WriteableFileProviderSerializer` also reads JSON (`FromJson`) and writes a provider back (`ToJson`, `ToJsonString`).
The JSON holds the options' secrets: never log it. To build a provider of your own from configuration, call
`WriteableFileProviderSerializer.Register<TProvider>()` first, or pass its assembly.

## Learn more

- The guide: [paths and listings](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/paths-and-listings.md),
  [reading and writing](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/reading-and-writing.md),
  [signed URLs](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/signed-urls.md),
  [dependency injection](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/dependency-injection.md),
  [errors](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/errors.md),
  [writing a provider](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/writing-a-provider.md),
  [providers compared](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/providers.md).
- [Moving from 2026](https://github.com/serionist/WriteableFileProviders/blob/main/docs/migration-2027.md).
