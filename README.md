<h1 align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/assets/logo-dark.svg">
    <img src="docs/assets/logo.svg" alt="WriteableFileProviders" width="480">
  </picture>
</h1>

<p align="center">
  <a href="https://github.com/serionist/WriteableFileProviders/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/serionist/WriteableFileProviders/actions/workflows/ci.yml/badge.svg?branch=main"></a>
  <a href="https://github.com/serionist/WriteableFileProviders/actions/workflows/codeql.yml"><img alt="CodeQL" src="https://github.com/serionist/WriteableFileProviders/actions/workflows/codeql.yml/badge.svg?branch=main"></a>
  <a href="LICENSE"><img alt="Licence: MIT" src="https://img.shields.io/badge/licence-MIT-blue"></a>
  <a href="https://dotnet.microsoft.com/download/dotnet/10.0"><img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4"></a>
</p>

One writeable `IFileProvider` for .NET, over the local disk and six cloud stores: read, write, list, move and sign
links with the same code everywhere.

## Install

```
dotnet add package WriteableFileProviders.AzureBlob
```

One package per store ([Providers](#providers)), each with `WriteableFileProviders.Common`. Add
`WriteableFileProviders.AspNetCore` for signed links on every store. .NET 10 only; on .NET 8, stay on `2026.10.10.x`.

## The interface

```csharp
public interface IWriteableFileProvider : IFileProvider, IDisposable, IAsyncDisposable
{
    // Look and list
    Task<IWriteableFileInfo> GetFileInfoAsync(ProviderPath path);
    Task<IDirectoryContents> GetDirectoryContentsAsync(ProviderPath path);
    IAsyncEnumerable<IWriteableFileInfo> EnumerateAsync(ProviderPath path, bool recursive = false);

    // Read: the whole file, a byte range, or a stream you can seek
    Task<Stream> ReadFileAsync(ProviderPath path);
    Task<Stream> ReadRangeAsync(ProviderPath path, long offset, long? length, ReadOptions? options = null);
    Task<Stream> OpenReadSeekableAsync(ProviderPath path, SeekableReadOptions? options = null);

    // Write (overwrite or append; only if unchanged, IfMatch, or absent, IfNotExists), delete, move, copy
    Task UpdateFileAsync(ProviderPath path, Stream contents, UpdateFileOptions? options = null);
    Task<bool> DeleteAsync(ProviderPath path);
    Task MoveAsync(ProviderPath source, ProviderPath destination);
    Task CopyAsync(ProviderPath source, ProviderPath destination);

    // Edit through a local copy, uploaded when it is disposed; a signed link to download or upload the file
    Task<LocalFileStream> CreateLocalFileStreamAsync(ProviderPath path, bool readOnly);
    Task<SignedUrl> GetSignedUrlAsync(ProviderPath path, SignedUrlOptions options);
}
```

Every member also takes a `CancellationToken`. A path is a `ProviderPath` relative to the provider's root; a string
converts to one. Writes are all or nothing, no path reaches outside the root, and failures are the same few
[exceptions](docs/guide/errors.md) on every store. Register a provider:

```csharp
builder.Services.AddWriteableFileProvider(builder.Configuration.GetSection("Files"));
```

```json
{ "Files": { "Type": "WriteableAzureBlobFileProvider", "AccountName": "contoso", "ContainerName": "files",
             "UseDefaultAzureCredential": true } }
```

Then inject `IWriteableFileProvider` wherever you need files. Another store is another configuration, not new code.

## Providers

| Package | Store | NuGet | Downloads |
|---|---|---|---|
| [WriteableFileProviders.Physical](src/WriteableFileProviders.Physical/README.md) | A folder on the local disk | [![NuGet](https://img.shields.io/nuget/v/WriteableFileProviders.Physical?label=)](https://www.nuget.org/packages/WriteableFileProviders.Physical) | [![Downloads](https://img.shields.io/nuget/dt/WriteableFileProviders.Physical?label=)](https://www.nuget.org/packages/WriteableFileProviders.Physical) |
| [WriteableFileProviders.AzureBlob](src/WriteableFileProviders.AzureBlob/README.md) | Azure Blob Storage | [![NuGet](https://img.shields.io/nuget/v/WriteableFileProviders.AzureBlob?label=)](https://www.nuget.org/packages/WriteableFileProviders.AzureBlob) | [![Downloads](https://img.shields.io/nuget/dt/WriteableFileProviders.AzureBlob?label=)](https://www.nuget.org/packages/WriteableFileProviders.AzureBlob) |
| [WriteableFileProviders.AmazonS3](src/WriteableFileProviders.AmazonS3/README.md) | Amazon S3, or an S3-compatible server | [![NuGet](https://img.shields.io/nuget/v/WriteableFileProviders.AmazonS3?label=)](https://www.nuget.org/packages/WriteableFileProviders.AmazonS3) | [![Downloads](https://img.shields.io/nuget/dt/WriteableFileProviders.AmazonS3?label=)](https://www.nuget.org/packages/WriteableFileProviders.AmazonS3) |
| [WriteableFileProviders.Minio](src/WriteableFileProviders.Minio/README.md) | MinIO | [![NuGet](https://img.shields.io/nuget/v/WriteableFileProviders.Minio?label=)](https://www.nuget.org/packages/WriteableFileProviders.Minio) | [![Downloads](https://img.shields.io/nuget/dt/WriteableFileProviders.Minio?label=)](https://www.nuget.org/packages/WriteableFileProviders.Minio) |
| [WriteableFileProviders.GoogleCloudStorage](src/WriteableFileProviders.GoogleCloudStorage/README.md) | Google Cloud Storage | [![NuGet](https://img.shields.io/nuget/v/WriteableFileProviders.GoogleCloudStorage?label=)](https://www.nuget.org/packages/WriteableFileProviders.GoogleCloudStorage) | [![Downloads](https://img.shields.io/nuget/dt/WriteableFileProviders.GoogleCloudStorage?label=)](https://www.nuget.org/packages/WriteableFileProviders.GoogleCloudStorage) |
| [WriteableFileProviders.Box](src/WriteableFileProviders.Box/README.md) | Box | [![NuGet](https://img.shields.io/nuget/v/WriteableFileProviders.Box?label=)](https://www.nuget.org/packages/WriteableFileProviders.Box) | [![Downloads](https://img.shields.io/nuget/dt/WriteableFileProviders.Box?label=)](https://www.nuget.org/packages/WriteableFileProviders.Box) |
| [WriteableFileProviders.SharePoint](src/WriteableFileProviders.SharePoint/README.md) | SharePoint, through Microsoft Graph | [![NuGet](https://img.shields.io/nuget/v/WriteableFileProviders.SharePoint?label=)](https://www.nuget.org/packages/WriteableFileProviders.SharePoint) | [![Downloads](https://img.shields.io/nuget/dt/WriteableFileProviders.SharePoint?label=)](https://www.nuget.org/packages/WriteableFileProviders.SharePoint) |
| [WriteableFileProviders.AspNetCore](src/WriteableFileProviders.AspNetCore/README.md) | Signed links for every store, served by your app | [![NuGet](https://img.shields.io/nuget/v/WriteableFileProviders.AspNetCore?label=)](https://www.nuget.org/packages/WriteableFileProviders.AspNetCore) | [![Downloads](https://img.shields.io/nuget/dt/WriteableFileProviders.AspNetCore?label=)](https://www.nuget.org/packages/WriteableFileProviders.AspNetCore) |
| [WriteableFileProviders.Common](src/WriteableFileProviders.Common/README.md) | The interface, the base class and configuration | [![NuGet](https://img.shields.io/nuget/v/WriteableFileProviders.Common?label=)](https://www.nuget.org/packages/WriteableFileProviders.Common) | [![Downloads](https://img.shields.io/nuget/dt/WriteableFileProviders.Common?label=)](https://www.nuget.org/packages/WriteableFileProviders.Common) |

## Signed links

`GetSignedUrlAsync` returns the store's own link where it has one: an Azure SAS, an S3 or MinIO presigned URL, a Google
Cloud Storage V4 URL. Elsewhere, the gateway in `WriteableFileProviders.AspNetCore` serves it from your app, and
`IWriteableFileUrlService` picks the right one ([Signed URLs](docs/guide/signed-urls.md)).

## Learn more

- [Getting started](docs/guide/getting-started.md): local files, download links, then a cloud store.
- [Providers compared](docs/guide/providers.md): what each store can do.
- [Paths and listings](docs/guide/paths-and-listings.md): the root, folders, case, listings.
- [Reading and writing](docs/guide/reading-and-writing.md): ranges, appends, conditions, copies, local streams.
- [Signed URLs](docs/guide/signed-urls.md): native links, the gateway, uploads, security.
- [Dependency injection and configuration](docs/guide/dependency-injection.md): one provider or several.
- [Errors](docs/guide/errors.md): what each member throws.
- [Writing a provider](docs/guide/writing-a-provider.md) for another store.
- [Moving from 2026](docs/migration-2027.md) and the [changelog](CHANGELOG.md).

## Versioning

All packages ship together at one SemVer version: use the same version of each. `2027.x` adds features and
fixes; `2028.0.0` is the next breaking release. The major is a sequence number, not a date
([policy](docs/guide/versioning.md)).

## Contributing

Issues and pull requests are welcome: see [CONTRIBUTING.md](CONTRIBUTING.md), and [AGENTS.md](AGENTS.md) for the rules.

## Security

Report vulnerabilities privately, never in an issue: see [SECURITY.md](SECURITY.md).

## Licence

[MIT](LICENSE). Copyright (c) 2018-2026 the WriteableFileProviders contributors.
