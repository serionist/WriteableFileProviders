# WriteableFileProviders.Physical

A folder on the local disk as an `IWriteableFileProvider`, with atomic writes and change notifications. Part of
[WriteableFileProviders](https://github.com/serionist/WriteableFileProviders#readme).

## Install

```
dotnet add package WriteableFileProviders.Physical
```

Needs .NET 10, and is native AOT compatible. Keep every WriteableFileProviders package in an app on the same
major version.

## Use

```csharp
using WriteableFileProviders;
using WriteableFileProviders.Physical;

await using var files = new WriteablePhysicalFileProvider("/srv/files");

await files.UpdateFileAsync("reports/2026.csv", stream);                // creates reports/ if missing
await files.UpdateFileAsync("app.log", line, UpdateFileOptions.Append);
await using var slice = await files.ReadRangeAsync("big.bin", 1_000_000, 4096);
var changed = files.Watch("reports/*.csv");
```

From configuration, with `WriteableFileProviderSerializer.FromConfiguration(section)` or
`services.AddWriteableFileProvider(section)`:

```json
{ "Files": { "Type": "WriteablePhysicalFileProvider", "RootDirectoryPath": "/srv/files" } }
```

## Configuration

| Option | Default | Meaning |
|---|---|---|
| `RootDirectoryPath` | required | The folder the provider is confined to: absolute (`/srv/files`, `C:\files`, `\\server\share\files`) or relative to the current directory, resolved once when the provider is built. It need not exist; the first write creates it. |
| `RootDirectorySegments` | | The same folder as segments, the form 2026 stored: `["root:", "srv", "files"]`, `["C:", "files"]`, `["\\", "server", "share"]`. Set this or `RootDirectoryPath`. |
| `RefuseLinks` | `false` | Refuse a path that names or goes through a symbolic link, junction or Windows mount point inside the root (`ArgumentException`), and leave links out of listings. The root itself may be a link. New in 2027.1.0: an older version ignores it and follows links. |

## Authentication

None. The provider reads and writes with the permissions of the process.

## What's different

- **Writes are atomic.** An overwrite, an append, a copy or a local stream's commit writes `.wfp-<guid>.tmp` next to
  the target, then renames it over the target. Listings hide these files; a crash can leave one.
  `DeleteTempFilesAsync(olderThan)` deletes those older than an age you choose, which must be longer than your longest
  write, plus any lag of a file server's clock: a write whose temp file goes fails, and its file keeps its content. It
  doesn't walk into linked folders, and skips a folder below the root that it may not list; a root it can't read
  throws.
- **An append copies the whole file**, so frequent appends to a large file (a log) are expensive. It first copies your
  stream to a hidden `.wfp-<guid>.tmp` beside the file (on Linux and macOS readable by the process's user only), so that
  after a concurrent write it can read the file again and append after it, up to five attempts in all. Where the file
  system has no hard links, an append that loses a new name to another writer still throws `FileAlreadyExistsException`
  while that writer's file is empty. On Windows, which
  doesn't rename over a file that is open, one of two appends to one file at once can fail with
  `UnauthorizedAccessException`.
- **A write puts a new file at the path.** Other hard links keep the old content, and a symbolic link at the path
  becomes a file. On Windows the file takes the folder's permissions; on Linux and macOS, the old file's mode bits.
  ACLs and extended attributes are not kept.
- **`IfMatch` is not atomic.** It is checked just before the rename. ETags come from the last-write time and length.
- **On Windows, a write to a file that is open for reading fails** (`UnauthorizedAccessException` or `IOException`),
  and the file keeps its content. Linux and macOS let the write go ahead.
- **Case** is ignored on Windows and macOS, and matters elsewhere. `:` is refused in names everywhere. On Windows, so
  are names Windows can't hold or would read as another name: `report.txt.`, `a?b`, `CON`, `con.txt`. Listings leave
  out such names, and on Linux and macOS, names holding `:` or `\`.
- **Symbolic links and junctions** inside the root are followed, wherever they point, unless you set `RefuseLinks`.
  Without it, a recursive listing lists a linked folder but doesn't walk into it. `RefuseLinks` checks the path just
  before each operation, so a process that can create links inside the root can still swap one in between; a recursive
  folder delete can even be steered into a folder elsewhere by a link swapped in while it runs. Hard links and mounts on
  Linux and macOS aren't visible as links, and other reparse points (cloud placeholders, Windows container layers)
  aren't links. The polling `Watch` can still report a change behind a linked folder.
- **No content types.** `UpdateFileOptions.ContentType` is ignored; `ContentType` is null.
- **`Watch` works** like ASP.NET Core's `PhysicalFileProvider`: a glob relative to the root. Hidden and dot-prefixed
  files aren't watched. Where file system events don't arrive (some network and container mounts), set
  `DOTNET_USE_POLLING_FILE_WATCHER=1`.
- **No native signed URLs.** `GetSignedUrlAsync` throws `NotSupportedException`. For download and upload links, use
  the gateway in [WriteableFileProviders.AspNetCore](https://www.nuget.org/packages/WriteableFileProviders.AspNetCore).

## Learn more

- [Getting started](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/getting-started.md):
  local files first, then download links through the gateway.
- The guide: [reading and writing](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/reading-and-writing.md),
  [paths and listings](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/paths-and-listings.md),
  [errors](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/errors.md#physical),
  [providers compared](https://github.com/serionist/WriteableFileProviders/blob/main/docs/guide/providers.md).
- [Moving from 2026](https://github.com/serionist/WriteableFileProviders/blob/main/docs/migration-2027.md#physical).
