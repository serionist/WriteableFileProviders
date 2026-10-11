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
| D8 | MIT licence | MIT, "Copyright (c) 2018-2026 the WriteableFileProviders contributors", chosen by @serionist on 2026-10-11. The agreement of László Nemes and Starschema that D8 asked for is for the maintainer to confirm before 2027.0.0 is tagged. Built in S12's launch half (`16-launch`): `LICENSE` at the root; `PackageLicenseExpression` MIT and that copyright line in `src/Directory.Build.props`; contributions under MIT (CONTRIBUTING.md); the README's Licence section. Before: once László Nemes and Starschema (a co-owner of the nuget.org ids) agree, with no `LICENSE` file until then. |
| D9 | No Newtonsoft anywhere | Not in our code, our packages or our tests, and no bridge package; System.Text.Json only. GCS's own SDK (`Google.Apis`) still depends on it transitively, which we cannot change. |
| D10 | Build files | `Directory.Build.props` for shared properties, `Directory.Packages.props` for central package management, with lock files. |
| D11 | Testcontainers | Emulator tests start their containers through Testcontainers; no manual `docker run`. |
| D12 | READMEs | A root README and one per package, packed into the nupkg. |
| D13 | How the work lands | Several issues and PRs under #3, which this session opens and merges itself without waiting for a maintainer; the maintainers review them afterwards (decided 2026-10-10, superseding "one single PR"). Each PR builds, passes its tests and has its `review-code` pass before merge. This is a scoped exception to finish-work steps 4 and 6 (the developer's look before review, and a maintainer marking the PR ready) for the #3 issues only; AGENTS.md is not amended. Every merged PR is listed for the maintainers' after-the-fact review under "For the maintainers". |
| D14 | Scope | All findings, the P1 and P2 features, full unit tests, Testcontainers. P3 goes under Later. |
| D15 | `IfMatch` guarantees | The precondition is atomic on Azure, S3 on AWS, GCS (generation), Box and SharePoint simple uploads. On MinIO and any other S3-compatible server (`ServiceUrl`), which may ignore a condition, the provider checks it first, which is not atomic, and still sends the header for the server to enforce where it can (S7 review, round 1). On Physical (synthesized ETag from length and last-write time) and on SharePoint upload sessions it is checked, not atomic, and the XML docs say so. `IfNotExists` is atomic everywhere it can be: on Physical, Windows renames with `File.Move(overwrite: false)`, which refuses an existing file; on Linux and macOS, where .NET 10's `File.Move(overwrite: false)` checks and then renames (found in S5), an exclusive create (`FileMode.CreateNew`) takes the name and a rename fills it. Amended in S9 (2026-10-11, main-session decision): SharePoint declares `SupportsAtomicPreconditions = false`, "checked, not atomic", for simple uploads too, because Graph does not document `If-Match` on `PUT /content`. The provider still sends `If-Match` and the conflict behaviour `fail` with every upload; the maintainers' live run decides whether simple uploads become atomic (see "For the maintainers"). Amended in S10 (main-session decision, 2026-10-11): on Box, `IfMatch` (and the version an append rewrites, D19) is checked, not atomic, until the maintainers' live run confirms that Box matches the quoted `If-Match` the v10 SDK can only send (Box documents unquoted ETags); the provider's wire path is behind an internal switch, tested on the fake, and turning it on is one line. `IfNotExists` is atomic on Box (409 on a taken name). |
| D16 | GCS move | Copy with `IfGenerationMatch = 0` (destination must not exist), then delete the source with its generation. fake-gcs-server has no native move; `MoveObject` goes under Later. |
| D17 | Two P2 items kept | Public constructors that take a prebuilt SDK client (with the options) on every cloud provider, for managed identity, Aspire, custom retries and tests. A provider never disposes a client it was given; the caller owns it. SDK types in public signatures (these constructors, `S3Region` and `S3StorageClass` on the S3 options) are part of the contract, so an SDK major that changes them is a WriteableFileProviders major. SharePoint signs in with a client secret, a certificate (`ClientCertificateBase64` and `ClientCertificatePassword`), or managed identity / `DefaultAzureCredential` (`UseDefaultAzureCredential`). |
| D18 | URLs with a lifetime set by the service | Requested validity V and fixed lifetime L. If V < L, `NotSupportedException`, so the gateway takes over. If V ≥ L, the URL is returned only with `AllowShorterValidity`, with `ExpiresOn` = now + L (Box) or null (SharePoint). S3 and MinIO: when the credential expires before now + V, or is a session credential with an unknown expiry, the URL counts as shorter than asked. |
| D19 | Concurrent appends | An append never silently loses a concurrent write, except on Physical and on SharePoint (its upload sessions, and its simple uploads until the maintainers' live run shows Graph honours `If-Match` there: D15 as amended in S9), where the check is not atomic and the XML docs say so (round 2, P14; the conformance test for it runs only where the provider fixture declares the capability). Azure commits the block list with `IfMatch` on the ETag it read, re-reading and retrying a bounded number of times. `AppendByRewriteAsync` writes with `IfMatch` on the ETag or generation it read. A concurrent change it cannot absorb throws `PreconditionFailedException`. |
| D20 | Maintainers' agreement for this release | @serionist's word is the maintainers' agreement for the decisions of #3, and @NemesLaszlo reviews the merged PRs afterwards with him (the maintainer's instruction, 2026-10-10). Posted on #3. |
| D21 | Live suites stay per PR (round 2, P3) | AGENTS.md's rule is unchanged: a provider change runs its emulator suite, or its live suite where there is none, and its PR says which ran or that it did not and why. Box and SharePoint get offline fakes in S9 and S10, but their PRs still say "live suite not run"; their live runs are outstanding items for the maintainers. |
| D22 | Gateway defaults (round 2, P13, P26) | Fail closed: `MaxValidity` defaults to 1 hour for Create and Write tokens (configurable, never above 7 days) and to 7 days for Read; Create and Write tokens are one-time by default; the default replay store is in memory and holds only on one instance, which the README and XML docs say (a scaled-out app registers a shared `IGatewayReplayStore` or turns one-time off explicitly); `MaxUploadBytes` defaults to 100 MiB. Added in the review of #24 (S11): these rules bind gateway URLs only (`SignedUrlKind.Gateway`); a provider's native upload URL can be reused until it expires and lives up to the provider's maximum (7 days), which S6–S8 treat as intended, and an app that wants the same upload rules everywhere sets `PreferGateway` (F1). A gateway download is served as the content type its read token names, else `application/octet-stream`, never a type the store keeps, and every gateway response carries `Content-Security-Policy: default-src 'none'; sandbox`; the README recommends a host name of its own for the gateway (F2, the maintainer's call: fail closed and harden). |
| D23 | Smaller surface (round 2, P29, P31, P32, P40) | No Box path-to-id cache: every call resolves the path, as today. Serializer type resolution: the assemblies given, a built-in map from provider class names to their package assemblies, and one registration API, `WriteableFileProviderSerializer.Register<TProvider>()`; no scan of loaded assemblies. Extensions only keep old call shapes compiling. No internal GCS client seam: the tests use the public constructor. The dropped pieces are under Later. |
| D24 | Content type and unsupported options (round 2, P16, P17) | `UpdateFileOptions.ContentType` is a hint: stored where the store keeps one, ignored elsewhere, and `IWriteableFileInfo.ContentType` is null where the store keeps none. An option a provider cannot honor throws `NotSupportedException`; on `GetSignedUrlAsync` that lets the gateway take over. A signed URL validity that is not positive throws `ArgumentOutOfRangeException`; one above what the provider can sign natively throws `NotSupportedException`, so the URL service falls back on every provider alike. |
| D25 | Provider classes are sealed | Every provider class is sealed in 2027; a custom provider derives from WriteableFileProvider<TOptions>, or wraps IWriteableFileProvider. Each provider step applies it and lists it in the migration guide. |
| D26 | New options keep the stored shape | Every options property new in 2027 is written to JSON only when it differs from its default ([JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]), so a configuration that uses none of them keeps its 2026 shape. |
| D27 | Listings show only addressable names | A name the provider's path validation would refuse is left out of listings on every provider; S12 brings the providers that do not yet do it into line. |
| D28 | Versioning policy (decided by @serionist on 2026-10-11) | SemVer from git tags (MinVer, prefix `v`, minimum `2027.0`), every package in lockstep at one version (D2). The major is a sequence number, not a date: it continues above the 2018–2026 date-stamped versions (`yyyy.M.d.n`), so NuGet orders every 2027 release after them; `2027.0.0` is the first SemVer release, `2027.1.0` adds features, `2027.0.1` fixes, and the next breaking release is `2028.0.0` whenever it ships, even years later. Breaking means a major: a change to a one-way door in AGENTS.md, dropping a target framework, or a dependency change that breaks consumers' builds (such as an SDK major in a public signature, D17); adding a target framework or a feature is a minor. Target frameworks: the current .NET LTS (net10.0); a new LTS is added in a minor, an out-of-support one is dropped only in a major. Support: the latest major gets every fix; the previous major gets security fixes for 6 months after the next major ships; `2026.10.10.x` (.NET 8) gets security fixes until a date the maintainers announce. Prereleases: `-alpha.0.<height>` from untagged builds, `-rc.N` tags before a major. Written in the root README's Versioning section, SECURITY.md's supported versions, AGENTS.md (One-way doors) and CONTRIBUTING.md. |
| D29 | Failures outside the contract | Every member of every provider throws only the contract's exceptions: `ArgumentException` (a path or an option), `FileNotFoundException`, `FileExistsException`, `PreconditionFailedException`, `NotSupportedException` (a feature this provider or store cannot give), `InvalidOperationException` (the store is not set up as configured: a missing container, bucket, library or root folder, or no credentials to find), `UnauthorizedAccessException` (the store refused the credential or the permission), `IOException` (any other failure of the store, the service or the network, timeouts included), and `OperationCanceledException` only for the caller's token, plus `ObjectDisposedException`. A provider's or SDK's exception travels as `InnerException`, unless it already derives from one of the contract's types, which passes as it is; `OutOfMemoryException` passes unmapped. It is enforced once, in `WriteableFileProvider<TOptions>`: the public members map what escapes a `...CoreAsync` member (and what escapes a stream or an enumeration they return) through `IsNotFound` (existing) and a new `protected virtual bool IsAccessDenied(Exception exception)`; anything else that is not one of the contract's types becomes `IOException` naming the operation and the path. A provider still maps what it knows better itself. Agreed by @serionist on 2026-10-11 and posted on #3 ([comment](https://github.com/serionist/WriteableFileProviders/issues/3#issuecomment-6108746458)). The pass-through of the contract's own subtypes and of OutOfMemoryException is the plan's reading of the agreed contract (review of PR #42). |
| D30 | A file and a folder may share a name on object stores | On S3, MinIO, Azure Blob and Google Cloud Storage, folders are name prefixes, so writing a file at a folder's path, or a folder under a file's path, succeeds and a listing then shows both. Physical, Box, SharePoint and the in-memory provider refuse it with `IOException`. The conformance suite pins both behind a capability flag; the README matrix and the migration guide say so. (An extra listing request on every object-store write was rejected for its cost.) Agreed by @serionist on 2026-10-11 and posted on #3 ([comment](https://github.com/serionist/WriteableFileProviders/issues/3#issuecomment-6108746458)). |
| D31 | Create-only signed URLs are native only where the store is trusted with conditions | A provider issues a native `SignedUrlAccess.Create` URL only where it trusts the store to enforce `If-None-Match` (S3 on AWS; Azure; GCS); elsewhere (S3 toward a named endpoint, MinIO) it answers `NotSupportedException`, so the URL service issues a gateway URL, which enforces create-only. Read and write URLs stay native. Agreed by @serionist on 2026-10-11 and posted on #3 ([comment](https://github.com/serionist/WriteableFileProviders/issues/3#issuecomment-6108746458)). |
| D32 | Providers hide only their own temporary names | A listing leaves out only names the provider cannot address (D27) and the temporary names it creates itself, exactly: Azure its `.wfp-tmp/` folder at the root, Physical `.wfp-` followed by 32 hex digits and `.tmp`. Any other `.wfp-` name is listed like any other name. Agreed by @serionist on 2026-10-11 and posted on #3 ([comment](https://github.com/serionist/WriteableFileProviders/issues/3#issuecomment-6108746458)). |

## Design

### Repo layout and build

```
WriteableFileProviders.slnx
Directory.Build.props        shared: net10.0, LangVersion, Nullable, strictness, analyzers, docs, audit, lock files
Directory.Packages.props     central package versions, transitive pinning only for advisories (with a comment)
global.json                  SDK 10.0.1xx, test runner Microsoft.Testing.Platform
eng/coverage-gate.cs         file-based app: fails when an assembly falls below its floor in eng/coverage.json
src/Directory.Build.props    packaging: MinVer, README, icon, SourceLink, snupkg, package validation
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
    the end no project has it. Done in S12 part 2: the last one, the old test project, is deleted, and the switch with
    it.
- **Versions:** MinVer sets them; Common's hard-coded `<Version>1.1.2</Version>` goes (F50).

### The behaviour contract (D4)

Every provider passes the same conformance tests for each of these rules:

- **Paths:**
  - A `ProviderPath` is relative to the provider root.
  - Segments are validated as today: no empty, dot-only, control-character or percent-encoded escapes.
  - The root cannot be modified (`InvalidOperationException`) or read as a file (`ArgumentException`).
  - No public member takes absolute segments (F1, F2, F47).
  - The root in the options is checked with the same rules when the provider is built (`ArgumentException`), except
    where it is a folder of the disk (Physical); final review F1.
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
  - Listings leave out names the provider cannot address (names its own path validation would refuse), so everything
    listed can be read back (D27).
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
    SharePoint use conflict "fail"; Physical uses `File.Move(overwrite: false)` on Windows and, on Linux and macOS, takes the destination with an exclusive create and renames onto it (D15).
  - *Large files (P6):* write, overwrite, append and local-stream edit of a large file keep their content, and a failed
    large write leaves the old content. The suite's default size is 100 MB; a fixture can lower the provider's threshold
    so the test crosses S3 and MinIO multipart, Azure staged blocks, SharePoint upload sessions, Box chunked upload and
    GCS resumable upload.
  - *Temp files (P28, P38):* temp files are owner-only on Unix (`UnixCreateMode = UserRead | UserWrite`) and
    `DeleteOnClose`; a Unix-only test checks the mode. Physical's temp file next to the target cannot be either, since it
    becomes the file: before any content is written it takes the permission bits of the file it replaces or copies, so
    it is never readable by more users than that file (S5). Fixtures create a unique folder per class and delete it in class
    cleanup, and the suite checks no `.wfp-*.tmp` is left.
  - *Physical sharing (P20):* Physical opens readers with `FileShare.ReadWrite | FileShare.Delete`, so a delete can
    proceed, and a replace on Linux and macOS; Windows does not rename over a file that is open, so there a write to a
    file being read fails and keeps its content: with `UnauthorizedAccessException` when the reader shares writing, as
    Physical's readers do, and with `IOException` when another process holds the file without sharing writes. A
    Physical test (not a shared conformance case, since what an open reader sees differs per store) writes while a read
    stream is open, with the result expected per OS, and runs on Windows CI (S5).
- **Errors (D29, final review):** every member throws only the contract's types, the store's exception inside; the
  base class maps what escapes untyped, from members, streams and listings alike. Conformance cases on a fixture that
  can make its store throw (`CreateProviderThatFails`): an arbitrary exception is `IOException`, a timeout is
  `IOException`, a contract type passes through as it is.
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
public sealed record SignedUrlOptions { required TimeSpan Validity; SignedUrlAccess Access = Read;
    string? DownloadFileName; string? ContentType; bool AllowShorterValidity; }
public sealed class SignedUrl { required Uri Url; required string Method; DateTimeOffset? ExpiresOn;
    IReadOnlyDictionary<string,string> Headers; SignedUrlKind Kind; }      // a class with init members, so it can grow
public enum SignedUrlAccess { Read, Create /* only if absent */, Write }      // built as SignedUrlAccess (CA1711)
public enum SignedUrlKind { Presigned, PreAuthenticated, Gateway }          // "Signed" trips CA1720
public class PreconditionFailedException : IOException      public class FileExistsException : IOException
```

- **`WriteableFileProviderExtensions`** held, until S12 part 2 (simplify), when the maintainer moved all of it into the
  `[Obsolete]` compatibility class (`WriteableFileProviderCompatibilityExtensions`), to go in 2028.0.0, and the class
  became an internal helper:
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
- **`RangeReadStream`** (internal since S12 part 2, simplify; only Common builds it): a read-only seekable stream over a range callback, with read-ahead buffering and version
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
  - `services.AddWriteableFileProvider(IConfiguration)` and `AddWriteableFileProvider(factory)` register a plain
    singleton `IWriteableFileProvider` (`AddSingleton`, so several are all in `GetServices`); an app with several
    providers tells them apart itself, such as with keyed services. Changed in S12 part 2 (simplify, the maintainer's
    request of 2026-10-11): S4 registered keyed singletons by name.
  - It lives in Common, on `Microsoft.Extensions.DependencyInjection.Abstractions`, in the namespace
    `Microsoft.Extensions.DependencyInjection` since S12 part 2 (simplify), as the gateway's registration does; the
    gateway's mapping is in `Microsoft.AspNetCore.Builder`.
- **Package references:** `Microsoft.Extensions.Configuration` and Newtonsoft go (F72, D9).

### Providers

