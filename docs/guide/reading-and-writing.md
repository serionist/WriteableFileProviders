# Reading and writing

These members are async. Each takes a `ProviderPath` and, last, a `CancellationToken`.

## Read a file

```csharp
await using var whole = await files.ReadFileAsync("reports/2026.csv");
await using var slice = await files.ReadRangeAsync("big.bin", offset: 1_000_000, length: 4096);
await using var seekable = await files.OpenReadSeekableAsync("big.bin");
```

- `ReadFileAsync` may return a network stream that only reads forward.
- `ReadRangeAsync` returns just those bytes: empty past the end or for a length of 0, to the end for a null length. A
  negative offset or length throws `ArgumentOutOfRangeException`.
- `OpenReadSeekableAsync` returns a read-only stream that knows its length and fetches ranges as you read, at least
  `SeekableReadOptions.BufferSize` bytes a fetch (1 MiB by default).
- A missing file, or a folder, throws `FileNotFoundException`.

A seekable stream is pinned to the version it opened. If the file changes, the next fetch throws
`PreconditionFailedException`. A file without an `ETag` can't be pinned and throws `NotSupportedException`; set
`SeekableReadOptions.AllowModifications` to read it anyway, each fetch reading the current content.

## Read a known version

```csharp
var info = await files.GetFileInfoAsync("big.bin");
await using var part = await files.ReadRangeAsync("big.bin", 0, 4096, new ReadOptions { IfMatch = info.ETag });
```

If the file is no longer that version, the read throws `PreconditionFailedException`.

## Write a file

```csharp
await files.UpdateFileAsync("reports/2026.csv", stream);
await files.UpdateFileAsync("reports/2026.csv", stream, new UpdateFileOptions { ContentType = "text/csv" });
await files.UpdateFileAsync("logs/today.log", line, UpdateFileOptions.Append);
```

- Overwrites by default. `UpdateFileOptions.Append` (or `Mode = UpdateFileMode.Append`) appends.
- Atomic: a write that fails leaves the old content, and readers never see half a write.
- Creates missing parent folders.
- Reads your stream from its current position to its end, and leaves it open. Its position afterwards isn't promised.
- Writing the root throws `InvalidOperationException`. Writing onto a folder, or under a file, throws `IOException` on
  stores with real [folders](paths-and-listings.md#folders).

`ContentType` is a hint. Stores that keep a type store it; Physical and Box ignore it, and SharePoint picks its own from
the file name. Without one, an overwrite stores the store's default (such as `application/octet-stream`), while an
append and a local stream's commit keep the file's type.

## Append

Where the store has no native append, the provider rewrites the file on condition that it is still the version it read.
If another writer changed or created the file in between, or deleted it, the append reads it again and appends after
what is there now, on every provider, as Azure Blob's native append does: up to five attempts in all, then
`PreconditionFailedException`. With your own `IfMatch` or `IfNotExists`, the first conflict throws instead, since you
asked for that version, or for no file at all.

- The condition is atomic on Azure Blob, Google Cloud Storage and S3 on AWS.
- Elsewhere it is checked, so a write that lands between the check and the rewrite can be lost
  ([providers compared](providers.md#reads-and-writes)).
- An append by rewrite needs the file's `ETag`; a file without one throws `NotSupportedException`.
- A write whose answer was lost, and which the store's SDK sent again, can meet itself: the append then reads its own
  write as another writer's and appends its bytes a second time, as a caller who retried by hand would.
- On Windows, which doesn't rename over a file that is open, Physical can't replace a file another append is reading: of
  two appends to one file at once, one can fail with `UnauthorizedAccessException`.
- On Linux and macOS, Physical's append that loses a new name throws `FileAlreadyExistsException` while the file there is
  still empty (a move's destination, a file system without hard links, or a file another writer left empty): the rename
  that fills such a file would undo an append made to it.

## Write only if unchanged, or only if absent

```csharp
var info = await files.GetFileInfoAsync("settings.json");
await files.UpdateFileAsync("settings.json", edited, new UpdateFileOptions { IfMatch = info.ETag }); // only that version
await files.UpdateFileAsync("once.txt", stream, new UpdateFileOptions { IfNotExists = true });       // only if absent
```

- A condition that fails throws `PreconditionFailedException`, and nothing is written. When the file already exists
  under `IfNotExists`, it is `FileAlreadyExistsException`, which derives from it: catch that first to tell "created
  meanwhile" from "changed meanwhile".
- Only a file counts for `IfNotExists`. A folder at the path gets what a write without conditions gets there:
  `IOException` where folders are real, the file stored beside the folder on an object store.
- Setting both throws `ArgumentException`.
- The `ETag` is the store's version of the file: a generation on Google Cloud Storage, a file version id on Box, the
  last-write time and length on Physical.
- **Atomic** stores refuse the write themselves. **Checked** stores compare just before writing, so a change in between
  is missed. [Providers compared](providers.md#reads-and-writes) says which store does which.

## Copy, move, delete

```csharp
await files.CopyAsync("reports/2026.csv", "archive/2026.csv");
await files.MoveAsync("archive/2026.csv", "archive/2026-final.csv");
bool deleted = await files.DeleteAsync("tmp");
```

- `CopyAsync` and `MoveAsync` take files, and create the destination's missing folders.
- An existing destination throws `FileExistsException` and is left unchanged. A missing source, or a folder, throws
  `FileNotFoundException`.
- Moving a file onto itself does nothing. Where case is ignored, moving `a.txt` to `A.txt` renames it.
- On stores that move by copy, then delete: if the source changed after the copy, the move keeps the newer source,
  removes the copy, and throws `PreconditionFailedException`.
- `DeleteAsync` deletes a file, or a folder with everything in it, and returns false when nothing was there. Deleting
  `tmp` never touches `tmp2`.

## Edit through a local file

For a library that needs a seekable, writeable `Stream` (a ZIP archive, a spreadsheet), copy the file to a local temp
file:

```csharp
await using (var local = await files.CreateLocalFileStreamAsync("book.xlsx", readOnly: false))
{
    // read, seek and write `local` as a file
}   // uploaded here, once and atomically
```

- A writeable stream uploads when it is disposed, or on `CommitAsync`, once and atomically. It keeps the file's content
  type.
- A dispose after an exception uploads too. Set `SkipReUploadOnDispose` to keep half-written content out.
- A writeable stream of a missing file starts empty; of a folder, it throws `IOException`.
- A read-only stream never uploads. Of a missing file, or a folder, it throws `FileNotFoundException`.

## Synchronous code

The only synchronous members are `IFileProvider`'s: `GetFileInfo`, `GetDirectoryContents` and `Watch`.

The 2026 forms still compile, with a warning that names the async member to call: the synchronous `ReadFile`,
`UpdateFile`, `Delete`, `Move` and `CreateLocalFileStream`, the overloads that take an `IFileInfo` or an
`UpdateFileMode`, and `GetDownloadUrl`. They are `[Obsolete]` extension methods in
`WriteableFileProviderCompatibilityExtensions`, and go in 2028.0.0 ([Moving from 2026](../migration-2027.md)).
