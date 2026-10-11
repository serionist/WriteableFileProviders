---
title: Modernize WriteableFileProviders into the 2027.0.0 release
issue: "#3"
status: approved
approved_by: serionist
approved_at: 2026-10-10
---

# Modernize WriteableFileProviders into the 2027.0.0 release

This plan is the record of the modernization. It holds the decisions, the design, the order of work, every review
finding with its status, and a progress log. Read it first when picking the work up, and update its progress log and
finding tracker at the end of every step.

Approval: @serionist accepted every recommendation in [the roadmap](../reviews/2026-10-10-roadmap.md) (D1–D8), the
additions recorded under Decisions, and the recommended answers to the plan review's questions (D15–D19), on
2026-10-10. They authorized autonomous execution: issues, PRs and merges, across one long session while they are away.
They had not read this file when it was approved.

Review history: round 1 (standard) was applied before S2. Round 2 (deep) ran after S0, S1 and S2 had merged and while
S3 and S4 were being built. Its findings on S3 were applied before S3 merged, and S4 was re-briefed with them before it
finished. The maintainer's standing instruction for this session ("do the recommended") answers its owner questions;
D20–D24 record the answers. The maintainers read the reviewed text after the fact, with the merged PRs listed under
"For the maintainers".

## Goal

When this is done, `2027.0.0` of every package is ready to publish:

- **A modern .NET 10 library:** one API that means the same thing on all seven providers.
- **Every finding fixed:** F1–F77 of the [codebase review](../reviews/2026-10-10-codebase.md).
- **The P1 and P2 features of the roadmap.**
- **Tests:** unit tests, plus emulator tests on Testcontainers, at 90% or more line coverage per assembly.
- **CI**, READMEs and contributor docs.

**In scope:**
- **Build and layout:** `.slnx`, `src/` and `tests/`, `Directory.Build.props`, `Directory.Packages.props`.
- **Strictness:** nullable, warnings as errors, code style enforced in the build.
- **Packaging:** MinVer versions.
- **The Common API for 2027:** cancellation, `ProviderPath`, async listing, range reads, signed URLs, ETags and
  conditional writes, content type, copy, disposal, DI, and System.Text.Json serialization with no Newtonsoft.
- **The one-meaning contract** below, applied on every provider.
- **Providers:** SDK upgrades and a provider-by-provider rewrite. MinIO moves onto the S3 code.
- **The new `WriteableFileProviders.AspNetCore` gateway package.**
- **Tests:** a conformance suite shared by every provider.
- **Emulators:** Testcontainers for Azurite, S3Mock (S3 and MinIO) and fake-gcs-server. Stateful in-memory fakes for
  the Graph and Box APIs.
- **Coverage gate, CI, Dependabot, CodeQL, a release workflow.**
- **Docs:** READMEs, CONTRIBUTING, SECURITY, a migration guide.
- **The security patch** on the current line (D1), as its own small PR first.

**Out of scope** (recorded under Later):
- **Publishing:** pushing any package to nuget.org, and the GitHub security advisory. These are the maintainers'
  actions; see "For the maintainers" in the progress log.
- **The licence file:** pending D8's agreement.
- **The P3 features:**
  - change notifications on the object stores;
  - `IProgress`;
  - OpenTelemetry;
  - health checks;
  - trimming and AOT;
  - custom object metadata beyond content type;
  - maximum upload size on signed URLs.

## Decisions

All of these were taken on 2026-10-10 by @serionist, and are posted on #3, where AGENTS.md requires the maintainers'
agreement for a one-way door. D20 records how the two maintainers agree for this release.

| # | Decision | Detail |
|---|----------|--------|
| D1 | Security patch first | Fix F1, F2 and F47 on the current net8.0 line in a small PR before the modernization; delete the commented test of F9. Done: the local `ado` remote is removed. For the maintainers: rotate the MinIO key pair of F9, delete the Azure DevOps repository and organization, publish the patch packages, and publish a GitHub security advisory. |
| D2 | One major release, `2027.0.0` | net10.0 only. SemVer from git tags (MinVer, tag prefix `v`, minimum `2027.0`). All packages released in lockstep at the same version. Providers take a `ProjectReference` to Common, and `UseLocalCommon` goes. `.NET 8` consumers stay on `2026.10.10.x`. |
| D3 | Every break goes into 2027.0.0 | Listed under Compatibility. |
| D4 | One meaning on every provider | The contract below. |
| D5 | Azure on block blobs | Writes go through staged blocks. Append stages a block and recommits the list. Existing append blobs stay readable; appending to one rewrites it as a block blob. |
| D6 | MinIO on the S3 code | `WriteableMinioFileProvider` and `MinioFileProviderOptions` keep their names and properties. They run on AWSSDK.S3 through an internal core shared with the S3 provider. The Minio SDK dependency goes. |
| D7 | Safer defaults | MinIO (F10), amended in round 2 (P36): `UseSSL` stays a `bool`, because every configuration saved with `ToJson` holds `"UseSSL": false` and an unset value cannot be told apart. `UseSSL = false` toward a host that is not loopback throws, unless the new `AllowInsecureHttp` option is `true`. Stored configurations that point at plain-HTTP MinIO on another host add that option; the migration guide says how to find them. SharePoint (F7): the effective root is `WorkFolderPath` followed by `RootDirectorySegments`, after removing one leading copy of the work folder from `RootDirectorySegments`, so a configuration that sets both to the same folder keeps working. Caller paths are never stripped, and the options are never mutated, so `ToJson` round-trips. Round 2 (P37): S9 pins every overlap shape the old `StripWorkFolder` handled (a full copy, a partial prefix, a caller path that starts with the work folder, case differences) in a table test, and a strict-prefix shape that would relocate files throws at construction; the relocated shapes are listed in the migration guide. |
| D8 | MIT licence | Once László Nemes and Starschema (a co-owner of the nuget.org ids) agree. Until then no `LICENSE` file and no `PackageLicenseExpression`. |
| D9 | No Newtonsoft anywhere | Not in our code, our packages or our tests, and no bridge package; System.Text.Json only. GCS's own SDK (`Google.Apis`) still depends on it transitively, which we cannot change. |
| D10 | Build files | `Directory.Build.props` for shared properties, `Directory.Packages.props` for central package management, with lock files. |
| D11 | Testcontainers | Emulator tests start their containers through Testcontainers; no manual `docker run`. |
| D12 | READMEs | A root README and one per package, packed into the nupkg. |
| D13 | How the work lands | Several issues and PRs under #3, which this session opens and merges itself without waiting for a maintainer; the maintainers review them afterwards (decided 2026-10-10, superseding "one single PR"). Each PR builds, passes its tests and has its `review-code` pass before merge. This is a scoped exception to finish-work steps 4 and 6 (the developer's look before review, and a maintainer marking the PR ready) for the #3 issues only; AGENTS.md is not amended. Every merged PR is listed for the maintainers' after-the-fact review under "For the maintainers". |
| D14 | Scope | All findings, the P1 and P2 features, full unit tests, Testcontainers. P3 goes under Later. |
| D15 | `IfMatch` guarantees | The precondition is atomic on Azure, S3, MinIO, GCS (generation), Box and SharePoint simple uploads. On Physical (synthesized ETag from length and last-write time) and on SharePoint upload sessions it is checked, not atomic, and the XML docs say so. `IfNotExists` is atomic everywhere it can be: on Physical it uses `File.Move(overwrite: false)`. |
| D16 | GCS move | Copy with `IfGenerationMatch = 0` (destination must not exist), then delete the source with its generation. fake-gcs-server has no native move; `MoveObject` goes under Later. |
| D17 | Two P2 items kept | Public constructors that take a prebuilt SDK client (with the options) on every cloud provider, for managed identity, Aspire, custom retries and tests. A provider never disposes a client it was given; the caller owns it. SDK types in public signatures (these constructors, `S3Region` and `S3StorageClass` on the S3 options) are part of the contract, so an SDK major that changes them is a WriteableFileProviders major. SharePoint signs in with a client secret, a certificate (`ClientCertificateBase64` and `ClientCertificatePassword`), or managed identity / `DefaultAzureCredential` (`UseDefaultAzureCredential`). |
| D18 | URLs with a lifetime set by the service | Requested validity V and fixed lifetime L. If V < L, `NotSupportedException`, so the gateway takes over. If V ≥ L, the URL is returned only with `AllowShorterValidity`, with `ExpiresOn` = now + L (Box) or null (SharePoint). S3 and MinIO: when the credential expires before now + V, or is a session credential with an unknown expiry, the URL counts as shorter than asked. |
| D19 | Concurrent appends | An append never silently loses a concurrent write, except on Physical and on SharePoint upload sessions, where the check is not atomic and the XML docs say so (round 2, P14; the conformance test for it runs only where the provider fixture declares the capability). Azure commits the block list with `IfMatch` on the ETag it read, re-reading and retrying a bounded number of times. `AppendByRewriteAsync` writes with `IfMatch` on the ETag or generation it read. A concurrent change it cannot absorb throws `PreconditionFailedException`. |
| D20 | Maintainers' agreement for this release | @serionist's word is the maintainers' agreement for the decisions of #3, and @NemesLaszlo reviews the merged PRs afterwards with him (the maintainer's instruction, 2026-10-10). Posted on #3. |
| D21 | Live suites stay per PR (round 2, P3) | AGENTS.md's rule is unchanged: a provider change runs its emulator suite, or its live suite where there is none, and its PR says which ran or that it did not and why. Box and SharePoint get offline fakes in S9 and S10, but their PRs still say "live suite not run"; their live runs are outstanding items for the maintainers. |
| D22 | Gateway defaults (round 2, P13, P26) | Fail closed: `MaxValidity` defaults to 1 hour for Create and Write tokens (configurable, never above 7 days) and to 7 days for Read; Create and Write tokens are one-time by default; the default replay store is in memory and holds only on one instance, which the README and XML docs say (a scaled-out app registers a shared `IGatewayReplayStore` or turns one-time off explicitly); `MaxUploadBytes` defaults to 100 MiB. Added in the review of #24 (S11): these rules bind gateway URLs only (`SignedUrlKind.Gateway`); a provider's native upload URL can be reused until it expires and lives up to the provider's maximum (7 days), which S6–S8 treat as intended, and an app that wants the same upload rules everywhere sets `PreferGateway` (F1). A gateway download is served as the content type its read token names, else `application/octet-stream`, never a type the store keeps, and every gateway response carries `Content-Security-Policy: default-src 'none'; sandbox`; the README recommends a host name of its own for the gateway (F2, the maintainer's call: fail closed and harden). |
| D23 | Smaller surface (round 2, P29, P31, P32, P40) | No Box path-to-id cache: every call resolves the path, as today. Serializer type resolution: the assemblies given, a built-in map from provider class names to their package assemblies, and one registration API, `WriteableFileProviderSerializer.Register<TProvider>()`; no scan of loaded assemblies. Extensions only keep old call shapes compiling. No internal GCS client seam: the tests use the public constructor. The dropped pieces are under Later. |
| D24 | Content type and unsupported options (round 2, P16, P17) | `UpdateFileOptions.ContentType` is a hint: stored where the store keeps one, ignored elsewhere, and `IWriteableFileInfo.ContentType` is null where the store keeps none. An option a provider cannot honor throws `NotSupportedException`; on `GetSignedUrlAsync` that lets the gateway take over. A signed URL validity that is not positive throws `ArgumentOutOfRangeException`; one above what the provider can sign natively throws `NotSupportedException`, so the URL service falls back on every provider alike. |
| D25 | Provider classes are sealed | Every provider class is sealed in 2027; a custom provider derives from WriteableFileProvider<TOptions>, or wraps IWriteableFileProvider. Each provider step applies it and lists it in the migration guide. |
| D26 | New options keep the stored shape | Every options property new in 2027 is written to JSON only when it differs from its default ([JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]), so a configuration that uses none of them keeps its 2026 shape. |

## Design

### Repo layout and build

```
WriteableFileProviders.slnx
Directory.Build.props        shared: net10.0, LangVersion, Nullable, strictness, analyzers, docs, audit, lock files
Directory.Packages.props     central package versions, transitive pinning only for advisories (with a comment)
global.json                  SDK 10.0.1xx, test runner Microsoft.Testing.Platform
eng/coverage-gate.cs         file-based app: fails when an assembly falls below its floor in eng/coverage.json
src/Directory.Build.props    packaging: MinVer, README, icon, SourceLink, snupkg, package validation, PublicAPI files
src/WriteableFileProviders.{Common,Physical,AzureBlob,AmazonS3,Minio,GoogleCloudStorage,Box,SharePoint,AspNetCore}/
tests/Directory.Build.props  test projects: MSTest 4 on Microsoft.Testing.Platform, coverage, no packing
tests/WriteableFileProviders.Testing/      the shared conformance suite and the container fixtures
tests/WriteableFileProviders.<Package>.Tests/   one per package
```

- **Strictness:**
  - `TreatWarningsAsErrors`, `EnforceCodeStyleInBuild`, `AnalysisLevel` latest-recommended,
    `GenerateDocumentationFile`, `Nullable`.
  - The `-> warning` lines in `.editorconfig` become warnings.
  - While a project is still old code it sets `WfpLegacy=true`, which relaxes these. Each rewrite removes it, and at
    the end no project has it.
- **Versions:** MinVer sets them; Common's hard-coded `<Version>1.1.2</Version>` goes (F50).

