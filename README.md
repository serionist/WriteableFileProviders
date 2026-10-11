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
dotnet add package WriteableFileProviders.Physical
```

Each store is a package of its own (see [Providers](#providers)), and each brings `WriteableFileProviders.Common`.
`WriteableFileProviders.AspNetCore` adds signed links for every store. The packages target .NET 10; on .NET 8, stay on
`2026.10.10.x`.

## Tutorial

One project carries the four steps: `dotnet new web -o Files`, then, in `Files`,
`dotnet add package WriteableFileProviders.Physical`. Each step's code is its `Program.cs`, run with `dotnet run`.

### 1. Files on the local disk

```csharp
using WriteableFileProviders;
using WriteableFileProviders.Physical;

await using var files = new WriteablePhysicalFileProvider("files");

await files.UpdateFileAsync("reports/2026.csv", new MemoryStream("year,total\n2026,42\n"u8.ToArray()));

await using (var csv = await files.ReadFileAsync("reports/2026.csv"))
using (var reader = new StreamReader(csv))
    Console.WriteLine(await reader.ReadToEndAsync());

await foreach (var entry in files.EnumerateAsync("reports"))
    Console.WriteLine($"{entry.Name}: {entry.Length} bytes");
```

Paths are relative to the provider's root, the `files` folder, and never reach outside it (symbolic links inside it
are followed: [Paths](docs/guide/paths-and-listings.md#paths)). A write is atomic, so a write that fails leaves the old
content, and it creates the folders above the file.

### 2. Download links for local files

The gateway in `WriteableFileProviders.AspNetCore` serves signed links for any store, the local disk included. In the
same project, where step 1 wrote `files/reports/2026.csv`, `dotnet add package WriteableFileProviders.AspNetCore`, and
`Program.cs` becomes:

```csharp
using WriteableFileProviders;
using WriteableFileProviders.AspNetCore;
using WriteableFileProviders.Physical;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWriteableFileProvider(_ => new WriteablePhysicalFileProvider("files"));
builder.Services.AddWriteableFileProviderGateway(new Uri("http://localhost:5000"));
var app = builder.Build();
app.MapWriteableFileProviderGateway();
app.MapGet("/link/{**path}", async (string path, IWriteableFileUrlService urls) =>
    (await urls.GetSignedUrlAsync(path, new SignedUrlOptions { Validity = TimeSpan.FromHours(1) })).Url.AbsoluteUri);
app.Run();
```

Run it with `dotnet run --urls http://localhost:5000` and open `/link/reports/2026.csv`: the answer is a link that
downloads the file for an hour.

- The `Uri` is the gateway's public address, the URL the app is reached at and every link is built from: `https`, or
  plain `http` to a loopback host such as `localhost`, as here.
- `/link` hands a working link to anyone who asks. A real app checks that the caller may read the file first.

### 3. The same code on a cloud store

Only the registration changes. Register the provider from configuration:

```csharp
builder.Services.AddWriteableFileProvider(builder.Configuration.GetSection("Files"));
```

```json
{
  "Files": {
    "Type": "WriteableAzureBlobFileProvider",
    "AccountName": "contoso",
    "ContainerName": "files",
    "UseDefaultAzureCredential": true
  }
}
```

With `WriteableFileProviders.AzureBlob` added, `/link` now answers with a user delegation SAS, which downloads
straight from the store, when the identity holds a Storage Blob Data role at the account (or Storage Blob Delegator);
otherwise with the gateway's link ([Authentication](src/WriteableFileProviders.AzureBlob/README.md#authentication)).
Another store is another `Type` and its keys, listed in its package's README.

### 4. One provider, or several

`IWriteableFileProvider` is all most apps need: register one, inject it anywhere. With several stores, tell them apart
your own way, with keyed services, `GetServices<IWriteableFileProvider>()` or a type of your own:

```csharp
builder.Services.AddKeyedSingleton<IWriteableFileProvider>("archive",
    (_, _) => WriteableFileProviderSerializer.FromConfiguration(builder.Configuration.GetSection("Archive")));
```

Links for it name the key: `urls.GetSignedUrlAsync(path, options, providerKey: "archive")`. Every provider the gateway
serves is a [singleton](docs/guide/signed-urls.md#the-url-service).

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

## Learn more

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
