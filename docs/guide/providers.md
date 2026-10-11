# Providers compared

Every provider has the same members, with the same meaning, and passes the same conformance suite. What differs is what
each store can do, and where a store cannot do something the provider says so with the same exception as the others.
Every cell below comes from the provider's code; its README says more.

## The stores

| Provider | Package | Store, SDK | Folders | Names | Tested on |
|---|---|---|---|---|---|
| Physical | [`WriteableFileProviders.Physical`](../../src/WriteableFileProviders.Physical/README.md) | A folder on disk, .NET's file APIs | Real | Case ignored on Windows and macOS, kept elsewhere; on Windows, no name Windows reads as another (`a.`, `CON.txt`) | A temp folder |
| Azure Blob | [`WriteableFileProviders.AzureBlob`](../../src/WriteableFileProviders.AzureBlob/README.md) | Azure Blob Storage, Azure.Storage.Blobs 12 (block blobs) | Virtual (name prefixes); a file and a folder may share a name | Case kept | Azurite |
| Amazon S3 | [`WriteableFileProviders.AmazonS3`](../../src/WriteableFileProviders.AmazonS3/README.md) | Amazon S3 or an S3-compatible server, AWSSDK.S3 4 | Virtual; a file and a folder may share a name | Case kept | S3Mock and MinIO |
| MinIO | [`WriteableFileProviders.Minio`](../../src/WriteableFileProviders.Minio/README.md) | MinIO, AWSSDK.S3 4 (no Minio SDK) | Virtual; a file and a folder may share a name, and MinIO then lists only the file | Case kept | S3Mock and MinIO |
| Google Cloud Storage | [`WriteableFileProviders.GoogleCloudStorage`](../../src/WriteableFileProviders.GoogleCloudStorage/README.md) | Cloud Storage, Google.Cloud.Storage.V1 5 | Virtual; a file and a folder may share a name | Case kept | fake-gcs-server |
| Box | [`WriteableFileProviders.Box`](../../src/WriteableFileProviders.Box/README.md) | Box, Box.V2.Core 10 (`Box.Sdk.Gen`) | Real | Case ignored | An in-memory Box API fake |
| SharePoint | [`WriteableFileProviders.SharePoint`](../../src/WriteableFileProviders.SharePoint/README.md) | A document library, Microsoft Graph 6 | Real | Case ignored | An in-memory Graph fake |

## Reads and writes

| Provider | Overwrite | `IfMatch` / `IfNotExists` | Append | Ranges | Seekable reads | Content type |
|---|---|---|---|---|---|---|
| Physical | Atomic: temp file, then rename | Checked / atomic | Rewrite through a temp file; a concurrent change is checked, not atomically | Native | Ranges, the ETag checked on each fetch | Not kept |
| Azure Blob | Atomic: staged blocks, one commit | Atomic / atomic | Native: the block list recommitted on the ETag read; atomic | Native | The SDK's stream, pinned to the ETag | Kept |
| Amazon S3 | Atomic: one PUT, or multipart above 8 MiB | Atomic on AWS (no `ServiceUrl`); checked, then sent, toward any named endpoint | Rewrite with `If-Match`; atomic on AWS (no `ServiceUrl`) | Native | Ranged GETs, pinned with `If-Match` | Kept |
| MinIO | Atomic: one PUT, or multipart above 8 MiB | Checked, then sent for the server to enforce | Rewrite with `If-Match`, checked first | Native | Ranged GETs, pinned with `If-Match` | Kept |
| Google Cloud Storage | Atomic: resumable upload | Atomic / atomic (generation) | Rewrite on the generation read; atomic | Native | Ranged GETs, pinned to the generation | Kept |
| Box | Atomic: Box keeps a new version once the upload completes | Checked / atomic | Rewrite on the version read; checked, not atomically | Native, on the version's download URL | Ranges of the pinned file version | Not kept |
| SharePoint | Atomic: one upload up to 4 MiB, or an upload session | Checked, and sent to the service | Rewrite with `If-Match`; checked, not atomically | Native, on the download URL | Ranges, the ETag checked before each fetch | SharePoint's own type; the one given is ignored |

## URLs, copies, notifications

