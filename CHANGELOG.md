# Changelog

Every package releases together, at one version
([versioning](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/versioning.md)). A pull request
that changes something consumers notice adds a line under the next version's heading. `release.yml` publishes a
version's section as its GitHub release notes.

## 2027.1.0 (unreleased)

### Added

- **`FileAlreadyExistsException`** (Common): a write with `IfNotExists` that finds a file at its path throws it, on
  every provider. It derives from `PreconditionFailedException`, so existing catches still catch it; catch it first to
  tell "created meanwhile" from "changed meanwhile". Code that checks for exactly `PreconditionFailedException` (its
  `GetType()`, or a test's `ThrowsExactly`) now sees the subtype there.

### Fixed

- **`IfNotExists` where a folder has the name** gets the answer a write without conditions gets there, on every
  provider, instead of "already exists" on some:
  - Box (as it ships, without `If-Match`) and SharePoint throw `IOException` instead of
    `PreconditionFailedException`, and so does Physical when a folder takes the name during the write;
  - MinIO and S3 toward a named endpoint write the file beside the folder, as AWS, Azure and Google Cloud Storage do;
  - an append with `IfNotExists` there does the same;
  - WriteableFileProviders.AspNetCore: a gateway create URL whose path is a folder answers 201 (the file beside it) or
    500 (a real folder), where it answered 412.
- **A plain append to a folder path stays create-only** on S3, MinIO and Google Cloud Storage. It could replace a file
  another writer created there at the same moment.
- **The default `CopyCoreAsync` and `MoveCoreAsync`** (third-party providers that keep them) throw
  `FileExistsException` when the destination appears during the copy, as `CopyAsync` documents, instead of
  `PreconditionFailedException`.
- **S3 and MinIO:** a create-only write that S3 answers with 409 `ConditionalRequestConflict` throws
  `FileAlreadyExistsException` only when a file is then there. When a concurrent delete overtook the write and nothing
  is there, it throws `PreconditionFailedException`, and the write can be retried.
- **SharePoint:** likewise for a 409 under `IfNotExists` whose name holds nothing when the provider looks.
- **Physical on Linux and macOS:** a write or a copy that takes a new name (`IfNotExists`, an append to a missing file,
  a copy) gives it the content with a hard link, so the name never holds an empty file meanwhile. An append that
  landed in that moment could report success and then be replaced. A move's destination, and a file system that
  refuses hard links, keep the empty file for that moment.

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
