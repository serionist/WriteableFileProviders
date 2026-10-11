# Paths and listings

How a provider names files, what it refuses, and what its listings show. The rules are the same on every provider;
where a store differs, the [provider comparison](providers.md) and the provider's README say how.

## Paths

Every member takes a `ProviderPath`, relative to the provider's root. A string converts to one by splitting on `/` and
`\` and dropping empty parts, so `"reports/2026.csv"`, `"/reports//2026.csv"` and `@"reports\2026.csv"` are the same
path. An array converts segment by segment, so `new[] { "reports", "2026.csv" }` is that path too.

```csharp
ProviderPath path = "reports/2026.csv";
string name = path.Name;                         // "2026.csv"
ProviderPath folder = path.Parent;               // "reports"
ProviderPath next = folder.Append("2027.csv");   // "reports/2027.csv"
IReadOnlyList<string> segments = path.Segments;  // ["reports", "2026.csv"]
bool isRoot = ProviderPath.Root.IsRoot;          // true; "" and null are the root too
```

A path is checked when it is used, never when it is built. A segment that is empty, only dots and spaces (`..`), holds
a control character, or becomes one of those when percent-decoded (`%2e%2e`) throws `ArgumentException`, naming the
parameter `path`. Some providers refuse more: Physical and SharePoint refuse a `:` in a segment; on Windows, Physical
also refuses a name Windows would read as another name or cannot hold (one ending in `.` or a space, one holding `"`,
`<`, `>`, `|`, `?` or `*`, a reserved device name such as `CON` or `con.txt`, an 8.3 short name such as
`REPORT~1.TXT`); and Box refuses a name Box itself does not allow when it is written (see each README).

**Nothing outside the root can be reached by a path.** On Physical, symbolic links and junctions inside the root are
followed wherever they point ([its README](../../src/WriteableFileProviders.Physical/README.md)).

## The root

The provider's root is a folder: the options' `RootDirectoryPath` (or `RootDirectorySegments`), inside the container,
bucket, library or folder the provider is configured for.

- It cannot be written, moved or deleted (`InvalidOperationException`), nor read as a file (`ArgumentException`).
- `GetFileInfoAsync` on the root returns an existing folder, and a listing of the root always exists.
- The root in the options goes through the same rules as a path when the provider is built, so a root such as
  `tenants/../other` throws `ArgumentException` naming `RootDirectoryPath`. Physical is the exception: its root is a
  folder of the disk, which the system resolves.
- The provider keeps the root it was built with: changing the options afterwards does not move it.

## Files, folders and missing paths

`GetFileInfoAsync` describes what is at a path:

| At the path | `Exists` | `IsDirectory` | `Length` |
|---|---|---|---|
| A file | true | false | its length |
| A folder | true | true | -1 |
| Nothing | false | false | -1 |

Every entry is an `IWriteableFileInfo`: besides `IFileInfo`'s members it has `Path` (relative to the root), `ETag` (the
file's version, which `IfMatch` takes) and `ContentType` (null where the store keeps none). `PhysicalPath` is the path
on disk for Physical, and null on every other provider.

**Folders** are real on Physical, Box and SharePoint: a folder exists while it is empty. On Azure Blob Storage, S3,
MinIO and Google Cloud Storage they are name prefixes: a folder exists while something is under it, and lists as
missing once the last file under it goes. Every write creates the missing folders above the file.

**A file and a folder of the same name** can both exist where folders are name prefixes: a file written at a folder's
path, or a folder under a file's path, is stored, reads back by path, and a listing shows both, so check `IsDirectory`
on each entry rather than assume a name is only one of the two. MinIO lists only the file, and hides what is under the
folder while the file exists. `GetFileInfoAsync` of such a name describes the file; deleting it removes the file first,
and a second delete the folder; a writeable local stream of a folder's path still throws `IOException`. Where folders
are real, either write throws `IOException`.

**Case** is ignored on Box and SharePoint, and on Physical on Windows and macOS: `a.txt` and `A.txt` are one file, and
moving one to the other renames it. Every other store keeps case.

**Missing things:** a read of a missing file, or of a folder, throws `FileNotFoundException`; `GetFileInfoAsync`
returns `Exists` false; a missing folder lists as not existing and empty; `DeleteAsync` returns false. A missing bucket
or container is not a missing path: every member that reaches it throws `InvalidOperationException`, naming it and the
option that creates it ([Errors](errors.md)).

## Listings

```csharp
await foreach (var entry in files.EnumerateAsync("reports"))                  // one level
    Console.WriteLine($"{entry.Name} {(entry.IsDirectory ? "folder" : entry.Length)}");

await foreach (var entry in files.EnumerateAsync("reports", recursive: true)) // every level
    Console.WriteLine(entry.Path);

IDirectoryContents contents = await files.GetDirectoryContentsAsync("reports"); // one level, as IFileProvider's type
```

- `EnumerateAsync` streams the entries as `IAsyncEnumerable<IWriteableFileInfo>`. Recursive, it yields each folder once,
  before its contents. A failure of the store surfaces from the step that met it, with the same exception types as
  every other member.
- `GetDirectoryContentsAsync` returns one level as `IDirectoryContents`; a missing folder is `Exists` false and empty.
  `IFileProvider`'s own `GetDirectoryContents(string)` and `GetFileInfo(string)` are the synchronous forms.
- **Listings show only names a path can address,** so everything listed can be read back: a name the provider's path
  rules would refuse (such as `..`, a name holding `\` or a control character, or a percent-encoded form of one of
  these) is left out, and so is a folder of that name, which is not walked into. Each provider's README names its own
  cases.
- **A provider hides its own temporary names**, and only those: Physical's write files, exactly `.wfp-`, 32
  lowercase hex digits and `.tmp`, and Azure Blob's `.wfp-tmp` folder at the provider root. Every other name is listed,
  one of yours that starts with `.wfp-` included, and so is the `.wfp-rename-<guid>.tmp` copy a case-only rename holds
  for a moment on a store that ignores case.
- On Physical, a recursive listing lists a linked folder (a symbolic link or a junction) but does not walk into it.

## Watching for changes

`Watch(filter)`, `IFileProvider`'s member, works on Physical, as ASP.NET Core's `PhysicalFileProvider` does it. Every
other provider throws `NotSupportedException` ([Physical's README](../../src/WriteableFileProviders.Physical/README.md)
says how the filter and polling work).
