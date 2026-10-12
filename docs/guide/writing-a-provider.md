# Writing a provider

Derive from `WriteableFileProvider<TOptions>` in `WriteableFileProviders.Common`. The base class validates and resolves
paths, protects the root, checks moves, copies and conditions, maps errors, and calls the protected `...CoreAsync`
members you implement. Every public member is the base's.

## Implement five members

```csharp
using System.Runtime.CompilerServices;
using WriteableFileProviders;

public sealed class MyStoreOptions : FileProviderOptions
{
    public string? ConnectionString { get; set; }
}

public sealed class MyStoreFileProvider(MyStoreOptions options) : WriteableFileProvider<MyStoreOptions>(options)
{
    // A file, a folder or a missing path: WriteableFileInfo, WriteableFileInfo.Folder or WriteableFileInfo.Missing.
    protected override Task<IWriteableFileInfo> GetFileInfoCoreAsync(ResolvedPath path, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    // One level of a folder; nothing for a missing one. Leave out names IsAddressable refuses.
    protected override async IAsyncEnumerable<IWriteableFileInfo> EnumerateCoreAsync(
        ResolvedPath path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }

    // The file's content; NotFound(path) when it is missing.
    protected override Task<Stream> ReadFileCoreAsync(ResolvedPath path, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    // Overwrite or append (options.Mode), atomically, honouring ContentType, IfMatch and IfNotExists.
    protected override Task UpdateFileCoreAsync(ResolvedPath path, Stream contents, UpdateFileOptions options, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    // A file, or a folder with everything in it; false when nothing was there.
    protected override Task<bool> DeleteCoreAsync(ResolvedPath path, CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
```

A `ResolvedPath` is a path the base has validated and placed under the root. `AbsoluteSegments` names it in the store;
`RelativeSegments` and `Path` are relative to the root.

To build it from configuration, give the provider a public constructor that takes only its `TOptions` and does no
network I/O. `TOptions` needs a public parameterless constructor and settable properties. Then register it with
`WriteableFileProviderSerializer.Register<MyStoreFileProvider>()`, or pass its assembly to the serializer
([Build a provider from configuration](dependency-injection.md#build-a-provider-from-configuration-or-json)).

## Override what your store does better

Each of these has a default that works on any store, through the five members above:

- Reads: `ReadRangeCoreAsync`, `OpenReadSeekableCoreAsync`.
- Copies and moves: `CopyCoreAsync`, `MoveCoreAsync` (by default a copy, then a delete of exactly the source),
  `DeleteFileCoreAsync`.
- Listings: `FolderExistsCoreAsync`, `EnumerateRecursiveCoreAsync`.
- Signed URLs and watching: `GetSignedUrlCoreAsync` (with `SupportsSignedUrl`), `WatchCore`.
- Errors and names: `IsNotFound`, `IsAccessDenied`, `IsSegmentAllowed`, `ValidatesRootDirectory`.
- Disposal: `Dispose(bool)`, `DisposeAsyncCore`.

Declare what the store can do. Until you do, the base refuses or weakens the operation:

| Member | Default | Effect of the default |
|---|---|---|
| `SupportsAtomicPreconditions` | false | The base checks `IfMatch` and `IfNotExists` just before the write: checked, not atomic. Only a file fails `IfNotExists` there; a folder at the path reaches your `UpdateFileCoreAsync`, which answers as it does without conditions. |
| `IsCaseInsensitive` | false | Moving `a.txt` to `A.txt` is an ordinary move. When true, it is a rename: the destination check is skipped, and the default `MoveCoreAsync` goes through a temporary name. |
| `SupportsSignedUrl(access)` | false | `GetSignedUrlAsync` throws `NotSupportedException`. |
| `SupportsSignedUrlContentHeaders(access)` | false | A URL with `DownloadFileName` or `ContentType` throws `NotSupportedException`. |
| `MaxSignedUrlValidity` | 7 days | A longer validity throws `NotSupportedException`. |
| `TimeProvider` | `TimeProvider.System` | The clock for expiry times; a test can set another. |

## Use the helpers

- `AppendByRewriteAsync`: an append for a store without one, conditional on the version read.
- `SpoolToTempFileAsync`: copies a stream to a local temp file, for an SDK that needs a length or a seekable stream.
- `NotFound(path)`: the `FileNotFoundException` for a missing file, naming the path under the root.
- `ReadWithContractAsync`: a read by a public member of your own, under the same error rules as `ReadFileAsync`.
- `IsAddressable(name)`: whether your path validation, `IsSegmentAllowed` included, accepts the name as one segment.

## Follow the rules the base can't enforce

- **ETags.** Give every file an `ETag`. The default seekable read pins it, and `AppendByRewriteAsync` writes with
  `IfMatch` on it; without one, both throw `NotSupportedException`.
- **Listings.** Skip every name `IsAddressable` refuses, and don't walk into such a folder, so everything listed can be
  read back. Hide your own temporary names, and only those.
- **Errors.** An exception from a `...CoreAsync` member, or from a stream or listing it returns, that isn't one of the
  [contract's types](errors.md) is wrapped. It becomes `FileNotFoundException` when `IsNotFound` recognises it,
  `UnauthorizedAccessException` when `IsAccessDenied` does, and `IOException` otherwise, timeouts included.
- Override `IsNotFound` and `IsAccessDenied` for your SDK. Where you know better, throw a contract type yourself: it
  passes through.
- **Conditions.** When your store refuses an `IfNotExists` write because a file is there, throw
  `FileAlreadyExistsException`; any other failed condition throws `PreconditionFailedException`. A folder at the path
  isn't a file: answer as you would without conditions (`IOException` where folders are real, the file beside it where
  they are name prefixes). The conformance suite checks the exact types.
- **The root** is checked when the provider is built, like a path, and kept. Read it as `Resolve(ProviderPath.Root)`,
  not from the options, which a caller can still change. If your root isn't a path in the store (a folder on disk),
  override `ValidatesRootDirectory` to return false and check it yourself.
- The base constructor calls `IsSegmentAllowed` and `ValidatesRootDirectory` before yours runs, so they must not
  depend on your provider's state.
- **Local streams** aren't customisable: `CreateLocalFileStreamAsync` reads through `ReadFileCoreAsync` and commits
  through `UpdateFileCoreAsync`.

## Test it

`ProviderConformanceTests`, in the repository's `tests/WriteableFileProviders.Testing`, is the contract: one test per
rule, which every built-in provider passes. Derive a test class from it, implement `CreateProvider`, and override what
your store can do (real folders, case, signed URLs, the large-file size). It then runs every rule against your store.