### The behaviour contract (D4)

Every provider passes the same conformance tests for each of these rules:

- **Paths:**
  - A `ProviderPath` is relative to the provider root.
  - Segments are validated as today: no empty, dot-only, control-character or percent-encoded escapes.
  - The root cannot be modified (`InvalidOperationException`) or read as a file (`ArgumentException`).
  - No public member takes absolute segments (F1, F2, F47).
- **File info:**
  - A file: `Exists`, not a directory, `Length`, `LastModified`, `ETag`, `ContentType` where the store keeps one.
  - A folder: `Exists`, `IsDirectory`, `Length` -1.
  - A missing path: `Exists` false, `IsDirectory` false, `Length` -1.
  - `PhysicalPath` is null except on Physical (F20, F21).
  - On object stores a folder exists while some object has its prefix. Physical, Box and SharePoint have real folders,
    which exist even when empty (F43).
- **Listing:** one level.
  - Folders before files is not promised.
  - A missing folder lists as `Exists` false and empty.
  - `EnumerateAsync(recursive: true)` walks every level and yields each folder once, before its contents.
- **Root and names:**
  - `GetFileInfo` on the root returns a folder: `Exists`, `IsDirectory`, `Name` empty.
  - Names are case-sensitive on the object stores, and case-insensitive on Box, SharePoint and Physical (Windows,
    macOS). A move between two names that differ only in case is a rename, not "destination exists".
  - Physical writes through a temp file named `.wfp-<guid>.tmp` in the target folder. Listings never show it, and a
    failed write removes it.
- **Reads:**
  - A missing path, or a path that is a folder, throws `FileNotFoundException`, with the store's error as
    `InnerException` (F18).
  - A ranged read past the end, or of length 0, returns an empty stream; a negative offset or length throws
    `ArgumentOutOfRangeException`.
  - A ranged read can carry `IfMatch` (`ReadOptions`). A seekable read pins the version (ETag or generation) through it
    and throws `IOException` if the file changes mid-read. SharePoint checks the ETag on each fetch, which is documented
    as not atomic.
- **Writes (overwrite and append):**
  - Atomic: a write that fails leaves the old content (F3, F4).
  - They create missing parent folders.
  - They read the caller's stream from its current position to its end, and never seek, rewind or dispose it (F6,
    F41).
  - Appending to a missing file creates it.
  - With `IfMatch` or `IfNotExists`, a failed precondition throws `PreconditionFailedException`, an `IOException`
    (D15 lists where the check is atomic). Both together throw `ArgumentException`.
  - An append never silently loses a concurrent write (D19).
  - What the caller observes: the bytes from the stream's position at the call to its end are stored, and the stream is
    left open. Where the position is afterwards is not promised, because SDKs seek seekable streams for retries.
- **Delete:**
  - A file, or a folder recursively.
  - A missing path returns false.
  - A folder delete never touches a sibling that shares its prefix.
- **Move and copy:**
  - Files only: a missing or folder source throws `FileNotFoundException`.
  - They create the destination's parent folders.
  - An existing destination throws `FileExistsException`, an `IOException`, and is left unchanged (F19).
  - Moving onto itself does nothing.
  - Move removes exactly the source key, never a prefix (F38).
- **Local file stream:**
  - Always a local temp copy.
  - A writeable stream commits on dispose, atomically, unless `SkipReUploadOnDispose` is set; it commits once, even
    when disposed twice.
  - A read-only stream of a missing file throws `FileNotFoundException` on every provider (F5, F31, F34).
- **Buckets and containers:** a missing bucket or container is never created unless the options say
  `CreateContainerIfMissing` / `CreateBucketIfMissing` (Azure, S3, MinIO, GCS; default false) (F44).
- **Signed URLs:**
  - `Validity` must be positive and at most the provider's maximum, else `ArgumentOutOfRangeException`.
  - A URL never lives longer than asked.
  - A URL that would live shorter than asked is returned only with `AllowShorterValidity`, and then carries its true
    `ExpiresOn` if known. Otherwise the call throws `NotSupportedException` (F11). Service-set lifetimes follow D18.
  - A read URL for a missing file throws `FileNotFoundException`.
- **Unsupported:** any operation a provider cannot do throws `NotSupportedException`, never
  `PlatformNotSupportedException`.
- **Added in round 2:**
  - *Path parsing (P8, P25):* a string, through `Parse` or the implicit conversion, keeps today's normalization: both
    `/` and `\` separate segments on every OS, a leading separator is dropped, and repeated and trailing separators
    collapse; then every segment is validated. A `string[]` keeps rejecting empty parts. A provider can reject more
    characters through its segment hook (Physical rejects `:`), and Physical checks that the resolved full path is
    under the root. Conformance cases: `/a/b`, `a\b` and `a//b` equal `a/b`; `a\..\b`, `a/../../x`, `%2e%2e`,
    `%252e%252e`, `a%2F..%2Fb` and `a:b` are refused. They run on Windows CI too.
  - *Pinned reads (P15):* "pinned" means "throws `IOException` when the file changes" on every provider. Box pins a
    range read to the file version behind the ETag; Physical re-checks its synthesized ETag on each fetch and says it is
    not atomic.
  - *Missing bucket or container (P18):* every member throws `InvalidOperationException` naming the bucket and the
    `Create*IfMissing` option; it is never reported as a missing path. Conformance case on Azurite, S3Mock and
    fake-gcs-server.
  - *Move and copy destination check (P19):* Azure copies with `IfNoneMatch = *`; S3 and MinIO copy with
    `If-None-Match: *` (verified on S3Mock in S7, else "checked, not atomic"); GCS uses `IfGenerationMatch = 0`; Box and
    SharePoint use conflict "fail"; Physical uses `File.Move(overwrite: false)`.
  - *Large files (P6):* write, overwrite, append and local-stream edit of a large file keep their content, and a failed
    large write leaves the old content. The suite's default size is 100 MB; a fixture can lower the provider's threshold
    so the test crosses S3 and MinIO multipart, Azure staged blocks, SharePoint upload sessions, Box chunked upload and
    GCS resumable upload.
  - *Temp files (P28, P38):* temp files are owner-only on Unix (`UnixCreateMode = UserRead | UserWrite`) and
    `DeleteOnClose`; a Unix-only test checks the mode. Fixtures create a unique folder per class and delete it in class
    cleanup, and the suite checks no `.wfp-*.tmp` is left.
  - *Physical sharing (P20):* Physical opens readers with `FileShare.ReadWrite | FileShare.Delete`, so a replace can
    proceed; a conformance case writes while a read stream is open, with the result expected per OS, and runs on Windows
    CI.
- **Library code:**
  - Every await uses `ConfigureAwait(false)`.
  - Sync members go through one `GetAwaiter().GetResult()` helper (F13, F33).
  - No network I/O in constructors (F13).

### Common API

Sketch; the PR's XML docs are the reference, and the progress log records any deviation.

```csharp
public readonly struct ProviderPath : IEquatable<ProviderPath>   // implicit from string ("a/b") and string[]
{   IReadOnlyList<string> Segments; bool IsRoot; string Name; ProviderPath Parent; Append(...); Parse(string); Root; }

public interface IWriteableFileInfo : IFileInfo { ProviderPath Path { get; } string? ETag { get; } string? ContentType { get; } }
public sealed class WriteableFileInfo : IWriteableFileInfo   // replaces FileInfo (F69)

public interface IWriteableFileProvider : IFileProvider, IDisposable, IAsyncDisposable
{
    FileProviderOptions Options { get; }
    Task<IWriteableFileInfo> GetFileInfoAsync(ProviderPath path, CancellationToken ct = default);
    Task<IDirectoryContents> GetDirectoryContentsAsync(ProviderPath path, CancellationToken ct = default);
    IAsyncEnumerable<IWriteableFileInfo> EnumerateAsync(ProviderPath path, bool recursive = false, CancellationToken ct = default);
    Task<Stream> ReadFileAsync(ProviderPath path, CancellationToken ct = default);
    Task<Stream> ReadRangeAsync(ProviderPath path, long offset, long? length, ReadOptions? options = null, CancellationToken ct = default);
    Task<Stream> OpenReadSeekableAsync(ProviderPath path, SeekableReadOptions? options = null, CancellationToken ct = default);
    Task UpdateFileAsync(ProviderPath path, Stream contents, UpdateFileOptions? options = null, CancellationToken ct = default);
    Task<bool> DeleteAsync(ProviderPath path, CancellationToken ct = default);
    Task MoveAsync(ProviderPath source, ProviderPath destination, CancellationToken ct = default);
    Task CopyAsync(ProviderPath source, ProviderPath destination, CancellationToken ct = default);
    Task<LocalFileStream> CreateLocalFileStreamAsync(ProviderPath path, bool readOnly, CancellationToken ct = default);
    Task<SignedUrl> GetSignedUrlAsync(ProviderPath path, SignedUrlOptions options, CancellationToken ct = default);
}   // plus IFileProvider's GetFileInfo(string), GetDirectoryContents(string), Watch(string)

public sealed record UpdateFileOptions { UpdateFileMode Mode; string? ContentType; string? IfMatch; bool IfNotExists; }
public sealed record ReadOptions { string? IfMatch; }
public sealed record SeekableReadOptions { int BufferSize = 1 MiB; bool AllowModifications; }
public sealed record SignedUrlOptions { required TimeSpan Validity; SignedUrlAccess Permission = Read;
    string? DownloadFileName; string? ContentType; bool AllowShorterValidity; }
public sealed class SignedUrl { required Uri Url; required string Method; DateTimeOffset? ExpiresOn;
    IReadOnlyDictionary<string,string> Headers; SignedUrlKind Kind; }      // a class with init members, so it can grow
public enum SignedUrlAccess { Read, Create /* only if absent */, Write }      // built as SignedUrlAccess (CA1711)
public enum SignedUrlKind { Presigned, PreAuthenticated, Gateway }          // "Signed" trips CA1720
public class PreconditionFailedException : IOException      public class FileExistsException : IOException
```

- **`WriteableFileProviderExtensions`** holds:
  - the sync forms (`ReadFile`, `UpdateFile`, `Delete`, `Move`, `Copy`, `CreateLocalFileStream`,
    `GetDownloadUrl`);
  - the `IFileInfo` overloads;
  - `UpdateFileAsync(path, contents, UpdateFileMode)`;
  - `GetDownloadUrlAsync(path, validity)`.

  Nothing else: conveniences such as `ReadAllBytes` and a sync `Copy` wait under Later (D23).

  Old call shapes that cannot be kept, such as `UpdateFileAsync(Stream, mode, params string[])`, compile through
  `[Obsolete]` shims that name the new call, for this major only.
- **`WriteableFileProvider<TOptions>`:**
  - Its public members validate and resolve the path, check the root, map errors, and call `protected` PascalCase
    `...CoreAsync` members, following the BCL convention (F22).
  - `GetFileInfoCoreAsync`, `EnumerateCoreAsync`, `ReadFileCoreAsync`, `UpdateFileCoreAsync` and `DeleteCoreAsync` are
    abstract.
  - The rest are virtual with a shared default:
    - `ReadRangeCoreAsync`: read and skip;
    - `OpenReadSeekableCoreAsync`: `RangeReadStream`;
    - `CopyCoreAsync`: read, then write;
    - `MoveCoreAsync`: copy, then delete exactly;
    - `GetSignedUrlCoreAsync` and `WatchCore`: throw `NotSupportedException`.
  - Shared helpers (F53, F74, F4):
    - `ResolvedPath`, the absolute plus the relative path;
    - `AppendByRewriteAsync`;
    - `SpoolToTempFileAsync`;
    - `NotFound(...)`;
    - root checks.
  - Disposal goes through `DisposeAsyncCore` (F52).
  - `TimeProvider` is injectable for expiry maths.
- **`RangeReadStream`:** a read-only seekable stream over a range callback, with read-ahead buffering and version
  pinning.
- **`LocalFileStream`:**
  - `override ValueTask DisposeAsync` (F37), with a `Dispose(bool)` fallback.
  - Only the base class creates it: its constructor is internal to Common (F47).
  - The commit goes through the provider's resolved path, never public segments.
- **Serialization, `WriteableFileProviderSerializer`:**
  - `FromConfiguration(IConfiguration, params Assembly[])`.
  - `FromJson(string | JsonElement | JsonObject, params Assembly[])`.
  - `ToJson(provider) → JsonObject` and `ToJsonString(provider)`.
  - The shape is kept: a `Type` key holding the class name, matched case-insensitively. Options properties are
    PascalCase, read case-insensitively, with both root properties emitted.
  - Lenient readers: numbers into strings, `"true"` into bools, S3's storage class as `"STANDARD"` or
    `{"Value":"STANDARD"}`, written in the shape the old version reads.
  - Resolving `Type` (D23):
    - the assemblies given;
    - a built-in map from each provider class name to its package's assembly, loaded with `Assembly.Load`;
    - types registered with `WriteableFileProviderSerializer.Register<TProvider>()`.

    An ambiguous name throws.
  - 2027 `ToJson` output is not promised to read back on `2026.10.10.x` (round 2, P9); the migration guide says so. A test resolves the Azure type in a fresh `AssemblyLoadContext` without touching it
    first.
  - A golden corpus generated from the old Newtonsoft code pins all of this (F30, F48).
