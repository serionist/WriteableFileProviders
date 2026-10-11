# Changelog

Every package releases together, at one version
([versioning](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/versioning.md)). A pull request
that changes something consumers notice adds a line under the next version's heading. `release.yml` publishes a
version's section as its GitHub release notes.

## 2027.0.0 (2026-10-11)

The first SemVer release, and a major one: rebuilt on .NET 10 around one async API that means the same on every store.
[Moving from 2026](https://github.com/serionist/WriteableFileProviders/blob/main/docs/migration-2027.md) lists every
break and what to do.

### Highlights

- **One contract on every provider.** Every member means the same on the local disk, Azure Blob Storage, Amazon S3,
  MinIO, Google Cloud Storage, Box and SharePoint, and one conformance suite checks each. Where a store can't do
  something, the provider throws `NotSupportedException`.
- **New operations:** byte ranges (`ReadRangeAsync`), seekable reads (`OpenReadSeekableAsync`), conditional writes
  (`IfMatch`, `IfNotExists`), content types, server-side copies (`CopyAsync`), streamed listings (`EnumerateAsync`), and
  signed URLs to read, create and write (`GetSignedUrlAsync`).
- **Atomic writes:** a failed write leaves the old content on every store. An append never silently loses a concurrent
  write, except where a store's check isn't atomic
  ([providers compared](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/providers.md)).
- **One set of exceptions:** every member throws only the
  [contract's types](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/errors.md), with the SDK's
  exception inside.
- **New package, WriteableFileProviders.AspNetCore:** signed download and upload links for every store, the local disk
  included, through a gateway in your app where the store can't sign.
- **Dependency injection:** `AddWriteableFileProvider(configuration)` or `AddWriteableFileProvider(factory)`.
- **Providers:**
  - Azure Blob Storage on block blobs, with Microsoft Entra ID sign-in and user delegation SAS;
  - Amazon S3 on the AWS SDK for .NET v4, with S3-compatible servers (`ServiceUrl`) and the default credential chain;
  - MinIO on the AWS SDK, without the Minio SDK;
  - Google Cloud Storage on its SDK 5, with other endpoints and emulators (`Endpoint`);
  - Box on Box.V2.Core 10;
  - SharePoint on Microsoft Graph 6, with certificate and managed identity sign-in;
  - every cloud provider takes your own SDK client too.
- **System.Text.Json** reads and writes stored configurations. Newtonsoft.Json is gone from every package.
- **Native AOT:** every package but GoogleCloudStorage and Box works in trimmed and native AOT apps, which CI checks
  by publishing and running one. Building a provider from configuration by its `Type` warns there: bind the options
  and build the provider in code
  ([Native AOT](https://github.com/serionist/WriteableFileProviders#native-aot)).
- **MIT licence**, a package icon, and SemVer versions in lockstep.

### Breaking changes

- .NET 10 only. .NET 8 apps stay on `2026.10.10.x`.
- Every member takes one `ProviderPath` and a `CancellationToken`. The 2026 call shapes, synchronous forms and
  `GetDownloadUrl` compile through `[Obsolete]` forms until 2028.0.0.
- `GetSignedUrlAsync` never returns a URL that lives shorter, or other, than asked: it throws `NotSupportedException`,
  and the AspNetCore URL service falls back to its gateway.
- Every provider class is sealed. `FileInfo` is `WriteableFileInfo`. `ToJson` returns a `JsonObject` and leaves the
  interface. Azure's options class is `AzureBlobFileProviderOptions` (the old name compiles, obsolete, until 2028.0.0).
  The base class's protected members are renamed for third-party providers.
- Store exceptions no longer escape as they are: catch the contract's types.
- Every provider changed behaviour to follow the contract. Missing containers and buckets are created only when asked,
  moves refuse an existing destination, SharePoint's work folder is the start of the root, and more.
- Amazon S3 and MinIO refuse plain `http` to a host that isn't loopback unless `AllowInsecureHttp` is set. For S3 that
  covers `ServiceUrl`, a client you pass in, and an endpoint from the AWS SDK's configuration.
- A stored root with a dot segment (`tenants/../other`) no longer builds.
