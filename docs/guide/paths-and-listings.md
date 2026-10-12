# Paths and listings

## Paths

```csharp
ProviderPath path = "reports/2026.csv";          // also "/reports//2026.csv" or @"reports\2026.csv"
string name = path.Name;                         // "2026.csv"
ProviderPath folder = path.Parent;               // "reports"
ProviderPath next = folder.Append("2027.csv");   // "reports/2027.csv"
IReadOnlyList<string> segments = path.Segments;  // ["reports", "2026.csv"]
bool isRoot = ProviderPath.Root.IsRoot;          // true; "" and null are the root too
```

- A string splits on `/` and `\`, and empty parts are dropped. An array is taken as segments:
  `new[] { "reports", "2026.csv" }` is the same path.
- Paths are relative to the provider's root. No path reaches outside it. On Physical, symbolic links and junctions
  inside the root are followed, wherever they point, unless `RefuseLinks` is set: then a path that names or goes
  through one throws `ArgumentException`, and listings leave links out.
- A path is checked when you use it, not when you build it. These segments throw `ArgumentException` (parameter
  `path`): empty, only dots and spaces (`..`), holding a control character, or any of those once percent-decoded
  (`%2e%2e`).

Some providers refuse more:

- **Physical and SharePoint:** a `:` in a segment.
- **Physical on Windows:** a name Windows reads as another or can't hold: one ending in `.` or a space, one holding
  `"`, `<`, `>`, `|`, `?` or `*`, a device name (`CON`, `con.txt`), or an 8.3 short name (`REPORT~1.TXT`).
- **Box:** a name Box itself refuses, when it is written.

## The root

The root is `RootDirectoryPath` (or `RootDirectorySegments`) in the options, inside the container, bucket, library or
folder the provider uses.

- Writing, moving or deleting the root throws `InvalidOperationException`. Reading it as a file throws
  `ArgumentException`.
- `GetFileInfoAsync` returns the root as an existing folder, and the root always lists as existing.
- The root is checked like a path when the provider is built: `tenants/../other` throws `ArgumentException`. Physical
  is the exception: its root is a folder on disk, which the operating system resolves, dot segments included.
- The provider keeps the root it was built with. Changing the options afterwards doesn't move it.

## Files, folders and missing paths

`GetFileInfoAsync` tells you what is at a path:

| At the path | `Exists` | `IsDirectory` | `Length` |
|---|---|---|---|
| A file | true | false | its length |
| A folder | true | true | -1 |
| Nothing | false | false | -1 |

Every entry is an `IWriteableFileInfo`: an `IFileInfo` plus `Path` (relative to the root), `ETag` (the file's version,
for `IfMatch`) and `ContentType` (null where the store keeps none). `PhysicalPath` is set on Physical only.

### Folders

- **Real folders** (Physical, Box, SharePoint) exist while empty. Writing a file at a folder's path, or under a file,
  throws `IOException`.
- **Prefix folders** (Azure Blob, Amazon S3, MinIO, Google Cloud Storage) exist while something is under them. They go
  once the last file under them goes.
- Writes create missing parent folders.

Where folders are prefixes, a file and a folder can share a name. Both are stored, both read back by path, and a
listing shows both, so check `IsDirectory`.

- `GetFileInfoAsync` returns the file.
- The first delete removes the file, a second the folder.
- A writeable local stream on that path throws `IOException`.
- MinIO lists only the file, and hides what is under the folder while the file exists.

### Case

Box, SharePoint, and Physical on Windows and macOS ignore case: `a.txt` and `A.txt` are one file, and moving one to the
other renames it. The other stores keep case.

### Missing paths

- Reading a missing file, or a folder, throws `FileNotFoundException`.
- `GetFileInfoAsync` returns `Exists` false.
- A missing folder lists as not existing and empty.
- `DeleteAsync` returns false.

A missing bucket or container is not a missing path. Every member that reaches it throws `InvalidOperationException`,
naming it and the option that creates it.

## List a folder

```csharp
await foreach (var entry in files.EnumerateAsync("reports"))                  // one level
    Console.WriteLine($"{entry.Name} {(entry.IsDirectory ? "folder" : entry.Length)}");

await foreach (var entry in files.EnumerateAsync("reports", recursive: true)) // every level
    Console.WriteLine(entry.Path);

IDirectoryContents contents = await files.GetDirectoryContentsAsync("reports"); // one level, as IFileProvider's type
```

- `EnumerateAsync` streams entries. Recursive, it yields each folder once, before its contents. A store failure is
  thrown from the step that hit it, as one of the usual [exception types](errors.md).
- `GetDirectoryContentsAsync` returns one level. A missing folder is `Exists` false and empty. `IFileProvider`'s
  `GetDirectoryContents` and `GetFileInfo` are the synchronous forms.
- Listings show only names a path can address, so everything listed can be read back. A name such as `..`, one holding
  `\` or a control character, or a percent-encoded form of these is skipped, and such a folder is not walked.
- A provider hides its own temporary names, and only those: Physical's write files (`.wfp-`, 32 lowercase hex digits,
  `.tmp`) and Azure Blob's `.wfp-tmp` folder at the root. Your own `.wfp-` names are listed, and so is the
  `.wfp-rename-<guid>.tmp` copy that a case-only rename holds for a moment. Physical's
  `DeleteTempFilesAsync(olderThan)` deletes the write files a crash left behind.
- On Physical, a recursive listing lists a linked folder (symbolic link or junction) but doesn't walk into it; with
  `RefuseLinks`, it leaves the link out.

## Watch for changes

`Watch(filter)` works on Physical, as in ASP.NET Core's `PhysicalFileProvider`. Every other provider throws
`NotSupportedException`. [Physical's README](../../src/WriteableFileProviders.Physical/README.md) covers filters and
polling.