- **DI:**
  - `services.AddWriteableFileProvider(name, IConfiguration)` and `AddWriteableFileProvider(name, factory)` register
    keyed singletons.
  - It lives in Common, on `Microsoft.Extensions.DependencyInjection.Abstractions`.
- **Package references:** `Microsoft.Extensions.Configuration` and Newtonsoft go (F72, D9).

### Providers

| Provider | SDK | What changes besides the contract and the API |
|---|---|---|
| Physical | BCL | Readers share `ReadWrite | Delete` (P20); atomic writes (temp file, then `File.Move` overwrite) (F3); append rewrites through a temp file, so it is atomic; synthesized ETag (D15); local stream on a temp copy (F5); ranges through `RandomAccess`; `Watch` through `PhysicalFilesWatcher` (F54); signed URLs only through the gateway. |
| AzureBlob | Azure.Storage.Blobs 12.30, Azure.Identity 1.21 | Block blobs (D5); `OpenReadAsync` gives a native seekable stream (F64); `AzureFileStream` goes (F15, F70); ranges; service SAS with no start time (F40); user delegation SAS with `UseDefaultAzureCredential`; read and write SAS; ETag conditions; append commits with `IfMatch` (D19); legacy append blobs (P30): a block blob cannot replace an append blob in place, so the sequence is (1) stage the new content to a temp block blob, (2) delete the destination with `IfMatch` on the ETag read, (3) copy the temp blob onto the destination and poll until done, (4) delete the temp blob only after the copy succeeds; on any failure the temp blob stays and the exception names it. The file is absent between (2) and (3), which the XML docs and the migration guide say. Azurite tests cover overwrite and append over a legacy blob, and a failed copy that leaves the content in the temp blob; a public constructor that takes a `BlobContainerClient` (D17). |
| AmazonS3 | AWSSDK.S3 4.x | `ServiceUrl`, `ForcePathStyle` and the default credential chain (additive options); multipart above a threshold (F65); ranges with `EtagToMatch`; presigned GET and PUT; `IfNoneMatch` and `IfMatch`; the public absolute-path members go (F1, F2); never disposes the caller's stream (F41). Shared internal `S3Core`, which also sets the checksum behaviour (`WHEN_REQUIRED`) for a custom `ServiceUrl`, so the tests run the production configuration; presigned URLs honour D18 for expiring credentials; a public constructor that takes an `IAmazonS3` (D17). `ReadFileVersionAsync` (added in S0) stays, on `ProviderPath`. |
| Minio | AWSSDK.S3 4.x via `S3Core` | D6 and D7; `S3Core` builds the client so every stored MinIO configuration keeps working (P23): path-style forced, the scheme from `UseSSL`, and the region discovered once per bucket (`GetBucketLocation`, cached) unless the new `Region` option names it, with a unit test of the client configuration; download URLs now work (F45); Extensions 9 and System.Reactive leave consumers' graphs (F73). |
| GoogleCloudStorage | Google.Cloud.Storage.V1 5.x | `Endpoint` option for fake-gcs-server (F75), and the tests build their client through the public constructor (D23); ranges with generation pinning; V4 signed GET and PUT; move by conditional copy then delete (D16); `IfGenerationMatch`; append through `AppendByRewriteAsync` (F4); credentials created lazily (F13); a public constructor that takes a `StorageClient` (D17). As built in S8: reads, ranged or whole, stream through a raw media GET on the client's own HTTP client (Google's byte-range sample), pinned with `generation` and `ifGenerationMatch`, a 416 read as empty; copies and moves send `ifGenerationMatch=0`; a move deletes with the copied `generation` and `ifGenerationMatch` and, when the source was written meanwhile, undoes the copy and throws `PreconditionFailedException`; a missing bucket is told from a missing object by a one-object list; `CreateBucketIfMissing` needs `ProjectId` or a key that names its project. After review round 1 of #27: the class is `sealed` (D25); `Endpoint`, `AllowInsecureHttp` and `CreateBucketIfMissing` are written to JSON only when set (D26); a plain-`http` `Endpoint` toward a host that is not loopback needs `AllowInsecureHttp` (D7, as MinIO); copies are not retried; downloads send the client's customer-supplied encryption key. |
| SharePoint | Microsoft.Graph 6, Graph.Core 4 | Lazy initialization (F13); `WorkFolderPath` folded into the root (F7); `LargeFileUploadTask`, or a loop with `ReadAtLeastAsync`, with one bounded retry (F8, F39); only `itemNotFound` means not-found (F66); one folder creator (F62); ranges on the download URL, falling back to skipping when the range is ignored; pre-authenticated read URL per D18; certificate and managed-identity sign-in (D17); a public constructor that takes a `GraphServiceClient` (D17). |
| Box | Box.V2.Core 10 (the generated SDK) | Lazy authentication (F13, F32); marker paging (F67); every call resolves the path fresh, as today (no path-to-id cache, D23); range reads pinned to the file version (P15); never rewinds the caller's stream (F6); pre-authenticated read URL per D18; a public constructor that takes a Box client (D17). |

### Signed URLs and the gateway

- **Native, with read and write:**
  - Azure (service SAS, or user delegation SAS when signed in through Entra; writes require
    `x-ms-blob-type: BlockBlob`);
  - S3 and MinIO (presigned GET and PUT);
  - GCS (V4 signed GET and PUT).

  All are capped at 7 days.
- **Create (only if absent):** S3 and MinIO sign an `If-None-Match: *` header, GCS an `x-goog-if-generation-match: 0`
  header, Azure uses SAS permission `c` (to verify on Azurite that it refuses an overwrite), and the gateway uses
  `IfNotExists`.
- **Box and SharePoint:** read only, through their fixed-lifetime pre-authenticated URLs, under D18.
- **Physical:** none natively.
- **`WriteableFileProviders.AspNetCore`:**
  - `AddWriteableFileProviderGateway(options)` and `MapWriteableFileProviderGateway()` serve GET, HEAD and PUT on
    `{BasePath}/{token}/{fileName?}`.
  - `IWriteableFileUrlService.GetSignedUrlAsync(providerName, path, options)` tries the provider's native URL first,
    unless `PreferGateway` is set, and falls back to a gateway URL when the native call throws `NotSupportedException`.
  - **Tokens:** ASP.NET Core Data Protection time-limited payloads, with purpose
    `WriteableFileProviders.Gateway.v1/<provider>/<permission>`.
    - The payload holds the path, permission, content type, file name and a `jti`.
    - A bad token returns 404.
    - The path comes only from the token and is validated again.
  - **GET:** HTTP ranges through `OpenReadSeekableAsync`; `Content-Disposition: attachment`, `nosniff`,
    `no-referrer` and `no-store`.
  - **PUT:** a size cap; optional one-time tokens through an `IGatewayReplayStore.TryConsumeAsync(jti, expiresOn)`
    whose default is an atomic in-memory store. An `IDistributedCache` adapter is offered and documented as best effort,
    since that interface has no add-if-absent.
  - **URLs** are built from a required `PublicBaseUri` option, never from the request's `Host`.
  - **Defaults** follow D22. Tokens travel in the URL path, so the README tells consumers to redact `{token}` from
    request logging.
  - **Headers (P27):** `{fileName?}` is cosmetic and ignored. `Content-Disposition` uses only the token's file name,
    encoded per RFC 6266 through `ContentDispositionHeaderValue` (`filename*=UTF-8''…` with an ASCII fallback), and the
    native response-disposition overrides use the same encoding. A PUT stores the token's content type and refuses a
    request `Content-Type` that differs.
  - **Tests (P13):**
    - a tampered, expired or wrong-purpose token returns 404, including a read token used for PUT and a token for
      provider A used on provider B;
    - a `..` path or a path outside the root inside a validly protected payload is refused;
    - a one-time token is consumed exactly once under concurrent PUTs;
    - a forged `Host` header does not change a generated URL;
    - HTTP ranges and the response headers;
    - an edited file-name segment leaves the header unchanged, and a file name with a quote, `;` or CRLF yields a valid
      header;
    - a PUT over `MaxUploadBytes`, or with a different `Content-Type`, is refused.
- **`GetDownloadUrl`** stays as an extension over `GetSignedUrlAsync`, which validates what it means everywhere (F11,
  F12, F28).

### Tests and coverage

- **The conformance suite:** `tests/WriteableFileProviders.Testing` holds an abstract MSTest class with the contract
  above as test methods. Each provider's test project runs it against:
  - Physical: a temp folder;
  - Azure: Azurite (`Testcontainers.Azurite`);
  - S3 and MinIO: `adobe/s3mock` (`Testcontainers`);
  - GCS: fake-gcs-server (`Testcontainers.FakeGcsServer`);
  - Box and SharePoint: stateful in-memory API fakes behind an `HttpMessageHandler`.

  Unit tests cover what the suite cannot reach.
- **Docker:** without it, emulator tests are inconclusive locally. CI sets `WFP_REQUIRE_DOCKER=1`, which makes them
  fail instead.
- **Live suites:** the same suite against real accounts, run only when `WFP_LIVE=1` and the provider's credentials are
  present. Credentials come from environment variables, user-secrets or an optional `testsettings.json`, so the build
  no longer needs that file. Each live run works under its own `wfp-test-<guid>/` folder and deletes it (F29).
- **Coverage:**
  - Microsoft's `Microsoft.Testing.Extensions.CodeCoverage`, with Cobertura output.
  - `eng/coverage-gate.cs` enforces a floor per assembly from `eng/coverage.json`. Every floor is at least 90% of
    lines when this plan is done (F27).
- **Test-suite fixes:** F28, F29, F56, F57 and F68 are fixed as the suite is rewritten. MSTest 4 runs on
  Microsoft.Testing.Platform (F42).

### CI and release

- **`ci.yml`**, on PRs and `main`, checking out with `fetch-depth: 0` for MinVer:
  - Ubuntu: restore with locked mode, build, test with coverage (Docker available), coverage gate, pack.
  - Windows: Common and Physical tests.
  - `permissions: contents: read`; actions pinned by SHA.
- **Also:** `codeql.yml`, and `dependabot.yml` (nuget grouped, github-actions, dotnet-sdk).
- **`live.yml`:** manual, and nightly on `main`. Runs the live suites only where their secrets are set, never on PRs
  from forks.
- **`release.yml`**, on a `v*` tag, with `fetch-depth: 0`:
  - pack, and push through nuget.org trusted publishing, behind a `nuget` environment;
  - the maintainers configure the trusted publisher and the environment.
- **After merge:** CI becomes a required check in the ruleset, which closes REVIEW.md's "until CI exists" gap. That is
  for the maintainers to switch on.

### Docs

- **Repo docs:**
  - a root README: what it is, packages, a quick start, the provider matrix, links;
  - a README per package: install, configuration keys, auth, example, limits;
  - `docs/migration-2027.md`, the breaking-changes ledger and how to move over;
  - CONTRIBUTING (pointing at AGENTS.md), SECURITY (private vulnerability reporting).
- **Harness docs:** AGENTS.md and REVIEW.md updated for the new layout and commands.
- **API docs:** XML docs on every public member (F12, F58–F61); the package descriptions fixed (F76).

### Alternatives considered

- **A Newtonsoft bridge package:** dropped (D9); the golden corpus pins the stored shape, and System.Text.Json reads it.
- **Multi-targeting net8.0 and net10.0:** dropped (D2); .NET 8 ends a month after this, and it doubles the test matrix.
- **Splitting S4 into API and serializer steps:** kept as one (P34), so Newtonsoft leaves in the step that replaces it.
- **WireMock stubs for Box and Graph:** replaced by stateful in-memory fakes, which can run the whole conformance suite.
- **A hand-rolled HMAC token for the gateway:** Data Protection instead, for key rotation and an encrypted payload.
- **`UseSSL` as `bool?` for MinIO:** dropped in round 2 (D7), because stored configurations always hold `false`.
- **A Box path-to-id cache, a loaded-assembly scan, convenience extensions, an internal GCS seam:** dropped (D23) for a
  smaller surface; each can be added later without a break.

## Compatibility

2027.0.0 is a major release. The ledger, also kept in `docs/migration-2027.md`:

- **Target:** net10.0 only (was net8.0). Consumers on .NET 8 or 9 stay on `2026.10.10.x`.
- **Common API:**
  - `IWriteableFileProvider` is rebuilt on `ProviderPath` and `CancellationToken`.
  - The `string[]`/`params` overloads go; sync forms and `IFileInfo` overloads move to extension methods.
  - `WatchAsync` and `OnLocalFileStreamDisposingAsync` leave the interface.
  - `ToJson()` returns `JsonObject` and leaves the interface.
  - `FileInfo` becomes `WriteableFileInfo`.
  - `LocalFileStream`'s constructor becomes internal, and `DisposeAsync` becomes a `ValueTask` override.
  - The protected members of `WriteableFileProvider<T>` are renamed (`...CoreAsync`), which breaks third-party
    providers. `normalizePath`, `fileSegments` and both `RunTaskSerial` overloads go (`ProviderPath.Parse`,
    `IWriteableFileInfo.Path`, await). `createLocalFileStreamAsync` and the public virtual `CreateLocalFileStreamAsync`
    become `CreateLocalFileStreamCoreAsync`; the public member is no longer virtual. `GetRelativeSegments`, `IsRootPath`
    and `NormalizePem` are `[Obsolete]` and go before 2027.0.0 (S12).
  - Capability hooks whose defaults refuse or weaken an operation until a provider overrides them:
    `SupportsAtomicPreconditions`, `IsCaseInsensitive`, `SupportsSignedUrl`, `SupportsSignedUrlContentHeaders`,
    `MaxSignedUrlValidity`, and the `TimeProvider` property.
  - `DirectoryContents` is sealed and takes `IEnumerable<IFileInfo>`; null entries throw.
  - The `IFileInfo` overloads take only entries a writeable provider returned (`IWriteableFileInfo`); 2026 fell back to
    any entry's `PhysicalPath`.
  - Providers are disposable.
