# Reading and writing

Every operation is one async member of `IWriteableFileProvider` that takes a `ProviderPath` and, last, a
`CancellationToken`. The members mean the same on every provider; the [provider comparison](providers.md) says how each
store does them, and which conditions it checks atomically.

## Reads

```csharp
await using var whole = await files.ReadFileAsync("reports/2026.csv");
await using var slice = await files.ReadRangeAsync("big.bin", offset: 1_000_000, length: 4096);
await using var seekable = await files.OpenReadSeekableAsync("big.bin");
```

- **`ReadFileAsync`** opens the file. The stream may be a network stream that reads forward only.
- **`ReadRangeAsync(path, offset, length)`** reads only those bytes: empty past the end of the file or for a length of
  0, to the end for a null length. A negative offset or length throws `ArgumentOutOfRangeException`.
- **`OpenReadSeekableAsync`** returns a read-only stream that knows its length and fetches ranges as it is read, at
  least `SeekableReadOptions.BufferSize` bytes a fetch (1 MiB by default). By default it is **pinned** to the version
  it opened: if the file changes while it is read, the next fetch throws `PreconditionFailedException` (an
  `IOException`). A file the store keeps no `ETag` for cannot be pinned and throws `NotSupportedException`; set
  `SeekableReadOptions.AllowModifications` to read such a file, each fetch then reading whatever the file holds.
- **Conditions:** `ReadOptions.IfMatch` reads only the version it names, else throws `PreconditionFailedException`:

  ```csharp
  var info = await files.GetFileInfoAsync("big.bin");
  await using var part = await files.ReadRangeAsync("big.bin", 0, 4096, new ReadOptions { IfMatch = info.ETag });
  ```

A read of a missing file, or of a folder, throws `FileNotFoundException`. The reads of a stream a member returned throw
the same exception types as the members themselves ([Errors](errors.md)).

## Writes

```csharp
await files.UpdateFileAsync("reports/2026.csv", stream);
await files.UpdateFileAsync("reports/2026.csv", stream, new UpdateFileOptions { ContentType = "text/csv" });
await files.UpdateFileAsync("logs/today.log", line, UpdateFileOptions.Append);
```

`UpdateFileAsync` overwrites by default, or appends (`UpdateFileOptions.Append`, or `Mode = UpdateFileMode.Append`).
Every write:

- is **atomic**: a write that fails leaves the old content, and readers never see half of it;
- creates the missing folders above the file;
- reads the stream you give from its current position to its end, and leaves it open (its position afterwards is not
  promised);
- throws `InvalidOperationException` for the root, and `IOException` onto a folder or under a file where folders are
  real (Physical, Box, SharePoint); where folders are name prefixes, the file and the folder then share the name
  ([Paths and listings](paths-and-listings.md#files-folders-and-missing-paths)).

**Content type** (`UpdateFileOptions.ContentType`) is a hint: stored where the store keeps one, ignored where it does
not (Physical, Box; SharePoint names the type from the file name). Without one, an overwrite stores the store's
default, such as `application/octet-stream`, while an append and a local stream's commit keep the type the file has.
`IWriteableFileInfo.ContentType` reports it, or null where the store keeps none.

**Appends** never silently lose a concurrent write. Where the store has no append, the provider rewrites the file on
the condition that it is still the version it read, and throws `PreconditionFailedException` when another write landed
meanwhile: retry the append then. On Physical, Box and SharePoint that condition is checked rather than atomic (see
below). Every file needs an `ETag` for an append by rewrite; one without throws `NotSupportedException`.

## Conditions

```csharp
var info = await files.GetFileInfoAsync("settings.json");
await files.UpdateFileAsync("settings.json", edited, new UpdateFileOptions { IfMatch = info.ETag }); // only that version
await files.UpdateFileAsync("once.txt", stream, new UpdateFileOptions { IfNotExists = true });       // only if absent
```

A condition that does not hold throws `PreconditionFailedException`, and nothing is written. Setting both throws
`ArgumentException`. The `ETag` is the store's version of the file (a generation on Google Cloud Storage, a file version
id on Box, the last-write time and length on Physical).

A condition is **atomic** where the store itself refuses the write, and **checked** where the provider compares just
before it writes, so a change landing in between is not seen:

| Provider | `IfMatch` | `IfNotExists` |
|---|---|---|
| Physical | Checked | Atomic |
| Azure Blob | Atomic | Atomic |
| Amazon S3 | Atomic on AWS; checked, then sent, toward a named endpoint (`ServiceUrl`, a client's `ServiceURL`, or the AWS SDK's configured endpoint) | Same as `IfMatch` |
| MinIO | Checked, then sent for the server to enforce | Same as `IfMatch` |
| Google Cloud Storage | Atomic (generation) | Atomic |
| Box | Checked | Atomic |
| SharePoint | Checked, and sent to the service | Checked, and sent to the service |

## Copies, moves and deletes

```csharp
await files.CopyAsync("reports/2026.csv", "archive/2026.csv");
await files.MoveAsync("archive/2026.csv", "archive/2026-final.csv");
bool deleted = await files.DeleteAsync("tmp");
```

- **`CopyAsync` and `MoveAsync`** work on files, create the destination's missing folders, and refuse an existing
  destination with `FileExistsException` (an `IOException`), leaving it unchanged. A missing source, or a folder,
  throws `FileNotFoundException`. Moving a file onto itself does nothing; on a store that ignores case (Physical on
  Windows and macOS, Box, SharePoint), moving `a.txt` to `A.txt` renames it. A move that finds its source changed after
  the copy keeps the newer source, removes the copy and throws `PreconditionFailedException` on the stores that copy
  and then delete (each README says how).
- **`DeleteAsync`** deletes a file, or a folder with everything in it, and returns false when nothing was there. A
  folder delete never touches a sibling that only shares its name's prefix (`tmp` never deletes `tmp2`).

## Local streams

`CreateLocalFileStreamAsync(path, readOnly)` copies the file to a local temp file and opens it, for code that needs a
seekable, writeable `Stream` (a ZIP archive, a spreadsheet library):

```csharp
await using (var local = await files.CreateLocalFileStreamAsync("book.xlsx", readOnly: false))
{
    // read, seek and write `local` as a file
}   // uploaded here, once and atomically
```

- A writeable stream uploads its content when it is disposed, or when `CommitAsync` is called, once and atomically,
  keeping the file's content type. A dispose after an exception uploads too, so set `SkipReUploadOnDispose` to keep
  partially written content out.
- A writeable stream of a missing file starts empty; of a folder it throws `IOException`.
- A read-only stream never uploads; of a missing file, or of a folder, it throws `FileNotFoundException`.

## Synchronous code

The only synchronous members are the three `IFileProvider` has: `GetFileInfo`, `GetDirectoryContents` and `Watch`.
Everything else is async. The synchronous forms of 2026 (`ReadFile`, `UpdateFile`, `Delete`, `Move`,
`CreateLocalFileStream`, `GetDownloadUrl`) are `[Obsolete]` extension methods in
`WriteableFileProviderCompatibilityExtensions`, kept for the 2027 versions to ease the move and removed in the next
major ([Moving from 2026](../migration-2027.md)).
