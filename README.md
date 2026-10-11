<h1 align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/assets/logo-dark.svg">
    <img src="docs/assets/logo.svg" alt="WriteableFileProviders" width="480">
  </picture>
</h1>

<p align="center">
  <a href="https://www.nuget.org/packages/WriteableFileProviders.Common"><img alt="NuGet" src="https://img.shields.io/nuget/v/WriteableFileProviders.Common?label=nuget"></a>
  <a href="https://www.nuget.org/packages/WriteableFileProviders.Common"><img alt="Downloads" src="https://img.shields.io/nuget/dt/WriteableFileProviders.Common?label=downloads"></a>
  <a href="https://github.com/serionist/WriteableFileProviders/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/serionist/WriteableFileProviders/actions/workflows/ci.yml/badge.svg?branch=main"></a>
  <a href="https://github.com/serionist/WriteableFileProviders/actions/workflows/codeql.yml"><img alt="CodeQL" src="https://github.com/serionist/WriteableFileProviders/actions/workflows/codeql.yml/badge.svg?branch=main"></a>
  <a href="LICENSE"><img alt="Licence: MIT" src="https://img.shields.io/badge/licence-MIT-blue"></a>
  <a href="https://dotnet.microsoft.com/download/dotnet/10.0"><img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4"></a>
</p>

One writeable `IFileProvider` for .NET, over the local disk and six cloud stores: Azure Blob Storage, Amazon S3 (and
S3-compatible servers), MinIO, Google Cloud Storage, Box and SharePoint. Read, write, append, list, copy, move and
delete files, read byte ranges, write only if a file is unchanged or absent, and hand out signed download and upload
links, with the same code and the same meaning on every store. Every provider passes one shared conformance suite, and
where a store cannot do something, it says so with the same exception as the others.

## Install

```
dotnet add package WriteableFileProviders.AzureBlob
```

Each store is a package of its own (see [Providers](#providers)), and each brings `WriteableFileProviders.Common`.
`WriteableFileProviders.AspNetCore` adds signed links for every store. The packages target .NET 10; on .NET 8, stay on
`2026.10.10.x`.

## The interface

Every provider is an `IWriteableFileProvider`, an `IFileProvider` that can also write. Each member also takes a
`CancellationToken`, and a path is a `ProviderPath` (a string converts to one), relative to the provider's root:

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

    // Edit through a local copy that is written back when it is disposed
    Task<LocalFileStream> CreateLocalFileStreamAsync(ProviderPath path, bool readOnly);

    // A signed link to download or upload the file
    Task<SignedUrl> GetSignedUrlAsync(ProviderPath path, SignedUrlOptions options);
}
```

Every provider means the same thing by every member: a write is all or nothing, nothing outside the root can be
reached (on the local disk, symbolic links inside the root are followed), and each failure is one of a short list of
exceptions, the same on every store ([errors](docs/guide/errors.md)).

Register a provider and inject `IWriteableFileProvider` wherever files are needed. Changing store is a change of
configuration, not of code:

```csharp
builder.Services.AddWriteableFileProvider(builder.Configuration.GetSection("Files"));
```

```json
{ "Files": { "Type": "WriteableAzureBlobFileProvider", "AccountName": "contoso", "ContainerName": "files",
             "UseDefaultAzureCredential": true } }
```

## Providers

| Package | Store |
|---|---|
| [WriteableFileProviders.Physical](src/WriteableFileProviders.Physical/README.md) | A folder on the local disk |
| [WriteableFileProviders.AzureBlob](src/WriteableFileProviders.AzureBlob/README.md) | An Azure Blob Storage container |
| [WriteableFileProviders.AmazonS3](src/WriteableFileProviders.AmazonS3/README.md) | An Amazon S3 bucket, or one on an S3-compatible server |
| [WriteableFileProviders.Minio](src/WriteableFileProviders.Minio/README.md) | A MinIO bucket |
| [WriteableFileProviders.GoogleCloudStorage](src/WriteableFileProviders.GoogleCloudStorage/README.md) | A Google Cloud Storage bucket |
| [WriteableFileProviders.Box](src/WriteableFileProviders.Box/README.md) | A Box folder |
| [WriteableFileProviders.SharePoint](src/WriteableFileProviders.SharePoint/README.md) | A SharePoint document library, through Microsoft Graph |
| [WriteableFileProviders.AspNetCore](src/WriteableFileProviders.AspNetCore/README.md) | Signed links for every store, through your app's gateway where the store cannot sign |
| [WriteableFileProviders.Common](src/WriteableFileProviders.Common/README.md) | The interface, the base class, configuration and dependency injection |

## Signed links

`GetSignedUrlAsync` returns a store's own signed link where it has one: a SAS on Azure, a presigned URL on S3 and
MinIO, a V4 signed URL on Google Cloud Storage. For everything else (the local disk, uploads to Box and SharePoint, a
lifetime a store cannot give), the gateway in `WriteableFileProviders.AspNetCore` serves the link from your app, and
`IWriteableFileUrlService` picks the right one. [Signed URLs](docs/guide/signed-urls.md) has the details.

## Learn more

- [Getting started](docs/guide/getting-started.md): four short steps, from files on the local disk to download links
  and a cloud store.
- [Providers compared](docs/guide/providers.md): what each store can do, side by side.
- [Paths and listings](docs/guide/paths-and-listings.md): the root, folders, case, and what a listing shows.
- [Reading and writing](docs/guide/reading-and-writing.md): conditions, appends, ranges, seekable reads, local streams.
- [Signed URLs](docs/guide/signed-urls.md): each store's own links, the gateway, uploads, and keeping links safe.
- [Dependency injection and configuration](docs/guide/dependency-injection.md): one provider or several, and the
  stored configuration.
- [Errors](docs/guide/errors.md): what each member throws, on every provider.
- [Writing a provider](docs/guide/writing-a-provider.md) for another store.
- [Moving from 2026](docs/migration-2027.md), and the [changelog](CHANGELOG.md).

## Versioning

Every package is released together, at one SemVer version from the release's git tag: use the same version of all of
them. The major is a sequence number that continues above the 2018–2026 date versions, not a date: `2027.x` adds
features and fixes, and the next breaking release is `2028.0.0`, whenever it ships.
[The policy](docs/guide/versioning.md) says what counts as breaking and which versions get fixes.

## Contributing

Issues and pull requests are welcome. [CONTRIBUTING.md](CONTRIBUTING.md) is the short path, and [AGENTS.md](AGENTS.md)
holds the rules every change follows, for people and agents alike.

## Security

Report a vulnerability privately, never in an issue: [SECURITY.md](SECURITY.md) says how, and which versions get fixes.

## Licence

[MIT](LICENSE). Copyright (c) 2018-2026 the WriteableFileProviders contributors.