- **Configuration shape:**
  - Kept: the `Type` key and every options property name, including the misspelt `IsAnonymus`.
  - Additions:
    - `CreateContainerIfMissing` (Azure) and `CreateBucketIfMissing` (S3, MinIO, GCS);
    - `ServiceUrl`, `ForcePathStyle` (S3);
    - `UseDefaultAzureCredential` (Azure);
    - `Endpoint` and `AllowInsecureHttp` (GCS).
  - Behavior changes:
    - Azure and MinIO no longer auto-create containers and buckets unless the option is set.
    - MinIO throws without `UseSSL` on a non-loopback host (D7).
    - SharePoint's `WorkFolderPath` is the root (D7).
- **Behaviour:** the contract above. Each change from today is listed per provider in the migration guide.
- **One meaning, per provider (P2):** D15 (`IfMatch` checked but not atomic on Physical and SharePoint upload
  sessions), D18 (service-set URL lifetimes) and D19 (appends) differ by provider, and each says where.
- **Provider APIs (P10):** S3's `EnumerateObjects` and `ReadFileAsync(versionId, …)` are removed in favour of
  `ReadFileVersionAsync` on `ProviderPath`; the providers gain public constructors that take an SDK client (D17).
  SharePoint gains `ClientCertificateBase64`, `ClientCertificatePassword` and `UseDefaultAzureCredential`; MinIO gains
  `AllowInsecureHttp` and `Region` (D7, P23).
- **Stored configuration (P9):** 2027 `ToJson` output is not promised to read back on `2026.10.10.x`.
- **Azure (P22):** files written by 2027 are block blobs. `2026.10.10.x` cannot append to them (`InvalidBlobType`), so a
  rolling upgrade, or a fleet mixing the two lines, must not append to shared files during the switch; the first 2027
  append to an append blob converts it, after which writers using Append Block fail; above the Put Blob limit the
  conversion leaves the file absent for a moment (P30).
- **SDK majors (P24):** consumers who use these SDKs directly move with us: AWSSDK.S3 3 → 4, Box.V2.Core 5 → 10,
  Microsoft.Graph 5 → 6 and Graph.Core 3 → 4, Google.Cloud.Storage.V1 4 → 5. Common no longer brings in
  Microsoft.Extensions.Configuration. Each gets a section in the migration guide.
- **Mixing lines (P21):** every WriteableFileProviders package must be on the same major; a `2026.10.10.x` provider does
  not load against Common 2027. The migration guide and every README say so, and S12 makes the packed dependency on
  Common carry an upper bound (`[2027.0.0, 2028.0.0)`) if MSBuild can express it, else documents why not.
- **Packages:**
  - The new `WriteableFileProviders.AspNetCore`.
  - Newtonsoft.Json and Minio leave the dependency graph.
  - `AzureFileStream` and `UploadResult<T>` become internal (F70).

## Steps

Each step is an issue under #3, a branch `<issue>-<slug>`, and a PR that builds and passes its tests, reviewed with
`review-code` before merge. Issue numbers go in the table as they are created.

| Step | Issue | PR | Contents | Findings | Status |
|------|-------|----|----------|----------|--------|
| S0 Security patch | #4 | #17 | F1, F2: the S3 version read and the listing accept only the root followed by a valid path; `ReadFileVersionAsync(versionId, params string[] pathSegments)` is added, and the old members are `[Obsolete]` and `[EditorBrowsable(Never)]`. F47: the local stream commit checks the provider and the root. F9: the commented test goes. Current line, net8.0. Tests: offline refusals, the concrete-typed binding, F47 cases, and on LocalStack that valid paths still resolve under the root. Release notes for the maintainers are in "For the maintainers". | F1 F2 F47 F9 | done (aa265f5) |
| S1 Record | #5 | #18 | This plan, the review, the roadmap. | — | done (b13ecda); round 2 applied in #21 |
| S2 Build and layout | #6 | #19 | `.slnx`, `src/` and `tests/`, both build-props files, lock files, net10.0, `ProjectReference` to Common, MinVer, unused packages dropped, `WfpLegacy` ratchet, LF renormalization; REVIEW.md, finish-work, `.editorconfig` and `.gitattributes` updated; the `-> warning` analyzer rules become warnings (legacy projects silence them). Pulled forward from S3, because worktrees have no `testsettings.json`: settings optional, the Physical suite on a temp folder, and `test.runsettings` and the publish profile deleted (part of F56). Old tests still run. No copyright line in the packages until D8 settles. | F23 F46 F50 F55 F60 F71 F72, F26 (one Azure.Identity version) | done (7d83e44) |
| S3 Test platform and CI | #7 | #20 | MSTest 4 on Microsoft.Testing.Platform, Testcontainers fixtures, live suites tagged `Live`, the golden corpus of the stored configuration shape from the old code (JSON for six providers, configuration for Azure, S3, MinIO and Physical, Physical built at run time, the `Type` key rules, F78 on both paths), coverage collection and gate, `ci.yml`, the F68 assertion fixes. AGENTS.md's suite rule is unchanged (D21). F28 and F29 move to the conformance suite (S4), F56's remaining dead weight goes with TestBase (S5–S10). | F27 F30 F42 F68 | todo |
| S4 Common 2027 | #8 | | The Common API and contract, `RangeReadStream`, signed URL types, the System.Text.Json serializer, DI, disposal, `LocalFileStream`, helpers, XML docs, the full conformance suite run against an in-memory reference provider in `WriteableFileProviders.Testing`, Common at 90%. Every provider moved mechanically onto the new base, so `main` keeps building: signature renames only, no behaviour change, and the PR lists per provider what verified it; AmazonS3, GCS, Box and SharePoint are "not verified until S7, S8, S9, S10", which run the full conformance suite including root protection and delete scope (P7). The old tests whose behaviour changes are updated and listed in the PR. One PR with two doors, the Common API and the configuration-shape serializer, both named in its title and description; splitting would keep Newtonsoft alive one step longer (P34). Docs (P11): `docs/migration-2027.md` starts here, Common's README, and AGENTS.md (the Testing project, the conformance suite, One-way doors extended to the 2027 Common surface). Shared files (`Directory.Packages.props`, Testing) change here or one step at a time. It also carries one unplanned harness change, e041292: the deep review's refuters probe in a scratch copy, never the checkout under review; its PR names it. | F4 F11 F12 F13 F14 F16 F17 F18 F19 F20 F21 F22 F28 F29 F31 F33 F34 F35 F36 F37 F38 F47 F48 F52 F53 F54 F57 F58 F59 F61 F69 F72 F74 F78 | in review |
| S5 Physical | #9 | | The Physical row. Each provider step wires its own test project to the conformance suite, deletes its old TestBase class, raises its coverage floor, and updates its README, options docs and the migration ledger. | F3 F5 F17–F21 F53 F54 F74 F77 | todo |
| S6 AzureBlob | #10 | | The AzureBlob row, with Azurite. | F3 F11 F15 F17–F21 F26 F40 F44 F53 F64 F70 F74 | todo |
| S7 AmazonS3 and MinIO | #11 | | `S3Core`, the S3 and MinIO rows, with S3Mock. | F1 F2 F4 F10 F11 F17–F21 F24 F28 F38 F41 F45 F51 F53 F65 F73 F74 | todo |
| S8 GoogleCloudStorage | #12 | | The GCS row, with fake-gcs-server. | F4 F11 F13 F17–F21 F28 F38 F41 F44 F53 F56 F63 F74 F75 | built on `12-gcs`, in review |
| S9 SharePoint | #13 | | The SharePoint row, with the Graph fake; its live run is outstanding for the maintainers (D21). | F4 F6 F7 F8 F11 F13 F17–F21 F26 F39 F43 F49 F53 F62 F66 F70 F74 F77 | todo |
| S10 Box | #14 | | The Box row, with the Box fake; its live run is outstanding for the maintainers (D21). | F4 F6 F11 F13 F17–F21 F25 F32 F53 F63 F67 F74 F77 | todo |
| S11 AspNetCore gateway | #15 | | The new package, the URL service, endpoints, the gateway tests and defaults (D22, P13, P27); its README, migration entry and AGENTS.md entry; whether the token purpose string and URL shape are a one-way door (they are: tokens issued by one version must redeem on the next minor; recorded under One-way doors in AGENTS.md). | F11 | in review (branch `15-aspnetcore-gateway`) |
| S12 Docs, CI, launch prep | #16 | | The root README, the migration guide's final pass, CONTRIBUTING, SECURITY, AGENTS.md and REVIEW.md, codeql, dependabot, `live.yml`, the release workflow, every floor at 90% or more, a check that no project sets `WfpLegacy`, the removal of the `[Obsolete]` 2026 helpers in Common (`GetRelativeSegments`, `IsRootPath`, `NormalizePem`, which Box and GCS take over), the PublicAPI files, and a final deep `review-code` of the whole repo. | F12 F76 | part 1 in review (#31, branch `16-repo-infra`: codeql, dependabot, `live.yml`, the release workflow, CONTRIBUTING, SECURITY, the issue forms); the rest todo |

The provider steps S5–S10 are independent once S4 is merged. They may run in parallel agents, each in its own worktree,
touching only their provider's folders, but they merge one at a time (P41): each rebases onto main before its PR and
re-runs the build, the tests and the coverage gate. Shared files (`eng/coverage.json`, the `.slnx`, the old test
project and its lock file, `docs/migration-2027.md`, this plan's trackers and log, and shared package versions such as
Azure.Identity, owned by S6 and consumed by S9) are edited only in the rebased branch. Each provider step also updates
AGENTS.md's Tests section for its provider, its README and the migration guide (P11). Coverage floors rise as each step
lands, not only in S12. The PublicAPI analyzer files are added per package as each step rewrites it, starting with the
2027 surface; seeding them with the 2026 surface would only record removals that the migration ledger already lists.

## Tests

- **Offline (no Docker):** Common, Physical, SharePoint (fake), Box (fake), AspNetCore, and the unit tests of every
  provider.
- **Emulator, through Testcontainers:**
  - AzureBlob on Azurite;
  - AmazonS3 and Minio on S3Mock;
  - GoogleCloudStorage on fake-gcs-server.
- **Live, opt-in:** every cloud provider. They run only when the maintainers have credentials. This session runs none.
- **Every PR:** the full offline and emulator run with `WFP_REQUIRE_DOCKER=1` and no inconclusive emulator test (the
  S3 suites on `AWS_ENDPOINT_URL_S3` excepted until S7 moves them to S3Mock), the coverage gate, and a green `ci.yml`
  run on the head commit, linked from the PR until CI is a required check. Its description says which suites ran.
- **Floors** in `eng/coverage.json` are the whole-point floor of the lower of the CI Ubuntu and a Windows run, minus
  one point for variance, so the gate passes on both (Common measures about 3 points lower on Windows). They were
  re-baselined once from S3's first green CI run. A floor only rises; a PR that lowers coverage adds tests rather than
  editing a floor.

## Docs

Covered under Design, Docs. AGENTS.md changes in S2 (layout, commands), in S3 (tests) and in S12 (final pass).

## Risks

- **The Box v10 and Graph 6 rewrites:** without live accounts, their fakes are only as faithful as the API docs they
  were built from. Accepted. The live suites exist for the maintainers to run before publishing, and the progress log
  says so.
- **S3Mock fidelity:** possible gaps with AWS SDK v4 checksums and presigned URLs, which S3Mock accepts without
  validating. Mitigation: the test client sets `RequestChecksumCalculation = WHEN_REQUIRED` if needed. Signed URL tests
  check the signature parameters, not their validity. Accepted.
- **Azure block-blob append:**
  - The 50,000 committed-block limit is handled by a rewrite.
  - A blob uploaded in one Put Blob has no block list, so its first append rewrites it.
  - Covered by tests on Azurite.
- **Serializer compatibility:** pinned by the golden corpus generated from the old code before the switch.
- **Scope and time:** the work spans many PRs in one long session, and context compaction can lose detail. Mitigation:
  this file is the record, and it is updated after every step.
- **Publishing and the advisory:** the patch and 2027.0.0 packages are published by the maintainers, not this session.
  Until then, `main` holds fixes that no released package has.
- **The review workflow edited the working tree during the audit** (refuter probes, reverted). Fixed in e041292,
  which rides in S4's PR: the refuters probe in a scratch copy, never the checkout under review.

## Later

- **The P3 features:**
  - polling change tokens for the object stores, MinIO listen, Graph delta;
  - `IProgress<long>`;
  - OpenTelemetry and `ILogger`;
  - health checks;
  - trimming and AOT;
  - custom metadata;
  - maximum upload size on signed URLs (POST policies).
- **Sharing links:** Box shared links and Graph `createLink` as an explicit signed URL kind.
- **GCS native move** (`MoveObject`, roadmap P2.11): deferred by D16, because fake-gcs-server cannot emulate it.
- **A Box path-to-id cache**, for latency on deep paths (D23).
- **Convenience extensions** such as `ReadAllBytes`, `WriteAllText` and a sync `Copy` (D23).
- **Scanning loaded assemblies** for provider types (D23).
- **Package validation baselines:** set them to `2027.0.0` after it ships, and keep the PublicAPI files from then on.

