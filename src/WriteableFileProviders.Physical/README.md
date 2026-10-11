# WriteableFileProviders.Physical

A writeable `IFileProvider` over a folder on the local disk, part of
[WriteableFileProviders](https://github.com/serionist/WriteableFileProviders): the same `IWriteableFileProvider` API as
the Azure Blob, S3, MinIO, Google Cloud Storage, Box and SharePoint providers, with atomic writes, ranged reads and
change notifications.

```
dotnet add package WriteableFileProviders.Physical
```

Every WriteableFileProviders package must be on the same major version: a `2026.10.10.x` provider does not load
against `WriteableFileProviders.Common` 2027.

## Use

```csharp
using WriteableFileProviders;
using WriteableFileProviders.Physical;

await using IWriteableFileProvider files = new WriteablePhysicalFileProvider("/srv/files");

await files.UpdateFileAsync("reports/2026.csv", stream);                         // atomic, parent folders created
await files.UpdateFileAsync("log.txt", more, UpdateFileMode.Append);              // atomic append
await using var slice = await files.ReadRangeAsync("big.bin", 1_000_000, 4096);   // only those bytes are read
var info = await files.GetFileInfoAsync("reports/2026.csv");                      // info.PhysicalPath is the disk path
await files.UpdateFileAsync("reports/2026.csv", newer, new UpdateFileOptions { IfMatch = info.ETag });
IChangeToken token = files.Watch("reports/*.csv");                                // fires when a write lands
```

## Configuration

```json
{ "Files": { "Type": "WriteablePhysicalFileProvider", "RootDirectoryPath": "/srv/files" } }
```

```csharp
services.AddWriteableFileProvider("files", configuration.GetSection("Files"));
```

| Key | Meaning |
|---|---|
| `Type` | `WriteablePhysicalFileProvider`. |
| `RootDirectoryPath` | The folder the provider is confined to: absolute (`/srv/files`, `C:\files`, `\\server\share\files`) or relative to the current directory, with `/` or `\`. A relative path is resolved once, when the provider is built, and written back to the options, so `ToJson` names the same folder wherever it is read. The folder need not exist; the first write creates it. |
| `RootDirectorySegments` | The same path as segments, in the form every stored configuration has: a drive (`C:`), `\` for a network share, or `root:` for a path from the file system root (`/srv/files` is `["root:", "srv", "files"]`). Either key alone is enough. |

Configuration stored by 2026 reads the same, including the `root:/srv/files` form 2026 wrote for `RootDirectoryPath` on
Linux and macOS. Since 2027, `RootDirectoryPath` reads back as the real path on every system.

## Authentication

None: the provider reads and writes with the permissions of the process that runs it.

## Signed URLs

None natively: `GetSignedUrlAsync` throws `NotSupportedException`. `WriteableFileProviders.AspNetCore` issues gateway URLs
for this provider instead.

## How it behaves

- **Writes are atomic.** An overwrite, an append, a copy and a local stream's commit write a temp file named
  `.wfp-<guid>.tmp` next to the target, then rename it over the target, so the file has its old content or its new
  content, never a part. A failed write removes its temp file; listings never show one (a crash can leave one behind,
  which you can delete). An append copies the file and the new bytes into the temp file, so it costs a copy of the file.
- **Replacing by rename** puts a new file at the path, so:
  - a file the process may not write (read-only, or another user's) is refused with `UnauthorizedAccessException` and
    keeps its content, on every system, as in 2026;
  - on Linux and macOS the new file takes the permission bits of the file it replaces, and a copy those of its source,
    before any content is written; the setuid, setgid and sticky bits, access control lists and extended attributes are
    not carried over;
  - on Windows the new file takes the permissions the folder gives a new file, not the replaced file's own;
  - other hard links to the file keep the old content, and a symbolic link at the path is replaced by a file, its target
    left as it was.
- **ETags** are made from the last-write time and the length. Each write through the provider stamps a last-write time
  later than the file it replaces, reading it back and stepping forward (up to 2 seconds) where the file system stores
  times coarsely (FAT, exFAT, HFS+), so it changes the ETag. A change by another program that keeps the length within
  the file system's time resolution may not.
- **Conditions.** `IfMatch` is checked just before the rename, which is not atomic: another process can change the file
  in between. An append checks the same way that the file is still the version it copied, and throws
  `PreconditionFailedException` if not.
- **New names are taken atomically.** `IfNotExists`, an append to a missing file, and the destination of a move or a
  copy never replace a file that is there. Windows renames without replacing. Linux and macOS, where .NET checks the name
  and then renames, take the name with an exclusive create and rename onto it, so the file exists empty for that moment:
  a watcher sees it, and a process that dies there leaves an empty file behind.
- **Reading while writing.** Readers share the file with writers and deleters and keep the content they opened. A delete
  goes ahead while a file is read. So does a write on Linux and macOS; on Windows, which does not rename over a file that
  is open, a write to a file being read fails with `UnauthorizedAccessException` and the file keeps its content.
- **Ranges.** `ReadRangeAsync` reads through `RandomAccess` on one handle, checking `IfMatch` on that handle;
  `OpenReadSeekableAsync` re-checks the ETag on each fetch and throws `IOException` when the file changed.
- **Names** are matched without regard to case on Windows and macOS (a move from `a.txt` to `A.txt` renames the file),
  and with regard to case on Linux. A `:` in a name is refused. Content types are not kept.
- **The root.** Every path stays under the root folder, checked on the path: symbolic links and junctions inside the
  root are followed wherever they point. A recursive listing lists a linked folder but does not walk into it, since it
  can lead outside the root or back into it without end.
- **Watch** works as ASP.NET Core's `PhysicalFileProvider` does, through `PhysicalFilesWatcher`: the filter is a glob
  relative to the root, leading `/` and `\` are ignored (so `/a.txt` is `a.txt`), and a filter that is null, names a
  drive, leads above the root or holds a character no file name can never fires. Dot-prefixed, hidden and system files are not watched (the provider's temp
  files among them), and a write fires when its rename puts the new content in place. The first `Watch` creates the root
  folder if it is missing. It relies on file system events, which some network and container-mounted folders do not
  deliver; set `DOTNET_USE_POLLING_FILE_WATCHER` to `1` or `true` to poll instead.

See [the migration guide](https://github.com/serionist/WriteableFileProviders/blob/main/docs/migration-2027.md) for
what changed from 2026.
