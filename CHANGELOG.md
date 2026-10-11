# Changelog

What each release of the WriteableFileProviders packages changed. Every package is released together, at one version
([versioning](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/versioning.md)). A change that
consumers notice adds its line under the next version's heading, in the pull request that makes it; `release.yml`
publishes the section of the version it releases as the GitHub release's notes.

## 2027.0.0 (unreleased)

The first SemVer release, and a major one: the library is rebuilt on .NET 10 around one async API that means the same
thing on every store.
[Moving from 2026](https://github.com/serionist/WriteableFileProviders/blob/main/docs/migration-2027.md) lists every
break and what to do about it.

### Highlights

- **One contract on every provider.** Every member means the same on the local disk, Azure Blob Storage, Amazon S3,
  MinIO, Google Cloud Storage, Box and SharePoint, and one conformance suite checks it on each. Where a store cannot do
  something, the provider throws `NotSupportedException`, never something else.
- **New operations:** byte ranges (`ReadRangeAsync`), seekable reads that fetch ranges on demand
  (`OpenReadSeekableAsync`), conditional writes (`IfMatch`, `IfNotExists`), content types, server-side copies
  (`CopyAsync`), streamed listings (`EnumerateAsync`), and signed URLs for reading, creating and writing
  (`GetSignedUrlAsync`).
- **Atomic writes:** a write that fails leaves the old content on every store, and an append never silently loses a
  concurrent write, except where a store's check is not atomic
  ([providers compared](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/providers.md)).
- **One set of exceptions:** every member of every provider throws only the
  [contract's types](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/errors.md), with the SDK's
  exception inside.
- **New package, WriteableFileProviders.AspNetCore:** signed download and upload links for every store, the local disk
  included, through a gateway in your app where the store cannot sign them.
- **Dependency injection:** `AddWriteableFileProvider(configuration)` or `AddWriteableFileProvider(factory)` registers
  a provider as `IWriteableFileProvider`.
- **Providers:**
  - Azure Blob Storage on block blobs, with Microsoft Entra ID sign-in and user delegation SAS;
  - Amazon S3 on the AWS SDK for .NET v4, with S3-compatible servers (`ServiceUrl`) and the default credential chain;
  - MinIO on the AWS SDK, without the Minio SDK;
  - Google Cloud Storage on its SDK 5, with other endpoints and emulators (`Endpoint`);
  - Box on Box.V2.Core 10;
  - SharePoint on Microsoft Graph 6, with certificate and managed identity sign-in;
  - a constructor that takes your own SDK client on every cloud provider.
- **System.Text.Json** reads and writes stored configurations; Newtonsoft.Json leaves every package.
- **MIT licence**, a package icon, and SemVer versions in lockstep.

### Breaking changes

- .NET 10 only; .NET 8 applications stay on `2026.10.10.x`.
- Every member takes one `ProviderPath` and a `CancellationToken`; the 2026 call shapes, synchronous forms and
  `GetDownloadUrl` compile through `[Obsolete]` forms until 2028.0.0.
- `GetSignedUrlAsync` never returns a URL that lives shorter than asked, or other than asked: it throws
  `NotSupportedException`, and the URL service of WriteableFileProviders.AspNetCore falls back to its gateway.
- Every provider class is sealed; `FileInfo` is `WriteableFileInfo`; `ToJson` returns a `JsonObject` and leaves the
  interface; Azure's options class is `AzureBlobFileProviderOptions` (the old name compiles, obsolete, until
  2028.0.0); the base class's protected members are renamed for third-party providers.
- Store exceptions no longer escape as they are: catch the contract's types.
- Every provider changed behaviour to follow the contract: missing containers and buckets are no longer created unless
  asked, moves refuse an existing destination, MinIO refuses plain HTTP to a host that is not loopback unless
  `AllowInsecureHttp` is set, SharePoint's work folder is the start of the root, and more, provider by provider.
- Amazon S3 refuses plain `http` toward a host that is not loopback, whether through `ServiceUrl`, a client you pass
  in, or an endpoint the AWS SDK takes from its configuration, unless `AllowInsecureHttp` is set.
- A stored root with a dot segment (`tenants/../other`) no longer builds.

[Moving from 2026](https://github.com/serionist/WriteableFileProviders/blob/main/docs/migration-2027.md) has each
change with what to do.