## Finding tracker

Status: `todo`, `done` (with the PR), `wontfix` (with the reason). F-numbers are those of
[the review](../reviews/2026-10-10-codebase.md).

| F | Short | Step | Status |
|---|-------|------|--------|
| F1 | S3 version read skips root and hides base overloads | S0, S7 | S0 done (#17); S7 removes the member |
| F2 | S3 EnumerateObjects lists outside the root | S0, S7 | S0 done (#17); S7 removes the member |
| F3 | Failed overwrite truncates on Physical and Azure | S5, S6 | todo |
| F4 | Append-by-rewrite five ways; S3/GCS upload half-appended files | S4, S7, S8 | S4 done (`AppendByRewriteAsync`: spools, then writes with `IfMatch` on the version read, and refuses a file without an ETag); S8 done (GCS appends through it, conditional on the generation); S7 moves S3 onto it |
| F5 | Physical local stream writes in place; Skip does nothing | S5 | todo |
| F6 | Box and SharePoint rewind the caller's stream | S9, S10 | todo |
| F7 | SharePoint strips WorkFolderPath from caller paths | S9 | todo |
| F8 | SharePoint unbounded itemNotFound retry | S9 | todo |
| F9 | Commented MinIO test, leaked key in ado history | S0 | done (#17); key rotation is the maintainers' |
| F10 | MinIO plain HTTP by default | S7 | todo |
| F11 | Download URL lifetime differs per provider | S4, S6–S11 | S4 done (validity rules, read URLs need the file); S8 done (GCS: V4 URLs live the validity rounded down to whole seconds, 1 s to 7 days, else `NotSupportedException`; `ExpiresOn` read from the URL); S11 done (the URL service falls back to the gateway on `NotSupportedException`); every provider gets a URL that lives as asked once S9 and S10 make SharePoint and Box throw per D18; S6, S7, S9, S10 per provider |
| F12 | Download URL docs wrong | S4, S12 | S4 done (interface docs); S12 READMEs |
| F13 | Sync-over-async, no ConfigureAwait, I/O in constructors | S4, S9, S10 | S4 done (ConfigureAwait, one sync helper in Common); S8 done (GCS: no I/O in the constructors; the client, Application Default Credentials and the bucket check on the first call, asynchronously); S9, S10 lazy init |
| F14 | Local stream copies with sync CopyTo | S4 | done (S4): the local stream copies asynchronously |
| F15 | AzureFileStream lacks async reads | S6 | todo |
| F16 | No CancellationToken | S4 | done (S4): every async member takes a token |
| F17 | No ranged reads | S4, S5–S10 | S4 done (API, base default); S8 done (GCS: ranged media GET); S5–S7, S9, S10 native ranges |
| F18 | Missing-file exception differs | S4, S5–S10 | S4 done (the base turns a store's not-found into `FileNotFoundException`; conformance tests); S8 done (GCS, the `GoogleApiException` as inner); S5–S7, S9, S10 per provider |
| F19 | Move semantics differ | S4, S5–S10 | S4 done (the base checks the source and the destination; case-only renames on stores that ignore case); S8 done (GCS copies with `ifGenerationMatch=0`); S5–S7, S9, S10 per provider |
| F20 | GetFileInfo on folders differs | S4, S5–S10 | S4 done (the contract and its conformance tests); S8 done (GCS); S5–S7, S9, S10 per provider. Box still reports a folder as missing; its offline test leaves that unchecked until S10 |
| F21 | PhysicalPath means different things | S4, S5–S10 | S4 done (the contract and its conformance test); S8 done (GCS: null); S5–S7, S9, S10 per provider |
| F22 | Naming rule vs protected camelCase members | S4 | done (S4): the protected members are PascalCase `...CoreAsync` |
| F23 | net8.0 hard-coded, no Nullable | S2 | done (#19) |
| F24 | AWS SDK v3 end of support | S7 | todo |
| F25 | Box.V2.Core 5.8 outdated | S10 | todo |
| F26 | Azure.Identity unused and deprecated | S6 | todo |
| F27 | Coverage not configured or enforced | S3, S12 | todo |
| F28 | Download URL test passes on NotSupported | S4 (suite), S7, S8 | S4 done (suite: `NotSupportedException` is asserted exactly where the fixture says so, never swallowed); S8 done (`GcsOptionsTests` checks `X-Goog-Expires`, `ExpiresOn` and the signed headers offline; `GcsClientConformanceTests` asserts `NotSupportedException` without a signer); S7 offline expiry tests |
| F29 | No cleanup after failed live tests | S4 (suite) | done (S4): each conformance test works under its own root folder, deleted in `[TestCleanup]` whether it passed or failed |
| F30 | Configuration shape not pinned | S3 | todo |
| F31 | LocalFileStream contract untested | S4 | done (S4): `LocalFileStreamTests` and the `LocalStream_*` conformance tests |
| F32 | Box cannot be constructed offline | S4 (internal test constructor over the 5.8 SDK), S10 (public client constructor, lazy auth) | in part |
| F33 | Move/GetDownloadUrl throw AggregateException | S4 | done (S4): every sync form goes through the one helper, `WriteableFileProviderExtensions.Wait` |
| F34 | LocalFileStream leaks temp stream on failed upload | S4 | done (S4): the local stream always disposes its temp file |
| F35 | Additions must not break implementers | S4 (moot in a major; recorded) | done (S4): moot in the major; new members ship with defaults in the base |
| F36 | 45-member interface | S4 | done (S4): one member per operation on `ProviderPath`; the rest are extensions |
| F37 | LocalFileStream.DisposeAsync hides Stream's | S4 | done (S4): `LocalFileStream` overrides `DisposeAsync` |
| F38 | Move deletes a prefix if the key vanished | S4, S7, S8 | S4 done (`DeleteFileCoreAsync`, and the conformance test `Move_RemovesExactlyTheSourceKey`); S8 done (GCS deletes exactly the copied generation); S7 todo |
| F39 | SharePoint chunked upload fragile | S9 | todo |
| F40 | Azure SAS starts now | S6 | todo |
| F41 | S3/GCS dispose the caller's stream | S7, S8 | S8 done (GCS uploads through a forward-only wrapper that never disposes it); S7 todo |
| F42 | MSTest 3.6/VSTest dated | S3 | todo |
| F43 | SharePoint empty folder lists as missing | S9 | todo |
| F44 | Bucket auto-create differs | S6, S7, S8 | S8 done (GCS: `CreateBucketIfMissing`, default false; `InvalidOperationException` otherwise); S6, S7 todo |
| F45 | MinIO download URL unsupported | S7 | todo |
| F46 | .sln, no CPM | S2 | done (#19) |
| F47 | Public LocalFileStream ctor bypasses root | S0, S4 | done: S0 (#17) checked it; S4 makes the constructor internal |
| F48 | JObject in the public API | S4 | done (S4): System.Text.Json; no Newtonsoft in any package |
| F49 | Graph.Core 3.2.6 deprecated pin | S9 | todo |
| F50 | Common's Version 1.1.2 | S2 | done (#19) |
| F51 | MinIO and S3 duplicate the protocol | S7 | todo |
| F52 | Providers not disposable | S4 | done (S4) for the base; S5–S10 dispose their clients |
| F53 | Root guard written seven times | S4 | S4 done (one resolution in the base); S8 done (GCS's own guard is gone); S5–S7, S9, S10 drop their own guards |
| F54 | Seven identical Watch overrides | S4, S5 | S4 done (the seven overrides removed; the base `WatchCore` throws `NotSupportedException`); S5 Physical Watch |
| F55 | UseLocalCommon switch | S2 | done (#19) |
| F56 | Test-suite dead weight | S2 (runsettings, publish profile), S5–S10 (with TestBase) | in part; S8 done for GCS (its TestBase suite, its TestContext stub and its settings keys are gone) |
| F57 | Contract cases missing from the shared suite | S4 | done (S4): the conformance suite |
| F58 | Root and segment exceptions undocumented | S4 | done (S4): the interface documents them |
| F59 | Interface docs copy-paste | S4 | done (S4): the interface docs are rewritten |
| F60 | XML docs only for Common | S2 | done (#19) |
| F61 | Serializer and options undocumented | S4, S12 | S4 done (serializer); S12 options docs |
| F62 | SharePoint folder creation three ways | S9 | todo |
| F63 | Whole-object download before read | S8, S9, S10 (S7 MinIO via S3Core) | S8 done (GCS reads stream); S7, S9, S10 todo |
| F64 | AzureFileStream vs OpenReadAsync | S6 | todo |
| F65 | S3 single PutObject (5 GB) | S7 | todo |
| F66 | SharePoint maps all errors to not-found | S9 | todo |
| F67 | Box offset paging | S10 | todo |
| F68 | Assertion bugs in the shared suite | S3 | todo |
| F69 | FileInfo collides with System.IO.FileInfo | S4 | done (S4): `WriteableFileInfo` |
| F70 | UploadResult and AzureFileStream public | S6, S9 | todo |
| F71 | net10-only drops .NET 8 consumers | S2 (D2) | done (#19) |
| F72 | Unused packages | S2, S4 | S2 part done (#19); S4 done (Newtonsoft removed from every project) |
| F73 | Minio forces Extensions 9 and Reactive | S7 | todo |
| F74 | Temp-file spool written nine times | S4 | S4 done (`SpoolToTempFileAsync`, `AppendByRewriteAsync`); S8 done (GCS spools only through `AppendByRewriteAsync`); S5–S7, S9, S10 use them |
| F75 | GCS has no emulator seam | S8 | done (S8): the `Endpoint` option, the `StorageClient` constructor, and the conformance suite on fake-gcs-server |
| F76 | Package descriptions and READMEs | S12 | todo |
| F77 | Dead code in providers | S5, S9, S10 | todo |
| F78 | S3 `StorageClassForNewFiles` is written as `{"Value":…}` and read back as `STANDARD`, and the configuration path never reads it at all (found in S3 by the golden corpus) | S4, S7 | done (S4): both forms read, the string is written, configuration converts it |
| F79 | On Unix, Physical's `Options.RootDirectoryPath`, and so its `ToJson` output, carries the internal `root:` marker of an absolute path instead of the path (found by S3's first Linux CI run) | S5 | todo |

## Feature tracker

| Feature (roadmap) | Step | Status |
|---|---|---|
| P1.1 Index-based reads: ranges and seekable streams, all seven | S4, S5–S10 | S4 done (API, defaults, conformance); S8 done (GCS); S5–S7, S9, S10 native |
| P1.2 Signed URLs, read and write, all seven, and the gateway | S4, S6–S11 | S4 done (API, validity rules); S8 done (GCS V4: read, create, write); S11 done (the gateway and the URL service: read, create and write URLs for every provider); S6, S7, S9, S10 native |
| P1.3 Cancellation everywhere | S4 | done (S4) |
| P1.4 Dependencies current | S2, S6–S10 | S8 done (Google.Cloud.Storage.V1 5.0.0); S6, S7, S9, S10 todo |
| P2.5 Streaming writes without temp-file round trips where the SDK can | S5–S8 | S8 done (GCS uploads stream from the caller's stream); S5–S7 todo |
| P2.6 ETags and conditional writes | S4, S5–S10 | S4 done (API, non-atomic default); S8 done (GCS: the generation, atomic); S5–S7, S9, S10 native |
| P2.7 Content type on write and read | S4, S5–S10 | S4 done (API, hint); S8 done (GCS); S5–S7, S9, S10 |
| P2.8 Async listing, recursive | S4, S5–S10 | S4 done (API, default walk); S8 done (GCS: paged prefix listing, the base's walk); S5–S7, S9, S10 |
| P2.9 Dependency injection | S4, S11 | S4 done (registration); S11 done (`AddWriteableFileProviderGateway`, `MapWriteableFileProviderGateway`, keyed providers resolved by name) |
| P2.10 More credential options | S6, S7, S8 | S8 done (GCS: a given `StorageClient`, the `Endpoint`, lazy Application Default Credentials); S6, S7 todo |
| P2.11 Server-side copy, GCS native move | S4, S5–S10 | S4 done (API, default); S8 done (GCS copies by rewrite; native move stays under Later, D16); S5–S7, S9, S10 native |
| P2.12 Physical Watch | S5 | todo |

## Progress log

Newest last. Each entry: date, what was done (PRs), what is next, anything a later session must know.

- **2026-10-10, session 1:**
  - Done:
    - The harness was merged (#1).
    - The deep review ran: 77 findings, every one confirmed by its refuter.
    - Research went into the features and into current .NET practice.
    - The roadmap was written.
    - The maintainer accepted D1–D8 and added D9–D14.
    - The local `ado` remote is removed.
  - Next: S0, then S1.
  - Raw research is in `.local/review/codebase/` (git-ignored; this machine only).
- **2026-10-10, session 1, continued:**
  - S0 merged as #17 (aa265f5) after one `review-code` round.
    - Ran: offline, Azurite and S3Mock.
    - Could not run: the S3 suites. LocalStack 4.0.3, the last image without an auth token, runs here, but its
      virtual-host bucket names (`wfp-test.<host>`) do not resolve on this network, and the provider has no
      path-style option until S7. `AbsolutePathMembers_ResolveUnderTheRootOnly` runs in S7 against S3Mock.
  - The branch `release/2026.10` starts at aa265f5, for the maintainers' patch release.
  - Issues #4–#16 exist for S0–S12.
  - The plan review (round 1) was applied; the maintainer chose the recommended answers (D7, D15–D19).
  - S2 (#6) and S3 (#7) are committed on their branches (worktrees `D:\workspaces\wfp-wt\6` and `7`), waiting to
    rebase onto main.
    - S3's baseline coverage (lines, without the S3 suites): Common 78.7%, Physical 92.6%, Minio 92.8%, AzureBlob
      78.0%, GCS 25.1%, SharePoint 20.8%, Box 8.8%, AmazonS3 6.2%.
    - S3 found F78.
  - Running long test suites in the foreground stalls the session (the maintainer stopped a 6-minute run). Run them in
    the background.
  - S2 merged as #19 (7d83e44) after one review round. Plan review round 2 (deep) was applied in #21 (936a6ef). D15–D24
    are posted on #3.
  - S3 (#20):
    - Its first CI run found F79 (Physical's `root:` marker in the options on Unix), now pinned.
    - CI's Windows job runs every non-Live test without Docker, so the no-Docker path is exercised on every run.
    - The coverage floors are re-baselined from the first green CI run (run 38089480611) and a Windows run: Common
      82/80, Physical 92/80, Minio 92/70, AzureBlob 78/64, GCS 24/14, SharePoint 20/15, AmazonS3 16/14, Box 8/0
      (lines/branches).

- **2026-10-11, S4 (#8):** Common 2027, built in a forked session on branch `8-common-2027`.
  - **Common:**
    - The API of the plan's sketch, the contract, the System.Text.Json serializer, DI.
    - Builds strict (nullable, warnings as errors, code style).
    - 96% of lines and 92% of branches, from `WriteableFileProviders.Common.Tests`.
  - **Tests:**
    - `WriteableFileProviders.Testing` holds `ProviderConformanceTests`, one test per contract rule, which passes
      against `InMemoryFileProvider`, and covers every case D20–D24 asked for.
    - The golden corpus reads identically (`Config.*` through `FromConfiguration` too), and F78 is fixed on both paths.
  - **Deviations from the sketch, each forced by an analyzer:** `SignedUrlPermission` became `SignedUrlAccess`
    (CA1711), and `SignedUrlKind.Signed` became `Presigned` (CA1720). CA1716 is suppressed on the two members whose
    public `readOnly` parameter predates it (`GlobalSuppressions.cs` in Common), nowhere else.
  - **ProviderPath** is built through its constructor, `Parse` and the implicit conversions. `FromString` and
    `FromSegments` were dropped in review: CA2225 is not on, so no analyzer asks for named alternates.
  - **Hooks added beyond the sketch:**
    - `FolderExistsCoreAsync`: an empty listing asks whether the folder exists, so Physical keeps empty folders.
    - `DeleteFileCoreAsync`: move deletes exactly one key.
    - `SupportsAtomicPreconditions`: the base checks `IfMatch` and `IfNotExists` non-atomically unless the provider
      does.
    - `IsCaseInsensitive`, `IsNotFound`, `MaxSignedUrlValidity`, `SupportsSignedUrl`.
    - `SupportsSignedUrlContentHeaders`: a signed URL request with `DownloadFileName` or `ContentType` throws
      `NotSupportedException` unless the provider honours them (each provider turns it on in its step).
  - **Providers:** moved by renames only. The base's contract now applies to them:
    - a missing or folder source, or an existing destination, on move and copy;
    - the root is a folder;
    - read-only local streams of missing files;
    - signed URL validity.

    Physical's local stream is now the base's temp copy: its in-place override needed `LocalFileStream`'s public
    constructor, which is gone.
  - **Old tests changed for the new contract:**
    - `LocalFileStreamCommitTests` is deleted; a hand-built stream no longer exists. `Common.Tests` covers the commit.
    - `AmazonS3PathTests`: `ParamName` is `path`.
    - `SegmentValidationTests`: the root is a folder; the `(bool, string)` local stream shim parses the path.
    - `AzureBlobProviderTests`: the root lists as existing; a read URL needs the file; the SAS checks go through a
      probe.
    - `MinioProviderTests`: the root lists as existing.
    - `SharePointPathTests`: moving the work folder is refused as a folder source.
  - **Coverage floors:** raised to the measured figures, lines/branches: Common 95/90, Physical 98/86, AzureBlob
    80/65, Minio 93/70, AmazonS3 23/13, GCS 27/14, SharePoint 20/15, Box 72/61. Box's floor comes from
    `BoxOfflineTests`: an internal constructor takes a ready `BoxClient`, built over a fake `IRequestHandler` that
    answers the Box API from memory (the start of S10's fake).
  - **Review round 1** (`.local/review/8/r1/report.md`), all 26 findings fixed:
    - Box: the offline tests above instead of a lowered floor (R1-F1).
    - Signed URLs: the content-header hook (F2); the in-memory provider honours both options, and a fixed-lifetime
      fixture checks D18 (F7); GCS without a signer throws `NotSupportedException` before signing (F10).
    - Case-only renames: Physical (Windows, macOS), Box and SharePoint set `IsCaseInsensitive`, which restores the 2026
      rename (F3); the default `MoveCoreAsync` renames through a temporary name (F12); a case-insensitive in-memory
      fixture and a Physical test pin it (F4).
    - `RangeReadStream` never serves a buffer a failed fetch half wrote (F5).
    - Conformance: a move removes exactly the source key (F6); a read-only local stream of a folder throws
      `FileNotFoundException` (F21).
    - A file without an ETag refuses a pinned seekable read and an append by rewrite (F13, F14).
    - A built-in provider type resolves in a fresh `AssemblyLoadContext` (F8); `ResolveProviderType` is internal (F17).
    - Every sync site uses the one helper (F9). The temp file of a failed commit is checked deleted (F20).
    - Docs: the migration ledger (F15, F16, F24), the tracker and this row (F23), the GCS cref (F26), the test packages'
      group (F25), the harness commit (F11). The 2026 path helpers are `[Obsolete]`, removed in S12 (F18).
    - Each new test was checked to fail with its fix reverted.
  - **Verified by:**
    - Physical, MinIO (S3Mock) and Azure (Azurite) ran their suites with `WFP_REQUIRE_DOCKER=1`.
    - AmazonS3, GCS, Box and SharePoint are not verified until S7, S8, S9 and S10: their suites need an endpoint or
      an account. Only their offline tests ran.

- **2026-10-11, S4 review:**
  - Deep review round 1 of #23: 1 blocker, 5 majors, 18 minors, 2 nits, all fixed (f9aa55f, 0e2299b, 355b4ef).
  - Round 2 verified them and found three minors (a failed case-only rename could hide the file, the move docs, and
    the S10 handoff notes), fixed in the next commit.
  - Carried to later steps: a test that GCS without a signer throws `NotSupportedException` lands with S8's seam
    (round-1 F10). `BoxOfflineTests` asserts today's Box URL behaviour, which S10 replaces with D18's.
  - Main-session decisions for S5–S10 are in `.local/briefs/decisions-s5-s10.md`; each step records the ones it
    applies.

- **2026-10-11, S11 (#15):** `WriteableFileProviders.AspNetCore`, built in a forked session on branch
  `15-aspnetcore-gateway`.
  - **The package:** builds strict, XML docs on every public member, README packed. Public surface:
    `AddWriteableFileProviderGateway(Action<WriteableFileProviderGatewayOptions>)`, `MapWriteableFileProviderGateway()`
    (returns the endpoints' convention builder), `IWriteableFileUrlService`, `IGatewayReplayStore`,
    `DistributedCacheGatewayReplayStore`, `WriteableFileProviderGatewayOptions`. Common is unchanged.
  - **Tokens:** `{r|c|w}.{base64url provider name}.{payload}`, the payload protected by a time-limited Data Protection
    protector with the purpose chain `WriteableFileProviders.Gateway.v1`, the provider name, the access. The plan wrote
    the purpose as one slash-joined string; a chain isolates the same way without parsing a provider name that holds a
    `/`. The access and the provider name travel in the clear only to pick the protector: changing either changes the
    purpose, so the payload no longer unprotects. The payload holds the path segments, the access, the content type, the
    file name and a `jti`. The one-way door (format, purpose chain, URL shape) is recorded in AGENTS.md.
  - **Choices beyond the plan's text**, each the fail-closed or recommended option, for the maintainers' review:
    - Options: `MaxReadValidity` (7 days) and `MaxUploadValidity` (1 hour), both capped at 7 days, Read included, like
      the native URLs; `OneTimeUploadTokens` (true); `MaxUploadBytes` (100 MiB); `PreferGateway` (false, global);
      `BasePath` (`/files`); and `AllowInsecureHttp` (false), the D7 rule for MinIO applied to `PublicBaseUri`: plain
      `http` only on a loopback host unless set. A validity over the maximum throws `NotSupportedException`, or with
      `AllowShorterValidity` gets the maximum and its true `ExpiresOn`, as D18 has the providers do.
    - Every token problem answers 404, a replayed one-time token included. 412 for a Create that finds the file, 413
      over `MaxUploadBytes` (a declared `Content-Length` before the token is used; a chunked body cut off as read), 415
      for a `Content-Type` that differs from the token's (exact, case-insensitive). The token's content type, or else
      the request's, goes to the provider as the `UpdateFileOptions.ContentType` hint: stored where the store keeps
      one, ignored elsewhere (D24). The one-time token is used up after the request's checks and before the body is
      read, so concurrent `PUT`s cannot both write; a failed upload needs a new URL.
    - A download is always `attachment`; the file name defaults to the path's name. Since review round 1 it is served
      as the read token's content type or `application/octet-stream` (D22), and read with one `ReadRangeAsync` per
      contiguous span, pinned with `IfMatch` on the `ETag` read first; a file without an `ETag` is served whole,
      without ranges or an `ETag`.
    - The endpoints `AllowAnonymous` (a fallback authorization policy would break every URL), are excluded from output
      caching (a cached response would outlive its token) and from API descriptions. An `IHttpLoggingInterceptor`
      replaces the request path of gateway requests in `UseHttpLogging`; the README lists the other logs to redact
      (hosting diagnostics, the `RequestPath` log scope, OpenTelemetry `url.path`, proxies).
    - `DistributedCacheGatewayReplayStore` checks an in-memory set first, so it is exact within one instance and best
      effort across instances.
  - **Tests:** `WriteableFileProviders.AspNetCore.Tests`, 168 tests on TestServer over `InMemoryFileProvider` and a
    delegating provider without native URLs: every case of the gateway's Tests bullet, plus the URL service's choice,
    DI, options validation and start-up failure, and the HTTP logging, output caching and fallback authorization
    pipelines. Key cases were checked to fail with their code reverted (the codec's access check, an atomic replay
    store). The package measured 99.7% of lines and 97.9% of branches (Windows; Linux CI 100/97.9).
  - **Verified by:** the solution builds (warnings only in the `WfpLegacy` projects, as on `main`); every non-Live test
    with `WFP_REQUIRE_DOCKER=1` passes, the 30 `AWS_ENDPOINT_URL_S3` suites inconclusive as before S7; the coverage gate
    passes; `dotnet pack` puts the README in the package.
  - **Not done here:** PublicAPI files (no package has them yet; S12). A live check behind a real proxy and Kestrel's
    own body limit (TestServer has none; a test installs the feature and checks the gateway raises it).

- **2026-10-11, S11 review round 1 (#24):** the deep review found 10 minors and 1 nit, no blocker or major
  (`.local/review/15/r1/report.md`). All fixed in one commit:
  - **F1** (native upload URLs differ from gateway ones): the README's "Which URL you get", the
    `IWriteableFileUrlService` XML docs, the migration guide and D22 say the gateway's rules bind gateway URLs only,
    and that `PreferGateway` gives one behaviour.
  - **F2** (the uploader's content type served on the app's origin), the maintainer's call, fail closed and harden: a
    download is served only as its read token's type, else `application/octet-stream`; every gateway response carries
    `Content-Security-Policy: default-src 'none'; sandbox`; the README (a new "Serve it from a host of its own"
    section, and a quick start on `files.example.com`) and the `PublicBaseUri` XML docs recommend a cookieless host of
    its own; the README says an uploader's type is stored but never served back unless a read token names it. Tests:
    a script uploaded as `text/javascript` is served as `application/octet-stream` under the CSP; a stored type is
    never served; the CSP on downloads, uploads and 404s.
  - **F3** (the new one-way door had no golden test): the purpose chain and the payload's access are spelled with
    literals (`Read`, `Create`, `Write`), no longer through the enum's member names. `GatewayTokenGoldenTests` pins the
    door: three tokens issued by the codec with a test-only Data Protection key (kept in the test, unencrypted, with
    its expiry moved to 2126) redeem through the codec and the gateway; tokens built from literal JSON and the literal
    purpose chain redeem; the codec writes exactly the literal JSON. Each of the review's three format breaks (a renamed
    JSON key, the enum converter dropped, the access lowercased in the chain) fails all six tests.
  - **F4**: `HttpLogging_UnderAPathBase_DoesNotLogTheToken`, with HTTP logging before and after `UsePathBase`; the
    logging-first row fails if the interceptor checks only `Request.Path`.
  - **F5**: the F11 tracker row now says Box and SharePoint join the gateway fallback in S9 and S10 (D18).
  - **F6**: the floor follows the Tests "Floors" rule: the whole-point floor of the lower of the CI Ubuntu and a
    Windows run, minus one point for variance. Measured after this round: Linux CI 100/98.63, Windows 100/98.97, so the
    floor rises from 98/96 to 99/97.
  - **F7** (each download fetched 1 MiB at a time): the gateway reads the file's info once, then serves through
    `PinnedRangeStream`, which opens one `ReadRangeAsync` with `IfMatch` on that `ETag` from where the response starts
    and keeps reading it while reads stay contiguous; a single HTTP range opens just that span. Measured with a
    counting provider: a whole 3 MiB download is one store read (the 1 MiB seekable stream made one per MiB, so a
    100 MiB file was a hundred sequential reads), a range is one read of exactly its span, and `HEAD` and 304 read
    nothing.
  - **F8**: the README and this log say the content type is a hint stored where the provider keeps one (D24).
  - **F9**: the README quick start issues URLs from an authorized endpoint that checks the caller may read the report.
  - **F10**: the test provider wraps every stream it returns and counts the open ones; the read tests assert each is
    disposed when the response ends (whole file, ranges, `HEAD`, 304, a stale `If-Range`, a file that changed). Leaving
    out `PinnedRangeStream`'s dispose fails six of them.
  - **F11**: the test provider lost its unused `NativeUrls` switch and `AllowModifications` branch.
  - Tests: 204 in `WriteableFileProviders.AspNetCore.Tests`.

- **2026-10-11, S11 review round 2 (#24):** round 2 verified F1–F11 and found N1–N6, all fixed in one commit:
  - **N1**: the Tests "Floors" bullet now states the minus-one margin the floors use, and the F6 line cites it. The
    AspNetCore floor stays 99/97 (after this round: Linux CI 100/98.63 at 61dcfb8, Windows 100/99.01).
  - **N2**: a download whose file is deleted between its info and its first read answered 500. The download result is
    wrapped (`NotFoundIfGoneResult`): a `FileNotFoundException` before the response has started clears it, re-applies
    the security headers and answers 404. `Get_FileGoneBeforeItIsRead_Answers404` runs on both paths, pinned and not;
    the pinned row fails without the wrapper.
  - **N3**: the range hint is null when an `If-Range` does not match (an entity tag compared strongly, or a date
    earlier than the file's last change, as ASP.NET Core judges it): a stale `If-Range` gets the whole file, which is
    now one read, where a range starting at byte 0 used to open its short span first and then the rest.
  - **N4**: the README quick start registers authentication (cookies), authorization and its `ReportAccess` example,
    with its usings; it was compiled with warnings as errors and started, and its link endpoint answers 302 to sign-in.
  - **N5**: a file without an `ETag` is read lazily, in one `ReadRangeAsync` from the start (`PinnedRangeStream` with
    no pin), with `Content-Length` from its info: a `HEAD` reads nothing on either path.
  - **N6**: a double blank line in `GatewayReadTests`.
  - D25 and D26 were added to Decisions (decided 2026-10-11 under the maintainer's standing instruction).
  - Tests: 214 in `WriteableFileProviders.AspNetCore.Tests`.

- **2026-10-11, S8 GoogleCloudStorage (#12):** built on branch `12-gcs` (worktree `D:\workspaces\wfp-wt\12`).
  - **Provider:** rewritten on the 2027 base, strict (no `WfpLegacy`), on Google.Cloud.Storage.V1 5.0.0. The
    main-session decisions for S8 are applied:
    - fake-gcs-server enforces generation preconditions only on uploads, so the provider also sends the generation
      selectors it honours (a pinned read names `generation`, a move deletes with it) and relies on the base class's
      destination check before a copy or move;
    - reads, whole or ranged, go through a raw media GET on the client's own `HttpClient` (Google's byte-range sample),
      and a 416 reads as empty;
    - a missing bucket is told from a missing object by a one-object list; listings read the raw pages for the
      prefixes;
    - append through `AppendByRewriteAsync`, no compose; credentials lazy and async; the `Endpoint` option and the
      `StorageClient` constructor;
    - no content type given stores `application/octet-stream`.
  - **Beyond the row, and deviations:**
    - The service account key is parsed in the constructor (local work, no I/O) so a bad key fails as an options error
      (`ArgumentException`); the client, Application Default Credentials and the bucket check are built on the first
      call. A failed first call is tried again by the next.
    - `CreateBucketIfMissing` (F44, listed under Compatibility) is in: it needs `ProjectId` or a key that names its
      project.
    - A move whose source delete finds the source written meanwhile undoes the copy and throws
      `PreconditionFailedException`, as S6 decided for Azure; a source deleted meanwhile completes the move.
    - Downloads are not checked against the object's hash (the SDK's whole-object check cannot cover a range); TLS
      covers the transfer. Uploads keep the SDK 5.0 CRC32C check.
    - A V4 URL's validity is rounded down to whole seconds; under one second throws `NotSupportedException`, as over
      seven days does. A download name on an upload URL throws `NotSupportedException`. `ExpiresOn` is read from the
      URL. A create URL signs `x-goog-if-generation-match: 0`; an upload URL signs `Content-Type` only when given.
    - Listings leave out objects whose names no path can address (`a//b`, `x/../y`, a backslash).
    - The SDK's `UpdatedDateTimeOffset` throws on RFC 3339 times with other than 0, 3, 6 or 9 fractional digits, which
      fake-gcs-server sends; the provider parses `UpdatedRaw` itself.
  - **Tests:** `tests/WriteableFileProviders.GoogleCloudStorage.Tests`, on fake-gcs-server 1.56.1 through
    Testcontainers.FakeGcsServer 4.16.0 (added to `Directory.Packages.props`): the conformance suite through the options
    (a key generated per run, the `Endpoint`; the client signs its own tokens, so no Google call) and over a given
    unauthenticated client (signed URLs `NotSupportedException`: the round-1 F10 test of #23); `GcsProviderTests` for the
    missing bucket on every member, bucket creation, Application Default Credentials (the one test that resolves them),
    signed URLs used against the emulator, and races and server answers injected through the client's HTTP seam
    (`FaultInjection`, an `HttpClientFactory`); offline `GcsOptionsTests` (options, keys, endpoint forms, signed URL
    shape and expiry, F28). **No capability flag is set:** with the selectors and the base class's checks every
    conformance rule holds on the emulator. What only Cloud Storage enforces, `ifGenerationMatch=0` on a copy racing a
    writer, a signed create URL refusing an overwrite, and URL signatures and expiry, is in `GcsLiveAtomicityTests`,
    with `GcsLiveConformanceTests`; both need `WFP_LIVE=1` and `WFP_GCS_*` (or a `testsettings.json` next to the test
    assembly) and never ran here. The old `GoogleCloudStorageFileProvider` TestBase suite and `GcsOptionsTests` left
    `WriteableFileProviders.Tests` (their tests moved), with GCS's settings keys and project reference.
  - **Coverage:** GCS 99.6% lines, 95.8% branches locally (Windows); floor raised to 98/94.
  - **Not done here:** no PublicAPI files, as S4 added none (S12).
  - **Review round 1 (PR #27, standard): 8 minors and 4 nits, all fixed:**
    - F1: `GcsProviderTests.Requests_CarryTheGenerationPreconditions` checks, through the HTTP seam's request log, that
      each condition the emulator ignores is sent: `ifGenerationMatch` on uploads (the version, or 0), `generation` and
      `ifGenerationMatch` on a pinned read, `ifGenerationMatch=0` on a copy, and on a move `sourceGeneration`,
      `ifSourceGenerationMatch` and the delete's `generation` and `ifGenerationMatch`.
    - F2: both fixtures' `LargeFileSize` is 22 MiB, so the failed large write sends a whole 10 MiB chunk before it fails;
      `Write_AFailureAfterAWholeChunkKeepsTheOldContent` checks that a chunk went out.
    - F3: the raw download applies the client's customer-supplied encryption key (`EncryptionKey.ModifyRequest`).
    - F4: a 403 on the one-object list means "cannot tell", so a missing file under a credential that may get but not
      list objects stays `FileNotFoundException`.
    - F5: the conditional rewrite runs with `RetryOptions.Never`: a retry after a lost answer would find its own copy
      and report `FileExistsException`. Tested by answering the first rewrite 503 after the copy lands.
    - F6 (owner: yes): `AllowInsecureHttp`, new, written only when set; a plain-`http` `Endpoint` toward a host that is
      not loopback throws `ArgumentException` without it.
    - F7 (owner: keep the type), **a Common change:** `AppendByRewriteAsync` and the default local-stream commit keep the
      file's content type when the caller gives none (`UpdateFileOptions.ContentType` docs say so). Pinned by
      `BaseClassTests.AppendAndLocalStream_KeepTheFilesContentType` and the new conformance test
      `FileInfo_AppendAndLocalStreamKeepTheContentType` (where `KeepsContentType`); it settles S7's note that a local
      stream commit stored `application/octet-stream`. A provider with a native append keeps the type itself.
    - F8: `CreateBucketIfMissing` documents Cloud Storage's defaults (the `US` multi-region, Standard class).
    - F9: a version must be a positive generation, so `IfMatch = "0"` never means "create only".
    - F10: AGENTS.md gives the live filter as `TestCategory=Live&FullyQualifiedName~<Provider>`.
    - F11: the floor is the brief's rule, the whole-point measured figure minus 1: after this round GCS measures 99.6% of
      lines and 95.3% of branches on Windows, so the floor stays 98/94.
    - F12: the README says a create or write signed URL is issued for a missing bucket and fails when used.
    - D25 and D26 (main session): the provider is `sealed`, and the 2027 options carry
      `[JsonIgnore(Condition = WhenWritingDefault)]`, so `ConfigurationShapeTests` is back to its exact golden check.
  - **Review round 2 (PR #27):** F1–F12, D25 and D26 verified; 2 minors and 2 nits fixed, docs only:
    - N1 (owner: document): a copy or move that fails with a server error or a timeout may have landed its copy; the
      class docs, the README and the migration guide say to check the destination before retrying, and no longer claim
      more.
    - N2: the README's 403 sentence covers reads only, and a "Permissions" item says the provider needs
      `storage.objects.list` (without it, `GetFileInfoAsync` of a non-file, listings, copies, appends to a new file and
      folder deletes get a 403); the migration guide notes that `GetFileInfoAsync` now lists.
    - N4: no plan ids in the public XML docs.
    - Rebased onto S11 (bce935c): the plan's trackers carry both steps' notes.

- **2026-10-11, S12 part 1 (#16):** the repository infrastructure, on branch `16-repo-infra`.
  - **Workflows**, every action pinned by SHA:
    - `codeql.yml`: C# in build mode none (no source generators, one package source, one target framework, so
      GitHub's caveats for it do not apply), with `security-extended`; on PRs, `main` and weekly. It also scans the
      workflows (language `actions`), beyond the plan.
    - `live.yml`: by hand (a box per provider) or nightly on `main`, never on a PR. A first job in the `live-cloud`
      environment picks the providers whose secrets are set (it sees only true or false) and whose live suite has its
      own test project; the nightly run skips the others with a warning, a manual run fails on them. Each provider's
      job gets only its own secrets, as the `WFP_*` variables, with `WFP_LIVE=1`, and runs
      `dotnet test --project tests/WriteableFileProviders.<Provider>.Tests --filter "TestCategory=Live"`. The results
      stay in the job's log, as in CI; review round 1 below adds the masks and the minimum test count.
    - `release.yml`, on a `v*` tag: the tag must be on `main`; locked restore, build and pack with
      `ContinuousIntegrationBuild`, the non-Live tests with `WFP_REQUIRE_DOCKER=1`; `eng/package-check.cs` (new)
      checks a `.nupkg` and `.snupkg` per project under `src/`, each at the tag's version, and every dependency on one
      of our packages at that version as its lowest. Then, behind the `nuget` environment, `NuGet/login` and
      `dotnet nuget push` with the symbols; then the GitHub release with the packages, a prerelease for a `-` tag.
      Beyond the plan: the package check and the GitHub release. No coverage gate here: CI enforced it on `main`.
  - **`dependabot.yml`:** NuGet weekly, grouped as azure, aws, google, box, msgraph, test, microsoft-extensions and
    build; the provider SDK groups take minor and patch only, so an SDK major comes on its own PR (D17). Majors of
    Microsoft.Extensions and ASP.NET Core packages, and of the SDK in `global.json`, are ignored: they move with the
    target framework. A 7-day cooldown on every ecosystem. Dependabot can miss the lock file of a project that gets a
    package only through a `ProjectReference` (dependabot/dependabot-core#13950), which fails CI's locked restore with
    NU1004; the file says how to fix such a PR.
  - **Docs:** CONTRIBUTING (the short path through AGENTS.md; outside PRs run only in CI), SECURITY (private
    vulnerability reporting; 2027.x supported, 2026.10.10.x for security fixes until a date the maintainers set), bug
    and feature forms with blank issues off and security pointed to SECURITY.md, a line on CI in the PR template, and
    AGENTS.md's rows and a Release bullet.
  - **Live variables:** the live classes on `main` read only `testsettings.json`, so `live.yml` maps the names the
    provider steps S5–S10 read on their branches (#9–#14). To align as they merge: Box reads a file path
    (`WFP_BOX_CONFIG_FILE`), so the workflow writes the `WFP_BOX_CONFIG_JSON` secret to a temp file and deletes it;
    Google Cloud Storage gets `WFP_GCS_CLIENT_EMAIL` and `WFP_GCS_PRIVATE_KEY`, and `WFP_GCS_CREDENTIALS_JSON` is not
    mapped (review round 1); `WFP_AZURE_USE_DEFAULT_CREDENTIAL` and `WFP_SHAREPOINT_USE_DEFAULT_CREDENTIAL` are not
    mapped, since the workflow has no Azure sign-in; Physical's live suite (`WFP_PHYSICAL_ROOT`) is a folder on a
    maintainer's machine and is not in the workflow.
  - **Verified:** actionlint 1.7.12 with shellcheck finds nothing in the four workflows; every YAML file parses; the
    provider selection ran in bash against simulated inputs; the package check passes on a local pack at its version and
    fails on another version, a missing symbol package, a missing package and an exclusive lowest version. Not
    verified until GitHub runs them: CodeQL, Dependabot's reading of its file, the environments, trusted publishing
    and a release.
  - **Next, S12 part 2:** the rest of the S12 row, and REVIEW.md's "until CI exists".

- **2026-10-11, S12 part 1 review round 1 (#31):** needs-fixes; rebased onto `main` after S11 (bce935c) and S8
  (e1c4b52), so the package check now covers nine packages, `WriteableFileProviders.AspNetCore` included, and
  `live.yml` finds its first provider project, Google Cloud Storage's, whose variable names match. Fixed:
  - **F1** (a part of a secret printed alone is not masked): before the Box suite runs, the step masks the client
    secret, the passphrase and every line of the private key, read from the one `WFP_BOX_CONFIG_JSON` secret with
    `jq`. Google Cloud Storage takes the key's `client_email` and `private_key` as two secrets, never the whole key
    file, and the step masks every line of the key, with `\n` escapes or real line breaks. The workflow's header and
    the checklist below say what each secret holds.
  - **F2**: the dependency graph does not read versions kept in `Directory.Packages.props`; the checklist and
    `dependabot.yml` ask for the dependency graph and Automatic dependency submission, and NuGetAudit stays the guard
    at restore time.
  - **F3**: SECURITY.md shows no placeholder: 2026.10.10.x gets security fixes until a date the maintainers will
    announce there.
  - **F4**: every `actions/checkout`, `ci.yml`'s included, sets `persist-credentials: false`.
  - **F5**: the live results stay in the job's log, which is as public as the repository; at worst a failed test
    prints a signed URL there, which expires within minutes and points at a test file the suite deletes.
  - **F6**: a workflow that names an environment creates it unprotected on its first run, so the checklist and both
    workflow headers protect `nuget` and `live-cloud` before any secret or the nuget.org policy.
  - **F7**: CONTRIBUTING asks a provider PR whose live suite did not run to say so in "Tested by", and why.
  - Also: each live run passes `-- --minimum-expected-tests 1`. Checked locally: after `--` the minimum counts per
    test module and leaves out skipped tests, so an all-inconclusive run exits 9 (Google Cloud Storage's live suite
    without `WFP_LIVE`: 74 skipped, exit 9); before `--` it counts skipped tests and exits 0.
  - Verified: actionlint 1.7.12 and the schema checks pass; the masking ran in bash with `jq` (Alpine) on a sample
    Box configuration and on a key in both forms; the package check passes on a fresh pack of nine packages.

### For the maintainers

What this session cannot do, kept current as the work goes on:

- [ ] Rotate the MinIO access key pair from F9 on that server, and any other credential that was ever in the Azure
  DevOps history.
- [ ] Delete the Azure DevOps repository and organization.
- [ ] Review the merged PRs of #3 after the fact (D13): #17, #18, #19, and each later one as it merges. #19 (S2), S3,
  S4 and S12 touch the harness or a one-way door.
- [ ] Run the live suites the PRs could not: AmazonS3 for #17 and every provider changed after it; Box and SharePoint
  for S9 and S10 (D21).
- [ ] Google Cloud Storage (S8): run `GcsLiveConformanceTests` and `GcsLiveAtomicityTests` (`WFP_LIVE=1`, `WFP_GCS_*`)
  against a bucket you own, and check:
  - a copy or move racing a writer of its destination throws `FileExistsException` (`ifGenerationMatch=0` on rewrite);
  - a signed create URL refuses an overwrite (412), and signed URLs expire and carry the response overrides;
  - on a bucket with object versioning on, a pinned read and a move's delete, which send both `generation` and
    `ifGenerationMatch`, still refuse a stale generation (the combination is unverified on real Cloud Storage);
  - signing without a key, through `signBlob` (Workload Identity or Cloud Run), and `CreateBucketIfMissing` with
    permission to create buckets;
  - a client with a customer-supplied encryption key reads its objects through the provider (the emulator ignores the
    key).
- [ ] Publish the S0 patch from the `release/2026.10` branch (it starts at S0's merge commit; `main` has moved to the
  2027 layout). Step 0 (P33): on that branch, run `AmazonS3ProviderTests`, at least
  `AbsolutePathMembers_ResolveUnderTheRootOnly`, against an S3 endpoint whose virtual-host bucket names resolve (your
  own AWS account, or LocalStack with a hosts entry for `wfp-test.<host>`), and publish only when it passes. Pin
  Common in the patched providers as `[2026.10.10.5, 2027.0.0)`, so a consumer who mixes in 2027 gets a restore
  warning (NU1608) instead of a runtime type load failure (P21).
  1. `dotnet pack WriteableFileProviders.Common -c Release -p:Version=2026.10.10.5 -o out`, then push it.
  2. In `WriteableFileProviders.AmazonS3.csproj`, raise the Common pin to `2026.10.10.5` on that branch, so one update
     brings in F47 as well. Then
     `dotnet pack WriteableFileProviders.AmazonS3 -c Release -p:Version=2026.10.10.2 -o out`, and never pass
     `UseLocalCommon` here, or the dependency becomes `>= 1.1.2` (F50).
  3. Check that the nuspec depends on Common `2026.10.10.5`, then push.
  4. Publish a GitHub security advisory for F1, F2 and F47, naming the fixed versions.
- [ ] Agree the licence with László Nemes and Starschema (D8), then add `LICENSE` and `PackageLicenseExpression`.
- [ ] Configure the release (`release.yml`, whose header says the same), in this order: a workflow that names an
  environment creates it unprotected on its first run, so protect it before it holds a secret or nuget.org trusts it.
  1. The `nuget` environment: required reviewers, the maintainers; deployment tags `v*` only.
  2. A tag ruleset on `v*`, so only the maintainers create, move or delete release tags.
  3. The `nuget` environment's secret `NUGET_USER`: the nuget.org profile name (not the email) of the account that
     owns the packages.
  4. On nuget.org, as that account, a trusted publishing policy: owner `serionist`, repository
     `WriteableFileProviders`, workflow `release.yml`, environment `nuget`; and reserve the `WriteableFileProviders.`
     id prefix.
- [ ] Create the `live-cloud` environment for `live.yml`, protected first: deployment branches `main` only, and no
  required reviewers (the nightly run would wait). Then one secret per `WFP_*` variable the workflow maps, for
  accounts you own and keep for it: Box's app configuration JSON as the one secret `WFP_BOX_CONFIG_JSON`; Google Cloud
  Storage's `client_email` and `private_key` as `WFP_GCS_CLIENT_EMAIL` and `WFP_GCS_PRIVATE_KEY`, never the whole key
  file. A provider without its secrets is skipped at night.
- [ ] Make CI a required check in the `main` ruleset: "Build, test, coverage, pack (Linux)" and "Build and test
  (Windows)", and CodeQL's "Analyze (csharp)" if you want code scanning to gate merges too.
- [ ] Turn on private vulnerability reporting (Settings > Code security), which SECURITY.md and the issue forms
  point to. It was off on 2026-10-11.
- [ ] Turn on Dependabot alerts and Dependabot security updates; `dependabot.yml` only schedules version updates.
  Both were off on 2026-10-11.
- [ ] Turn on the dependency graph and Automatic dependency submission (Settings > Code security): the graph does
  not read the versions in `Directory.Packages.props`, so without the submission the alerts miss every package.
  NuGetAudit stays the guard at restore time.
- [ ] Keep secret scanning and its push protection on (on as of 2026-10-11), and CodeQL's default setup off:
  `codeql.yml` is the advanced setup, and the two conflict.
- [ ] Fill in the date in SECURITY.md until which 2026.10.10.x gets security fixes; it now says the maintainers will
  announce it there.
- [ ] Run the live suites against your accounts before tagging `v2027.0.0`.
- [ ] Unlist the 2018–2024 package versions on nuget.org, keeping `2026.10.10.x` listed for .NET 8 consumers.

## Plan review

**Round 1:** standard tier, Opus, 2026-10-10. Verdict: needs-fixes, with four owner questions. A deep pass was recommended
after the edits.

The edits below were applied on 2026-10-10. The owner questions took the recommended answers on the maintainer's word
("do the recommended"), recorded as D7 and D15–D17.

| # | Sev | Section | Finding | Status |
|---|-----|---------|---------|--------|
| P1 | major | S0 | The patch had no versions, pack steps, pins or tests | fixed: the S0 row, and "For the maintainers" |
| P2 | major | D7 | D7 was worded more strictly than the roadmap (MinIO HTTP, SharePoint root equal to the work folder) | fixed: D7 (owner: recommended) |
| P3 | major | Serializer | Searching only the loaded assemblies misses providers that are not loaded yet | fixed: resolution order, ambiguity, a test |
| P4 | major | URLs | Service-set lifetimes and expiring credentials could outlive or undercut what was asked | fixed: D18 |
| P5 | major | D5, append | Unconditional block-list commits and rewrites lose concurrent appends | fixed: D19 and the contract |
| P6 | major | Contract | `IfMatch` cannot be atomic on Physical or on SharePoint upload sessions | fixed: D15 (owner: recommended) |
| P7 | major | Box | The path-to-id cache could act on a stale id | fixed: the Box row |
| P8 | major | Reads | Version pinning had no hook in the API | fixed: `ReadOptions` and the contract |
| P9 | major | Steps | The new suite and the old tests could not both pass in S4–S10 | fixed: the S4 and S5 rows |
| P10 | major | Scope | P2 items dropped without a reason | fixed: D17, `Create` permission, `live.yml`, unlisting (owner: recommended) |
| P11 | major | GCS | The emulator has no move; the constructor does I/O | fixed: D16, the GCS row, F13 in S8 (owner: recommended) |
| P12 | major | Gateway | One-time tokens through `IDistributedCache` race; the URL host was unspecified | fixed: the replay store, `PublicBaseUri` |
| P13 | minor | Harness | The harness docs lagged behind S2 | fixed: done in S2, and the S3 row |
| P14 | minor | Approval | Approved before review; #3 did not record the agreement | fixed: this section; the decisions are posted on #3 |
| P15 | minor | Contract | Case sensitivity, recursive folders, the root, never-seek, zero-length ranges and temp files were unstated | fixed: the contract |
| P16 | minor | Azure | Legacy append blobs: overwrite and the size limit | fixed: the Azure row |
| P17 | minor | S3 | The checksum mitigation lived only in the tests | fixed: `S3Core` |
| P18 | minor | Tracker | Findings mapped to the wrong steps | fixed: the step rows |
| P19 | minor | Docs | Docs moved only in S12 | fixed: each step updates its docs; the PublicAPI files per step |
| P20 | minor | CI | `fetch-depth`, floors that rise as steps land, a `WfpLegacy` check | fixed |
| P21 | nit | API | Positional `SignedUrl`, an untyped exists exception, `IfMatch` together with `IfNotExists`, the constructor wording | fixed |

Refuted by the reviewer:
- Testcontainers bringing Newtonsoft in.
- S3Mock lacking conditional PUT.
- Package versions that do not exist.
- GCS lacking `MoveObject`.
- The 7-day caps.
- `default(ProviderPath)`.
- The shims clashing.
- Atomic replace on Windows.
- Audit strictness.

**Round 2:** deep tier (11 lenses, reconcile, solo refuters and three voters), Opus, 2026-10-10, read-only.
- Verdict: needs-fixes, with owner questions: 18 majors, 20 minors, 3 nits.
- The report is in `.local/review/plan-r2/` (git-ignored).
- All findings are applied in this revision. The owner questions took the recommended answers (D20–D24).
- Where they landed:
  - P1 (code ran ahead of review): the approval history above, honestly stated.
  - P2: D20, and the decisions posted on #3.
  - P3: D21.
  - P4: D13 and "For the maintainers".
  - P5: `docs/plans/README.md`.
  - P6, P8, P15, P18–P20, P25, P28, P38: the contract's round-2 block.
  - P7, P11, P34: the S4 row.
  - P9, P10, P21, P22, P24: Compatibility.
  - P12: Later.
  - P13, P26, P27: D22 and the gateway.
  - P14: D19.
  - P16, P17: D24.
  - P23: the MinIO row.
  - P29, P31, P32, P40: D23.
  - P30: the Azure row.
  - P33: "For the maintainers".
  - P35: Tests.
  - P36: D7.
  - P37: D7.
  - P39: Alternatives.
  - P41: Steps.

Status: approved for execution under the maintainer's standing instruction (see Approval). It is open to the
maintainers' after-the-fact review.
