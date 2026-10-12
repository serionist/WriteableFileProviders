# Providers compared

Every provider has the same members and passes the same conformance suite. What differs is what each store can do.
Where a store can't do something, the provider throws `NotSupportedException`.

## The stores

| Provider | Package | SDK | Folders | Case | Native AOT | Tested against |
|---|---|---|---|---|---|---|
| Physical | [Physical](../../src/WriteableFileProviders.Physical/README.md) | .NET file APIs | Real | Ignored on Windows and macOS | ✓ | A temp folder |
| Azure Blob | [AzureBlob](../../src/WriteableFileProviders.AzureBlob/README.md) | Azure.Storage.Blobs 12, block blobs | Prefixes | Kept | ✓ | Azurite |
| Amazon S3 | [AmazonS3](../../src/WriteableFileProviders.AmazonS3/README.md) | AWSSDK.S3 4 | Prefixes | Kept | ✓ | S3Mock, MinIO |
| MinIO | [Minio](../../src/WriteableFileProviders.Minio/README.md) | AWSSDK.S3 4 (no Minio SDK) | Prefixes | Kept | ✓ | S3Mock, MinIO |
| Google Cloud Storage | [GoogleCloudStorage](../../src/WriteableFileProviders.GoogleCloudStorage/README.md) | Google.Cloud.Storage.V1 5 | Prefixes | Kept | | fake-gcs-server |
| Box | [Box](../../src/WriteableFileProviders.Box/README.md) | Box.V2.Core 10 (`Box.Sdk.Gen`) | Real | Ignored | | A fake Box API |
| SharePoint | [SharePoint](../../src/WriteableFileProviders.SharePoint/README.md) | Microsoft.Graph 6 | Real | Ignored | ✓ | A fake Graph API |

- What real and prefix folders mean, and the names each provider refuses: [Paths and listings](paths-and-listings.md).
- CI runs the conformance suite on every provider against the store in the last column; the emulators start through
  Testcontainers. Each provider also has a live suite against a real account, which the maintainers run.
- Native AOT: the Google Cloud Storage and Box SDKs read and write JSON through reflection, so those two providers
  don't work in a trimmed or native AOT app ([Native AOT](../../README.md#native-aot)).

## Reads and writes

| Provider | Overwrite | `IfMatch` | `IfNotExists` | Append | Seekable reads | Content type |
|---|---|---|---|---|---|---|
| Physical | Temp file, then rename | Checked | Atomic | Rewrite through a temp file, checked | Ranges, ETag checked on each fetch | Not kept |
| Azure Blob | Staged blocks, one commit | Atomic | Atomic | Native: block list recommitted, atomic | SDK stream, pinned to the ETag | Kept |
| Amazon S3 | One PUT, multipart above 8 MiB | Atomic¹ | Atomic¹ | Rewrite with `If-Match`, atomic¹ | Ranged GETs with `If-Match` | Kept |
| MinIO | One PUT, multipart above 8 MiB | Checked, then sent² | Checked, then sent² | Rewrite with `If-Match`, checked, then sent² | Ranged GETs with `If-Match` | Kept |
| Google Cloud Storage | Resumable upload | Atomic (generation) | Atomic | Rewrite on the generation, atomic | Ranged GETs, pinned to the generation | Kept |
| Box | New version once the upload completes | Checked | Atomic | Rewrite on the version, checked | Ranges of the pinned version | Not kept |
| SharePoint | One upload up to 4 MiB, else an upload session | Checked, and sent | Checked, and sent | Rewrite with `If-Match`, checked | Ranges, ETag checked before each fetch | Set by SharePoint |

Every overwrite is atomic: a write that fails leaves the old content. **Atomic** means the store itself refuses a write
whose condition fails. **Checked** means the provider compares just before writing, so a change in between is missed.

1. On AWS. Toward a named endpoint (`ServiceUrl`, a client's `ServiceURL`, or an endpoint in the AWS SDK's
   configuration), checked, then sent for the server to enforce.
2. Atomic where the MinIO release enforces conditional writes.

`IfNotExists` that finds a file throws `FileAlreadyExistsException` on every provider. Only a file counts: where a
folder has the name, the write gets the answer it gets without conditions.

An append to a missing file that another writer creates at the same moment ends differently: Azure Blob appends after
the new content, and every other provider refuses with `FileAlreadyExistsException`. Where the store enforces the
condition (**Atomic** in the `IfNotExists` column), the other writer's content is kept either way; where it is only
checked, a file created in between can be replaced.

Every provider reads byte ranges natively; Box and SharePoint read them from the file's download URL.

## URLs, copies, notifications

| Provider | Native signed URLs | Copy | Move | `Watch` |
|---|---|---|---|---|
| Physical | None | Temp file, then atomic rename | Rename | Yes |
| Azure Blob | Read, create, write: SAS, up to 7 days | On the server | Server copy, then conditional delete | No |
| Amazon S3 | Read, write: presigned, up to 7 days; create on AWS only | On the server | Server copy, then conditional delete | No |
| MinIO | Read, write: presigned, up to 7 days | On the server | Server copy, then checked delete | No |
| Google Cloud Storage | Read, create, write: V4, 1 second to 7 days | On the server | Server copy, then conditional delete | No |
| Box | Read: Box's download URL, about 15 minutes, with `AllowShorterValidity` | On the server | On the server | No |
| SharePoint | Read: Graph's download URL, up to an hour, with `AllowShorterValidity` | On the server | On the server | No |

Every other URL comes from the gateway in your app. When and why, provider by provider:
[Signed URLs](signed-urls.md#native-urls-by-provider).

## Choosing one

The provider is a registration, or a `Type` in configuration, so you can switch later without touching the code that
reads and writes ([Dependency injection](dependency-injection.md)).

- **Where the files live:** this machine's disk (Physical), a cloud account (Azure Blob, Amazon S3, Google Cloud
  Storage), a server you run (MinIO, or any S3-compatible server through Amazon S3's `ServiceUrl`), or where people
  already work with them (Box, SharePoint).
- **Concurrent writers:** if two writers must never overwrite each other unseen, pick a store with atomic `IfMatch`
  (Azure Blob, S3 on AWS, Google Cloud Storage).
- **Links:** stores that sign their own URLs serve downloads without your app. Every other URL goes through the
  [gateway](signed-urls.md#the-gateway).