| Provider | Native signed URLs | Through the gateway | Copy | Move | Watch |
|---|---|---|---|---|---|
| Physical | None | Every URL | A copy into a temp file, then an atomic rename | Native rename | Yes |
| Azure Blob | Read, create, write: SAS (service, or user delegation), up to 7 days | Upload URLs with a content type or file name; a user delegation key that ends sooner, without `AllowShorterValidity`; a client that cannot sign | On the server | Server copy, then a conditional delete | No |
| Amazon S3 | Read and write: presigned (SigV4), up to 7 days; create on AWS only (no `ServiceUrl`, client `ServiceURL` or endpoint in the SDK's configuration) | Create URLs toward a named endpoint; anonymous access; credentials that expire sooner, a session token or a client of yours, without `AllowShorterValidity`; upload URLs that ask for a file name | On the server | Server copy, then a conditional delete | No |
| MinIO | Read and write: presigned, up to 7 days | Create URLs; anonymous access; a client of yours, without `AllowShorterValidity`; upload URLs that ask for a file name | On the server | Server copy, then a checked delete | No |
| Google Cloud Storage | Read, create, write: V4, 1 second to 7 days | A credential that cannot sign, or whose signing IAM refuses or cannot be reached; upload URLs that ask for a file name; a validity under 1 second | On the server | Server copy, then a conditional delete | No |
| Box | Read only: Box's download URL, about 15 minutes, with `AllowShorterValidity` | Create and write; a read without `AllowShorterValidity`, under 15 minutes, or with a file name or content type | On the server | On the server | No |
| SharePoint | Read only: Graph's download URL, up to an hour, with `AllowShorterValidity` | Create and write; a read without `AllowShorterValidity`, under an hour, or with a file name or content type | On the server | On the server | No |

## Notes

- **Atomic** means the store itself refuses a write whose condition fails; **checked** means the provider checks the
  condition just before the write, so a change that lands in between is not seen. Box's `IfMatch` is checked, not
  atomic; a later release may make it atomic once a live run confirms Box honours the ETag form its SDK sends.
- **Folders:** a real folder exists while empty; a virtual one exists while something is under it. Where folders are
  virtual, they are name prefixes, so a file can be written at a folder's path, or a folder under a file's path: both
  are stored, read back by path, and listed side by side (MinIO lists only the file, and hides what is under the folder
  while the file exists). Deleting the name removes the file first, and a second delete the folder; a writeable local
  stream of a folder's path still throws `IOException`. Where folders are real, either write throws `IOException`.
- **Listings** leave out names the provider cannot address on every provider, such as `..`, a name holding `\`, or a
  percent-encoded form of one, so everything listed can be read back. They also leave out two kinds of temporary
  names the providers make: Physical's `.wfp-<guid>.tmp` write files and Azure's `.wfp-tmp` folder at the root. Every
  other name is listed: a name of yours that starts with `.wfp-`, and the `.wfp-rename-<guid>.tmp` copy a case-only
  rename holds for a moment on a store that ignores case.
- **Create URLs** upload only if the file does not exist yet. A provider issues one natively only where the store
  enforces that condition; toward an S3-compatible server, which may ignore it, the gateway issues it.
- **Signed URLs** never outlive the validity asked. A URL whose lifetime the service sets (Box, SharePoint) is returned
  only with `SignedUrlOptions.AllowShorterValidity`; otherwise, and wherever a provider cannot sign, the call throws
  `NotSupportedException`, which is what lets the gateway take over ([Signed URLs](signed-urls.md)).
- **Tested on:** every provider runs the conformance suite there, in CI, declaring what its store cannot do; the
  emulators start through Testcontainers. Each provider also has a live suite against a real account (for Physical, a
  folder you name), which the maintainers run.

## Choosing one

The provider is a registration, or a `Type` in configuration, so the choice can change without touching the code that
reads and writes ([Dependency injection and configuration](dependency-injection.md)). What usually decides:

- **Where the files must live:** the disk of the machine (Physical), a cloud account you have (Azure Blob, Amazon S3,
  Google Cloud Storage), a server you run (MinIO, or any S3-compatible server through Amazon S3's `ServiceUrl`), or a
  place where people already work with the files (Box, SharePoint).
- **Conditions you rely on:** where two writers must never overwrite each other unseen, prefer a store whose
  `IfMatch` is atomic (Azure Blob, S3 on AWS, Google Cloud Storage).
- **Links you hand out:** the stores that sign their own URLs serve downloads without your app; every other URL goes
  through the [gateway](signed-urls.md#the-gateway) in your app.
