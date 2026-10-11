<h1 align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/assets/logo-dark.svg">
    <img src="docs/assets/logo.svg" alt="WriteableFileProviders" width="480">
  </picture>
</h1>

<p align="center">
  <a href="https://www.nuget.org/packages?q=WriteableFileProviders"><img alt="NuGet" src="https://img.shields.io/nuget/v/WriteableFileProviders.Common?label=nuget"></a>
  <a href="https://github.com/serionist/WriteableFileProviders/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/serionist/WriteableFileProviders/actions/workflows/ci.yml/badge.svg?branch=main"></a>
  <a href="https://github.com/serionist/WriteableFileProviders/actions/workflows/codeql.yml"><img alt="CodeQL" src="https://github.com/serionist/WriteableFileProviders/actions/workflows/codeql.yml/badge.svg?branch=main"></a>
  <a href="LICENSE"><img alt="Licence: MIT" src="docs/assets/badges/licence-mit.svg"></a>
  <a href="https://dotnet.microsoft.com/download/dotnet/10.0"><img alt=".NET 10" src="docs/assets/badges/dotnet-10.svg"></a>
  <a href="#native-aot"><img alt="Native AOT" src="docs/assets/badges/native-aot.svg"></a>
</p>

One writeable `IFileProvider` for .NET, over the local disk and six cloud stores: read, write, list, move and sign
links with the same code everywhere. Native AOT compatible, except Google Cloud Storage and Box.

## Providers

| Package | Store | Native AOT | Downloads |
|---|---|---|---|
| [Physical](src/WriteableFileProviders.Physical/README.md) | A folder on the local disk | Yes | [![Downloads](https://img.shields.io/nuget/dt/WriteableFileProviders.Physical?label=)](https://www.nuget.org/packages/WriteableFileProviders.Physical) |
| [AzureBlob](src/WriteableFileProviders.AzureBlob/README.md) | Azure Blob Storage | Yes | [![Downloads](https://img.shields.io/nuget/dt/WriteableFileProviders.AzureBlob?label=)](https://www.nuget.org/packages/WriteableFileProviders.AzureBlob) |
| [AmazonS3](src/WriteableFileProviders.AmazonS3/README.md) | Amazon S3, or an S3-compatible server | Yes | [![Downloads](https://img.shields.io/nuget/dt/WriteableFileProviders.AmazonS3?label=)](https://www.nuget.org/packages/WriteableFileProviders.AmazonS3) |
| [Minio](src/WriteableFileProviders.Minio/README.md) | MinIO | Yes | [![Downloads](https://img.shields.io/nuget/dt/WriteableFileProviders.Minio?label=)](https://www.nuget.org/packages/WriteableFileProviders.Minio) |
| [GoogleCloudStorage](src/WriteableFileProviders.GoogleCloudStorage/README.md) | Google Cloud Storage | No | [![Downloads](https://img.shields.io/nuget/dt/WriteableFileProviders.GoogleCloudStorage?label=)](https://www.nuget.org/packages/WriteableFileProviders.GoogleCloudStorage) |
| [Box](src/WriteableFileProviders.Box/README.md) | Box | No | [![Downloads](https://img.shields.io/nuget/dt/WriteableFileProviders.Box?label=)](https://www.nuget.org/packages/WriteableFileProviders.Box) |
| [SharePoint](src/WriteableFileProviders.SharePoint/README.md) | SharePoint, through Microsoft Graph | Yes | [![Downloads](https://img.shields.io/nuget/dt/WriteableFileProviders.SharePoint?label=)](https://www.nuget.org/packages/WriteableFileProviders.SharePoint) |
| [AspNetCore](src/WriteableFileProviders.AspNetCore/README.md) | Signed links for every store, served by your app | Yes | [![Downloads](https://img.shields.io/nuget/dt/WriteableFileProviders.AspNetCore?label=)](https://www.nuget.org/packages/WriteableFileProviders.AspNetCore) |
| [Common](src/WriteableFileProviders.Common/README.md) | The interface, the base class and configuration | Yes | [![Downloads](https://img.shields.io/nuget/dt/WriteableFileProviders.Common?label=)](https://www.nuget.org/packages/WriteableFileProviders.Common) |

Each package is `WriteableFileProviders.<name>` on NuGet, all at the same version, and brings
`WriteableFileProviders.Common`.

**Another store?** Derive from `WriteableFileProvider<TOptions>` in Common and implement five members. The base class
handles paths, the root, conditions, copies, moves and errors ([Writing a provider](docs/guide/writing-a-provider.md)).

## Install

```
dotnet add package WriteableFileProviders.AzureBlob
```

One package per store. Add `WriteableFileProviders.AspNetCore` for signed links on every store. .NET 10 only; on
.NET 8, stay on `2026.10.10.x`: it is unlisted on nuget.org, so reference it by version.

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

## Native AOT

Every package works in trimmed and native AOT apps except GoogleCloudStorage and Box, whose SDKs read and write JSON
through reflection. CI publishes an app over the others with native AOT and runs it: on the local disk, Azurite,
MinIO and an in-memory Microsoft Graph.

Building a provider from configuration by its `Type` uses reflection too, so `AddWriteableFileProvider(section)` and
`WriteableFileProviderSerializer` warn in an AOT app. Bind the options and build the provider in code instead:

```csharp
var options = builder.Configuration.GetSection("Files").Get<AzureBlobFileProviderOptions>()!;
builder.Services.AddWriteableFileProvider(_ => new WriteableAzureBlobFileProvider(options));
```

In an ASP.NET Core AOT app, SharePoint adds one warning, `IL2104` for `Microsoft.IO.RecyclableMemoryStream`, which
Microsoft Graph uses. It is harmless: that package's event logging passes only primitives.

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