| Provider | SDK | What changes besides the contract and the API |
|---|---|---|
| Physical | BCL | Readers share `ReadWrite | Delete` (P20); atomic writes (temp file, then `File.Move` overwrite) (F3); append rewrites through a temp file, so it is atomic; synthesized ETag (D15); local stream on a temp copy (F5); ranges through `RandomAccess`; `Watch` through `PhysicalFilesWatcher` (F54); signed URLs only through the gateway. Listings leave out names a path cannot name, such as `a:b` on Linux or `%2e%2e` anywhere (D27, S12); a symbolic link to a file is described by its target (S12). |
| AzureBlob | Azure.Storage.Blobs 12.30, Azure.Identity 1.21 | Block blobs (D5); `OpenReadAsync` gives a native seekable stream (F64); `AzureFileStream` goes (F15, F70); ranges; service SAS with no start time (F40); user delegation SAS with `UseDefaultAzureCredential`; read and write SAS; ETag conditions; append commits with `IfMatch` (D19); legacy append blobs (P30): a block blob cannot replace an append blob in place, so the sequence is (1) stage the new content to a temp block blob, (2) delete the destination with `IfMatch` on the ETag read, (3) copy the temp blob onto the destination and poll until done, (4) delete the temp blob only after the copy succeeds; on any failure the temp blob stays and the exception names it. From (2) until the copy of (3) completes the file is missing, or empty while the server copy runs, which the XML docs and the migration guide say. Azurite tests cover overwrite and append over a legacy blob, and a failed copy that leaves the content in the temp blob; a public constructor that takes a `BlobContainerClient` (D17). |
| AmazonS3 | AWSSDK.S3 4.x | `ServiceUrl`, `ForcePathStyle` and the default credential chain (additive options); multipart above a threshold (F65); ranges with `EtagToMatch`; presigned GET and PUT; `IfNoneMatch` and `IfMatch`; the public absolute-path members go (F1, F2); never disposes the caller's stream (F41). Shared internal `S3Core`, which also sets the checksum behaviour (`WHEN_REQUIRED`) for a custom `ServiceUrl`, so the tests run the production configuration; presigned URLs honour D18 for expiring credentials; a public constructor that takes an `IAmazonS3` (D17). `ReadFileVersionAsync` (added in S0) stays, on `ProviderPath`. |
| Minio | AWSSDK.S3 4.x via `S3Core` | D6 and D7; `S3Core` builds the client so every stored MinIO configuration keeps working (P23): path-style forced, the scheme from `UseSSL`, and the region discovered once per bucket (`GetBucketLocation`, cached) unless the new `Region` option names it, with a unit test of the client configuration; download URLs now work (F45); Extensions 9 and System.Reactive leave consumers' graphs (F73). |
| GoogleCloudStorage | Google.Cloud.Storage.V1 5.x | `Endpoint` option for fake-gcs-server (F75), and the tests build their client through the public constructor (D23); ranges with generation pinning; V4 signed GET and PUT; move by conditional copy then delete (D16); `IfGenerationMatch`; append through `AppendByRewriteAsync` (F4); credentials created lazily (F13); a public constructor that takes a `StorageClient` (D17). As built in S8: reads, ranged or whole, stream through a raw media GET on the client's own HTTP client (Google's byte-range sample), pinned with `generation` and `ifGenerationMatch`, a 416 read as empty; copies and moves send `ifGenerationMatch=0`; a move deletes with the copied `generation` and `ifGenerationMatch` and, when the source was written meanwhile, undoes the copy and throws `PreconditionFailedException`; a missing bucket is told from a missing object by a one-object list; `CreateBucketIfMissing` needs `ProjectId` or a key that names its project. After review round 1 of #27: the class is `sealed` (D25); `Endpoint`, `AllowInsecureHttp` and `CreateBucketIfMissing` are written to JSON only when set (D26); a plain-`http` `Endpoint` toward a host that is not loopback needs `AllowInsecureHttp` (D7, as MinIO); copies are not retried; downloads send the client's customer-supplied encryption key. |
| SharePoint | Microsoft.Graph 6, Graph.Core 4 | Lazy initialization (F13); `WorkFolderPath` folded into the root (F7); `LargeFileUploadTask`, or a loop with `ReadAtLeastAsync`, with one bounded retry (F8, F39); only `itemNotFound` means not-found (F66); one folder creator (F62); ranges on the download URL, falling back to skipping when the range is ignored; pre-authenticated read URL per D18; certificate and managed-identity sign-in (D17); a public constructor that takes a `GraphServiceClient` (D17). |
| Box | Box.V2.Core 10 (the generated SDK) | Lazy authentication (F13, F32); marker paging (F67); every call resolves the path fresh, as today (no path-to-id cache, D23); range reads pinned to the file version (P15); never rewinds the caller's stream (F6); pre-authenticated read URL per D18; a public constructor that takes a Box client (D17). Listings leave out names Box allows and a path cannot name, such as `...` or `%2e%2e` (D27, S12). |

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
  - `AddWriteableFileProviderGateway(options)` (or `(publicBaseUri)`, added in S12 part 2) and
    `MapWriteableFileProviderGateway()` serve GET, HEAD and PUT on `{BasePath}/{token}/{fileName?}`.
  - `IWriteableFileUrlService.GetSignedUrlAsync(path, options)`, for the app's `IWriteableFileProvider`, and
    `GetSignedUrlAsync(path, options, providerKey)`, for a keyed one (both since S12 part 2; S11 took a provider name
    first), try the provider's native URL first, unless `PreferGateway` is set, and fall back to a gateway URL when the
    native call throws `NotSupportedException`.
  - **Tokens:** ASP.NET Core Data Protection time-limited payloads, with purpose
    `WriteableFileProviders.Gateway.v1/<provider>/<permission>` (built as a chain in S11; since S12 part 2, a token for
    the app's default provider has an empty provider segment and leaves the provider out of the chain).
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
  - S3 and MinIO: `adobe/s3mock` and a MinIO container (`Testcontainers`);
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
  - the tests with coverage and the gate, and only once `ci.yml` concluded success on the tagged commit (final review
    F12);
  - pack, and push through nuget.org trusted publishing, behind a `nuget` environment;
  - the maintainers configure the trusted publisher and the environment.
- **After merge:** CI becomes a required check in the ruleset, which closes REVIEW.md's "until CI exists" gap. That is
  for the maintainers to switch on.

### Docs

- **Repo docs:**
  - a root README: what it is, packages, a quick start, the provider matrix, links; since S12's launch half, a short
    README (install, a four-step tutorial, the packages, links) with the detail in a guide under `docs/guide/`, one
    topic a page (the provider matrix, paths and listings, reading and writing, signed URLs, dependency injection,
    errors, writing a provider, versioning);
  - a README per package: install, configuration keys, auth, example, limits;
  - `CHANGELOG.md`, whose version section `release.yml` publishes as the GitHub release's notes; `LICENSE` (D8);
    `docs/releasing.md`, the release runbook;
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
  - The `string[]`/`params` overloads go; sync forms and `IFileInfo` overloads move to extension methods, `[Obsolete]`
    until 2028.0.0 (S12 part 2, simplify), as are `UpdateFile[Async](..., UpdateFileMode)` and `GetDownloadUrl[Async]`.
  - `WatchAsync` and `OnLocalFileStreamDisposingAsync` leave the interface.
  - `ToJson()` returns `JsonObject` and leaves the interface.
  - `FileInfo` becomes `WriteableFileInfo`.
  - `LocalFileStream`'s constructor becomes internal, and `DisposeAsync` becomes a `ValueTask` override.
  - The protected members of `WriteableFileProvider<T>` are renamed (`...CoreAsync`), which breaks third-party
    providers. `normalizePath`, `fileSegments` and both `RunTaskSerial` overloads go (`ProviderPath.Parse`,
    `IWriteableFileInfo.Path`, await). `createLocalFileStreamAsync` and the public virtual `CreateLocalFileStreamAsync`
    go: the public member is no longer virtual, and how a local stream is made is not customisable (final review F11,
    agreed on #3: [comment](https://github.com/serionist/WriteableFileProviders/issues/3#issuecomment-6108746458)).
    `GetRelativeSegments`, `IsRootPath` and `NormalizePem` were `[Obsolete]` and went in S12.
  - A store's exceptions no longer escape as they are: the base class maps them (D29), with `IsAccessDenied` beside
    `IsNotFound`; the root in the options is checked when the provider is built (`ValidatesRootDirectory`), so a stored
    configuration whose root has a dot segment no longer builds (final review F1, agreed on #3:
    [comment](https://github.com/serionist/WriteableFileProviders/issues/3#issuecomment-6108746458)).
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
    - `ServiceUrl`, `ForcePathStyle`, `UseDefaultCredentials` (S3; the last decided for S7), and `AllowInsecureHttp`
      (S3, added in the final fixes: plain `http` toward a host that is not loopback needs it, as on MinIO);
    - `UseDefaultAzureCredential` (Azure);
    - `Endpoint` and `AllowInsecureHttp` (GCS).
  - Behavior changes:
    - Azure and MinIO no longer auto-create containers and buckets unless the option is set.
    - MinIO throws without `UseSSL` on a non-loopback host (D7).
    - SharePoint's `WorkFolderPath` is the root (D7).
- **Behaviour:** the contract above. Each change from today is listed per provider in the migration guide.
- **One meaning, per provider (P2):** D15 (`IfMatch` checked but not atomic on Physical, and on SharePoint: its
  upload sessions, and its simple uploads until a live run confirms otherwise), D18 (service-set URL lifetimes) and D19
  (appends) differ by provider, and each says where.
- **Provider APIs (P10):** S3's `EnumerateObjects` and `ReadFileAsync(versionId, …)` are removed in favour of
  `ReadFileVersionAsync` on `ProviderPath`; the providers gain public constructors that take an SDK client (D17).
  SharePoint gains `ClientCertificateBase64`, `ClientCertificatePassword` and `UseDefaultAzureCredential`; MinIO gains
  `AllowInsecureHttp` and `Region` (D7, P23).
- **Stored configuration (P9):** 2027 `ToJson` output is not promised to read back on `2026.10.10.x`.
- **Azure (P22):** files written by 2027 are block blobs. `2026.10.10.x` cannot append to them (`InvalidBlobType`), so a
  rolling upgrade, or a fleet mixing the two lines, must not append to shared files during the switch; the first 2027
  append to an append blob converts it, after which writers using Append Block fail; during every conversion the file is
  missing, or empty while the server copy runs, for a moment (P30).
- **SDK majors (P24):** consumers who use these SDKs directly move with us: AWSSDK.S3 3 → 4, Box.V2.Core 5 → 10,
  Microsoft.Graph 5 → 6 and Graph.Core 3 → 4, Google.Cloud.Storage.V1 4 → 5. Common no longer brings in
  Microsoft.Extensions.Configuration. Each gets a section in the migration guide.
- **Mixing lines (P21):** every WriteableFileProviders package must be on the same major; a `2026.10.10.x` provider does
  not load against Common 2027. The migration guide and every README say so, and since S12 part 2 the packed dependency
  on Common carries an upper bound, `[V, NextMajor.0.0-0)` such as `[2027.0.0, 2028.0.0-0)`, so mixing majors gives a
  restore warning (NU1608) rather than a type-load failure at run time.
- **Packages:**
  - The new `WriteableFileProviders.AspNetCore`.
  - Newtonsoft.Json and Minio leave the dependency graph.
  - `AzureFileStream` and `UploadResult<T>` become internal (F70).

## Steps

Each step is an issue under #3, a branch `<issue>-<slug>`, and a PR that builds and passes its tests, reviewed with
`review-code` before merge. Issue numbers go in the table as they are created.

| Step | Issue | PR | Contents | Findings | Status |
|------|-------|----|----------|----------|--------|
| S0 Security patch | #4 | #17 | F1, F2: the S3 version read and the listing accept only the root followed by a valid path; `ReadFileVersionAsync(versionId, params string[] pathSegments)` is added, and the old members are `[Obsolete]` and `[EditorBrowsable(Never)]`. F47: the local stream commit checks the provider and the root. F9: the commented test goes. Current line, net8.0. Tests: offline refusals, the concrete-typed binding, F47 cases, and on LocalStack that valid paths still resolve under the root. Release notes for the maintainers are in "For the maintainers". | F1 F2 F47 F9 | done (#17, aa265f5); the patch is published (Common 2026.10.10.6, AmazonS3 2026.10.10.3), the advisory is the maintainers' (see "For the maintainers") |
| S1 Record | #5 | #18 | This plan, the review, the roadmap. | — | done (#18, b13ecda); round 2 applied in #21 (936a6ef) |
| S2 Build and layout | #6 | #19 | `.slnx`, `src/` and `tests/`, both build-props files, lock files, net10.0, `ProjectReference` to Common, MinVer, unused packages dropped, `WfpLegacy` ratchet, LF renormalization; REVIEW.md, finish-work, `.editorconfig` and `.gitattributes` updated; the `-> warning` analyzer rules become warnings (legacy projects silence them). Pulled forward from S3, because worktrees have no `testsettings.json`: settings optional, the Physical suite on a temp folder, and `test.runsettings` and the publish profile deleted (part of F56). Old tests still run. No copyright line in the packages until D8 settles. | F23 F46 F50 F55 F60 F71 F72, F26 (one Azure.Identity version) | done (#19, 7d83e44) |
| S3 Test platform and CI | #7 | #20 | MSTest 4 on Microsoft.Testing.Platform, Testcontainers fixtures, live suites tagged `Live`, the golden corpus of the stored configuration shape from the old code (JSON for six providers, configuration for Azure, S3, MinIO and Physical, Physical built at run time, the `Type` key rules, F78 on both paths), coverage collection and gate, `ci.yml`, the F68 assertion fixes. AGENTS.md's suite rule is unchanged (D21). F28 and F29 move to the conformance suite (S4), F56's remaining dead weight goes with TestBase (S5–S10). | F27 F30 F42 F68 | done (#20, 8da61fc; floors fixed in #22, 3715284) |
| S4 Common 2027 | #8 | #23 | The Common API and contract, `RangeReadStream`, signed URL types, the System.Text.Json serializer, DI, disposal, `LocalFileStream`, helpers, XML docs, the full conformance suite run against an in-memory reference provider in `WriteableFileProviders.Testing`, Common at 90%. Every provider moved mechanically onto the new base, so `main` keeps building: signature renames only, no behaviour change, and the PR lists per provider what verified it; AmazonS3, GCS, Box and SharePoint are "not verified until S7, S8, S9, S10", which run the full conformance suite including root protection and delete scope (P7). The old tests whose behaviour changes are updated and listed in the PR. One PR with two doors, the Common API and the configuration-shape serializer, both named in its title and description; splitting would keep Newtonsoft alive one step longer (P34). Docs (P11): `docs/migration-2027.md` starts here, Common's README, and AGENTS.md (the Testing project, the conformance suite, One-way doors extended to the 2027 Common surface). Shared files (`Directory.Packages.props`, Testing) change here or one step at a time. It also carries one unplanned harness change, e041292: the deep review's refuters probe in a scratch copy, never the checkout under review; its PR names it. | F4 F11 F12 F13 F14 F16 F17 F18 F19 F20 F21 F22 F28 F29 F31 F33 F34 F35 F36 F37 F38 F47 F48 F52 F53 F54 F57 F58 F59 F61 F69 F72 F74 F78 | done (#23, 65f22a2) |
| S5 Physical | #9 | #26 | The Physical row. Each provider step wires its own test project to the conformance suite, deletes its old TestBase class, raises its coverage floor, and updates its README, options docs and the migration ledger. Capability flag: `DetectsConcurrentAppends` is false, because the version check before the rename is not atomic (D15, D19); a Physical test pins that it catches a change made while the append copies. | F3 F5 F17–F21 F53 F54 F56 F74 F77 F79 | done (#26, 0d82bcc); live suite not run (it needs a folder you name) |
| S6 AzureBlob | #10 | #29 | The AzureBlob row, with Azurite. The main-session decisions applied are in the progress log (2026-10-11, S6). | F3 F11 F15 F17–F21 F26 F40 F44 F53 F64 F70 F74 | done (#29, dd566ac); live suite not run |
| S7 AmazonS3 and MinIO | #11 | #30 | `S3Core`, the S3 and MinIO rows, with S3Mock and MinIO. The core is shared source (`src/Shared/S3`) compiled into both packages: neither package depends on the other, and MinIO's has no Minio SDK. Capability flags on S3Mock 5.2.3, one per bug: `FailedLargeWriteKeepsOldContent` false (an aborted multipart upload deletes the existing object) and `SupportsLargeConditionalWrites` false (a multipart completion with the right `If-Match` answers 404); MinIO runs both cases fully. From the main session: both providers are `sealed` (D25), and their new options are written to JSON only when set (D26), so the golden written-shape test is unchanged. | F1 F2 F4 F10 F11 F17–F21 F24 F28 F38 F41 F44 F45 F51 F53 F56 F63 F65 F73 F74 F78 | done (#30, e473aaa); live suites and the AWS checklist not run |
| S8 GoogleCloudStorage | #12 | #27 | The GCS row, with fake-gcs-server. | F4 F11 F13 F17–F21 F28 F38 F41 F44 F53 F56 F63 F74 F75 | done (#27, e1c4b52); live suites not run |
| S9 SharePoint | #13 | #28 | The SharePoint row, with the Graph fake; its live run is outstanding for the maintainers (D21). | F4 F6 F7 F8 F11 F13 F17–F21 F26 F39 F43 F49 F53 F62 F66 F70 F74 F77 | done (#28, 70e3f74); live suite not run (D21) |
| S10 Box | #14 | #25 | The Box row, with the Box fake; its live run is outstanding for the maintainers (D21). | F4 F6 F11 F13 F17–F21 F25 F32 F53 F63 F67 F74 F77, Box's part of F56 | done (#25, 3ce3b89); live suites not run (D21) |
| S11 AspNetCore gateway | #15 | #24 | The new package, the URL service, endpoints, the gateway tests and defaults (D22, P13, P27); its README, migration entry and AGENTS.md entry; whether the token purpose string and URL shape are a one-way door (they are: tokens issued by one version must redeem on the next minor; recorded under One-way doors in AGENTS.md). | F11 | done (#24, bce935c) |
| S12 Docs, CI, launch prep | #16 | #31, #40, #41, #42, #43, #44, #45, #46 | The root README, the migration guide's final pass, CONTRIBUTING, SECURITY, AGENTS.md and REVIEW.md, codeql, dependabot, `live.yml`, the release workflow, every floor at 90% or more, a check that no project sets `WfpLegacy`, the removal of the `[Obsolete]` 2026 helpers in Common (`GetRelativeSegments`, `IsRootPath`, `NormalizePem`, which Box and GCS take over), the PublicAPI files (dropped afterwards, on the maintainer's decision of 2026-10-11, for package validation alone), and a final deep `review-code` of the whole repo. | F12 F76 | done: part 1 #31 (65cfd40); part 2 #41 (581e050, code) and #40 (b6e78b8, docs); the final whole-repo review (`docs/reviews/2026-10-11-2027-final.md`) and its fixes #42 (02790dd) and #43 (c1181a8); the API simplified #44 (a435930); the launch #45 (2435c69); the floors and this status #46 |

The provider steps S5–S10 are independent once S4 is merged. They may run in parallel agents, each in its own worktree,
touching only their provider's folders, but they merge one at a time (P41): each rebases onto main before its PR and
re-runs the build, the tests and the coverage gate. Shared files (`eng/coverage.json`, the `.slnx`, the old test
project and its lock file, `docs/migration-2027.md`, this plan's trackers and log, and shared package versions such as
Azure.Identity, owned by S6 and consumed by S9) are edited only in the rebased branch. Each provider step also updates
AGENTS.md's Tests section for its provider, its README and the migration guide (P11). Coverage floors rise as each step
lands, not only in S12. The PublicAPI analyzer files are added per package as each step rewrites it, starting with the
2027 surface; seeding them with the 2026 surface would only record removals that the migration ledger already lists.
The maintainer dropped them on 2026-10-11, for package validation alone (progress log).

## Tests

- **Offline (no Docker):** Common, Physical, SharePoint (fake), Box (fake), AspNetCore, and the unit tests of every
  provider.
- **Emulator, through Testcontainers:**
  - AzureBlob on Azurite;
  - AmazonS3 and Minio on S3Mock 5.2.3 and on MinIO (`cgr.dev/chainguard/minio`, pinned by digest);
  - GoogleCloudStorage on fake-gcs-server.
- **Live, opt-in:** every cloud provider. They run only when the maintainers have credentials. This session runs none.
- **Every PR:** the full offline and emulator run with `WFP_REQUIRE_DOCKER=1` and no inconclusive emulator test, the
  coverage gate, and a green `ci.yml` run on the head commit, linked from the PR until CI is a required check. Its
  description says which suites ran.
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
  - The 50,000 committed-block limit is handled by restaging the content into fewer blocks on the server before the
    append (S6).
  - A blob uploaded in one Put Blob has no block list, so its first append rewrites it.
  - Covered by tests on Azurite, which enforces neither the block limit nor the append-blob type rules: S6's test
    pipeline (`AzureHandler`) refuses those requests as Azure does, with the limit lowered.
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
- **Package validation baselines:** set them to `2027.0.0` after it ships ([docs/releasing.md](../releasing.md),
  Afterwards); since 2026-10-11 they are the only guard of the shipped API, the PublicAPI files being dropped.
  Package validation itself runs on every pack since the final review (F17).
- **One reader of the live settings** (final review F19, the owner's call: no, for now): the six live classes keep
  their own readers of `WFP_*` variables and `testsettings.json`. A shared helper in WriteableFileProviders.Testing would
  remove five copies, but only live accounts can check a refactor of how test credentials are read.

## Finding tracker

Status: `todo`, `done` (with the PR), `wontfix` (with the reason). F-numbers are those of
[the review](../reviews/2026-10-10-codebase.md).

| F | Short | Step | Status |
|---|-------|------|--------|
| F1 | S3 version read skips root and hides base overloads | S0, S7 | done: S0 (#17); S7 removed the member, and `ReadFileVersionAsync(versionId, ProviderPath)` stays |
| F2 | S3 EnumerateObjects lists outside the root | S0, S7 | done: S0 (#17); S7 removed the member (`EnumerateAsync(path, recursive: true)`) |
| F3 | Failed overwrite truncates on Physical and Azure | S5, S6 | S5 done (Physical writes a `.wfp-<guid>.tmp` next to the target and renames it over); S6 done (Azure: stage, then one conditional commit; conformance and an injected commit failure) |
| F4 | Append-by-rewrite five ways; S3/GCS upload half-appended files | S4, S7, S8 | S4 done (`AppendByRewriteAsync`: spools, then writes with `IfMatch` on the version read, and refuses a file without an ETag); S7 done (S3 and MinIO append through it, the store checking `If-Match`); S8 done (GCS appends through it, conditional on the generation); S9 done (SharePoint appends through it); S10 done for Box (`BoxUploadBuffer.CombineAsync` gone) |
| F5 | Physical local stream writes in place; Skip does nothing | S5 | done (S4 made it the base's temp copy; S5 commits it through the atomic write) |
| F6 | Box and SharePoint rewind the caller's stream | S9, S10 | S9 done (SharePoint spools from the position and never seeks, rewinds or disposes the caller's stream); S10 done for Box (spooled from the position, never seeked; `Write_NeverSeeksTheCallersStream`) |
| F7 | SharePoint strips WorkFolderPath from caller paths | S9 | done (S9): D7's fold, the P37 table test, a strict-prefix root refused at construction |
| F8 | SharePoint unbounded itemNotFound retry | S9 | done (S9): parents are created once before the write, the provider retries no request, and Kiota never sees the caller's stream |
| F9 | Commented MinIO test, leaked key in ado history | S0 | done (#17); key rotation is the maintainers' |
| F10 | MinIO plain HTTP by default | S7 | done (S7): D7 as amended; plain HTTP toward a host that is not loopback needs `AllowInsecureHttp` |
| F11 | Download URL lifetime differs per provider | S4, S6–S11 | S4 done (validity rules, read URLs need the file); S6 done (Azure: ends exactly at the validity, 7-day maximum, a user delegation SAS never outlives its key, D18 for a key that would); S7 done (S3, MinIO: SigV4 up to 7 days, never a silent SigV2 fallback, D18 for expiring credentials); S8 done (GCS: V4 URLs live the validity rounded down to whole seconds, 1 s to 7 days, else `NotSupportedException`; `ExpiresOn` read from the URL); S9 done (SharePoint per D18); S11 done (the URL service falls back to the gateway on `NotSupportedException`); S10 done for Box (D18: under 15 minutes `NotSupportedException`, else only with `AllowShorterValidity`, `ExpiresOn` = now + 15 min), so the URL service gives Box URLs that live as asked; every provider gets a URL that lives as asked |
| F12 | Download URL docs wrong | S4, S12 | done: S4 (the interface docs say what a URL lives and when each provider throws); S12 part 2 (every README states its provider's native URLs, their lifetime and when the gateway takes over, and the root README's matrix) |
| F13 | Sync-over-async, no ConfigureAwait, I/O in constructors | S4, S9, S10 | S4 done (ConfigureAwait, one sync helper in Common); S8 done (GCS: no I/O in the constructors; the client, Application Default Credentials and the bucket check on the first call, asynchronously); S9 done (SharePoint looks up the site and library on first use, caching only a success); S10 done for Box (no network I/O in the constructor, sign-in on the first call) |
| F14 | Local stream copies with sync CopyTo | S4 | done (S4): the local stream copies asynchronously |
| F15 | AzureFileStream lacks async reads | S6 | done (S6): reads return the SDK's `DownloadStreamingAsync` stream; `AzureFileStream` is gone |
| F16 | No CancellationToken | S4 | done (S4): every async member takes a token |
| F17 | No ranged reads | S4, S5–S10 | S4 done (API, base default); S5 done (Physical: `RandomAccess` on one handle, `IfMatch` checked on it); S6 done (Azure: ranged download, `OpenReadAsync` seekable stream); S7 done (ranged GETs with `If-Match`); S8 done (GCS: ranged media GET); S9 done (SharePoint: `Range` on the download URL, and a range the service ignores is cut); S10 done for Box (`Range` on the version's download URL) |
| F18 | Missing-file exception differs | S4, S5–S10 | S4 done (the base turns a store's not-found into `FileNotFoundException`; conformance tests); S5 done (Physical: a missing file, a folder or a path below a file, with the BCL's exception inside); S6 done (Azure, conformance on Azurite); S7 done; S8 done (GCS, the `GoogleApiException` as inner); S9 done (SharePoint); S10 done for Box (a 404 after the path was found maps too) |
| F19 | Move semantics differ | S4, S5–S10 | S4 done (the base checks the source and the destination; case-only renames on stores that ignore case); S5 done (Physical takes the destination atomically, `FileExistsException`); S6 done (Azure: server-side copy with `IfNoneMatch = *`, move deletes the source on its ETag); S7 done (copy with `If-None-Match: *`; the source deleted with `If-Match` on the version copied, the copy undone when it changed; atomic on AWS, checked first elsewhere); S8 done (GCS copies with `ifGenerationMatch=0`); S9 done (SharePoint: one `PATCH` with conflict behaviour `fail`, server-side copy); S10 done for Box (files only, destination folders created, Box's 409 is `FileExistsException`) |
| F20 | GetFileInfo on folders differs | S4, S5–S10 | S4 done (the contract and its conformance tests); S5 done (Physical); S6 done (Azure); S7 done; S8 done (GCS); S9 done (SharePoint: a folder's `Length` is -1); S10 done for Box (a folder exists, `Length` -1; a missing path `Length` -1) |
| F21 | PhysicalPath means different things | S4, S5–S10 | S4 done (the contract and its conformance test); S5 done (Physical: the disk path of files and folders, null for a missing path); S6 done (Azure: null); S7 done (null); S8 done (GCS: null); S9 done (SharePoint: null); S10 done for Box (null in info and listings) |
| F22 | Naming rule vs protected camelCase members | S4 | done (S4): the protected members are PascalCase `...CoreAsync` |
| F23 | net8.0 hard-coded, no Nullable | S2 | done (#19) |
| F24 | AWS SDK v3 end of support | S7 | done (S7): AWSSDK.S3 4.0.104.2 |
| F25 | Box.V2.Core 5.8 outdated | S10 | done (S10): Box.V2.Core 10.19.0 (`Box.Sdk.Gen`), no Newtonsoft, no advisory |
| F26 | Azure.Identity unused and deprecated | S6 | done (S6): AzureBlob uses it (`UseDefaultAzureCredential`) at the one shared version, 1.21.0, which SharePoint also takes |
| F27 | Coverage not configured or enforced | S3, S12 | done: S3 (#20, the coverage run and `eng/coverage-gate.cs`); every floor is 90% or more of lines and branches (S12 part 2) |
| F28 | Download URL test passes on NotSupported | S4 (suite), S7, S8 | S4 done (suite: `NotSupportedException` is asserted exactly where the fixture says so, never swallowed); S7 done (offline expiry and credential-lifetime tests; presigned URLs used for real on S3Mock and MinIO, of which only MinIO checks signatures and expiry; on AWS a manual check, see "For the maintainers"); S8 done (`GcsOptionsTests` checks `X-Goog-Expires`, `ExpiresOn` and the signed headers offline; `GcsClientConformanceTests` asserts `NotSupportedException` without a signer) |
| F29 | No cleanup after failed live tests | S4 (suite) | done (S4): each conformance test works under its own root folder, deleted in `[TestCleanup]` whether it passed or failed |
| F30 | Configuration shape not pinned | S3 | done: S3 (#20, the golden corpus and `ConfigurationShapeTests`) |
| F31 | LocalFileStream contract untested | S4 | done (S4): `LocalFileStreamTests` and the `LocalStream_*` conformance tests |
| F32 | Box cannot be constructed offline | S4 (internal test constructor over the 5.8 SDK), S10 (public client constructor, lazy auth) | done (S10): lazy sign-in, the public `IBoxClient` constructor, and the whole suite offline against `FakeBox` (root protection, paging, case-insensitive match) |
| F33 | Move/GetDownloadUrl throw AggregateException | S4 | done (S4): every sync form goes through the one helper, `WriteableFileProviderExtensions.Wait` |
| F34 | LocalFileStream leaks temp stream on failed upload | S4 | done (S4): the local stream always disposes its temp file |
| F35 | Additions must not break implementers | S4 (moot in a major; recorded) | done (S4): moot in the major; new members ship with defaults in the base |
| F36 | 45-member interface | S4 | done (S4): one member per operation on `ProviderPath`; the rest are extensions |
| F37 | LocalFileStream.DisposeAsync hides Stream's | S4 | done (S4): `LocalFileStream` overrides `DisposeAsync` |
| F38 | Move deletes a prefix if the key vanished | S4, S7, S8 | S4 done (`DeleteFileCoreAsync`, and the conformance test `Move_RemovesExactlyTheSourceKey`); S7 done (one `DeleteObject` of the exact key); S8 done (GCS deletes exactly the copied generation) |
| F39 | SharePoint chunked upload fragile | S9 | done (S9): a provider-owned session loop over a spooled copy, 320 KiB multiples, one bounded resend from the session's status, the session cancelled on failure |
| F40 | Azure SAS starts now | S6 | done (S6): `StartsOn` is now − 15 minutes, which a "Block" SAS expiration policy also requires |
| F41 | S3/GCS dispose the caller's stream | S7, S8 | S7 done (the stream is read into part buffers and never handed to the SDK); S8 done (GCS uploads through a forward-only wrapper that never disposes it) |
| F42 | MSTest 3.6/VSTest dated | S3 | done: S3 (#20, MSTest 4 on Microsoft.Testing.Platform) |
| F43 | SharePoint empty folder lists as missing | S9 | done (S9) |
| F44 | Bucket auto-create differs | S6, S7, S8 | S6 done (Azure: `CreateContainerIfMissing`, default false; reads never create); S7 done (`CreateBucketIfMissing`, false by default, checked once on first use); S8 done (GCS: `CreateBucketIfMissing`, default false; `InvalidOperationException` otherwise) |
| F45 | MinIO download URL unsupported | S7 | done (S7): read, create and write URLs |
| F46 | .sln, no CPM | S2 | done (#19) |
| F47 | Public LocalFileStream ctor bypasses root | S0, S4 | done: S0 (#17) checked it; S4 makes the constructor internal |
| F48 | JObject in the public API | S4 | done (S4): System.Text.Json; no Newtonsoft in any package |
| F49 | Graph.Core 3.2.6 deprecated pin | S9 | done (S9): Microsoft.Graph 6.8.0 and Graph.Core 4.0.1 (Kiota 2.0.0); the pin and its comment go |
| F50 | Common's Version 1.1.2 | S2 | done (#19) |
| F51 | MinIO and S3 duplicate the protocol | S7 | done (S7): one internal `S3Core`, shared source compiled into both packages |
| F52 | Providers not disposable | S4 | done (S4) for the base; S5–S10 dispose their clients |
| F53 | Root guard written seven times | S4 | S4 done (one resolution in the base); S5 done (Physical's own root guard goes; it keeps only the check that the disk path is under the root); S6 done (Azure has no guard of its own); S7 done (no guard of their own); S8 done (GCS's own guard is gone); S9 done (SharePoint's guard is gone); S10 done for Box (its guards are gone) |
| F54 | Seven identical Watch overrides | S4, S5 | done: S4 removed the seven overrides; S5 implements Physical's `Watch` through `PhysicalFilesWatcher` |
| F55 | UseLocalCommon switch | S2 | done (#19) |
| F56 | Test-suite dead weight | S2 (runsettings, publish profile), S5–S10 (with TestBase) | done: S2 (#19, runsettings and the publish profile); S5–S10 (each provider's TestBase suite and settings left with its rewrite: S5 deleted `PyhsicalFileProvider` (its TestContext stub), `PhysicalReadTests`, `SegmentValidationTests` and the Physical test setting; S8 GCS's suite, its TestContext stub and its settings keys); S12 part 2 deleted the old test project, `TestBase` with it |
| F57 | Contract cases missing from the shared suite | S4 | done (S4): the conformance suite |
| F58 | Root and segment exceptions undocumented | S4 | done (S4): the interface documents them |
| F59 | Interface docs copy-paste | S4 | done (S4): the interface docs are rewritten |
| F60 | XML docs only for Common | S2 | done (#19) |
| F61 | Serializer and options undocumented | S4, S12 | done: S4 (the serializer); every options property is documented, which the strict build (missing docs are errors) holds on every package since the provider steps |
| F62 | SharePoint folder creation three ways | S9 | done (S9): one iterative creator with conflict behaviour `fail` |
| F63 | Whole-object download before read | S8, S9, S10 (S7 MinIO via S3Core) | S7 done for MinIO (reads stream); S8 done (GCS reads stream); S9 done (SharePoint streams from the download URL); S10 done for Box (reads stream the download) |
| F64 | AzureFileStream vs OpenReadAsync | S6 | done (S6): `OpenReadSeekableAsync` is `OpenReadAsync`, pinned to its ETag |
| F65 | S3 single PutObject (5 GB) | S7 | done (S7): multipart above 8 MiB, up to 10,000 parts; copies over 5 GiB in parts |
| F66 | SharePoint maps all errors to not-found | S9 | done (S9): only a 404 is missing; 401 and 403 throw `UnauthorizedAccessException`, the rest `IOException` |
| F67 | Box offset paging | S10 | done (S10): marker paging, 1000 a page |
| F68 | Assertion bugs in the shared suite | S3 | done: S3 (#20) |
| F69 | FileInfo collides with System.IO.FileInfo | S4 | done (S4): `WriteableFileInfo` |
| F70 | UploadResult and AzureFileStream public | S6, S9 | S6 done (`AzureFileStream` removed); S9 done (`UploadResult<T>` deleted) |
| F71 | net10-only drops .NET 8 consumers | S2 (D2) | done (#19) |
| F72 | Unused packages | S2, S4 | S2 part done (#19); S4 done (Newtonsoft removed from every project) |
| F73 | Minio forces Extensions 9 and Reactive | S7 | done (S7): the Minio SDK is gone |
| F74 | Temp-file spool written nine times | S4 | S4 done (`SpoolToTempFileAsync`, `AppendByRewriteAsync`); S5 done (Physical has one write path, its temp file next to the target, which is its spool); S6 done (Azure appends spool through it); S7 done (append through `AppendByRewriteAsync`; writes stream in parts, no spool); S8 done (GCS spools only through `AppendByRewriteAsync`); S9 done (SharePoint uses both); S10 done for Box (`BoxUploadBuffer` gone) |
| F75 | GCS has no emulator seam | S8 | done (S8): the `Endpoint` option, the `StorageClient` constructor, and the conformance suite on fake-gcs-server |
| F76 | Package descriptions and READMEs | S12 | done: S12 part 2 (a description and tags in every `.csproj`, the shared metadata in `src/Directory.Build.props`, a README per package packed through `PackageReadmeFile`, and the root README with the provider matrix); the icon waits for the maintainers ("For the maintainers", final review F17) |
| F77 | Dead code in providers | S5, S9, S10 | S5 done (Physical's `O` field, `info`, `isRoot`, `getPathSegments` and the unused usings go); S9 done (SharePoint rewritten); S10 done for Box (the catch-all rethrows are gone) |
| F78 | S3 `StorageClassForNewFiles` is written as `{"Value":…}` and read back as `STANDARD`, and the configuration path never reads it at all (found in S3 by the golden corpus) | S4, S7 | done (S4): both forms read, the string is written, configuration converts it; S7: the class read from either form reaches the store on new files and copies (S3Mock test) |
| F79 | On Unix, Physical's `Options.RootDirectoryPath`, and so its `ToJson` output, carries the internal `root:` marker of an absolute path instead of the path (found by S3's first Linux CI run) | S5 | done (S5): `RootDirectoryPath` is the real path on every system; the stored `root:` forms still read |

## Feature tracker

| Feature (roadmap) | Step | Status |
|---|---|---|
| P1.1 Index-based reads: ranges and seekable streams, all seven | S4, S5–S10 | done: S4 (API, defaults, conformance); S5 (Physical); S6 (Azure native); S7 (S3, MinIO); S8 (GCS); S9 (SharePoint); S10 (Box, pinned to the file version) |
| P1.2 Signed URLs, read and write, all seven, and the gateway | S4, S6–S11 | done: S4 (API, validity rules); S6 (Azure: service and user delegation SAS, read, create, write); S7 (S3, MinIO: read, create, write); S8 (GCS V4: read, create, write); S9 (SharePoint: read only, D18); S10 (Box: read only, D18); S11 (the gateway and the URL service: read, create and write URLs for every provider) |
| P1.3 Cancellation everywhere | S4 | done (S4) |
| P1.4 Dependencies current | S2, S6–S10 | done: S6 (Azure.Storage.Blobs 12.30.1, Azure.Identity 1.21.0); S7 (AWSSDK.S3 4, the Minio SDK removed); S8 (Google.Cloud.Storage.V1 5.0.0); S9 (Microsoft.Graph 6.8.0, Graph.Core 4.0.1); S10 (Box.V2.Core 10.19.0) |
| P2.5 Streaming writes without temp-file round trips where the SDK can | S5–S8 | done: S5 (Physical streams into its temp file next to the target, no spool); S6 (Azure overwrites stage blocks as they read; appends spool, to retry); S7 (one part in memory at a time); S8 (GCS uploads stream from the caller's stream). Box and SharePoint spool to a temp file, which their uploads need |
| P2.6 ETags and conditional writes | S4, S5–S10 | done: S4 (API, non-atomic default); S5 (Physical: synthesized ETag, atomic `IfNotExists`, `IfMatch` checked before the rename); S6 (Azure, atomic); S7 (atomic on AWS; on MinIO and other S3-compatible servers checked first and sent to the server); S8 (GCS: the generation, atomic); S9 (SharePoint: checked, and sent to the service; D15 as amended); S10 (Box: `IfNotExists` atomic, `IfMatch` checked until the live run, D15) |
| P2.7 Content type on write and read | S4, S5–S10 | done: S4 (API, hint); S5 (Physical keeps none; the hint is ignored, D24); S6 (Azure); S7 (S3, MinIO); S8 (GCS); S9 (SharePoint reports the service's type and ignores the hint); S10 (Box keeps none: ignored, null) |
| P2.8 Async listing, recursive | S4, S5–S10 | done: S4 (API, default walk); S5 (Physical: its own walk, which lists linked folders without entering them); S6 (Azure: one flat listing); S7 (a recursive walk is one paged listing); S8 (GCS: paged prefix listing, the base's walk); S9 (SharePoint: paged children, the base's walk); S10 (Box: marker paging, recursive by folder id) |
| P2.9 Dependency injection | S4, S11, S12 | done: S4 (registration); S11 (`AddWriteableFileProviderGateway`, `MapWriteableFileProviderGateway`, keyed providers resolved by name); S12 part 2 (one provider by interface, `AddWriteableFileProviderGateway(publicBaseUri)`, the URL service for the default provider or a key) |
| P2.10 More credential options | S6, S7, S8 | done: S6 (`UseDefaultAzureCredential`, a `BlobContainerClient` constructor); S7 (`UseDefaultCredentials`, a caller's `IAmazonS3`); S8 (GCS: a given `StorageClient`, the `Endpoint`, lazy Application Default Credentials); S9 (SharePoint: certificate and `DefaultAzureCredential` sign-in, a `GraphServiceClient` constructor); S10 (Box: an `IBoxClient` constructor) |
| P2.11 Server-side copy, GCS native move | S4, S5–S10 | done: S4 (API, default); S5 (Physical: a native rename; copy streams into the destination's temp file); S6 (Azure Copy Blob); S7 (CopyObject, in parts above 5 GiB); S8 (GCS copies by rewrite; native move stays under Later, D16); S9 (SharePoint: `/copy` and its monitor, move by `PATCH`); S10 (Box: copy and move on the server) |
| P2.12 Physical Watch | S5 | done (S5): `PhysicalFilesWatcher` |

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
  - Main-session check of the round: every fix verified; the live suite step now names `shell: bash`, because the
    default shell runs without `pipefail`, so a `jq` that failed in a masking pipeline went unnoticed and the step
    carried on unmasked.

- **2026-10-11, S6 (#10):** AzureBlob on the 2027 contract, branch `10-azureblob`. Common is unchanged.
  - **Writes (D5, D15, D19, F3):** an overwrite stages blocks while it reads the stream and commits them once, with the
    caller's condition on that commit (`SupportsAtomicPreconditions`); no write uses Put Blob. An append spools its
    content (`SpoolToTempFileAsync`), then commits the block list read plus the new blocks on `IfMatch`, retrying after a
    concurrent change up to 5 times. A blob with no block list (one Put Blob), foreign block ids, or no room under the
    50,000-block limit is first restaged on the server (Put Block From URL, under a read SAS or a user delegation SAS),
    or through the client when the provider cannot sign one. Content type `application/octet-stream` unless given;
    appends carry headers, metadata and a set tier. Page blobs are read-only (`NotSupportedException`).
  - **Append blobs from 2026 (P30):** written through a temp blob under `.wfp-tmp/` in the provider root, deleted with
    `IncludeSnapshots` on its ETag, copied, verified (`CopyId` and status), then the temp removed; a failure after the
    delete keeps the temp and names its path. A copy of an append blob stays an append blob until its first write.
  - **Reads (F15, F17, F64):** `DownloadStreamingAsync`; native ranges (416 and length 0 still check `IfMatch`);
    `OpenReadSeekableAsync` is the SDK's `OpenReadAsync`, wrapped only to turn its 412/404 into
    `PreconditionFailedException`/`FileNotFoundException`. `AzureFileStream` is gone (F70).
  - **Listings:** recursive enumeration is one flat listing; `.wfp-` names and names the provider cannot address are
    left out. ETags are normalized to the quoted form (a HEAD returns them quoted, a listing bare).
  - **Moves and copies (F19, P19):** Copy Blob with `IfNoneMatch = *` and the source's ETag; a move deletes the source on
    that ETag, and when it changed meanwhile removes the copy and throws `PreconditionFailedException` (a source deleted
    meanwhile completes the move).
  - **Signed URLs (F11, F40, D18, D24):** service SAS, or user delegation SAS (key cached, a refusal remembered for 10
    minutes); `StartsOn` now − 15 minutes; Read, Create (`c`) and Write (`w`); a key that would expire first needs
    `AllowShorterValidity`. Upload URLs refuse `ContentType` and `DownloadFileName`, which a SAS cannot bind (D24), so
    the gateway takes over.
  - **Containers (F44, P18):** a missing container throws `InvalidOperationException` from every member;
    `CreateContainerIfMissing` lets writes and upload URLs create it.
  - **Options:** `CreateContainerIfMissing` and `UseDefaultAzureCredential` are written to JSON only when true
    (`[JsonIgnore(WhenWritingDefault)]`), so a configuration that uses neither keeps the 2026 shape and Common's shape
    tests stand unchanged; a later provider step adding options can do the same. The D17 constructor takes a
    `BlobContainerClient`. Azure.Identity stays at 1.21.0 (a facade over Azure.Core 1.60.0) as the line SharePoint
    shares; Azure.Storage.Blobs is 12.30.1.
  - **Main-session decisions applied:** the SAS start (F40); `IncludeSnapshots` for the append blob conversion only
    (delete and move refuse a blob with snapshots, review round 1); the `.wfp-tmp/` prefix and hidden `.wfp-` entries;
    the move rollback; the test-only pipeline for Azure's limits with internal limit settings; the two options.
  - **Deviation from the Azure row:** "service SAS with no start time" became a start 15 minutes back (the decision);
    the PublicAPI files are not added, as Common has none yet (S12).
  - **Tests:** `tests/WriteableFileProviders.AzureBlob.Tests`, 232 tests. The conformance suite runs twice on Azurite:
    from options, and over a client whose pipeline (`AzureHandler`) refuses what Azure refuses and Azurite allows, with
    the block limit lowered to 6. No capability flag skips a rule (content types and signed URLs on). Mutation checks: a
    Put Blob over an append blob fails the strict tests; dropping the ETag normalization fails the listing test. The
    old Azure suites in `WriteableFileProviders.Tests` are deleted, with their Azurite fixture and settings.
  - **Coverage:** AzureBlob 98.5% lines, 89.9% branches; floor 97/88.
  - **Not verified without an account:** Azure's own refusals that the test pipeline emulates (the exact codes for a Put
    Blob or Copy Blob onto an append blob, `BlockCountExceedsLimit`), Put Block From URL with the blob itself or an
    append blob as the source, Get Block List leaving out the ETag of a blob with no blocks, the user delegation key
    flow with a real Entra identity, and tags after a commit (not carried).
  - **Review round 1 (PR #29):** needs-fixes, 2 majors, 4 minors, 8 nits, all fixed:
    - F1: an unconditional overwrite whose staged blocks another writer's commit dropped (`InvalidBlockList`) stages one
      block's worth again, up to five attempts in all, and otherwise throws `IOException` saying nothing was stored; with
      a condition it throws `PreconditionFailedException`. Tested against a real competing commit on Azurite.
    - F2: the user delegation key ends at most 7 days less 5 minutes from now; a URL that would outlive it follows D18.
      Only 401/403 are remembered as refusals. The test service refuses a key past 7 days.
    - F3: a copy onto an existing blob counts as destroying it: on failure the temp blob is kept and named, and an
      aborted copy never deletes a blob it was overwriting.
    - F4 (owner call, fail closed): delete and move never delete snapshots; `SnapshotsPresent` is an `IOException`.
      `IncludeSnapshots` stays only in the P30 conversion.
    - F5: the conversion's window is "missing, or empty while the server copy runs", here, in P22, the README, the XML
      docs and the migration guide.
    - F6: a server copy still pending after 10 minutes (as 2026) is aborted and handled as a failed copy.
    - F7–F12, F14: appends size their blocks to fit and have their own limit message; "five attempts in all"; the
      create-on-append path stages inside its try and treats a concurrent append blob or commit as a lost race; only a
      412 whose code says a condition failed counts as one (a lease's 412 is reported as it is); the refusal of a
      server copy is remembered for 10 minutes, not for good; the move rollback says whether the copy was removed;
      AGENTS.md names the Azure live suite's settings.
    - D25: `WriteableAzureBlobFileProvider` is sealed. D26: the two options keep `WhenWritingDefault`.
    - After the rebase onto S11: 253 Azure tests; checked also with S8's pending Common change applied (its
      content-type conformance test passes on both Azurite fixtures). Coverage 99.2% lines, 91.1% branches; floor 98/90.
  - **Review round 2 (PR #29):** F1–F14, D25 and the S8 content-type claim verified; one minor and four nits, all fixed:
    - N1: a move whose source delete Azure refuses for any other definite reason (a 4xx such as a lease's
      `LeaseIdMissing`) rolls back the copy and throws `IOException`, as for snapshots; 5xx, network errors and
      cancellation are left as they are, since the delete may have happened. Tested with a leased source on Azurite.
    - N2: an overwrite of exactly one block is all in memory too, so a dropped-blocks failure stages it again. Tested.
    - N3: the cancelled-copy test cancels from the first poll after the copy started, not on a timer.
    - N4: the docs of `Clock`, `MaxAttempts` and `DeleteFileForTestsAsync`; the Azure row above says "missing, or empty
      while the server copy runs".
    - N5: AGENTS.md says the Azure live suite runs only with `WFP_LIVE=1` and is inconclusive without it.

- **2026-10-11, S9 SharePoint (#13):** built in a forked session on branch `13-sharepoint`; live suite not run (D21).
  - **Provider:** `WriteableSharePointFileProvider` on Microsoft.Graph 6.8.0 and Graph.Core 4.0.1 (Kiota 2.0.0), strict
    (`WfpLegacy` gone), every public member documented. The main-session decisions for S9 are applied: the caller's
    stream is spooled (Kiota disposes request bodies); the library's top level is `items/root` (`ItemWithPath("")`
    throws); every sub-request has its own builder helper (the typed-builder trap); the site path is unescaped; the
    provider owns, and disposes, the `HttpClient` and certificate it creates; `AggregateException` after the SDK's
    retries maps to `IOException`; the upload loop and the copy polling are the provider's own, not
    `LargeFileUploadTask` or `AsyncMonitor`; a 0-byte file reads empty; D7's fold with the P37 table; D17's three
    sign-ins and the `(options, GraphServiceClient)` constructor; `UploadResult<T>` is gone.
  - **Deviations, each recorded in the migration guide or the code:**
    - Reads take the file's `@microsoft.graph.downloadUrl` from its metadata and stream it, rather than following
      `/content`'s redirect: the same round trips, no dependence on a caller's client having a redirect handler, the
      size known before the body (so a 0-byte file needs no download), and ranges sent where Graph says to send them.
    - Every `%` in a path is escaped before `ItemWithPath`, which leaves a valid escape such as `%41` as it is for the
      service to decode, so `a%41` names an item called `a%41` (review round 1; the first version refused such
      segments). Listings leave out names the provider cannot address, such as one that decodes to `..`.
    - `WriteableSharePointFileProvider` is `sealed` (D25, decided by the main session for every provider).
    - Kiota stays at Graph.Core's floor, 2.0.0. An error body that is not JSON (a proxy's HTML) raises
      `InvalidOperationException` there, which the provider maps to `IOException`; pinning Kiota 2.1.4, which raises
      `ApiException` instead, is left to the maintainers (.local/research/graph-v6.md, section 0).
    - The three D17 options are written to JSON only when set (D26, a main-session decision), so SharePoint keeps its
      row in Common.Tests' `ConfigurationShapeTests`; `SharePointConfigurationShapeTests` adds the cases with them set,
      reading the same golden file.
    - Common is unchanged.
  - **Conformance fixtures and their flags:** `SharePointConformanceTests` (a caller's `GraphServiceClient`, a token for
    Graph's host only, a site URL with an escaped space) and `SharePointWorkFolderConformanceTests` (the options
    constructor, a work folder the root repeats in another case, and Graph's documented variants: ranges ignored,
    sessions that list their ranges, a copy monitor ending with 200, listings two items a page). Flags: real folders,
    case-insensitive, read URLs with a fixed lifetime of one hour (D18), the missing container is a missing library,
    and the large file crosses upload sessions (thresholds lowered to 256 KiB and 320 KiB fragments). Two flags skip a
    rule:
    - `KeepsContentType = false`: SharePoint names the type from the file name and takes no hint (D24); a unit test
      checks the reported type and the ignored hint.
    - `DetectsConcurrentAppends = false`: D15 as amended, checked not atomic until the live run; unit tests show the
      `If-Match` the provider sends refusing a change that lands after the check, on a simple upload, a session and an
      append.
  - **The fake** (`GraphFake`, .local/research/graph-v6.md section 7) sits at the end of the SDK's own pipeline. For
    S10: its responses must carry `RequestMessage`, as a real handler's do, or the SDK's retry handler never retries.
  - **Tests and coverage:** SharePoint.Tests runs 236 offline tests (68 in each fixture, 100 unit tests); all pass but
    the two concurrent-append cases the flag skips. The full non-Live run (`WFP_REQUIRE_DOCKER=1`) passes. SharePoint
    measures 99.87% of lines and 92.71% of branches on Windows (after review round 1, see below). Old SharePoint tests deleted:
    `SharePointPathTests` (its colon and paging cases live on in the new tests; its work-folder cases pinned the 2026
    behaviour D7 removes) and the `SharePointFileProvider` TestBase suite, replaced by `SharePointLiveTests`.

- **2026-10-11, S9 review round 1 (PR #28):** needs-fixes (1 major, 7 minors, 6 nits), all fixed:
  - **F1 (major):** every `%` in a library path is escaped as `%25` before `ItemWithPath`, so names holding `%XX`
    round-trip; the segment hook refuses only `:` again, and Common's decoded-form validation guards caller paths as on
    the other providers. Listed names that fail the provider's validation (such as `%2E%2E`) are never yielded or
    walked into. Tests seed `a%41` beside `aA`, `50%.txt`, `%2E%2E` and `%252E%252E`.
  - **F2:** in an upload session, a 416 resyncs from the session's status within the one resend per fragment, a 202
    that does not move past the fragment counts against it, and only 500, 502, 503, 504 and network faults are
    transient (503 and 504 reach the provider only from a client without the SDK's retry handler).
  - **F3:** a malformed JSON answer (`JsonException`) maps to `IOException`. Kiota stays at 2.0.0.
  - **F4:** a server-side copy is waited for at most 10 minutes, then throws `IOException` naming its last status.
  - **F5:** D25, the migration guide says to wrap `IWriteableFileProvider` or derive from `WriteableFileProvider<TOptions>`.
  - **F6:** D19 and the Compatibility P2 line include SharePoint simple uploads as not atomic.
  - **F7:** AGENTS.md's live-suite text: a filter by category and provider, inconclusive without `WFP_LIVE=1`, where a
    provider's own Live class reads its settings, and `SharePointConfigurationShapeTests`.
  - **F8:** an http `SiteUrl` is accepted as in 2026; only the host and path are used.
  - **Nits:** a file whose size Graph does not report is downloaded rather than read as empty; tests show the owned
    HTTP pipeline disposed and a caller's left alone; the fake's two assumptions are marked and the second added to the
    live checklist; plan ids are gone from the public XML docs; the floor is the whole-point floor of the measured
    figures; the options constructor disposes the certificate and HTTP client when a later step throws.
  - **D26:** the three 2027 options carry `[JsonIgnore(Condition = WhenWritingDefault)]`; the SharePoint row is back in
    Common.Tests' `ConfigurationShapeTests`.
  - SharePoint.Tests now runs 245 offline tests. Coverage on Windows: 99.87% of lines, 91.46% of branches; floor 99/91
    (it takes the Linux figure instead if CI measures lower, as the Tests section says).

- **2026-10-11, S9 review round 2 (PR #28):** every round-1 finding verified; the minors are fixed after rebasing onto
  S11 (bce935c):
  - **N1:** an upload session's one resend per fragment is tracked by offset: a failure or an answer without progress
    at or before the offset of the last resend ends the upload, so a session that keeps sending the upload back never
    loops, and a resync that moves forward leaves the next fragment its own resend.
  - **N3 (owner call, fail closed):** a valid percent-escape in `WorkFolderPath` or `RootDirectoryPath` is refused at
    construction with an `ArgumentException` that says to decode it, so a value copied from a browser never silently
    names another folder; the migration guide's percent row and D7 table name both options.
  - **N4:** D27 is added, and the contract's Listing rule says listings show only addressable names.
  - **N5:** the README and `CopyCoreAsync` say a copy that runs past the 10-minute wait may still create the destination.
  - **N6:** with `WFP_LIVE=1` and no account, `SharePointLiveTests` fails naming what is missing; AGENTS.md says so.
  - **N7–N11:** the README names the resent statuses; the copy wait uses `Task.Delay(delay, TimeProvider, ct)`; the
    segment test is split by cause; the live checklist is numbered in order; a file whose size Graph does not report
    has `Length` -1, and the provider's seekable read measures it from one byte of its download.
  - SharePoint.Tests runs 255 offline tests. Coverage on Windows: 99.88% of lines and 91.74% of branches; the floor
    stays 99/91. The full non-Live run after the rebase passes (856 tests, 0 failed).

- **2026-10-11, S9 review round 3 (PR #28):** every round-2 fix verified; one minor and one nit, fixed:
  - **R3-1 (option a):** a file whose size Graph does not report is measured, unpinned, when its info is read or it is
    listed (its download URL, then one ranged request for its first byte: a 206's `Content-Range`, a 416's for an
    empty file, or a 200's `Content-Length`), so `Length` is always the real length, as Common's contract and the
    gateway's `Content-Length` need; a file gone by then reads as missing and is left out of the listing. Files with
    a size cost nothing more. The `OpenReadSeekableCoreAsync` override, which only existed for the -1, is gone; the
    base handles seekable reads. The README says what the measurement costs.
  - **R3-2:** a 173-character line in `SharePointLiveTests` is wrapped.
  - SharePoint.Tests runs 257 offline tests. Coverage on Windows: 99.76% of lines and 91.22% of branches; the floor
    stays 99/91.

- **2026-10-11, S10 Box (#14):** built in a forked session on branch `14-box` (worktree `D:\workspaces\wfp-wt\14`).
  - **Provider:** rewritten on `Box.V2.Core` 10.19.0 (`Box.Sdk.Gen`) and the 2027 base; builds strict (`WfpLegacy`
    removed). The main-session decisions for S10 are applied: the fake behind a custom `INetworkClient`, ranged reads on
    the version's download URL, uploads spooled to a temp file with no `FileContentType`, a provider-owned chunked loop,
    folders that exist, marker paging, no path cache (D23), lazy sign-in.
  - **D15 amended for Box** (the "All provider steps" decision): `SupportsAtomicPreconditions` is false, so `IfMatch` and
    an append's version check are done just before the write. The wire path (quoted `If-Match` on version uploads and on
    the commit) exists behind the internal `SendIfMatch` switch, and `BoxAtomicConformanceTests` runs the whole suite
    with it on. `IfNotExists` is atomic either way (Box's 409).
  - **Deviations and additions, each for a reason:**
    - D17's constructor takes `IBoxClient` (which `BoxClient` implements, and `WithAsUserHeader` returns), not
      `BoxClient`: it exposes every manager the provider uses, and the network session.
    - Downloads go through the session's network client directly: `GET /files/{id}/content` without following the
      redirect (the SDK's `DownloadFileAsync` drops `Range` on the redirect and hides a 202's `Retry-After`), then the
      pre-authenticated URL, with `Range` and no credentials. A server that ignores `Range` is handled (the stream skips
      to the offset); a 202 is waited on, up to 60 times.
    - A chunked commit that answers 202 is asked again up to 60 times, a second apart; 409
      `operation_blocked_temporary` is retried twice; a name taken or freed by a concurrent writer (409, 404) is
      resolved once more, then the conditions are checked again.
    - A name Box refuses (400 `item_name_invalid`, `item_name_too_long`) is an `ArgumentException` on `path`; a write
      under a file is an `IOException` (was `InvalidOperationException`); a `RootFolderId` that does not exist or the app
      cannot see is an `InvalidOperationException` everywhere, as P18 asks of a missing bucket.
    - A case-only rename that Box refuses as a taken name goes through a temporary name (unverified on Box, item 4 of the
      live checklist), and puts the old name back if the second rename fails.
    - The recursive listing walks by folder id within one enumeration (not a cache: nothing outlives the call).
    - The constructor reads the private key (no network), so a wrong key or passphrase fails there with an
      `ArgumentException` that names neither; a missing credential names the option.
    - `NormalizePem` moved into the provider as `NormalizePrivateKey` (internal), so S12 can remove Common's.
    - No PublicAPI files, as in S4: S12 adds them.
  - **Tests:** `tests/WriteableFileProviders.Box.Tests` (offline, no Docker): the conformance suite twice (as shipped,
    through the options and the fake's token endpoint, which checks the JWT against the test key; and with a ready
    client and `If-Match` on), and unit tests for sign-in, options and the stored shape (the Box goldens are linked from
    Common.Tests and build a provider offline), paging, ranges, downloads, uploads, races, moves, deletes and signed URLs.
    `FakeBox` is a stateful Box API (an `HttpMessageHandler`); `FakeNetworkClient` copies the SDK client's quirks
    (rewinds request streams, validating headers, the redirect that drops `Range`, the retry strategy). Mutations of
    marker paging, spooling, `If-Match`, folder info and version pinning each fail tests. The live suites
    (`BoxLiveTests`, `BoxLiveAtomicTests`, `BoxLiveChecks`) were not run (D21); "For the maintainers" lists what they
    confirm. Capability flags: real folders, case-insensitive, read URLs with a fixed 15-minute lifetime, no content
    type. (`DetectsConcurrentAppends` was false in `BoxConformanceTests` at first; review round 1 turned it on.)
  - **Old tests:** `BoxFileProvider`, `BoxOfflineTests`, `BoxOptionsTests`, the Box settings and the Box project
    reference leave `tests/WriteableFileProviders.Tests`.
  - **Coverage:** Box 99.63% lines, 89.38% branches (Windows); floor 98/88.
  - **Verified:** the solution builds; every non-Live test passes with `WFP_REQUIRE_DOCKER=1` (587, 31 skipped: the S3
    suites without `AWS_ENDPOINT_URL_S3`, and the concurrent-append rule in `BoxConformanceTests`); the coverage gate
    passes; the Box package packs with its README.
  - **Review round 1** (PR #25), every finding fixed:
    - F1: a file's `ETag` is now the id of its current Box file version (or `{itemId}:{etag}` where Box sends none),
      since Box's own `etag` starts again at `0` for a file deleted and created again; reads, writes and appends compare
      it, and the wire `If-Match` carries Box's etag of the item found. Delete-and-recreate tests pin both.
    - F2: AGENTS.md keeps Box among the providers whose live suite stands for the emulator suite; its offline suite
      runs on top (D21).
    - F3: a write compares the ETag of the file it found with `IfMatch` at once, whether or not the header is sent.
    - F4: a failed download throws `FileNotFoundException` or `IOException` with its HTTP status only (the SDK's
      error names the pre-authenticated URL); the provider's sign-in is wrapped (`RedactedAuthentication`), so a refused
      token request surfaces as `BoxApiException` without the request body (client secret, assertion). A ready client's
      authentication is the caller's.
    - F5: the download wait is bounded by time (two minutes), the commit wait by time and size (two minutes plus two
      per whole GiB), both documented; a listing that repeats a page marker throws `IOException`.
    - F6: `BoxLiveChecks` commits an upload session with a stale quoted ETag (expects 412) and pages the root listing.
    - F7: AGENTS.md's "Where things are" names the per-provider test projects. F8: the folder-creation error says when
      the name is taken by something that is neither a file nor a folder. F9: a case-only rename's temporary name is
      short whatever the file's name, and a name Box refuses on that path is an `ArgumentException`.
    - F10: the concurrent-append rule runs in `BoxConformanceTests` and `BoxLiveTests` (it passes: the race it stages
      lands before the check); D15 still says the check is not atomic.
    - D25: `WriteableBoxFileProvider` is sealed (in the migration guide). D26: Box adds no options property.
    - Verified after the round: every non-Live test passes with `WFP_REQUIRE_DOCKER=1` (601, 30 skipped, all of them
      the S3 suites without `AWS_ENDPOINT_URL_S3`; no Box test is skipped); Box 99.33% lines, 90.40% branches, floor
      98/89; the coverage gate passes. Mutations of the ETag form and of both redactions each fail tests.
  - **Review round 2** (PR #25), F1–F10 verified, N1–N6 fixed:
    - N1: `RedactingNetworkClient` wraps the session's network client (the provider's own, and a copy of a ready
      `BoxClient`'s session), so every `BoxApiException` it throws or wraps (listing, upload, delete, move, copy, a
      precondition, a refused name, a not-found, a token request) has no `Authorization` header, no other credential
      header or parameter, and no body; status, code and message stay. It replaces round 1's `RedactedAuthentication`.
      The download keeps its status-only message (the SDK's error names the pre-authenticated URL). `BoxRedactionTests`
      walk each exception graph by reflection. Another kind of `IBoxClient` is used as it is.
    - N2: decided by the main session: D25 (every provider sealed) and D26 go into the Decisions table in S11's PR; Box
      stays sealed and cites D25.
    - N3: the live commit check asks again while Box answers 202, then expects 412; its cleanup is best effort.
    - N4: the timing test checks only an upper bound on the polls, which holds on any machine.
    - N5: the pinned read across a delete-and-recreate expects exactly `PreconditionFailedException`.
    - N6: `ProviderConformanceTests.DetectsConcurrentAppends` now reads "false where the staged race cannot be detected"
      (shared test code).
    - **Common change**, asked by the main session: the base class's case-only rename goes through `.wfp-<guid>.tmp`
      (41 characters; `.wfp-rename-<guid>.tmp` since round 3) instead of `.<name>.wfp-<guid>.tmp`, so a long name on a store with a 255-character limit
      still renames; `CaseOnlyRenameTests.Rename_OfALongName_FitsTheStoresNameLimit` pins it (it fails with the old
      name).
    - Verified after rebasing onto S11 (`bce935c`): every non-Live test passes with `WFP_REQUIRE_DOCKER=1` (829, 30
      skipped, all the S3 suites without `AWS_ENDPOINT_URL_S3`); Box 99.66% lines, 90.38% branches (floor 98/89
      unchanged); the coverage gate passes. Without the redacting client, the 14 redaction tests fail.
  - **Review round 3** (PR #25), N1–N6 verified; rebased onto S8 (`e1c4b52`), whose content-type rule
    (`FileInfo_AppendAndLocalStreamKeepTheContentType`) returns early on Box, which keeps no content type:
    - F1 (major, security): `RedactingNetworkClient` now fails closed. Of the request it keeps only the method, the URL
      without its user info, query and fragment, and allow-listed headers (`As-User`, `If-Match`, `Range`, `Digest`,
      `Content-MD5`, `Content-Type`) and query parameters (the listing, version and delete ones); of the response's
      headers, `Retry-After`, `Content-Type`, `Date` and `Box-Request-Id`. A ready client's extra headers (a `BoxApi`
      shared-link password, a proxy's or a cookie) and the query string no longer survive. Tests: the unit test's URL
      carries a query and its headers carry `BoxApi`, `Proxy-Authorization`, `Cookie` and a custom header; a ready
      client built `WithExtraHeaders` with a `BoxApi` password, whose errors carry neither anywhere; `CredentialAssert`
      walks non-generic dictionaries (`Exception.Data`, `Dictionary<string, object>` context values) and every value's
      text, and a test proves it catches both. With a deny-list and the query kept, three of them fail.
    - F2: the class remarks, the D17 constructor's docs, the README and the migration guide say that only a provider
      that built its client, or was given exactly a `BoxClient`, redacts; over any other `IBoxClient`, a class derived
      from `BoxClient` included, the errors are the client's own and may carry its credentials.
    - F3: a ready client built `.WithAsUserHeader("7")` sends `As-User: 7` on every API call of the provider, its own
      download URL requests included.
    - F4: `BoxOptionsTests.ReadyClient_SignsInForTheProvider_AndOutlivesIt`; the Sign-in remark describes the copy.
    - F5: the base class's case-only rename (and Box's own) goes through `.wfp-rename-<guid>.tmp` (48 characters), apart
      from Physical's write temp `.wfp-<guid>.tmp`, which Physical's listings hide; `CaseOnlyRenameTests` follows.
      Physical's hiding rule must match only `.wfp-` followed by 32 hex digits and `.tmp` for the two to stay apart.
    - Coverage: new tests for the redaction of an error that holds little, upload sessions without an id or a usable
      part size, a `Retry-After` that is not a number, and a listing entry with no version; Box 99.67% lines, 93.98%
      branches, floor 98/92.
    - Verified after the rebase onto `e1c4b52`: every non-Live test passes with `WFP_REQUIRE_DOCKER=1` (1065, 30
      skipped, all the S3 suites without `AWS_ENDPOINT_URL_S3`); the coverage gate passes.

- **2026-10-11, S7 (#11):** AmazonS3 and MinIO on AWSSDK.S3 4.0.104.2, built on branch `11-s3-minio`.
  - **Shared core:** `src/Shared/S3/` (`S3Core`, `S3CoreSettings`), internal, compiled into both packages through a
    linked `<Compile Include>`. Chosen over `InternalsVisibleTo` between the packages, which would make Minio depend on
    AmazonS3 at exactly its version and fail at run time on a skewed pair. Each assembly carries the code and is
    measured on its own, so the test code shared in `tests/Shared/S3` (emulators, the HTTP seam, the behaviour and
    offline cases) runs in both test projects.
  - **Writes:** a provider-owned multipart loop instead of `TransferUtility`, which disposes a non-seekable stream,
    always multiparts one, and aborts out of sight. Parts are read into a pooled buffer, so the caller's stream is
    never seeked or disposed and the SDK can rewind a part to retry. One PUT up to `PartSize` (8 MiB); parts grow for
    an unknown length. CreateMultipartUpload names no checksum algorithm and the completion sends part ETags only, the
    SDK's low-level default (AWS unverified; on the maintainers' live checklist).
  - **Decisions applied** (`.local/briefs/decisions-s5-s10.md`):
    - `UseDefaultCredentials` is opt-in; keyless configurations stay anonymous.
    - A caller's `IAmazonS3` presigns only with `AllowShorterValidity`, and `ExpiresOn` is then null.
    - The core sends checksums only where required (`WHEN_REQUIRED`) and presigns `Protocol = HTTP` for an http
      endpoint.
    - The 7-day cap is `MaxSignedUrlValidity`, plus a guard found here: the SDK measures expiry on its skew-corrected
      clock, and at exactly 7 days it signed with SigV2 in a local run (S3Mock's container clock). Such a URL is now
      refused with `NotSupportedException`, pinned offline with `AWSConfigs.ManualClockCorrection`.
    - No content type stores `application/octet-stream`. The `S3Region` constructors store system names. MinIO sends no
      storage class. The LocalStack suites are gone, and S0's positive test is `ReadFileVersionAsync` on `ProviderPath`
      against a versioned S3Mock bucket.
  - **Beyond the plan:**
    - An append, and a local stream's commit, keep the file's content type when the caller names none: Common does
      this since S8, so the provider keeps no helper of its own.
    - HeadBucket answering 403 or a redirect counts as "exists, or cannot be asked", so tight IAM policies keep working;
      a missing bucket is still reported by the first `NoSuchBucket`, and the check runs again after one.
    - Listings skip keys a path cannot name (D27): a blank segment, one of only dots and spaces such as `..`, a
      backslash, a control character, or a percent-encoded form of one of these.
    - The SDK throws `DeleteObjectsException` for a batch with failures: it maps to `IOException`. A 409
      `ConditionalRequestConflict` maps to `PreconditionFailedException`.
  - **Conformance suite** (Testing): two capability flags for emulator bugs, `FailedLargeWriteKeepsOldContent` and
    `SupportsLargeConditionalWrites`, true by default; S3Mock 5.2.3 sets both false (reasons in the S7 row).
  - **D25, D26** (main session): `WriteableAmazonS3FileProvider` and `WriteableMinioFileProvider` are `sealed`, and the
    seven new options properties (four on S3, three on MinIO) carry `[JsonIgnore(Condition = WhenWritingDefault)]`, as S6's do, so the golden written-shape test
    stays as it was; each options test pins both, and the migration guide lists the sealing.
  - **Verified:** the solution builds strict; all non-Live tests with `WFP_REQUIRE_DOCKER=1`, 775 passed and none skipped
    (S3Mock 5.2.3 and MinIO by digest through Testcontainers); the coverage gate. Coverage on Windows: AmazonS3 98.3% of
    lines and 93.0% of branches, Minio 99.2% and 88.3%; floors 97/91 and 98/87.
  - **Not verified:** anything only AWS shows: the live suite, and a manual checklist, under "For the maintainers".
  - **For later steps:**
    - No PublicAPI files yet: Common has none either (S12).
    - Docker Desktop's container clock can drift, so near-7-day presigns can be refused locally; CI's Linux containers
      share the host clock.

- **2026-10-11, S7 review round 1** (PR #30, read at 34611d9: 6 minors, 2 nits), all fixed:
  - **F1** The part loop reads the next part before deciding: a 0-byte read ends it, and the part limit throws only when
    part `MaxPartCount + 1` has data (offline: 2048 bytes in two 1024-byte parts completes; 5000 bytes fails).
  - **F8** Exactly `PartSize` is one PUT: the buffer reads one byte past the part, which carries into the next part
    (offline and emulator tests).
  - **F2** The bucket check counts only 403, 400 and 3xx as "exists"; any other failure faults it, and the next call
    checks again. Where a HEAD 404 would end a call as "file not found" (a zero-length range, a copy's source), the
    bucket is asked first, so a bucket that went missing is still `InvalidOperationException` (P18). The message says
    whether `CreateBucketIfMissing` will create it again.
  - **F3** MinIO's region question falls back to us-east-1 only on 403, 404 or `NoSuchBucket`; anything else fails the
    connection, which the next call retries.
  - **F4** (owner call: yes) `SupportsAtomicPreconditions` is true only on AWS. For MinIO and any `ServiceUrl` (or a
    given client with one) the base checks first, and the headers are still sent. D15 amended; READMEs and the
    migration guide say so. The provider decides it (`S3CoreSettings.ChecksAtomically`, `BucketLocation`), so neither
    assembly carries the other's unreachable branches.
  - **F5** (owner call: yes) The providers override the move: the source is deleted with `If-Match` on the version the
    copy matched; on 412 or 404 the copy is deleted and the move throws `PreconditionFailedException` (parity with
    Azure and GCS); a copy that cannot be removed throws `IOException` naming it. Off AWS the source's version is also
    checked just before the delete (MinIO ignores `If-Match` on deletes): checked, not atomic. Offline and emulator
    tests.
  - **F6** The live suites run the conformance suite only; the plan, the tracker (F19, F28) and the live classes now
    say so. Choice: a manual AWS checklist under "For the maintainers", not an AWS subclass of the behaviour tests,
    which create buckets and write 1,100 objects in the account they run against.
  - **F7** A range length past what a range can name reads to the end (`bytes={offset}-`) instead of overflowing.
  - **F9** A copy in parts (over 5 GiB) carries the source's Content-Type, -Encoding, -Disposition, Cache-Control and
    user metadata (offline test).
  - The core's static tests moved to `tests/Shared/S3/S3CoreTests.cs`, so both assemblies run them.
  - **Floors** from this Windows run (AmazonS3 98.3% / 92.6%, Minio 99.2% / 91.3%), by the rule under Tests (the
    whole-point floor, minus one): AmazonS3 97/91, Minio 98/90. CI on PR #30 reports Linux; the lower of the two counts.
  - Plan ids (D18, D26) are out of the public XML docs and the migration guide's S3 and MinIO section.

- **2026-10-11, S7 review round 2** (PR #30, read at b38bfdd: F1–F9, D25 and D26 verified; 5 minors, 4 nits), all
  fixed:
  - **N1** (decided: one meaning with GCS and Azure) A source deleted between the copy and its delete (404, or gone at
    the check off AWS) completes the move and keeps the copy; a changed source still undoes it.
  - **N2** `CopyAsync` returns the copy's ETag too (CopyObject or the completed upload); the undo deletes the copy with
    `If-Match` on it, and off AWS checks it first (not atomic). A copy another writer replaced is left, and the
    `PreconditionFailedException` says the source was not moved and the destination holds another write.
  - **N3** Any failure of the undo (a network error included) throws `IOException` naming the copy.
  - **N4** The part array holds exactly one part (8 MiB, not the next power of two); the lookahead byte lives apart.
  - **N5** The range test now has cases that overflow (`(2, long.MaxValue)`, `(3, long.MaxValue - 1)`); on the old
    expression S3Mock answers 400 and MinIO returns the whole file, so both fail without the fix.
  - **N6–N9** MinIO's `SupportsAtomicPreconditions` doc says always false; the AmazonS3 README says a provider redirected
    by `AWS_ENDPOINT_URL_S3` still counts as AWS; a copy in parts also carries Content-Language and Expires (from the
    HEAD's `ExpiresString`, as the SDK leaves the typed value empty), and leaves tags out, documented; the log counts
    seven new options properties.
  - Tests: offline `Move_WhoseSourceWentAfterTheCopyCompletes`, `Move_WhoseCopyWasOverwrittenMeanwhileLeavesIt`,
    `Move_WhoseSourceChangedRemovesTheCopyAtItsVersion`, `Move_ThatCannotRemoveItsCopyNamesIt` (a network failure);
    emulator `Move_WhoseSourceGoesAfterTheCopyCompletes`, `Move_WhoseCopyIsOverwrittenBeforeTheUndoLeavesIt`.
  - Rebased onto S6 (dd566ac) and S12 part 1: the trackers carry S6's, S7's and S8's parts. The old test project starts
    no emulator any more, so `Emulators.cs` and its Testcontainers reference go, and AGENTS.md no longer names it. P2.6
    says what S7 checks since F4. Verified on that tree: 1514 non-Live tests, the coverage gate.

- **2026-10-11, S5 Physical (#9):** built on branch `9-physical`.
  - **Provider:** builds strict (no `WfpLegacy`). Writes, appends, copies and local-stream commits go through a
    `.wfp-<guid>.tmp` next to the target and a rename (F3, F5). The ETag is synthesized from the last-write ticks and the
    length, and every write stamps a last-write time later than the file it replaces, so same-length writes inside the
    file system's timestamp granularity still change it. `IfMatch` is checked just before the rename. Ranges go through
    `RandomAccess` on one handle, with `IfMatch` checked on that handle. Readers share `ReadWrite | Delete` (P20).
    `Watch` goes through `PhysicalFilesWatcher` (F54, P2.12). The options getter fixes F79, and the stored `root:` forms
    still read.
  - **Deviations, and why:**
    - **D15 and P19, the atomic create:** in .NET 10, `File.Move(overwrite: false)` is not atomic on Linux and macOS: it
      lstats the destination, then renames. A hard-link API, which would allow link-then-unlink, is only a .NET 11
      proposal (`FileInfo.CreateAsHardLink`, under review), not confirmed to ship. There, `IfNotExists`, an append to a
      missing file, and the destination of a move or a copy take the name with an exclusive create (`FileMode.CreateNew`),
      then a rename fills it, so the file exists empty for that moment. On Windows, `File.Move(overwrite: false)` refuses
      an existing file atomically and is used as it is (review round 1). D15 and the P19 line say so.
    - **P20 on Windows:** `File.Move` over a file that is open fails with access denied, even with `FileShare.Delete`, so
      a write to a file being read fails with `UnauthorizedAccessException` and keeps the content. Linux and macOS
      replace the file, and the reader keeps its version. `File.Replace` goes past open readers on Windows, but without a
      backup file its documented failure mode deletes the target; that choice is the maintainers'. The P20 case is a
      Physical test rather than a shared conformance case, since what an open reader sees during a replace differs per
      store, and the other provider steps were in flight.
    - **P28:** the in-folder temp file cannot be `DeleteOnClose` (it becomes the file), nor owner-only (its mode becomes
      the file's). It is created with the replaced file's mode, so it is never readable by more users than that file, and
      the exact mode is copied before the rename. A new file gets the default mode. A mutation run showed the test fails
      without this (the temp file was 0644).
    - **F74:** Physical's spool is its temp file next to the target, its one write path, rather than Common's
      `SpoolToTempFileAsync`.
    - **Capability flag:** `DetectsConcurrentAppends` is false (the check before the rename is not atomic, D19), so the
      shared concurrent-append test is inconclusive on Physical. `Append_FailsWhenTheFileChangesWhileItCopies` pins the
      check.
    - **No PublicAPI files:** no package has them yet, since S4 added no analyzer. S12 adds them.
    - **`SegmentValidationTests`:** a Common suite that ran on Physical, it moved with Physical's tests, so the old test
      project no longer references Physical.
  - **Tests:** `WriteableFileProviders.Physical.Tests` holds the conformance fixture (a temp folder per class),
    `PhysicalProviderTests`, `PhysicalOptionsTests` and the Live `PhysicalLiveTests`. The F79 golden in
    `ConfigurationShapeTests` flipped to the fix.
    - Windows: 116 tests; 114 passed, plus 2 skipped (concurrent append, Unix permissions).
    - Linux, in a `dotnet/sdk:10.0` container as a non-root user: 116 tests; 115 passed, 1 inconclusive (concurrent
      append), and `Common.Tests` 217 passed.
    - The whole solution on Windows with `WFP_REQUIRE_DOCKER=1` (Azurite and S3Mock running): 457 tests, none failed. The
      32 skipped are the two `AWS_ENDPOINT_URL_S3` suites, excepted until S7, and Physical's two.
  - **Coverage:** Physical at 100% lines and 97.1% branches on Linux, and 99.3% lines and 94.1% branches on Windows; the
    floor was 98/93 (99/96 after review round 1). The partial branches are the OS checks and the race-only paths.
  - **Not verified:** macOS, which no CI runs. There, a case-only rename relies on .NET's same-inode rename in
    `File.Move`. The live suite did not run: it needs a folder you name.

- **2026-10-11, S5 review round 1 (#26):** needs-fixes (2 majors, 6 minors, 1 nit), all fixed in the next commit.
  - **F1 (major):** a copy took the default mode on Unix. It now takes its source's permission bits before any content is
    written. A test checks the copy of a 0600 file, and a mutation run showed it fails without the fix.
  - **F2 (major):** on Unix the rename replaced files the process may not write. A file it cannot open for writing is now
    refused with `UnauthorizedAccessException` and keeps its content, as in 2026 and as Windows refuses the rename, so
    the two systems agree. The copied mode is masked to 0777, so no setuid, setgid or sticky bit is carried over. A test
    on both systems covers it, also checked by mutation.
  - **F3:** each write reads its stamped time back and steps it forward (1 tick, 1 ms, 10 ms, 1 s, 2 s) until it is
    later than the replaced file's time, for file systems that store times coarsely. `Stamp` is tested with simulated
    resolutions.
  - **F4 (the owner's call):** Windows uses `File.Move(overwrite: false)`, with no placeholder. Linux and macOS keep the
    exclusive-create placeholder, documented with its crash leftover (an empty file) and the watcher event. Internal
    settings let the tests run both ways on each system.
  - **F5:** `Watch` goes through an inner `PhysicalFileProvider`, so invalid filters and `DOTNET_USE_POLLING_FILE_WATCHER`
    behave as in ASP.NET Core. Leading `/` and `\` are trimmed on every system.
  - **F6:** a recursive listing lists a linked folder (a symbolic link or a junction) but does not walk into it.
  - **F7:** the README and the migration guide say what replacing by rename means: hard links keep the old content, a
    symbolic link at the path is replaced by a file, ACLs and extended attributes are not carried over, and a file the
    process may not write is refused.
  - **F8:** the floor is the whole-point floor of the lower of the Linux figures (100% lines, 97.6% branches) and the
    Windows figures (99.7%, 96.0%): 99/96.
  - **F9:** the P20 and P28 bullets and the hard-link wording are corrected inline, and the S5 row says "in review (#26)".
  - **D25:** `WriteablePhysicalFileProvider` is sealed, and the migration guide says so. The tests reach its protected
    members by reflection, and its per-system settings through `InternalsVisibleTo`.
  - **D26:** Physical adds no options, so it writes nothing new to JSON.
  - **Tests:**
    - Physical on Windows: 132 tests; 129 passed, 3 skipped (concurrent append, two Unix-only tests).
    - Physical on Linux (container, non-root user): 131 passed, 1 inconclusive (concurrent append).
    - `Common.Tests` on Linux: 217 passed.
    - The whole solution on Windows with `WFP_REQUIRE_DOCKER=1`: 473 tests, none failed. The 33 skipped are the two
      `AWS_ENDPOINT_URL_S3` suites and Physical's three.

- **2026-10-11, S5 review round 2 (#26):** round 1 was verified, apart from one regression. Round 2 found 1 major
  (N1) and 8 minors, all fixed in the next commit, after a rebase onto S11 (bce935c) and S8 (e1c4b52).
  - **Rebase:** the trackers, the progress log and the migration guide carry both steps' notes. S8's
    `FileInfo_AppendAndLocalStreamKeepTheContentType` returns early on Physical, which keeps no content type.
  - **N1 (major, security):** round 1 created the temp file with the default mode and narrowed it afterwards, so
    another local user could open it in between and read a private file's new content. The temp file is now created
    with `UnixCreateMode` set to the mode it takes; the umask can only narrow that, and the exact bits follow on the
    handle. An internal `TempCreated` hook lets `Write_CreatesItsTempFileNoMoreReadableThanTheFileItReplaces` read the
    mode at creation. A mutation run in the Linux container showed it fails without the fix (the temp file was 0644).
  - **N2:** a link to nothing at the path is replaced by a file, by an overwrite or an append, and the new file does not
    take the link's mode.
  - **N3 (decided: fail closed):** .NET 10 has no public file type (`SafeFileHandle.Type` comes in .NET 11), and opening
    a FIFO to write waits forever. The check runs before anything else opens the file, an append's read included. An
    empty target is opened to read and write, which does not wait even on a FIFO, and a handle that cannot seek throws
    `IOException` ("not a regular file"). A file with content cannot be a FIFO, socket or device, so it keeps the write
    probe. A device that seeks (`/dev/null`) passes as an empty file, which the docs say. `Write_OverAFifoIsRefused` runs
    `mkfifo`.
  - **N4:** on Windows a write fails with `UnauthorizedAccessException` when the reader shares writing, and with
    `IOException` when another process holds the file without sharing writes. The README, the XML remarks, the
    migration row and P20 say so.
  - **N5:** a file in a folder the process may not write is refused with `UnauthorizedAccessException`, which names the
    file and no longer the temp file; tests cover Unix (chmod) and Windows (a deny ACL on creating files). A mount
    point (single-file bind mount, `subPath`) gets `IOException`, documented.
  - **N6:** the stamped time steps without bound on coarse file systems, documented; "up to 2 seconds" is gone.
  - **N7:** a copy takes its source's exact bits, whatever the umask, as `File.Copy` does, documented.
  - **N8:** the read-only test is inconclusive, with its reason, when run as root.
  - **N9:** the README's Watch bullet is wrapped and reworded, and the class remarks fit in 120 columns.
  - **Floor:** per the Tests "Floors" rule (the lower of Linux and Windows, minus one). Linux, in the container: 100%
    lines, 97.8% branches. Windows: 99.4% lines, 94.2% branches. So the floor is 98/93, a rise over main's 98/86. Round 1
    set 99/96 without the margin.
  - **Tests:**
    - Physical on Windows: 140 tests; 134 passed, 6 skipped (the concurrent-append case and five Unix-only tests).
    - Physical on Linux: 139 tests (it skips the Windows-only ACL test); 138 passed, 1 inconclusive (the concurrent
      append).
    - The whole solution on Windows with `WFP_REQUIRE_DOCKER=1`: 920 tests, none failed. The 36 skipped are the
      `AWS_ENDPOINT_URL_S3` suites and Physical's six.

- **2026-10-11, S12 part 2, harmonize (#16):** branch `16-harmonize` (worktree `D:\workspaces\wfp-wt\16b`), built in
  parallel with the docs half (`16-docs`), which wrote the READMEs and the migration guide as if this half were done.
  - **D27 on every provider:** Box listed names Box allows and the path rules refuse (`...`, a literal `%2e%2e`), and
    Physical listed `%2e%2e` anywhere and, on Linux and macOS, names with `:` or `\`, control characters or only dots;
    both now leave them out and never walk into such a folder, through a private `IsAddressable` like the other five
    providers' (resolving the name as a one-segment path). A Common helper would remove the seven copies, but adds to
    the Common API door, so it is left to the final review. The conformance suite now checks the rule everywhere:
    `StoreRawFileAsync`, a fixture hook (default: stores nothing, and the test is inconclusive), stores files past the
    provider under such names, at the top and as a folder one level down; `List_LeavesOutNamesThePathRulesRefuse` lists
    one level and recursively, reads back every entry listed, and checks that a folder delete removes what it leaves
    out. Every offline and emulator fixture implements the hook (disk, the in-memory store, `FakeBox`, `GraphFake`, the
    emulators' own SDK clients), and so does `PhysicalLiveTests`; the other live fixtures do not. A fixture reports a
    name its store cannot hold: Windows (Physical) holds only the percent-encoded names, S3, MinIO and Azure skip
    control characters (an XML listing cannot carry them back), and MinIO refuses `a\b.txt` (400). With each provider's
    filter disabled, the test fails on all fifteen fixtures. The in-memory reference provider follows the rule too.
  - **The live-class rule**, said once in AGENTS.md: inconclusive without `WFP_LIVE=1`; with it, a missing setting fails
    the class, naming it. S3 and MinIO (`tests/Shared/S3/LiveSettings`), Google Cloud Storage and Box were inconclusive
    either way, and Physical failed even without `WFP_LIVE`; all follow it now, and Azure names only what is missing.
    Google Cloud Storage and Physical got a `testsettings.sample.json` like the other test projects.
  - **D25 and D26:** checked, no miss. Every provider class is sealed; every options property new in 2027 is
    `WhenWritingDefault` (four on S3, three on MinIO, two on Azure, three on GCS, three on SharePoint; Box and Physical
    add none), against the 2026 options at `aa265f5`. The options classes keep their 2026 sealing (S3's and GCS's
    sealed, the others not), which D25 does not cover.
  - **Removed:** Common's `[Obsolete]` `GetRelativeSegments`, `IsRootPath` and `NormalizePem` (nothing called them; the
    migration guide's two rows say "Gone"); the `WfpLegacy` switch, since no project set it once the old test project
    went; `tests/WriteableFileProviders.Tests` with its lock file, sample settings, `.slnx` entry and Common's
    `InternalsVisibleTo` (every `TestBase` case lives in the conformance suite or the options tests). The review lens
    that named `TestBase` names the conformance suite.
  - **PublicAPI files:** `Microsoft.CodeAnalysis.PublicApiAnalyzers` 5.6.0 (central version, private) on every package
    under `src/`, through `src/Directory.Build.props`; `PublicAPI.Shipped.txt` holds `#nullable enable`,
    `PublicAPI.Unshipped.txt` the 2027 surface, generated with `dotnet format analyzers --diagnostics RS0016`. RS0016
    and RS0017 are errors (`.editorconfig`). RS0026/RS0027 flagged designed overloads: the extension methods' sync and
    `IFileInfo` forms, three `Stream` overrides, and the four 2026 S3 options constructors; each is suppressed by
    member, with its reason, so the rules stay on for new members. The analyzer caught the constructor reorder below at
    once.
  - **From the main session (items 10–13):**
    - P21: `src/Directory.Build.targets` (new; it imports the root one) rewrites `_ProjectReferencesWithVersions` after
      `_GetProjectReferenceVersions` to `[V, NextMajor.0.0)`, NextMajor being `$(MinVerMajor)` + 1 (NuGet/Home#5556's
      workaround holds on SDK 10.0.401). `eng/package-check.cs` requires exactly that range. Checked: a pack of every
      package gives `[2027.0.0-alpha.0.108, 2028.0.0)`, a `--no-build -c Release` pack the same, a pack at 2029.3.1
      `[2029.3.1, 2030.0.0)`; the check passes on the pack and fails on another version.
    - One constructor order, `(options, client)`: Azure's became `(AzureFileProviderOptions? options,
      BlobContainerClient container)` (options null for the container's root, no longer optional) and GCS's
      `(GoogleCloudStorageFileProviderOptions options, StorageClient client)`; S3 `(options, IAmazonS3)`, MinIO
      `(options, IAmazonS3)`, Box `(options, IBoxClient)` and SharePoint `(options, GraphServiceClient)` were already
      so.
    - XML docs: the S3 `(options, IAmazonS3)` constructor now says `ServiceUrl` marks the store S3-compatible and
      `BucketRegion` places a created bucket; `UpdateFileCoreAsync` says the conditions always reach it.
    - Physical and a symbolic link to a file: FileInfo describes the link (length 0 on Windows) while reads open the
      target, so the info and listings had the link's length, and a pinned or seekable read, or an append, through the
      link always failed its version check (a new test reproduced it on Windows). The length, time and ETag now come
      from the target, the physical path stays the link's own, and a write still replaces the link with a file. (As
      first built, the target came from `ResolveLinkTarget(true)` and also gave the new file's mode; review round 1
      below replaced both, and corrects what this line said about modes: only a dangling link has a mode of its own,
      FileInfo reporting the target's for any other.) A link to nothing, or in a loop, is described as itself.
  - **Error types outside the contract (item 9),** from a read of every provider. Fixed, each with a test that fails
    without it: Box's server-side move and copy of a source deleted meanwhile (404) is `FileNotFoundException`, as on
    SharePoint and S3; SharePoint's HTTP client timeout (`TaskCanceledException` holding a `TimeoutException`) is
    `IOException`, no longer a cancellation; SharePoint's measuring request maps a refused answer as a download does
    (403 is `UnauthorizedAccessException`). **For the final review** (each is a decision, or needs more than a small
    fix):
    1. One policy for errors outside the contract. SharePoint maps every failure (401/403 to
       `UnauthorizedAccessException`, the rest to `IOException`); the others let SDK exceptions escape: Azure
       (`RequestFailedException`, `AggregateException` after its retries, Azure.Identity's exceptions; none documented),
       S3 and MinIO (`AmazonS3Exception`, `AmazonClientException`, `HttpRequestException`, `TimeoutException`; not
       documented), GCS (`GoogleApiException`, `HttpRequestException`, `TokenResponseException`; only a 403 documented),
       Box (`BoxApiException` documented; `BoxSdkException` for a network failure after retries, sign-in included, and
       `JsonException` for an unreadable answer, not documented). Either document each provider's, or map them all the
       SharePoint way, which changes what consumers catch and flips GCS tests that pin `GoogleApiException`.
    2. Azure: a plain read's resumed download throws a raw `RequestFailedException` 412 or 404 mid-read, where seekable
       reads map them (`BlobReadStream`; the fix is small, but its test needs a body cut mid-way); a delete maps only
       `SnapshotsPresent`, while a move turns any 4xx refusal of the source delete into `IOException`;
       `CreateContainerIfMissing` refused with 403 escapes raw.
    3. GCS: an identity without `iam.serviceAccounts.signBlob` gets `HttpRequestException` from signing, not the
       `NotSupportedException` the docs promise, so the gateway cannot take over; the raw media GETs probably do not get
       the SDK's retries (unverified); missing Application Default Credentials throw `InvalidOperationException`, the
       contract's type for a missing bucket.
    4. Timeouts that look like cancellation: GCS's raw download (`TaskCanceledException`), Azure's inside its
       `AggregateException`.
    5. SharePoint: Azure.Identity's `AuthenticationFailedException` escapes raw from every member (Kiota asks for the
       token inside each request); a site answer without an id throws `InvalidOperationException`, the missing site's
       type.
    6. S3: `CreateBucketIfMissing` with a name another account owns gets 409 `BucketAlreadyExists` from every member; a
       file delete's `AccessDenied` escapes as `AmazonS3Exception`, while a folder delete's is `IOException`.
    7. Physical on Windows: names with `"`, `<`, `>`, `|`, `?` or `*` pass validation, read as missing and fail late
       with `IOException`, not `ArgumentException`; a trailing dot or space aliases (`a.` names `a`). Its
       `UnauthorizedAccessException` from reads, listings, deletes and moves is documented for writes only.
  - **Coverage** (Windows, every non-Live test): AmazonS3 98.06/92.53, AspNetCore 100/99.01, AzureBlob 99.22/91.00, Box
    99.68/93.07, Common 95.65/91.79, GCS 99.64/95.28, MinIO 98.32/91.27, Physical 99.46/94.52, SharePoint 99.76/91.48
    (lines/branches). Linux container: Physical 100/97.95, Common (its own tests only) 95.75/91.99. By the Tests rule
    (the whole-point floor of the lower of Linux and Windows, minus one) no floor rises: each is already there or above
    (Common, MinIO and SharePoint stay above it). Every floor is 90% or more of lines and branches; CI's Linux figures
    are for the main session to check.
  - **Verified:** the solution builds strict; every non-Live test on Windows with `WFP_REQUIRE_DOCKER=1`: 2089, none
    failed, 8 skipped (the three concurrent-append cases of Physical and SharePoint, and five Unix-only Physical tests);
    Physical (142: 140 passed, the concurrent append and the Windows-only ACL test skipped) and Common (223 passed) in a
    `dotnet/sdk:10.0` container as a non-root user; the coverage gate; the pack and the package check. No live suite ran
    (no accounts here; D21).
  - **For the docs:** the migration guide and the Physical README may say that a symbolic link to a file is described
    by its target, and that a write through it takes the target's permission bits.

- **2026-10-11, S12 part 2, harmonize, review round 1 (PR #41, read at `edf7323`):** needs-fixes, one major; D27's test,
  folder deletes of hidden names, the live-class rule on all ten classes, the PublicAPI wiring and its suppressions, the
  removed helpers and the error mappings were verified. Fixed:
  - **F1 (major, security):** Physical followed a link by string (`ResolveLinkTarget(true)`), which resolves a relative
    target against the link's path as written, while the system resolves it from where the link physically is. In a
    deploy layout (`current -> releases/r1`, `releases/r1/secret.txt -> ../../shared/secret.txt`) it named a decoy
    outside the root while reads opened the private file, and an append then wrote the private content into a file with
    the decoy's mode. The mode now comes from the path's own `FileInfo` again, as before this half (it reports the
    target's mode for any link but a dangling one, which has no mode of its own; the log line above said "0777"), and a
    copy's from its source's. A link's length, time and ETag are read from a handle the system opened through it; a FIFO
    behind a link is opened to read and write, so the open does not wait (only a FIFO the process may not write still
    would, as a read of it does), and one that cannot seek has length 0, so a write through the link refuses it as not a
    regular file. When nothing opens (a link to nothing, in a loop, unreadable), the link's own entry is used. Tests,
    run on Linux in the `dotnet/sdk:10.0` container as a non-root user: the deploy layout (the length, a pinned read, an
    append, the 0600 mode kept, the decoy untouched, a read-only target), and a link to a FIFO listed and refused
    without waiting. The general link test no longer asserts a mode it could not tell apart.
  - **F2:** the upper bound excluded only the next major's release, not its prereleases (`2028.0.0-rc.1` satisfied
    `[2027.0.0, 2028.0.0)`). The packed range is now `[V, NextMajor.0.0-0)`, `-0` being the lowest prerelease, in
    `src/Directory.Build.targets` and `eng/package-check.cs`; checked with NuGet.Versioning 6.14.0, which keeps the form
    as written and refuses `2028.0.0-0`, `2028.0.0-alpha.0.1`, `2028.0.0-rc.1` and `2028.0.0` while taking every 2027
    version from V on, and on a local pack (`[2027.0.0-alpha.0.119, 2028.0.0-0)`, the package check passing).
  - **F3:** a 404 on Box's server-side move or copy no longer always means "source not found": the source is looked up
    again, and only a missing one is `FileNotFoundException`; otherwise `IOException` (the source changed, or the
    destination folder was removed meanwhile). The rename through a temporary name maps its 404 the same way, where it
    used to let `BoxApiException` out. Tests: the destination folder removed during a move and a copy, and the source
    deleted during a case-only rename.
  - **F4:** `RawFiles.Store` reports a name the file system refuses (exFAT, vfat or SMB from Linux) as not storable,
    as the S3 fixture does a 400, so the D27 test does not fail in `PhysicalLiveTests` on such a share.
  - **F5:** only a reparse point is asked for its link target (a round trip on Windows and SMB).
  - **F6:** a client whose `Config.ServiceURL` is set now counts as S3-compatible for the requester-pays header and a
    created bucket's location too, as it already did for the conditions; offline test.
  - **F7:** the root `Directory.Build.props` no longer speaks of a strictness switch.
  - **F8:** `ci.yml`'s Linux job runs `eng/package-check.cs` on its prerelease pack, at the version MinVer gave Common
    (read from the package's file name), so a change that breaks the bound shows on its PR. actionlint 1.7.12 passes.
  - **F9:** floors from CI's Linux run at `edf7323` (AmazonS3 98.55/93.57, AspNetCore 100/99.01, AzureBlob 99.38/92.27,
    Box 99.80/94.12, Common 96.04/92.58, GCS 99.77/96.03, MinIO 99.01/92.64, Physical 100/98.63, SharePoint
    99.85/91.89) and the Windows run above: by the rule, only AspNetCore's branch floor rises, from 97 to 98.
  - **F10 (decided by the main session under the maintainer's standing instruction; it adds to the Common API, as part
    of the 2027 surface):** one protected helper, `WriteableFileProvider<TOptions>.IsAddressable(string name)`, says
    whether a listed name is one segment a path accepts, with `ValidateSegments`, the provider's `IsSegmentAllowed` and
    both again on the percent-decoded forms, exactly as `Resolve` would. The seven private copies are gone (S3, MinIO,
    Azure and GCS called the static `ValidateSegments` and so skipped `IsSegmentAllowed`; none of them overrides it, so
    their listings do not change). It is in `PublicAPI.Unshipped.txt`, and a unit test pins it, with an
    `IsSegmentAllowed` override, against what `Resolve` accepts for each name.
  - **Verified:** the solution builds strict; every non-Live test on Windows with `WFP_REQUIRE_DOCKER=1`: 2096, none
    failed, 10 skipped (the three concurrent-append cases, and seven Unix-only Physical tests); Physical (144: 142
    passed, the concurrent append and the Windows-only ACL test skipped) and Common (224 passed) in the container as a
    non-root user; the coverage gate (Windows: Physical 99.47/93.92, AspNetCore 100/99.01 over its new 98; Linux
    container: Physical 100/97.97); the pack and the package check.

- **2026-10-11, S12 part 2, harmonize, review round 2 (PR #41, read at `4b74a81`):** F2–F10 verified, and round 1's F1
  fix too; one new major and one owner call, both decided by the main session:
  - **N1 (major, sensitive; decided: never open a file to describe it):** round 1 described a link by opening its
    target, read-write first, from listings, `GetFileInfo` and the version checks. The reviewer's Linux probes, as a
    non-root user: a reader waiting on a FIFO behind a link got an early end of file, a writer's message was lost, a
    FIFO the process may read but not write hung every listing in `open()` (no token cancels it, so anyone who can make
    a link and a FIFO in the root could hang them all), inotify saw OPEN and CLOSE_WRITE, and on Windows the handle made
    another process's `File.OpenRead` fail. Before this half, listings never opened a target. Now nothing opens a file
    to describe it:
    - Checked first in the container: .NET's FileInfo for a link on Linux reports the link's own length (its target's
      text) and time, only its mode through the link; in the deploy layout it gave 23 bytes and the link's time, not the
      private file's 7 bytes. So a link is described by `PhysicalPathOf`: the path walked a component at a time from its
      root, each link replaced by its target, a relative target resolved against the part already resolved and `..`
      applied to that part, giving up after 40 links; then a FileInfo of the result, which stat describes without
      opening. On Windows, `ResolveLinkTarget(returnFinalTarget: true)`, which asks the system for the final path on a
      handle without access. A link that leads nowhere is described as itself; only a reparse point is asked (F5).
      `ResolvesLinksAsTheKernel` (internal, per system like the others) lets the tests run both ways on each system.
    - `OpenThroughLink` is gone. Writes keep `IsReplaceable`'s probe; a link to a FIFO now has length 0 from stat, so a
      write through it is still refused as not a regular file without waiting.
    - Tests: the link test runs both ways and checks the target's last-access and last-write times unchanged; the deploy
      layout test stays; on Linux, a link to a FIFO the process may read but not write is listed and described at once,
      and a reader waiting on a FIFO is still waiting after a listing and an info of a link to it (then released by a
      writer, and it reads what was written); `PhysicalPathOf` is tested on every system (the deploy layout, an absolute
      target, `..` through a linked folder, a loop, `..` at the root).
  - **N2 (owner call; decided: AWS hosts are AWS):** since F6 a client with its own `ServiceURL` counted as
    S3-compatible, so AWS clients through a VPC endpoint, FIPS or dual-stack lost requester-pays and the location of a
    created bucket. For both the options' `ServiceUrl` and a client's `ServiceURL`, a host that is `s3.amazonaws.com`,
    or ends in `.amazonaws.com` or `.amazonaws.com.cn`, is AWS for all three settings (`ChecksAtomically`,
    `RequesterPays`, `BucketLocation`). Offline tests: a VPC endpoint and MinIO's `localhost:9000`, each through the
    options and through a client, and the host rule on its own. The XML docs say so; the AmazonS3 README's lines are
    for the docs PR.
  - **Verified:** the solution builds strict; every non-Live test on Windows with `WFP_REQUIRE_DOCKER=1`: 2115, none
    failed, 11 skipped (the three concurrent-append cases and eight Unix-only Physical tests); Physical (147: 145
    passed, the concurrent append and the Windows-only ACL test skipped) and Common (224 passed) in the container as a
    non-root user; the coverage gate (Windows: Physical 99.50/94.38, AmazonS3 97.97/92.33; Linux container: Physical
    100/97.19).

- **2026-10-11, S12 part 2, harmonize, review round 3 (PR #41, read at `b74825e`):** N1 verified (strace showed only
  lstat, stat and readlink when describing; the walk matched `realpath -e` and `stat -L` on 1,530 paths over 12
  layouts). Two items, decided by the main session:
  - **N3 (major, data integrity; the round-2 instruction corrected):** any host under `amazonaws.com` passed round 2's
    rule, including names that front other servers (a load balancer, an instance, API Gateway), so a MinIO or Ceph
    server behind one got `SupportsAtomicPreconditions` true and no check first, which could lose concurrent writes
    (D15). Now: `ChecksAtomically` is true only with no endpoint named (neither `ServiceUrl` nor a client's
    `ServiceURL`), so the provider checks conditions first toward any endpoint, S3's own included (one HEAD; AWS still
    enforces the headers sent). `IsS3Endpoint` takes only S3's own host names (under `amazonaws.com` or
    `amazonaws.com.cn` after one trailing dot is trimmed, with a label exactly `s3`: `s3.amazonaws.com`,
    `s3.eu-west-1.amazonaws.com`, `bucket.s3.eu-west-1.amazonaws.com`, dual-stack, a VPC endpoint), and drives only
    `RequesterPays` and `BucketLocation`; the legacy dashed names (`s3-eu-west-1`, `s3-external-1`, `s3-fips`) do not
    match, the safe direction, and the docs say so. Tests: the VPC endpoint (conditions checked first, requester-pays
    and the location on), MinIO's localhost, an ELB, an EC2 and an API Gateway host, and a legacy dashed host (all off),
    each through the options or a client, and the host rule on 21 names. Found on the way: for a host under
    `amazonaws.com` the AWS SDK adds a `LocationConstraint` of its own, read from the host, when the request names none,
    so the test checks the provider's setting (`S3Core.BucketLocation`) there and the request body only off AWS.
  - **N4:** `PhysicalPathOf` stops where the kernel does: a component before the last that is missing or not a folder
    (a trailing separator or `.` included: `missing/../x`, `file.txt/../x`, `file.txt/`) leads nowhere, and the link is
    described as itself; the link limit is 32 on macOS and 40 elsewhere (`MaxLinks`). Unit tests for both, a chain of
    exactly the limit resolving and one link more not.
  - **Verified:** the solution builds strict; every non-Live test on Windows with `WFP_REQUIRE_DOCKER=1`: 2129, none
    failed, 11 skipped (as in round 2); Physical (148: 146 passed, the same two skipped) and Common (224 passed) in the
    container as a non-root user; the coverage gate (Windows: AmazonS3 98.08/92.78, Physical 99.50/94.15; Linux
    container: Physical 100/96.81).

- **2026-10-11, S12 part 2, docs (#16):** branch `16-docs` (worktree `D:\workspaces\wfp-wt\16c`), built beside
  `16-harmonize` and written as if that half has landed: D27 on Box and Physical, the live-class rule, the `[Obsolete]`
  path helpers removed, the old test project retired.
  - **D28** (the maintainer's versioning policy, 2026-10-11) is in Decisions, and written into the root README's
    Versioning section, SECURITY.md's supported versions (the latest major every fix; the previous major security fixes
    for 6 months after the next ships; 2026.10.10.x until a date the maintainers announce), AGENTS.md (One-way doors:
    a door, a dropped target framework or a breaking dependency change waits for the next major) and CONTRIBUTING.md.
    `src/Directory.Build.props` (MinVer prefix `v`, minimum `2027.0`, untagged builds `-alpha.0.<height>`) and
    `release.yml` (a `v*` tag on `main`, a prerelease for a tag with `-`) already match it.
  - **Root README** (new): what the library is, the packages, install, a quick start (Physical, Azure Blob, then
    configuration, DI and the serializer), the 2027 API in one screen with what every provider promises, the provider
    matrix, the gateway, the migration guide, versioning, contributing, security, and the licence "to be agreed" (D8).
    The matrix is three tables with one row per provider (the stores; reads and writes; URLs, copies, notifications),
    every cell taken from the provider's code: a read-only pass per package compared each README with its code first.
  - **Package READMEs**, all nine: one section order (Install, Use, Configuration, Authentication, How it behaves,
    Signed URLs, Limits, More; Common and AspNetCore keep the sections they have no use for out, and AspNetCore's own
    topics are subsections of How it behaves), the same install and same-major lines, absolute links back to the root
    README and to each provider's part of the migration guide. Where a README and its code disagreed, the code won and
    the README was fixed:
    - Common: the sync forms (three stay `IFileProvider` members, and copy, ranges, listings and URLs have none); which
      members throw `FileNotFoundException` for a missing file and which answer `Exists` false, empty or false.
    - Physical: a failed write's temp file is removed only where it can be, and listings hide any `.wfp-*.tmp` name;
      a seekable read checks the ETag only when pinned, and throws `PreconditionFailedException`, `IOException` or
      `FileNotFoundException`; case follows the operating system, not the volume; the sample's missing `using`.
    - Azure Blob: an append keeps the stored type only when none is given, and keeps the encoding too; an overwrite
      resets the other headers, metadata and tier; a conditional overwrite that loses its blocks fails at once,
      whatever its size; only overwrites stage while reading (appends spool first); the block-limit restage falls back
      to the client; the full list of names listings leave out (whitespace, dots and spaces, encoded forms, `.wfp-`).
    - Amazon S3: a client of yours still takes `ServiceUrl` and `BucketRegion` from the options; parts grow past about
      78 GiB (known length) or 7.8 GiB (unknown), not 80 GiB; ranged reads are pinned only with `ReadOptions.IfMatch`,
      seekable ones unless `AllowModifications`; presigned URLs do not carry requester-pays; the 2026 object form of
      `StorageClassForNewFiles` reads from JSON only; every overwrite without a type stores `application/octet-stream`;
      the destination check runs on AWS too; copy parts grow; a `dir/` marker is a folder; the full list of names left
      out.
    - MinIO: one key without the other throws `ArgumentException` (it is not anonymous); the read pinning; an append is
      only as safe as the server's conditional writes; a create URL refuses an overwrite only on such a server.
    - Google Cloud Storage: without `storage.objects.list`, the 403 reaches every member that has to tell a missing file
      apart (deletes, copies, seekable reads, local streams, read URLs), not only the ones listed; ranged reads are
      pinned only with `ReadOptions.IfMatch`; `ProjectId` is needed with `ClientEmail` and `PrivateKey` or a client of
      yours, whatever the key; "loopback", not "another machine"; one of `ClientEmail` and `PrivateKey` alone is
      ignored.
    - Box: a signed URL over 7 days throws, even with `AllowShorterValidity`; a commit is asked again every second (only
      downloads follow `Retry-After`); a case-only rename Box refuses takes three calls; `ReadRangeAsync` throws
      `PreconditionFailedException` only with `ReadOptions.IfMatch`; the download URL's own request is not wrapped like
      the download; delete goes to the trash unless the enterprise's settings purge it.
    - SharePoint: a signed URL over 7 days throws, and exactly an hour needs `AllowShorterValidity` too; the errors
      carry Graph's error in their message, with the SDK's or the network's exception inside where there is one; a
      move is one `PATCH` after the base checks and the destination's folders; an escaped `:` is refused too; the
      conditions' window includes copying the stream.
    - AspNetCore: the provider name is not inside the encrypted payload (it travels in the clear and in the purpose); a
      second 415 (an untyped token with a `Content-Type` that does not parse); `MaxUploadBytes` sets the server's limit
      where it can, which may lower it; the upload `Content-Type` compares without case; the security headers are on
      every answer of the gateway's endpoints; ASP.NET Core's W3C logging is in the app and not redacted.
  - **Package metadata (F76):** every `.csproj` under `src/` has a description and its own tags after the shared ones;
    the shared metadata (authors, project and repository URLs, the base tags) moved from the root
    `Directory.Build.props` into `src/Directory.Build.props`, where its comment always said packaging lives. No icon,
    licence or copyright line (D8). Checked: `dotnet pack` of the solution, and `eng/package-check.cs` on the nine
    packages (each nuspec carries the description, tags, README, project URL and repository).
  - **Migration guide, final pass:** a "Start here" list (net10, every package together, paths, the async API, the
    serializer, sealed classes, each provider's behaviour in a line, download URLs), one shape for every section (what
    changed, a table of 2026, 2027 and what to do, what is new), no plan ids. Added from the ledger: Common's dropped
    Microsoft.Extensions.Configuration and Binder dependency; the removed path helpers; D27 for every provider (a row
    in Common's behaviour and in each provider's table); the SharePoint behaviour table's "what to do" column.
  - **REVIEW.md:** "until CI exists" is gone: the build and tests question names `ci.yml` (Linux with Docker, Windows,
    the coverage gate, the pack) and `codeql.yml`, and asks whether a provider PR says if its live suite ran, which CI
    never does; an outside PR's live suite runs only through `live.yml` once on `main`.
  - **This plan:** every step row and finding row has its final status, the feature tracker says done for each item,
    and "For the maintainers" is complete and in order (security, the review of what merged, the repository's settings,
    the environments, the live runs with Box first, the decisions, the release).
  - **The packed upper bound on Common** (Compatibility, P21) is done in #41, the harmonize half: providers depend on
    Common as `[V, NextMajor.0.0-0)`, which keeps out the next major's prereleases. The root README's Versioning
    section and the migration guide's "Start here" say so, and the migration guide and Common's README list #41's new
    protected helper, `IsAddressable`.
  - **Found by the README passes in code this half does not touch**, and settled in #41: the `(options, IAmazonS3)`
    constructor's XML docs (the region and endpoint are used) and `UpdateFileCoreAsync`'s (the conditions always reach
    it); a symbolic link to a file on Physical, now described by its target (length, time, ETag; the new file takes the
    target's bits), which the Physical README and its migration section say; and the client constructors, which all
    take the options first now (Azure and GCS took the client first), as every sample and the migration guide show.
    Left for the final review: `StorageClassForNewFiles` in its 2026 object form is ignored without an error by
    `FromConfiguration` (as in 2026).

- **2026-10-11, S12 part 2, docs, review round 1 (PR #40, read at `432771e`):** needs-fixes, one major; the rest of the
  docs confirmed (every matrix cell but the gateway column's, D28, the S3 endpoint rule, Physical's links, the
  constructors, the options, the gateway, all 24 code samples compiled, the metadata and links). Fixed:
  - **F1 (major, build):** a stable release could not pack. The upper bound's `-0` makes NuGet raise NU5104 (a stable
    package with a prerelease dependency), an error with warnings as errors, so `v2027.0.0` would have failed in
    `release.yml`. `src/Directory.Build.targets` suppresses NU5104, saying why: the `-0` is only the exclusive upper
    bound. `ci.yml`'s Linux job now packs the commit's version without its prerelease suffix
    (`MinVerVersionOverride`) into a folder of its own and runs the package check on it, so a stable pack is proven on
    every PR. Checked: the stable pack failed before the fix and passes after it, its nuspecs say
    `[2027.0.0, 2028.0.0-0)`, and actionlint 1.7.12 with shellcheck finds nothing in `ci.yml`.
  - **F2:** mixing majors from 2027 on gives NU1608 or fails the restore (NU1107); the 2026.10.10.x providers carry no
    upper bound, so nothing warns there.
  - **F3, F4 (this plan):** the S0 patch is published (Common 2026.10.10.6, AmazonS3 2026.10.10.3, every provider again
    on it); the advisory names those versions; a decision whether to bound the 2026 providers below Common 2027; the
    unlist item names every version nuget.org has before the 2026.10.10 line; the review list names #17, #22 (which
    lowered two branch floors), #25 to #30 and #32; private vulnerability reporting is under "Security, now".
  - **F5 to F11 and the nits:** the gateway column (upload URLs with a file name, GCS under one second), GCS's
    `ProjectId`, Physical's followed links in the path promise, a non-obsolete `ManagedIdentityCredential`, Box's
    `IfMatch` without a promised date, `LocalFileStream`'s internal constructor for a provider of your own, the
    serializer's options-only constructor, and smaller wording; two plan ids left the Azure provider's XML docs.
  - Left for the final PR (N1): S3's FIPS host names (`s3-fips`) as S3's own.

- **2026-10-11, S12 final review and its fixes, common half (#16):** the final deep `review-code` of the whole repo ran
  at `581e050` (kind audit, label `2027-final`, 213 files, 11 lenses, 34 agents): 20 findings, F1 to F4 major, F5 to
  F20 minor, none refuted. The report, with a Decisions section naming each finding's fix and branch, is
  [`docs/reviews/2026-10-11-2027-final.md`](../reviews/2026-10-11-2027-final.md). The main session decided every finding
  under the maintainer's standing instruction; D29 to D32 are in Decisions, and @serionist agreed them, with the
  one-way-door changes below (the dot-segment root, the local stream hook), on #3
  ([comment](https://github.com/serionist/WriteableFileProviders/issues/3#issuecomment-6108746458)). Two branches fix
  them in parallel: `16-final-common` (worktree
  `D:\workspaces\wfp-wt\16d`; this entry) and `16-final-providers` (worktree `16e`; its own entry); the second to merge
  rebases.
  - **F1 (major), the root** (agreed on #3: [comment](https://github.com/serionist/WriteableFileProviders/issues/3#issuecomment-6108746458)):
    `WriteableFileProvider<TOptions>`'s constructor (now written out, `protected`; the PublicAPI line is the same)
    checks `RootDirectorySegments` with the rules of a caller's path (`ValidateSegments`, `IsSegmentAllowed`, the
    decoded forms) and throws `ArgumentException` (parameter `options`) naming
    `RootDirectoryPath`. A new `protected virtual bool ValidatesRootDirectory` (true) lets a provider whose root is not
    a store path opt out: Physical does (an operating system path, which it resolves; every path under it stays
    checked). SharePoint's own check stays, for its work folder, which the base cannot see. The base reads both hooks
    before the provider's constructor runs, which their docs say. Tests: `RootValidationTests` in Common.Tests (dot,
    percent-encoded, backslash, whitespace and control forms; empty and null segments; valid roots kept as given; the
    provider's own rule; the opt-out; a stored configuration that no longer builds) and the conformance case
    `Path_ARootWithADotSegmentIsRefused`, which every fixture runs (`ChecksItsRoot`, false for Physical's two), so the
    providers half needs no per-provider root test. SharePoint's options test moves its `%2E%2E` root to the base's
    refusal and adds dot segments in the work folder. Migration guide: a row in Stored configuration (a stored root with
    a dot segment no longer builds) and one in Azure Blob Storage.
  - **F4 / D29, the error policy**, as D29 words it, in one place (`WriteableFileProvider.Errors.cs`, `Map`):
    - Every public member maps what escapes it: the async members, the three `IFileProvider` members (through them, or
      `Watch` itself), and `GetSignedUrlAsync`'s store calls; path and argument checks throw before, as before. A read's
      stream is wrapped in `ContractStream` (internal), which keeps what the provider's stream can do (seek, length,
      position, timeouts) and maps the failures of its reads, seeks and disposal; a listing maps each `MoveNextAsync` and
      its disposal; a local stream's commit maps as a write.
    - The order: `OutOfMemoryException` passes; an `OperationCanceledException` passes when the caller's token is
      cancelled and is otherwise a timeout, `IOException`; what `IsNotFound` recognises becomes `FileNotFoundException`
      for the path (in every member now, not only reads); what `IsAccessDenied` recognises,
      `UnauthorizedAccessException`; the contract's types pass by type; after the caller cancelled, any other failure is
      `OperationCanceledException` with it inside; anything else is `IOException` naming the operation, the path and
      the inner exception's type.
    - Decided here: the outer message never copies the inner one, which may carry a credential (Box's errors over a
      caller's client); a provider predicate that throws counts as "no", since a throwing exception filter would let the
      store's exception out unmapped; an SDK exception that derives from a contract type (Kiota's
      `InvalidOperationException`, an `IOException` of the network) passes as it is, where the provider has not mapped
      it; a failure of the caller's own stream given to `UpdateFileAsync` arrives inside an `IOException` unless it is a
      contract type; one protected helper, `ReadWithContractAsync`, puts a provider's own public read under the same
      contract (S3's `ReadFileVersionAsync` uses it); Azure and SharePoint count Azure.Identity's
      `AuthenticationFailedException` as access denied, but not `CredentialUnavailableException` ("no credential
      found"), which each maps itself to `InvalidOperationException` (corrected in review round 1 of #42, below), as S3's
      empty default chain and GCS's missing Application Default Credentials are; Box's
      and GCS's token endpoints refusing a credential with OAuth's 400 (`invalid_grant`, `invalid_client`) count as
      access denied, beside D29's 401 and 403.
    - `IsAccessDenied` on every provider: Azure (`RequestFailedException` 401/403, `AuthenticationFailedException`),
      S3 and MinIO (`S3Core.IsAccessDenied`: 401/403 or `AccessDenied`), GCS (`GoogleApiException` 401/403,
      `TokenResponseException` 400/401/403), Box (401/403, and a 400 to `/oauth2/token`), SharePoint (Graph 401/403,
      `AuthenticationFailedException`); Physical's `UnauthorizedAccessException` passes through.
    - Tests: `ErrorPolicyTests` in Common.Tests drive every public member, both stream kinds, listings, a local
      stream's commit and `ReadWithContractAsync` through a provider that throws whatever a test gives it (an arbitrary
      exception, each contract type, a not-found, a refusal, a timeout, the caller's cancellation, a failure after it, a
      throwing predicate); the conformance suite's `Errors_OutsideTheContractAreIOException`,
      `Errors_ATimeoutIsIOException_NotACancellation` and `Errors_OfTheContractPassThrough` run where a fixture can make
      its store fail (`CreateProviderThatFails`; `InMemoryFileProvider.Failure`); offline tests of each provider's
      refusals (`AzureErrorTests`, `GcsErrorTests`, `BoxErrorTests`, `SharePointErrorTests`, the S3 offline and core
      tests). The provider tests that pinned an SDK's exception (Box's, S3's and MinIO's, GCS's, Azure's, SharePoint's
      timeout) now expect the contract's type with it inside, and S3's loose `ThrowsAsync<Exception>` became exact.
    - Docs: the migration guide's new section "What a member throws" and its rows, an "Errors" item in each provider's
      README, the root README's promises, Common's README and `IWriteableFileProvider`'s remarks.
  - **"For the final review" items 1, 2, 4, 5 and 6, closed under D29** (3 and 7 are the providers half's):
    1. One policy: D29, mapped by the base; SharePoint keeps its own mapping, which throws contract types the base
       passes; GCS's tests that pinned `GoogleApiException` expect the contract's type with it inside.
    2. Azure: a resumed plain read's raw 404 mid-read is `FileNotFoundException` and its 412 `IOException` (the stream
       is mapped); a delete's or a move's other refusals are `UnauthorizedAccessException` for 401 and 403,
       `IOException` otherwise; `CreateContainerIfMissing` refused with 403 is `UnauthorizedAccessException`.
    4. Timeouts: an `OperationCanceledException` the caller's token did not ask for is `IOException` on every provider
       (GCS's raw download, Box's and S3's clients); Azure's `AggregateException` after its retries is `IOException`.
    5. SharePoint: Azure.Identity's `AuthenticationFailedException` is `UnauthorizedAccessException`; a site answer
       without an id is `IOException` (the service's failure, not a site the options name wrongly), not cached.
    6. S3: a file delete's `AccessDenied` is `UnauthorizedAccessException`, and so is now a folder delete whose every
       refused key says `AccessDenied` (it was `IOException`; any other code stays `IOException`); `BucketAlreadyExists`
       with `CreateBucketIfMissing` is `InvalidOperationException` naming the bucket.
  - **F10:** the migration guide's first row: a call with no path, which meant the root, now takes `ProviderPath.Root`
    or `""`. No shims.
  - **F11** (agreed on #3: [comment](https://github.com/serionist/WriteableFileProviders/issues/3#issuecomment-6108746458)): `CreateLocalFileStreamCoreAsync` is a
    private member of the base class (`CreateLocalCopyAsync`), out of `PublicAPI.Unshipped.txt` and the migration
    guide's list of overrides; the guide says how a local stream is made is not customisable in 2027.0.0.
  - **F12:** `release.yml` runs the tests with ci.yml's coverage flags and the gate, and a job that `publish` needs
    (`actions: read`) waits for the tagged commit's `ci.yml` run on `main` and requires it concluded success (its Windows
    job included); a failed run, or none within 50 minutes, stops the release. `gh run list` with the job's filters
    answered `1 0 1` (one success, none running, one run) for `b6e78b8`. actionlint 1.7.12 with shellcheck 0.11.0 finds
    nothing. AGENTS.md's Release line says so.
  - **F13:** `AnalysisLevel` moved to `Directory.Build.targets`; `EffectiveAnalysisLevel` is still 10.0.
  - **F17:** `EnablePackageValidation` in `src/Directory.Build.props` (no baseline until 2027.0.0 ships; Later says to
    set it then); a pack and a stable pack (`MinVerVersionOverride`) of every package validate cleanly. No icon: "For
    the maintainers" gains it, and F76's row says the icon waits for them.
  - **F19 (the owner's call: no, for now):** under Later, with its reason.
  - **F20 (the owner's call: yes):** one internal `SliceReadStream` in `src/Shared/`, linked into Common, Box,
    GoogleCloudStorage and SharePoint, replaces Common's `BoundedReadStream` and skip helper, Box's `SliceStream`, GCS's
    `ResponseReadStream` and SharePoint's `DownloadStream`, keeping each one's behaviour: the owner disposed after the
    body (an HTTP response), a known length (GCS, from `Content-Length`), the skip of an ignored range (by seeking when
    the body can, by reading otherwise; at once with `SkipAsync`, as Common's default range read and SharePoint do, or
    on the first read). Its tests, one file in `tests/Shared/` (where the namespace rule is off for linked files),
    linked into the four test projects, replace the two provider test files and Common's three stream tests. Found on
    the way: the old skip by seeking overflowed for an offset near `long.MaxValue`; the new one does not.
  - **For the providers half:** the conformance root case and `CreateProviderThatFails` are in
    `ProviderConformanceTests`; Physical's `IsSegmentAllowed` is not read from the constructor, since Physical opts out
    of the root check, but any provider's rule must not depend on its state.
  - **Verified:** the solution builds strict; every non-Live test on Windows with `WFP_REQUIRE_DOCKER=1`: 2367, none failed, 11 skipped (the three concurrent-append cases and eight Unix-only Physical tests);
    Physical (157: 155 passed, the concurrent append and the Windows-only ACL test skipped) and Common (298 passed) in a
    `dotnet/sdk:10.0` container as a non-root user; the coverage gate on Windows (AmazonS3 98.12/93.05, AspNetCore 100/99.01, AzureBlob 99.21/91.04, Box 99.71/93.12, Common 96.88/93.12, GCS 99.49/94.20, MinIO 98.13/91.12, Physical 99.25/94.15, SharePoint 99.77/91.13, lines/branches; no floor rises without CI's Linux figures, which may let Common's branch floor and AmazonS3's rise to 92); a pack and a stable
    pack with package validation, and the package check. No live suite ran (no accounts here; D21).

- **2026-10-11, S12 final fixes, common half, review round 1 (PR #42, read at `e203072`):** 28 agents, nothing
  refuted: 1 blocker, 3 majors, 9 minors or nits; the main session decided the owner calls. Fixed:
  - **F1 (blocker), the doors:** @serionist agreed D29 to D32, the dot-segment root that no longer builds and the
    removal of `CreateLocalFileStreamCoreAsync`, on #3
    ([comment](https://github.com/serionist/WriteableFileProviders/issues/3#issuecomment-6108746458)); the PR names them.
    The D29 to D32 rows, the Compatibility lines and this half's log lines cite it.
  - **F2 (major, decided: option a):** "no credential found" is `InvalidOperationException` on Azure Blob and
    SharePoint too, as on S3, MinIO and GCS: Azure.Identity's `CredentialUnavailableException` leaves `IsAccessDenied`
    on both, and each provider maps it where it maps its SDK's errors (Azure's `GuardAsync`, now also around signing;
    SharePoint's `GraphErrors`), with the original inside and a message naming `UseDefaultAzureCredential`. A plain
    `AuthenticationFailedException` stays `UnauthorizedAccessException`. Tests in `AzureErrorTests` and
    `SharePointErrorTests`; the D29 note above is corrected.
  - **F3 (major):** an Azure move whose source delete is refused with 401 or 403 removes its copy, as for any refusal,
    and throws `UnauthorizedAccessException` (it was `IOException`). New tests on Azurite through the handler: a resumed
    plain read answering 404 (`FileNotFoundException`) and 412 (`IOException`, Azure's error inside), a move whose copy
    answers 403 and one whose source delete does (`UnauthorizedAccessException`), and a container creation refused with
    403 (`UnauthorizedAccessException`).
  - **F4 (major):** Box's constructor docs say a refused sign-in is `UnauthorizedAccessException`, the
    `BoxApiException` inside; the class remarks say "wraps".
  - **F5 (decided: option a):** the base constructor keeps a copy of the validated root and `Resolve` uses it, so
    changing `RootDirectoryPath` or a segment of `RootDirectorySegments` after the build does not move the provider;
    Azure's temp blobs and the names it strips read the same copy (`Resolve(ProviderPath.Root)`). Tests: the base
    (`RootValidationTests`), the in-memory provider, and an Azure request offline (`AzureRootTests`). The
    `RootDirectoryPath` docs say so.
  - **F6:** S3 and MinIO count as refusals only a 401, an error code that refuses the credential or the permission
    (`AccessDenied`, `InvalidAccessKeyId`, `SignatureDoesNotMatch`, `AllAccessDisabled`, `AccountProblem`,
    `InvalidToken`, `ExpiredToken`), or a 403 without a code, as a HEAD answers; an archived object
    (`InvalidObjectState`, `ObjectNotInActiveTierError`) and `RequestTimeTooSkewed` are `IOException`. Tests in
    `S3CoreTests` and the S3 offline tests (a HEAD's bare 403, an archived object); the READMEs' "Errors" items.
  - **F7:** the five places that said an SDK's exception is never the type thrown say it travels inside unless it
    already derives from one of the contract's types, and that `OutOfMemoryException` passes unmapped (D29's row,
    the migration guide, `IWriteableFileProvider`'s remarks, Common's README, the root README).
  - **F8:** the migration guide's summary: item 5 names the dot-segment root that no longer builds, item 7 the root
    check.
  - **F9:** AGENTS.md's configuration-shape door records the exception, linking the agreement.
  - **F10 and F11 (decided: option a, wire the hook):** `CreateProviderThatFails` is overridden by the fixtures with an
    offline seam: Azure's two (`AzureHandler.Intercept`), GCS's two (`FaultInjection`), SharePoint's two (a
    `GraphFake` of their own), Box's two (a `FakeBox` of their own) and S3's and MinIO's four (the intercepting HTTP
    factory). The `Errors_*` cases now check each SDK's wrapping: Azure retries a timeout into an `AggregateException`,
    which the base maps to `IOException`; the AWS SDK replaces a timeout with a `TimeoutException` of its own, so the
    timeout case accepts the injected timeout or the SDK's own timeout in the chain; `Errors_OfTheContractPassThrough`
    looks for the refused exception anywhere in the chain, as the hook's docs allow. A fixture without the hook
    (Physical's) reports the three cases inconclusive instead of passing them silently.
  - **F12:** Physical's README has an "Errors" item.
  - **F13:** AGENTS.md's offline class lists name `AzureErrorTests`, `AzureRootTests` and `GcsErrorTests`.
  - **Verified:** the solution builds strict; every non-Live test on Windows with `WFP_REQUIRE_DOCKER=1`: 2401, none failed, 14 skipped (the eleven of before and Physical's three `Errors_*` cases, inconclusive without a failure hook);
    Physical (157: 152 passed, the concurrent append, the Windows-only ACL test and the three `Errors_*` cases skipped) and Common (300 passed) in a `dotnet/sdk:10.0` container as a non-root user; the
    coverage gate on Windows (AmazonS3 98.14/93.32, AspNetCore 100/99.01, AzureBlob 99.23/91.67, Box 99.71/93.12, Common 96.87/93.14, GCS 99.49/94.20, MinIO 98.15/91.42, Physical 99.25/94.15, SharePoint 99.77/91.24; no floor rises without CI's Linux figures). PR #43 also edits the provider test projects; this half's changes there
    are members of their own, and the second to merge rebases.
- **2026-10-11, S12 final fixes, common half, review round 2 (PR #42, read at `b76f579`):** nine findings, all fixed:
  - **N1:** the "no credential found" message names what the caller set up. Built from the options, it names
    `UseDefaultAzureCredential` and the key or secret to set; over a caller's client (Azure's
    `(options, BlobContainerClient)`, SharePoint's `(options, GraphServiceClient)`) it says the given client's
    Microsoft Entra ID credential found no credential to use. Azure tells by whether it built the container client,
    SharePoint by whether it owns the Graph client (`GraphErrors.Wrap(…, givenClient)`). Tests through each client
    constructor (`AzureErrorTests`, `SharePointErrorTests`) and of both messages of `GraphErrors`.
  - **N2 (decided: invert the rule):** S3 and MinIO count every 401 and 403 as a refusal but `InvalidObjectState`,
    `ObjectNotInActiveTierError` and `RequestTimeTooSkewed`, which are `IOException`; that takes in AWS's
    `InvalidSecurity`, `NotSignedUp` and `InvalidPayer` and any S3-compatible server's own codes. F6's list is gone, but
    for its two codes that AWS sends with a 400, `ExpiredToken` and `InvalidToken` (a session token that expired or is
    not valid), which stay refusals so that an expired token does not turn into `IOException`. `S3CoreTests` has a row
    per named code, both ways, and a 400 with any other code is not a refusal; the providers' remarks and the READMEs'
    "Errors" items say so.
  - **N3:** Box's failing fixtures let the sign-in (`/oauth2/token`) through, so each member's own requests are the ones
    that fail.
  - **N4:** the conformance timeout case accepts only the injected exception or its own inner `TimeoutException` (which
    the AWS SDK passes on), no longer any timeout or cancellation in the chain.
  - **N5:** AGENTS.md says the emulator and offline provider fixtures wire `CreateProviderThatFails` and a fixture
    without it (Physical's, the live suites) reports the cases inconclusive.
  - **N6:** Azure reads its root once, in the constructor, so the "content is kept at '…'" messages of a write that
    could not finish still name the blob after the provider is disposed (`Resolve` throws `ObjectDisposedException`
    then).
  - **N7:** `InMemoryFileProvider.StoreRaw` stores under the root the provider was built with.
  - **N8:** Azure's seekable read stream maps a `CredentialUnavailableException` of a resumed range read to the
    provider's `InvalidOperationException`, as every other member does; a unit test reads through it.
  - **N9:** D29's row says the pass-through of the contract's own subtypes and of `OutOfMemoryException` is the plan's
    reading of the agreed contract.
  - **Verified:** the solution builds strict; every non-Live test on Windows with `WFP_REQUIRE_DOCKER=1`: 2414, none
    failed, 14 skipped (as in round 1); the coverage gate on Windows (AmazonS3 98.13/93.49, AspNetCore 100/99.01,
    AzureBlob 99.33/91.71, Box 99.71/93.12, Common 96.87/93.14, GCS 99.49/94.20, MinIO 98.14/91.67, Physical 99.25/94.15,
    SharePoint 99.77/91.27). Physical and the base are unchanged this round, so the Linux container run was not repeated.

- **2026-10-11, S12 final fixes, providers (#16):** branch `16-final-providers` (worktree `D:\workspaces\wfp-wt\16e`),
  built beside `16-final-common` (#42) and rebased onto it, from the final review
  ([`docs/reviews/2026-10-11-2027-final.md`](../reviews/2026-10-11-2027-final.md), F1–F20) and D29–D32. Each fix has a
  test that fails without it where a test can show one (checked by reverting the fix), unless noted.
  - **F2, and item 7 of "For the final review" (Physical on Windows), closed:** on Windows `IsSegmentAllowed` refuses,
    with `ArgumentException`, a segment that ends in `.` or a space, holds `"<>|?*`, or is a reserved device name
    (`CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9`, and `COM`/`LPT` with the superscripts the Windows
    docs also reserve; any case; the part before the first dot, trailing spaces trimmed, so `con.txt` and `aux .log`
    too); `IsAddressable` drops such names from listings. An internal switch, `RefusesWindowsNames` (on by default on
    Windows), runs the rule on every system in the tests; Linux and macOS keep such names. Its
    `UnauthorizedAccessException` from any member is documented (the type is D29's; the base class passes it through
    as it is). Tests: next to `a.txt` and `dir`, `UpdateFileAsync("a.txt.")`, an append to `a.txt `,
    `DeleteAsync("dir ")` and `("dir.")`, `GetFileInfoAsync("x?")`, `CON.txt` and the other device and character cases,
    with `a.txt` and `dir` unchanged; such names made through `\\?\` on Windows (any path on Linux) are left out of
    listings; look-alikes (`CONSOLE.txt`, `COM10`, `report.con`) land as given; on Linux and macOS, with the switch
    off, the names land as themselves; on Windows the rule holds by default. Beyond the report, found by this half's
    review: an 8.3 short name (`LONGFO~1`, `REPORT~1.TXT`) reached the long name it stands for, the same silent
    overwrite or delete, since .NET's `GetFullPath` expands it; with the rule on, a path whose resolved disk path
    differs from its segments (ignoring case) is refused with `ArgumentException`. A Windows test runs it where the
    volume keeps short names (inconclusive elsewhere).
  - **F3:** `AmazonS3FileProviderOptions.AllowInsecureHttp` (`WhenWritingDefault`, so the golden shape holds; in
    `PublicAPI.Unshipped.txt`). An http `ServiceUrl`, or a given client whose `ServiceURL` is http (or that sends http
    to AWS, `UseHttp`; beyond the report, the same hole), toward a host that is not loopback throws `ArgumentException`
    naming the option, as MinIO's rule does. The offline tests opt in for `http://s3.test`; the emulator fixtures opt in
    only off loopback (a remote Docker host). For one meaning, MinIO's client constructor now holds a given client's own
    endpoint to the same rule (its `AllowInsecureHttp`); the check is shared in the S3 core.
  - **Item 3 of "For the final review" (GCS signing), closed:** a signing refusal or failure (`GoogleApiException`,
    `HttpRequestException`, `TokenResponseException` from IAM's `signBlob`) becomes `NotSupportedException` with the
    cause inside, so the URL service falls back to the gateway (D24). Tested through the client's HTTP seam with a
    Compute Engine credential whose `signBlob` answers 403, 500 or fails on the network. The class and options docs say
    missing Application Default Credentials are `InvalidOperationException` (D29). Not settled: whether the raw media
    GETs get the SDK's retries (unverified, as the item said).
  - **F5 / D30:** `FileAndFolderMayShareAName` in the conformance suite (false by default; true on the S3, MinIO,
    Azure and GCS fixtures, live ones included), with `Write_AFileAtAFolderPath` and `Write_AFolderUnderAFilePath`:
    where false both throw `IOException` and change nothing; where true both succeed, read back and list.
    `Move_RemovesExactlyTheSourceKey` gates its object-store half on the flag. The in-memory provider now refuses a
    write under a file, which it took before. **Found:** MinIO (the pinned container) stores both, but lists only the
    file and hides what is under the folder from every listing, a flat one too, while the file exists (both still read
    back by path, and deleting the file shows the folder again); a second flag, `ListsAFileAndAFolderOfOneName`, false
    on the MinIO fixtures, pins it, and the READMEs, the matrix and the migration guide say it. S3Mock, Azurite and
    fake-gcs-server list both. An older MinIO may refuse the writes instead (`XMinioParentIsObject`), which only
    `MinioLiveTests` against such a server would show. **Not changed, the local stream route:** a writeable local
    stream of a folder's path stays `IOException` on every provider, object stores included, because Common's
    `CreateLocalFileStreamCoreAsync` refuses it before anything is written (the contract's "a writeable one of a folder
    throws `IOException`"); following D30 there needs a capability the base class reads, a Common API addition in the
    common half's files, and F11 makes the member non-virtual, so no provider can do it alone. Left to the main session.
    Copies and moves onto a folder's path stay `FileExistsException` on every provider (the destination exists).
  - **F6:** Azure sends `new HttpRange(offset)`, and SharePoint's `DownloadAsync` `bytes={offset}-`, when
    `length > long.MaxValue - offset`. The S3 behaviour test's four (offset, length) cases moved into the conformance
    suite (`Range_LongerThanARangeCanNameReadsToTheEnd`), where they fail without the fix on Azurite and on the Graph
    fake, which now answers a malformed range 416, as a strict server may; a SharePoint unit test checks the header.
    The SharePoint change is one line, as `DownloadAsync`'s stream is the common half's (F20).
  - **F7 / D31:** S3 and MinIO answer `SupportsSignedUrl(Create)` false where the core does not check atomically
    (toward any named endpoint; MinIO always), so `GetSignedUrlAsync` throws `NotSupportedException` before any
    request and the URL service issues a gateway create URL. Read and write URLs stay native. Tests: offline (AWS signs
    `If-None-Match`; an endpoint, a client with a `ServiceURL` and MinIO refuse), the fixtures' capability, and the
    core's create URL used for real on MinIO through the core (the signature carries `If-None-Match`; 200, then 412).
  - **F8 / D32:** Azure hides only `.wfp-tmp` at the provider root, in both listings; Physical only `.wfp-`, 32
    lowercase hex digits and `.tmp`. The conformance case `List_ShowsACallersNamesThatLookLikeTheProvidersOwn`
    (`.wfp-notes.txt`, `.wfp-draft.tmp`, `d/.wfp-cache/x.json`, one level and recursive) fails on Azure without the fix;
    Physical's listing test and a unit test of the temp name pin its rule.
  - **F9:** the S3 core's recursive listing yields the folders above a key up to its first name the provider cannot
    address, and the file only when every name is addressable. The conformance case
    `List_RecursiveShowsTheFoldersTheOneLevelListingShows` (a folder that holds only an unaddressable subfolder) fails
    on S3 and MinIO without it.
  - **F14:** `SharePointLiveChecks` (`[TestCategory("Live")]`, modelled on `BoxLiveChecks`) sends a stale `If-Match`
    on `PUT /content`, on `createUploadSession`, and on a session whose file changes before its last fragment, through
    `GraphServiceClient` directly, signed in as the provider signs in (`CreateCredential` is internal now), and expects
    412 with the content unchanged. The live settings moved into one `SharePointLive` helper both classes use; the
    live-class rule holds (inconclusive without `WFP_LIVE=1`, a named failure without the account). "For the
    maintainers" names it. Not run: no account (D21).
  - **F15:** the S3 and MinIO expiry test waits, at most a minute, until the server's `Date` is past `X-Amz-Date` plus
    `X-Amz-Expires`, then asserts 403 strictly; `GcsLiveAtomicityTests.SignedReadUrl_WorksUntilItExpires` waits on
    Cloud Storage's `Date` the same way (its assertion is unchanged: not 200, as Google's status for an expired URL is
    unverified).
  - **F16:** the AmazonS3 options remarks and README say the sign-in rule as the report words it. No test (docs only).
  - **F18:** Box's chunked upload rents its part buffer from `ArrayPool<byte>.Shared` and returns it in a `finally`.
    No test can show the allocation; the offline Box suite, its chunked cases included, passes. The live Box suite did
    not run.
  - **N1:** `IsS3Endpoint` also takes a label exactly `s3-fips` (`s3-fips.<region>.amazonaws.com`, dual-stack FIPS,
    a bucket's virtual host); tests; the README and XML docs stop calling `s3-fips` legacy.
  - **Docs:** the Physical, Azure, Amazon S3, MinIO, Google Cloud Storage, Box and SharePoint READMEs, the root
    README's matrix (the folders column, the S3 and MinIO URL cells, the GCS gateway cell) and notes, the migration
    guide, the XML docs, and AGENTS.md (the live classes, the MinIO fixtures' flag).
  - **PR #43 review, round 1** (no blocker or major; five items, each decided fail-closed by the main session):
    - F1: a Windows root in the extended or device form (`\\?\C:\…`, `\\?\UNC\…`, `\\.\C:\…`) skipped .NET's
      normalization, so 8.3 short names under it went unexpanded while NTFS resolved them, and the short-name check was
      bypassed (the reviewer overwrote a file and deleted a folder through `LONGFO~1`). The constructor now takes such a
      root in its plain form, and writes that back; one with no plain form of at most 259 characters (a volume GUID, a
      device) throws `ArgumentException`. Tests: a Windows root with each prefix refuses the short names; the plain
      form of each shape, on every system.
    - F2: an endpoint the AWS SDK takes from its own configuration (`AWS_ENDPOINT_URL_S3`, `AWS_ENDPOINT_URL`, a
      profile's `endpoint_url`) was never checked when the provider built its client. It now counts as a named endpoint,
      as `ServiceUrl` does: the plain-http rule, conditions checked first, create URLs through the gateway, and
      requester-pays and a created bucket's location only for S3's own host names. Offline tests set and restore both
      variables. The README's line that such a provider "counts as AWS" and the class remarks are corrected.
    - F3: the GCS signing catch no longer takes `TokenResponseException` or a `GoogleApiException` with 401, so a
      refused credential fails as on every other member (`UnauthorizedAccessException`, by the common half's mapping)
      instead of having the URL service issue a gateway URL that fails later; signBlob's 403, other error
      answers and `HttpRequestException` stay `NotSupportedException`, and a timeout is left to the base. Tests: a token
      endpoint answering `invalid_grant`, and signBlob answering 401.
    - F4: the D30 conformance tests now pin the delete order: the first delete removes the file and leaves the folder,
      the second removes the folder.
    - F5: the migration guide says the device-name rule follows Windows' documented reservation, which recent builds
      do not always enforce, so a file such as `con.txt` that 2026 served is now refused and left out of listings.
    - Nits: the 8.3 check and the device-name lookup allocate nothing (a span compare, a `FrozenSet` span lookup);
      `SharePointLiveChecks` pages through the site's libraries as the provider does.
  - **PR #43 review, round 2** (F1–F5 verified; four items, each decided by the main session):
    - N1 (root protection, decided: refuse): under `\\?\`, `data.` and `data ` are folders of their own, and `.` and
      `..` are names, but the plain form drops a trailing dot or space and applies `.` and `..`, so such a root would
      name another folder (`\\?\C:\data\..\..\Windows` becomes `C:\Windows`). `PlainRootOf` now has no plain form for
      a path holding such a name, and the constructor refuses it. Tests: each shape, and a real `rootdot.` folder.
    - N2: the 259-character cap refused long roots that work in plain form; it is gone, and a long `\\?\` root builds
      and still refuses short names (Windows test).
    - N3: the configured endpoint is read when the provider is built, so a client the provider builds for AWS now sets
      the SDK's `IgnoreConfiguredEndpointUrls`: an endpoint configured later, before the first call, cannot redirect it
      unchecked. Tests: the client configuration, and an environment variable set after construction.
    - N4: the README says the SDK-configured endpoint also gets checksums only where S3 requires them.
  - **Rebased onto #42** (`02790dd`), both sides kept: the progress log's two entries, the conformance suite's and
    the in-memory provider's new members, and the provider test files' `CreateProviderThatFails` overrides and error
    tests beside this branch's. The GCS signing test now asserts `UnauthorizedAccessException` for a token endpoint
    answering `invalid_grant` and for signBlob answering 401, the cause inside, and the GCS docs and the migration
    guide name the two types. Where this branch meets D29's base mapping nothing is wrapped twice or remapped: the
    GCS signing's `NotSupportedException`, Physical's `ArgumentException` for a name, a short name or a root, and the
    D30 refusals' `IOException` are the contract's types and pass through, as no provider's `IsNotFound` or
    `IsAccessDenied` looks inside an exception; S3's plain-http check throws in the constructor, outside the mapping;
    and a signBlob 403 becomes `NotSupportedException` inside `GetSignedUrlCoreAsync`, before the base would read it
    as `UnauthorizedAccessException`, which is item 3's decision.
  - **Verified** (after the rebase onto #42): the solution builds strict; every non-Live test on Windows with
    `WFP_REQUIRE_DOCKER=1`: 2583, none failed, 15 skipped (the three concurrent-append cases, nine Unix-only Physical
    tests, and the three error-contract cases Physical's fixture cannot stage); Physical (231: 220 passed; skipped the
    concurrent append, the three error-contract cases, the Windows-only ACL test and six Windows-only name and root
    tests) and Common (310 passed) in a `dotnet/sdk:10.0` container as a non-root user; the coverage gate (Windows:
    AmazonS3 98.19/93.84, AspNetCore 100/99.01, AzureBlob 99.33/91.93, Box 99.71/93.12, Common 96.87/93.14, GCS
    99.49/94.29, MinIO 98.18/91.85, Physical 99.34/94.12, SharePoint 99.77/91.30). The AmazonS3 branch floor rose from
    91 to 92 in round 2, by this branch's own figures. No live suite ran (no accounts here; D21).

- **2026-10-11, S12 part 2, simplify (#16):** branch `16-simplify` (worktree `D:\workspaces\wfp-wt\16f`), built beside
  `16-launch` from the maintainer's requests of 2026-10-11 (a simple API; one provider by interface, several the app's
  choice; download URLs for the local disk in a few lines).
  - **DI without keys (Common):** `AddWriteableFileProvider(IConfiguration)` and `AddWriteableFileProvider(factory)`
    register a plain singleton `IWriteableFileProvider` with `AddSingleton`, not `TryAdd`, so every registration is in
    `GetServices` and the last one is injected; the container builds it on first use and disposes it. The keyed `name`
    overloads are gone. Tests: one provider (same instance, disposed with the container), from configuration, two
    registered (`GetServices` sees both, the last is injected), null arguments.
  - **The URL service and the gateway, for the default provider or a key:** `IWriteableFileUrlService` has
    `GetSignedUrlAsync(path, options, ct)`, for the container's `IWriteableFileProvider`, and
    `GetSignedUrlAsync(path, options, providerKey, ct)`, for one the app registered with
    `AddKeyedSingleton<IWriteableFileProvider>(key, ...)`. One name for both, the key after the options so it cannot be
    taken for a path, and the token last as on every async member. No call binds to both: only the keyed form takes a
    string after the options, and a `null` key goes to it and throws. The analyzer's RS0026 (two overloads with optional
    parameters) is suppressed for the pair, with its reason, as Common does for its 2027 overloads. Rejected: an optional
    key (`string? providerKey = null` before the token makes `GetSignedUrlAsync(path, options, ct)` fail to compile, a
    trap in every minimal API), a second method name (two names for one operation), and a keyed URL service per key
    (`AnyKey` registrations: more machinery than the overload). No default provider registered:
    `InvalidOperationException`; an unknown key: `ArgumentException` naming `providerKey`. The gateway resolves the
    same way: no key `GetService<IWriteableFileProvider>()`, a key `GetKeyedService<IWriteableFileProvider>(key)`.
  - **The token (a one-way door, opened by the maintainer's request for this release):** the format is kept,
    `{access}.{provider}.{payload}`; an empty provider segment means the default provider, and its purpose chain leaves
    the provider out (`WriteableFileProviders.Gateway.v1`, the access). Not an empty purpose: Data Protection allows it
    but advises against it. Not a fixed name such as `default`: a key of that name would share the chain. A chain one
    purpose shorter matches no keyed chain, since Data Protection authenticates the number of purposes, so neither kind
    of token redeems as the other (tested both ways, and with a key that spells the access). A key is never empty, so
    the empty segment names no keyed provider. Tokens issued with a provider name since S11 redeem as keyed ones,
    unchanged: `GatewayTokenGoldenTests`' existing tokens and literal tests pass untouched (only additions to the file),
    and three default-provider goldens, issued once with the same test key and never regenerated, plus literal-built
    tokens and the literal format, pin the new form. AGENTS.md's door text says it.
  - **One-call gateway setup:** `AddWriteableFileProviderGateway(Uri publicBaseUri)`, next to the options form.
    `TutorialTests` runs the root README's tutorial line for line over the Physical provider (TestServer, a temp
    folder): the link endpoint returns a gateway URL, which downloads the file. The same snippet, with its three
    `using`s, also compiles as a standalone web app.
  - **The simplicity pass over the nine packages' PublicAPI files** listed 17 candidates, each with its trade-off. The
    maintainer agreed a set on 2026-10-11, posted on #3
    ([the agreement](https://github.com/serionist/WriteableFileProviders/issues/3#issuecomment-6108843232)), which is
    the maintainers' agreement for the API changes below; all of it is new 2027 surface or a 2026 shape kept compiling.
    Implemented:
    - **The 2026 conveniences become `[Obsolete]`, to go in 2028.0.0** (candidates 1 to 4): the sync wrappers
      (`ReadFile`, `UpdateFile`, `Delete`, `Move`, `CreateLocalFileStream`, and `GetFileInfo`/`GetDirectoryContents`
      on a `ProviderPath`), the 12 `IFileInfo` overloads, `UpdateFile[Async](path, contents, UpdateFileMode)` and
      `GetDownloadUrl[Async]` move into `WriteableFileProviderCompatibilityExtensions`. Each keeps working, and its
      message names the async `ProviderPath` member to call and says it goes in 2028.0.0; the 21 existing shims say so
      too. `WriteableFileProviderExtensions` had no member left, so it is now an internal class of the shared helpers.
      The main API is the async members taking a `ProviderPath`; `IFileProvider`'s own `GetFileInfo(string)`,
      `GetDirectoryContents(string)` and `Watch(string)` stay. Tests call the async forms; `ExtensionTests` keeps a
      compatibility test of every obsolete member, with the warning suppressed, and a reflection test that every
      extension method on `IWriteableFileProvider` (44) is in the compatibility class, `[Obsolete]`, and names its
      replacement and 2028.0.0. The obsolete forms call the public async members, so the error contract (D29) holds on
      them: `ErrorPolicyTests` checks that each one that reaches the store lets out `IOException`,
      `FileNotFoundException` and `UnauthorizedAccessException` with the store's exception inside, as the async members
      do, the synchronous ones unwrapped.
    - **Namespaces** (candidate 10): `AddWriteableFileProvider` and `AddWriteableFileProviderGateway` are in
      `Microsoft.Extensions.DependencyInjection`, `MapWriteableFileProviderGateway` in `Microsoft.AspNetCore.Builder`,
      as ASP.NET Core's own are. IDE0130 (namespace matches the folder) is disabled for those three declarations only,
      with the reason beside it.
    - **`SignedUrlOptions.Permission` is `Access`** (candidate 9), with `SupportsSignedUrl(access)` and
      `SupportsSignedUrlContentHeaders(access)`; nothing stored or signed carries the name, so no door moves.
    - **Azure** (candidates 14 and 15): `AzureFileProviderOptions` is `AzureBlobFileProviderOptions`; the old name is
      an `[Obsolete]`, empty subclass (not sealed, as the 2026 class was not), so `new WriteableAzureBlobFileProvider(new AzureFileProviderOptions
      { ... })` compiles and builds the same provider, and a test pins that it writes the same JSON and reads back as
      the new type. The configuration shape names the provider class and the properties, not the options class, so the
      goldens pass unchanged. The client constructor takes non-null options, as the other five do.
    - **Internal** (candidates 7 and 12): `RangeReadStream` (only Common builds it: the default seekable read) and the
      gateway's `MaxValidityLimit` (the docs say 7 days). AGENTS.md's door list drops `RangeReadStream`.

    Considered and kept, with the reason:
    - 5, `WriteableFileProvider<T>.ToJson()`: the direct replacement of 2026's `provider.ToJson()` on a concrete type.
    - 6, `FromJson(JsonObject)`: the direct replacement of 2026's `FromJson(JObject)`.
    - 8, `DirectoryContents` public: it was public in 2026, and making it internal would break code that built one.
    - 11, the records' generated members on the option types: `with` is how callers vary an option.
    - 13, the gateway's 7-day read default (D22): it matches the native URLs' 7-day maximum on purpose.
    - 16, `ReadFileVersionAsync(versionId, path)`: both parameters take a string, so swapping them would compile old
      calls with the arguments reversed.
    - 17, the 2026 positional options constructors: 2026 code compiles unchanged.
  - **Docs:** XML docs of every changed member; the migration guide (DI, the obsolete forms, `GetDownloadUrl`, the
    `IFileInfo` overloads, the Azure options rename and client constructor, the AspNetCore namespaces); AGENTS.md's
    gateway door, Names and Common API lines, and its tests line; and the READMEs' samples, edited only so they compile
    against this API (the launch half rewrites the READMEs). All 24 C# samples of the READMEs compile, in a scratch
    project where an obsolete call is an error.

- **2026-10-11, S12 part 2, simplify, review round 1 (PR #44, read at `7cadd5c`):** no blocker or major. The review
  confirmed the gateway token design (no collision between default and keyed chains, the purpose count authenticated,
  empty and whitespace keys refused, v1 tokens still redeeming, the gateway failing closed without a default provider),
  that the obsolete sync forms do not deadlock under a `SynchronizationContext`, and that Azure's binder, JSON and
  goldens are unchanged. Fixed:
  - **F1:** the PR's title (the main session).
  - **F2 (decided: string):** 2026's `GetDownloadUrl[Async]` returned `string`/`Task<string>`, so the obsolete forms
    return them again, the URL's `AbsoluteUri` (escaped, never `Uri.ToString()`), and `string url =
    p.GetDownloadUrl(...)` compiles as the agreement says. The compatibility tests take a string and pin the escaping.
  - **F3:** the obsolete `AzureFileProviderOptions` is not sealed, as 2026's class was not; the migration row says that
    `provider.Options` is now an `AzureBlobFileProviderOptions`, so code that types it with the old name changes it.
  - **F4 (decided: document):** the URL service resolves the provider from the root container and the gateway from the
    request's services, so a scoped or transient provider that names another store per scope would let a URL be
    redeemed against another store. The `IWriteableFileUrlService` remarks, the AspNetCore README and the migration
    guide say every provider the gateway serves, default or keyed, must be a singleton or resolve to the same store in
    every scope.

- **2026-10-11, S12 launch (`16-launch`, #16):** the maintainer's requests of the day for the launch: simple docs, a
  short README with the detail branching off it, MIT, a repo others find, and automated NuGet releases
  (`.local/briefs/simplify-launch.md`, launch items 1–6). Built beside `16-simplify`, against the API it agreed on #3,
  and rebased onto #43 and then `16-simplify` before the push, carrying their README and migration-guide facts into the
  new structure.
  - **The README** (about 160 lines, most of them code): badges, what the library is, install, a four-step tutorial
    (the local disk; download links for local files through the gateway, the `16-simplify` snippet; the same code on a
    cloud store by its configuration; one provider by interface or several), the packages, links to the guide, and
    Versioning, Contributing, Security and Licence in a few lines each.
  - **The guide**, `docs/guide/`, one topic a page: `providers.md` (the matrix and its notes, moved), `paths-and-listings.md`,
    `reading-and-writing.md`, `signed-urls.md` (native URLs provider by provider, the URL service, the gateway's
    endpoints, headers, tokens, one-time uploads and logs, moved from the AspNetCore README), `dependency-injection.md`,
    `errors.md` (the contract and each provider's mapping, moved from the package READMEs' "Errors" items),
    `writing-a-provider.md` (from Common's README and the migration guide) and `versioning.md` (D28, moved from the
    README). Text was moved, not rewritten, and each new sentence checked against the code.
  - **Package READMEs** keep install, a short use, their own options, authentication, behaviour and limits; their
    signed URL and error details moved to the guide, with a summary and a link. Every sample uses the final API:
    `AddWriteableFileProvider(configuration)`, `UpdateFileOptions.Append`, `AzureBlobFileProviderOptions`, `Access`.
  - **Licence (D8):** `LICENSE`, `PackageLicenseExpression` MIT and the copyright line, the README's Licence section,
    CONTRIBUTING's line that contributions are MIT. MIT is the maintainer's choice; the agreement of László Nemes and
    Starschema that D8 asked for is for the maintainer to confirm before the tag, the first item under the release in
    the list for the maintainers.
  - **Findability:** the shared tags (`ifileprovider`, `fileprovider`, `filesystem`, `file-storage`, `storage`,
    `cloud-storage`, `upload`, `download`, `signed-url`, `writeable`) and each package's own; descriptions that lead with
    what the package does and name the other stores; `PackageReleaseNotes` linking the changelog and the migration
    guide; `CHANGELOG.md` with the 2027.0.0 section, which `release.yml`'s GitHub release now uses as its notes (the
    version's section, a prerelease taking its release's, generated notes and a warning without one); the logo, the
    mark and a social preview as SVG under `docs/assets/`, with `icon.png` (128×128, packed as every package's
    `PackageIcon`) and `social-preview.png` (1280×640) rendered by `eng/render-assets.sh` in an Alpine container
    (librsvg, Inter).
  - **`docs/releasing.md`:** the one-time setup in order (the `nuget` environment, the tag ruleset, `NUGET_USER`,
    trusted publishing, the prefix reservation), each release (CHANGELOG, optional rc, tag, approve), what to check on
    nuget.org, moving PublicAPI Unshipped to Shipped (a script, run on this branch and built), and the package
    validation baseline. AGENTS.md's Release line, `release.yml`'s header and this plan link it.
  - **AGENTS.md, REVIEW.md, CONTRIBUTING.md, SECURITY.md:** the guide, the runbook, the assets and the changelog in
    "Where things are"; a change consumers notice adds a line to `CHANGELOG.md`; the versioning links point to
    `docs/guide/versioning.md`.
  - **Verified** (rebased onto `a435930`, #43 and #44 merged): the solution builds strict in Release; `dotnet pack` and
    `eng/package-check.cs` pass for the untagged prerelease (`2027.0.0-alpha.0.116`) and for a stable pack
    (`2027.0.0`, through `MinVerVersionOverride`), every package with its README, `icon.png`, the MIT expression, the
    copyright and the new tags; every non-Live test on Windows with `WFP_REQUIRE_DOCKER=1`: 2609, none failed, 15 skipped.
    Every C# block of the README, the guide and the package READMEs (41) compiles against this branch in a scratch
    project, the blocks with their own `using`s with nothing else imported; the release-notes extraction was run on
    the changelog with gawk and mawk; the move-to-shipped script was run on this branch and the packages built with it.
    No code changed, so the coverage gate was not re-run.
  - **Review round 1 (PR #45, read at `04c06a6`):** no blocker or major; every finding fixed:
    - F1: step 3 says Azure's link is a user delegation SAS when the identity holds a Storage Blob Data role at the
      account (or Storage Blob Delegator), else the gateway's.
    - F2 and F15: one project carries the tutorial (`dotnet new web`, then each step's package). Step 2 registers with
      `AddWriteableFileProvider(_ => ...)`, so step 3 changes only the registration; the disposal bullet, step 3's aside
      and step 4's endpoint went; `TutorialTests` follows.
    - F3: links are handed out as `Url.AbsoluteUri`, since `Uri.ToString()` unescapes.
    - F4: an append's condition on MinIO, and on S3 toward a named endpoint, is checked and then sent.
    - F5 to F7: links inside the root are followed (README); Common's README names the dot-segment root; the changelog
      lists S3's plain-http refusal.
    - F8 and F9 (decided): the tag ruleset's bypass takes roles (Repository admin and Write); the prefix reservation
      comes first; the trusted publishing policy is owned by `serionist`, scoped to new packages and new versions on
      `WriteableFileProviders.*`; `NUGET_USER` is `serionist`.
    - F10 (decided): `release.yml`'s build job fails a stable tag whose CHANGELOG section is missing or still says
      unreleased, before the approval; prereleases keep the generated-notes fallback. actionlint passes.
    - F11 to F14: the props file names the runbook's section; "the job's OIDC token"; the baseline is a
      `PackageDownload` at restore, and a package first released later sets its own or none; `release.yml`'s setup
      header points to the runbook.
    - F16 (decided: one copy): the signed URL page links the tutorial's step 2 instead of repeating it; the gateway's
      limits live in the AspNetCore README, the singleton rule in the signed URL page.
    - F17 (decided: trim): the package READMEs drop the bullets that restated the guide's rules (prefix folders,
      unaddressable names, a missing container or bucket, the error contract) for one line that links the guide; every
      provider-specific fact stays.
    - Q1: D8 records MIT as the maintainer's choice; the agreement of László Nemes and Starschema is the first item
      under the release in the list for the maintainers.
    - Verified: the solution builds in Release; pack and the package check pass at `2027.0.0-alpha.0.118` and
      `2027.0.0`; the 40 C# blocks of the docs compile; the AspNetCore tests pass (233, `TutorialTests` included);
      actionlint 1.7.7 is clean; the 25 docs have no broken link or anchor; the changelog gate was run on a dated, an
      undated and a missing section and on a prerelease.

- **2026-10-11, S12 done (#46):** the floors follow the rule on the merged code (CI Linux at #45, 267c61b, and the
  Windows run at #44's head): Common rises to 96/92 (Linux 97.21/94.24, Windows 97.01/93.14); every other floor is
  already at or above the whole-point floor of the lower figure minus one, and every floor is 90% or more. The S12 row
  is done. The GitHub repository has its description, homepage (the Common package on nuget.org) and 20 topics, set
  with `gh repo edit` after #45; GitHub detects the MIT licence. What is left for 2027.0.0 is in "For the maintainers"
  below: the licence agreement (D8), the security items, the nuget.org and environment setup of `docs/releasing.md`,
  dating the changelog, and the tag.

- **2026-10-11, repository settings (with the maintainer's yes):** set through the GitHub API:
  - private vulnerability reporting, Dependabot alerts and Dependabot security updates are on;
  - the `main: PRs only` ruleset requires "Build, test, coverage, pack (Linux)" and "Build and test (Windows)" (GitHub
    Actions);
  - the `nuget` environment requires serionist or NemesLaszlo to approve, deploys only from `v*` tags, and holds the
    secret `NUGET_USER` = `serionist`;
  - a "Release tags" ruleset restricts creating, moving and deleting `v*` tags to the admin and write roles.

  Left for the maintainer: reserving the id prefix and the trusted publishing policy on nuget.org (steps 1 and 5 of
  `docs/releasing.md`), and the other open items below.

- **2026-10-11, the PublicAPI files dropped (the maintainer's decision):** package validation alone guards the
  shipped API, rather than a per-member listing that every public change must update. Gone: every
  `src/*/PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`, the `Microsoft.CodeAnalysis.PublicApiAnalyzers`
  reference and `AdditionalFiles` in `src/Directory.Build.props` and its version, the RS0016 and RS0017 severities in
  `.editorconfig`, and the RS0026 and RS0027 suppressions (AmazonS3's and AspNetCore's `GlobalSuppressions.cs` went
  with them; Common's keeps its CA1716 suppression); the lock files were restored again. `EnablePackageValidation`
  stays, and the baseline is still set once 2027.0.0 ships (`docs/releasing.md`). AGENTS.md and `docs/releasing.md`
  no longer describe the files; the S12 row, Later and "For the maintainers" follow it. On the branch `3-no-plan-ids`,
  after its first commit, which replaced the plan and review ids outside `docs/plans/` and `docs/reviews/` with the
  reasons they stood for (D1-D32, F1-F79 and P1-P41 stay the records here).

- **2026-10-11, docs rewritten short (#49, #50, #51), at the maintainer's request:** "short, to the point, easy to
  read, no walls of text". The README shows the interface, the providers and how signed links work (#47); the package
  READMEs share one shape (install, use, options table, authentication, what's different; 1,317 lines to 767); the
  guide pages are code first with each rule on one page (Signed URLs 301 lines to 150); the migration guide is a
  checklist and short "2026 → 2027 → what to do" tables (512 lines to 352; one missing break added: Box and SharePoint
  reads no longer seek). Every sample compiles, and all 154 links and anchors in the docs resolve. Code, tests and
  docs no longer cite plan or review ids (#48), and AGENTS.md holds that rule.

### For the maintainers

What this session cannot do, in the order to do it: first what closes a security exposure, then the review of what
merged, the repository's settings, the live runs, and the release. Kept current as the work goes on.

**Security, now:**

- [ ] Rotate the MinIO access key pair from F9 on that server, and any other credential that was ever in the Azure
  DevOps history.
- [ ] Delete the Azure DevOps repository and organization.
- [x] Turn on private vulnerability reporting (Settings > Code security): the README, SECURITY.md and the issue forms
  send reporters there. It was off on 2026-10-11. Done 2026-10-11.
- [x] The S0 patch is published (nuget.org, seen on 2026-10-11): `WriteableFileProviders.Common` 2026.10.10.6 and
  `WriteableFileProviders.AmazonS3` 2026.10.10.3 (with `ReadFileVersionAsync` and the old members `[Obsolete]`), and
  every other provider released again on it: Physical, AzureBlob and Minio 2026.10.10.3, GoogleCloudStorage
  2026.10.10.6, Box and SharePoint 2026.10.10.4. Each depends on Common `[2026.10.10.6, )`, with no upper bound.
  Common 2026.10.10.5 and AmazonS3 2026.10.10.2 went out before #17 merged.
- [ ] Publish a GitHub security advisory for F1, F2 and F47, naming the fixed versions: `WriteableFileProviders.Common`
  2026.10.10.6 and `WriteableFileProviders.AmazonS3` 2026.10.10.3.
- [ ] Decide whether to ship 2026.10.10.7 of every provider with Common pinned `[2026.10.10.6, 2027.0.0-0)`, so a 2026
  provider cannot restore against Common 2027: today's 2026.10.10.x providers carry no upper bound, so an application
  that mixes in a 2027 package gets no restore warning, only a type load failure at run time (the root README's
  Versioning section says to update every package together).

**Review what merged (D13):**

- [ ] Review the merged PRs of #3 after the fact: #17 (S0), #18 (S1), #19 (S2), #20 and #22 (S3), #21 (plan round 2),
  #23 (S4), #24 (S11), #25 (S10), #26 (S5), #27 (S8), #28 (S9), #29 (S6), #30 (S7), #31 (S12 part 1), #41 (S12 part 2,
  harmonize), the docs PR of S12 part 2 and the final review's, as they merge; and Dependabot's #32 (the SDK in
  `global.json`). These touch the harness or a one-way door:
  - #17 (`IWriteableFileProvider.cs`, AGENTS.md);
  - #19 and #20 (the build, the test platform, CI, AGENTS.md, REVIEW.md);
  - #22, which **lowered** the AmazonS3 and Google Cloud Storage branch floors from 14 to 13 (the gate had printed a
    rounded 14.0% for a rate just under it); AGENTS.md says never to lower a floor to pass, so judge it, though both
    floors have risen far above since;
  - #23 (the Common API and the configuration shape) and #24 (the gateway's URLs);
  - #25 to #30 (AGENTS.md, `Directory.Packages.props`, `eng/coverage.json`, and new options, which extend the stored
    configuration shape);
  - #31 (the workflows, CONTRIBUTING, SECURITY) and #32 (`global.json`);
  - #41 (the client constructors, the PublicAPI files, `src/Directory.Build.targets`, AGENTS.md);
  - the docs PR (AGENTS.md, REVIEW.md, D28, CI's stable pack).

**The repository's settings** (Settings > Code security, and the rulesets):

- [x] Turn on Dependabot alerts and Dependabot security updates; `dependabot.yml` only schedules version updates.
  Both were off on 2026-10-11. Done 2026-10-11.
- [ ] Turn on the dependency graph and Automatic dependency submission: the graph does not read the versions in
  `Directory.Packages.props`, so without the submission the alerts miss every package. NuGetAudit stays the guard at
  restore time.
- [ ] Keep secret scanning and its push protection on (on as of 2026-10-11), and CodeQL's default setup off:
  `codeql.yml` is the advanced setup, and the two conflict.
- [x] Make CI a required check in the `main` ruleset: "Build, test, coverage, pack (Linux)" and "Build and test
  (Windows)", and CodeQL's "Analyze (csharp)" if you want code scanning to gate merges too. Done 2026-10-11.
- [ ] Set the social preview (Settings, General, Social preview): upload `docs/assets/social-preview.png`, 1280×640.
- [ ] Turn on Discussions, if you want questions kept apart from issues (Settings, General, Features); the issue forms'
  `config.yml` can then link them.

**Environments** (a workflow that names an environment creates it unprotected on its first run, so protect each one
before it holds a secret or nuget.org trusts it):

- [ ] Configure the release ([docs/releasing.md](../releasing.md), "Once: set it up", has each step), in this
  order, before the first tag:
  1. Reserve the `WriteableFileProviders.` id prefix on nuget.org: an email from the owning account, `serionist`, to
     `account@nuget.org`, naming the prefix `WriteableFileProviders.*`.
  2. The `nuget` environment: required reviewers, the maintainers; deployment tags `v*` only.
     Done 2026-10-11 (serionist and NemesLaszlo).
  3. A tag ruleset on `v*` restricting creations, updates and deletions. Its bypass takes roles, not users: Repository
     admin and Write (today exactly the two maintainers; anyone given write later can tag).
     Done 2026-10-11 ("Release tags").
  4. The `nuget` environment's secret `NUGET_USER`: `serionist`, the nuget.org profile name (not the email) of the
     account that owns the packages. Done 2026-10-11.
  5. On nuget.org, as `serionist`, a trusted publishing policy owned by `serionist`: repository owner `serionist`,
     repository `WriteableFileProviders`, workflow `release.yml`, environment `nuget`, with the scopes for new
     packages and new versions on the glob `WriteableFileProviders.*` (`WriteableFileProviders.AspNetCore` is a new id).
- [ ] Create the `live-cloud` environment for `live.yml`, protected first: deployment branches `main` only, and no
  required reviewers (the nightly run would wait). Then one secret per `WFP_*` variable the workflow maps, for
  accounts you own and keep for it: Box's app configuration JSON as the one secret `WFP_BOX_CONFIG_JSON`; Google Cloud
  Storage's `client_email` and `private_key` as `WFP_GCS_CLIENT_EMAIL` and `WFP_GCS_PRIVATE_KEY`, never the whole key
  file. A provider without its secrets is skipped at night.

**Live runs**, which no PR could make (D21). Run each provider's live suites against an account you own before tagging
`v2027.0.0`; each Live class says which settings it reads, is inconclusive without `WFP_LIVE=1`, and fails naming what
is missing when `WFP_LIVE=1` is set without them.

- [ ] Box (S10): run `BoxLiveChecks`, `BoxLiveTests` and `BoxLiveAtomicTests` (AGENTS.md, Tests) against a Box app you
  own, then empty its trash. Until item 1 is settled, expect the nightly `live.yml` run to fail on Box's quoted
  `If-Match` checks (`BoxLiveChecks.QuotedIfMatch_*` and `BoxLiveAtomicTests`) wherever Box does not match the quoted
  ETag: a red Box job there is this item, not a regression. What they confirm, first one first:
  1. Box matches the quoted `If-Match` the SDK sends (`BoxLiveChecks.QuotedIfMatch_MatchesTheETag`, and
     `BoxLiveChecks.QuotedIfMatch_OnACommit_IsChecked` and `BoxLiveAtomicTests` for the upload-session commit). If it
     does, turn `SendIfMatch` on by default in `WriteableBoxFileProvider`, and call `IfMatch` atomic in its docs, the
     README and its matrix row, the migration guide and D15. If not, Box stays "checked, not atomic", or the
     provider needs its own network client that sends the ETag unquoted (option C of the S10 research notes,
     `.local/research/box-v10.md`, on the machine of the session that wrote them); either way, settle what the
     nightly run expects.
  2. JWT sign-in works without `kid` in the header, also for an app with two public keys.
  3. The download URL honours `Range` with 206, and a URL for a version serves that version after a newer one lands
     (`BoxLiveChecks.DownloadUrl_HonoursRange_AndServesItsVersion`).
  4. `PUT /files/{id}` answers a taken name with 409 `item_name_in_use`, and allows a case-only rename
     (`BoxLiveChecks.Rename_OntoATakenName_Is409_AndACaseOnlyRenameWorks`; the provider falls back to a temporary name
     when it does not).
  5. `usemarker=True` is accepted, and the last page's `next_marker` is null, absent or empty
     (`BoxLiveChecks.Listing_EndsItsMarkerPagesAsTheProviderExpects`).
  6. The 100 MB conformance test passes: Box's chunked minimum, its part size, and how often a commit answers 202.
  7. The download URL still lives 15 minutes.
- [ ] SharePoint (S9): `SharePointLiveTests` and `SharePointLiveChecks` with `WFP_LIVE=1` must confirm what no offline
  test can, from .local/research/graph-v6.md section 9:
  1. A stale `If-Match` on a simple upload (`PUT /content`) gets 412 and writes nothing, and so does one on
     `createUploadSession` and on a session whose file changed before its last fragment:
     `SharePointLiveChecks.StaleIfMatch_OnASimpleUpload_Is412AndWritesNothing`,
     `SharePointLiveChecks.StaleIfMatch_AtCreateUploadSession_Is412` and
     `SharePointLiveChecks.IfMatch_OfASessionWhoseFileChangesBeforeItsLastFragment_Is412AndWritesNothing` send them
     through `GraphServiceClient` directly. If the simple upload's check passes, set `DetectsConcurrentAppends` in
     `SharePointLiveTests` and decide with D15 whether SharePoint can say "atomic" below the 4 MiB session threshold;
     if all three pass, for sessions too.
  2. `createUploadSession`'s answer, with or without `nextExpectedRanges`; fragments without a token succeed.
  3. The copy monitor's status codes when done and when failed (200, 202 or a 303), and whether a taken name is ever
     refused at once (409).
  4. `PATCH` honours the conflict behaviour in the query, and a case-only rename succeeds (`Move_ToANameDifferingOnlyInCase`).
  5. Folder creation with `@microsoft.graph.conflictBehavior` `fail` in the body; path-addressed `/copy`, `PATCH` and
     `DELETE` (`root:/path:`); whether a path upload creates missing folders itself (the provider creates them anyway).
  6. `$select` with `@microsoft.graph.downloadUrl` returns the URL; its real lifetime and its error once expired;
     whether it serves the version current when it was issued; a range past the end (416?) and how often a range is
     ignored; a 0-byte download.
  7. Certificate sign-in with the key loaded as `EphemeralKeySet` (Windows and Linux), and managed identity.
  8. A site URL with an escaped character resolves, and names with `#`, `%`, `&`, `+` and non-ASCII letters round-trip.
     In particular a folder named `a%41` beside one named `aA` (sent as `a%2541`: each stays itself), and an item named
     `%2E%2E`: whether SharePoint lets one exist, and whether it ever resolves a decoded `..` in a path (the provider
     escapes `%` and leaves such names out of listings).
  9. `GET …/children` of a file: an empty list or an error (the fake assumes an empty list; the provider reads either
     as "not a folder"). Also an assumption of the fake: `If-Match` enforced on a simple upload (item 1).
  10. The 100 MB conformance case through upload sessions of 5 MiB fragments.
  11. Optionally pin Kiota 2.1.4 (see S9's progress-log entry).
- [ ] Amazon S3 and MinIO (S7):
  - Automated: `AmazonS3LiveTests` and `MinioLiveTests` (`WFP_LIVE=1`, the `WFP_S3_*` and `WFP_MINIO_*` settings) run
    the conformance suite. On AWS that shows the SDK's default checksums (CRC32 on PUT and parts, validated ranged and
    seekable GETs), multipart completion with part ETags only (writes and appends past 8 MiB), conditional writes
    checked by AWS, a missing bucket reported, and presigned URLs issued (not used).
  - By hand on AWS, which no suite runs (the behaviour suite creates buckets and writes 1,100 objects, so it stays on
    the emulators):
    1. Presigned URLs: a 2-minute read URL downloads, with one character of its signature changed it is refused (403),
       and after 2 minutes it is refused; a create URL used twice answers 200 then 412; a write URL with a
       `ContentType` refuses another `Content-Type`.
    2. Conditions AWS must enforce, with the AWS CLI on scratch keys: `copy-object` with `--if-none-match '*'` onto an
       existing key, and `delete-object` with `--if-match` and a stale ETag, both answer 412.
    3. `UseDefaultCredentials` on a machine or role with credentials: read and write work; with STS session
       credentials, a signed URL needs `AllowShorterValidity` and carries their expiry.
    4. A requester-pays bucket of another account: the live suite against it passes.
    5. `CreateBucketIfMissing` with a new bucket name outside us-east-1: the bucket is created in that region.
    6. `ReadFileVersionAsync` on a bucket with versioning reads an older version.
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
- [ ] Azure (S6): run `AzureBlobLiveTests` with `WFP_LIVE=1`, once with a shared key and once with
  `WFP_AZURE_USE_DEFAULT_CREDENTIAL=1` (a Storage Blob Data Contributor role at the account), and by hand: append to and
  overwrite an append blob written by 2026.10.10.x, and append to a blob uploaded in one Put Blob. The progress log
  (S6) lists what only a real account can confirm.
- [ ] Physical (S5): decide whether writes on Windows should go past open readers through `File.Replace` with a backup
  file (see the S5 entry). If you can, run `WriteableFileProviders.Physical.Tests` on macOS, which no CI covers, and
  `PhysicalLiveTests` on a folder you name.

**Decisions only the maintainers can take:**

- [ ] Fill in the date in SECURITY.md until which 2026.10.10.x gets security fixes (D28); it now says the maintainers
  will announce it there.

**The release (D28):**

- [ ] Confirm László Nemes and Starschema agree to MIT (D8) before tagging v2027.0.0.
- [ ] Once everything above that blocks it is done, and the final review of S12 has merged: date the `2027.0.0`
  heading of `CHANGELOG.md` in a PR, optionally tag `v2027.0.0-rc.1` on `main` for a prerelease, then `v2027.0.0`.
  `release.yml` asks the `nuget` environment's reviewers before it pushes ([docs/releasing.md](../releasing.md),
  "Every release").
- [ ] After 2027.0.0 ships: check the packages on nuget.org, and set the package validation baselines to `2027.0.0`
  (Later); docs/releasing.md, "Afterwards", has each.
- [ ] Unlist every version before the 2026.10.10 line on nuget.org, keeping `2026.10.10.x` listed for .NET 8
  consumers. What nuget.org has (its flat-container index, 2026-10-11):
  - `WriteableFileProviders.Common`: 1.0.0, 1.0.1, 1.0.2, and 2018.11.26.2 to 2024.2.7.1 (27 versions in all);
  - `WriteableFileProviders.Physical`: 1.0.0, and 2018.11.26.1 to 2024.2.7.1 (27 in all);
  - `WriteableFileProviders.AzureBlob`: 2018.11.27.7 to 2024.12.10.1 (20);
  - `WriteableFileProviders.AmazonS3`: 2018.11.28.2 to 2024.3.25.1 (20);
  - `WriteableFileProviders.Minio`: 2019.9.4.3 to 2024.11.14.1, and 2025.6.19.2 (11 in all);
  - `WriteableFileProviders.GoogleCloudStorage`: 2026.6.15.1;
  - `WriteableFileProviders.Box`: 2025.6.22.1;
  - `WriteableFileProviders.SharePoint`: 2025.7.15.1, 2025.7.15.2, 2025.7.17.1, 2025.7.21.1.

  Once the advisory names the fixed versions, consider unlisting the 2026.10.10.x versions before the patch too, so
  only the patched line shows: Common .4 and .5, Physical, AzureBlob, AmazonS3 and Minio .1 and .2,
  GoogleCloudStorage .4 and .5, Box and SharePoint .2 and .3.

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
