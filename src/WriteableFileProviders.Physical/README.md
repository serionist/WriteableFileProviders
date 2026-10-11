# WriteableFileProviders.Physical

A writeable `IFileProvider` over a folder on the local disk, part of
[WriteableFileProviders](https://github.com/serionist/WriteableFileProviders#readme): the same `IWriteableFileProvider`
API, with the same meaning, as the Azure Blob, S3, MinIO, Google Cloud Storage, Box and SharePoint providers, with
atomic writes, ranged reads and change notifications.

## Install

```
dotnet add package WriteableFileProviders.Physical
```

It needs .NET 10 and brings `WriteableFileProviders.Common`. Every WriteableFileProviders package in an application
must be on the same major version: a `2026.10.10.x` package does not load against `WriteableFileProviders.Common` 2027.

## Use

```csharp
using Microsoft.Extensions.Primitives;
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

From configuration, with `WriteableFileProviderSerializer.FromConfiguration(section)` or dependency injection:

```json
{ "Files": { "Type": "WriteablePhysicalFileProvider", "RootDirectoryPath": "/srv/files" } }
```

```csharp
services.AddWriteableFileProvider("files", configuration.GetSection("Files"));
```

The provider class is sealed: to change what it does, wrap it, or derive your own provider from
`WriteableFileProvider<TOptions>`.

## Configuration

| Key | Meaning |
|---|---|
| `Type` | `WriteablePhysicalFileProvider`. |
| `RootDirectoryPath` | The folder the provider is confined to: absolute (`/srv/files`, `C:\files`, `\\server\share\files`) or relative to the current directory, with `/` or `\`. A relative path is resolved once, when the provider is built, and written back to the options, so `ToJson` names the same folder wherever it is read. On Windows, an extended or device path (`\\?\C:\files`, `\\?\UNC\server\share`, `\\.\C:\files`) is taken, and written back, in its plain form, so the name rules below hold under it; one with no plain form is refused: a volume GUID, a device, or a name the plain form would change (one ending in a dot or a space, `.` or `..`, which the extended form names as they are). The folder need not exist; the first write creates it. |
| `RootDirectorySegments` | The same path as segments, in the form every stored configuration has: a drive (`C:`), `\` for a network share, or `root:` for a path from the file system root (`/srv/files` is `["root:", "srv", "files"]`). Either key alone is enough. |

Configuration stored by 2026 reads the same, including the `root:/srv/files` form 2026 wrote for `RootDirectoryPath` on
Linux and macOS. Since 2027, `RootDirectoryPath` reads back as the real path on every system.

## Authentication

None: the provider reads and writes with the permissions of the process that runs it.

## How it behaves

- **Writes are atomic.** An overwrite, an append, a copy and a local stream's commit write a temp file named
  `.wfp-<guid>.tmp` next to the target, then rename it over the target, so the file has its old content or its new
  content, never a part. A failed write removes its temp file where it can; a crash, or a temp file that cannot be
  deleted, leaves one behind, which you can delete. Listings never show such a temp file: exactly `.wfp-`, 32
  lowercase hex digits and `.tmp`. Any other name, `.wfp-notes.tmp` of yours included, is listed. An append copies the
  file and the new bytes into the temp file, so it costs a copy of the file.
- **Replacing by rename** puts a new file at the path, so a write is refused, with the file keeping its content:
  - with `UnauthorizedAccessException` when the process may not write the file (read-only, or another user's), on every
    system, as in 2026, or may not create files in its folder, since the new content is created there;
  - with `IOException` when another process holds the file open without sharing writes (Windows), when the path is not
    a regular file or a link to one (a FIFO, a socket, a device that cannot seek), or when the file is a mount point (a
    single-file Docker bind mount, a Kubernetes `subPath`), where the system refuses the rename.

  And the new file is a new file:
  - on Linux and macOS it takes the permission bits of the file it replaces (of its target, for a symbolic link), and a
    copy those of its source, exactly, whatever the umask, as .NET's `File.Copy` does; both are set as the file is
    created, before any content is written. The setuid, setgid and sticky bits, access control lists and extended
    attributes are not carried over;
  - on Windows it takes the permissions the folder gives a new file, not the replaced file's own;
  - other hard links to the file keep the old content, and a symbolic link at the path, a link to nothing included, is
    replaced by a file, its target left as it was.
- **ETags** are made from the last-write time and the length. Each write through the provider stamps a last-write time
  later than the file it replaces, reading it back and stepping forward where the file system stores times coarsely
  (FAT, exFAT, HFS+), so it changes the ETag. Writes faster than that resolution step it further each time, so on such a
  file system a file's time can run ahead of the clock, without bound; bounding it would let an ETag repeat. A change by
  another program that keeps the length within the file system's time resolution may not change the ETag.
- **Symbolic links to files** are described by their target, which reads open: its length, last-write time and ETag.
  `PhysicalPath` stays the link's own path under the root. A link to nothing, or in a loop, is described as itself.
  The target is found through the system's resolution of the link; listing a link never opens its target.
- **Conditions.** `IfMatch` is checked just before the rename, which is not atomic: another process can change the file
  in between. An append checks the same way that the file is still the version it copied, and throws
  `PreconditionFailedException` if not.
- **New names are taken atomically.** `IfNotExists`, an append to a missing file, and the destination of a move or a
  copy never replace a file that is there. Windows renames without replacing. Linux and macOS, where .NET checks the
  name and then renames, take the name with an exclusive create and rename onto it, so the file exists empty for that
  moment: a watcher sees it, and a process that dies there leaves an empty file behind.
- **Reading while writing.** Readers share the file with writers and deleters and keep the content they opened. A delete
  goes ahead while a file is read. So does a write on Linux and macOS; on Windows, which does not rename over a file
  that is open, a write to a file being read fails and the file keeps its content: with `UnauthorizedAccessException`
  when the reader shares writing, as this provider's readers do, and with `IOException` ("being used by another
  process") when it does not.
- **Ranges.** `ReadRangeAsync` reads through `RandomAccess` on one handle, checking `ReadOptions.IfMatch` on that
  handle. `OpenReadSeekableAsync` reads ranges as it is read. Pinned, as by default, it checks the ETag on each fetch,
  and a file that changed, a shorter one included, throws `PreconditionFailedException`; with `AllowModifications`,
  only a file that got shorter throws, with `IOException`. A file deleted meanwhile throws `FileNotFoundException`.
- **Names** are matched without regard to case on Windows and macOS (a move from `a.txt` to `A.txt` renames the file),
  and with regard to case on other systems; the choice follows the operating system, not the volume. A `:` in a name is
  refused (`ArgumentException`). On Windows, so is a name Windows would read as another name or cannot hold: one that
  ends in `.` or a space, which Windows drops (`report.txt.` would name `report.txt`, `logs ` the folder `logs`), one
  that holds `"`, `<`, `>`, `|`, `?` or `*`, and a reserved device name (`CON`, `PRN`, `AUX`, `NUL`, `COM1` to `COM9`,
  `LPT1` to `LPT9`, and `COM` or `LPT` followed by `¹`, `²` or `³`), in any case, alone or with an extension
  (`con.txt`). So is an 8.3 short name (`REPORT~1.TXT`) of a file or folder whose long name differs, on a volume that
  keeps them. Listings leave out names no path can address: those names on Windows; on Linux and macOS a name holding
  `:` or `\`, a control character, or only dots and spaces (`...`); on any system a name such as `%2e%2e` that decodes
  to one of these.
- **A file and a folder** cannot share a name: writing a file at a folder's path, or under a file's path, throws
  `IOException`.
- **Access.** Where the operating system refuses the process access to a file or a folder that a member opens, lists,
  writes, moves or deletes, the member throws `UnauthorizedAccessException`, a read or a listing as well as a write. A
  path the system will not look up at all, such as one inside a folder the process may not enter, reads as missing:
  `GetFileInfoAsync` says it does not exist, and `DeleteAsync` returns false.
- **Content types** are not kept: `UpdateFileOptions.ContentType` is ignored, and `ContentType` is null.
- **The root.** Every path stays under the root folder, checked on the path: symbolic links and junctions inside the
  root are followed wherever they point. A recursive listing lists a linked folder but does not walk into it, since it
  can lead outside the root or back into it without end.
- **Watch** works as ASP.NET Core's `PhysicalFileProvider` does, through `PhysicalFilesWatcher`: the filter is a glob
  relative to the root, and leading `/` and `\` are ignored (so `/a.txt` is `a.txt`). A filter never fires when it is
  null, names a drive, leads above the root, or holds a character that no file name can hold. Dot-prefixed, hidden and
  system files are not watched (the provider's temp files among them), and a write fires when its rename puts the new
  content in place. The first `Watch` creates the root folder if it is missing. It relies on file system events, which
  some network and container-mounted folders do not deliver; set `DOTNET_USE_POLLING_FILE_WATCHER` to `1` or `true` to
  poll instead.
- **Errors** are the contract's types, and here they are the file system's own: `UnauthorizedAccessException` and
  `IOException` (a sharing violation, a full disk, a missing folder) pass through as .NET raises them, with no SDK in
  between; a missing file is `FileNotFoundException` naming the path under the root, with the system's exception
  inside; anything else that escapes is `IOException`, with it inside. The root folder is an operating system path,
  which the system resolves, dot segments included, rather than a path the provider checks.

## Signed URLs

None natively: `GetSignedUrlAsync` throws `NotSupportedException`. `WriteableFileProviders.AspNetCore` issues gateway
URLs for this provider instead, for reading, creating and writing files.

## Limits

- An append copies the whole file: files appended to very often, such as logs, pay a copy per append.
- `IfMatch` and an append's version check are not atomic (see Conditions).
- On Windows, a write to a file that another reader has open fails (see Reading while writing).
- `Watch` needs file system events, or polling.

## More

- [WriteableFileProviders](https://github.com/serionist/WriteableFileProviders#readme): every package, the provider
  matrix, versioning.
- [Moving from 2026](https://github.com/serionist/WriteableFileProviders/blob/main/docs/migration-2027.md#physical):
  what 2027.0.0 changed for this provider.
