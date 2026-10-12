# WriteableFileProviders

Writeable `IFileProvider` implementations for .NET: one interface, `IWriteableFileProvider`, over the local disk, Azure
Blob Storage, Amazon S3, MinIO, Box, SharePoint and Google Cloud Storage, each shipped as its own NuGet package. This
file is for anyone who changes the repo, person or agent: where things are, how to build and test, how a change lands,
and what never to do. What the packages do for those who use them is in [README.md](README.md) and the guide it
links, `docs/guide/`.

## Where things are

| Path | What |
|------|------|
| `src/WriteableFileProviders.Common/` | `IWriteableFileProvider`; `WriteableFileProvider<TOptions>`, the base class every provider derives from; `FileProviderOptions` (the root folder); `LocalFileStream`; `WriteableFileProviderSerializer`, which builds a provider from configuration or JSON. Package `WriteableFileProviders.Common`. |
| `src/WriteableFileProviders.<Provider>/` | One provider each: `Physical`, `AzureBlob`, `AmazonS3`, `Minio`, `Box`, `SharePoint`, `GoogleCloudStorage`. Package `WriteableFileProviders.<Provider>`. |
| `src/WriteableFileProviders.AspNetCore/` | The signed-URL gateway for ASP.NET Core: `IWriteableFileUrlService` (a provider's native URL, else a gateway URL), the `GET`/`HEAD`/`PUT` endpoints on Data Protection tokens, and the one-time token store. Package `WriteableFileProviders.AspNetCore`. |
| `src/Shared/`, `tests/Shared/` | Source compiled into more than one project, linked by each `.csproj`: the internal S3 core (`S3/`) that the AmazonS3 and Minio packages both build, and the test code their two test projects share; `SliceReadStream`, the ranged read of a download that Common, Box, GoogleCloudStorage and SharePoint each compile, and its tests, linked into those four test projects. |
| `tests/WriteableFileProviders.Testing/` | Shared test code: `ProviderConformanceTests`, the contract every provider passes, and `InMemoryFileProvider`, the reference provider that passes it. |
| `tests/WriteableFileProviders.Common.Tests/` | Common's tests: the conformance suite on the in-memory provider, unit tests, and the configuration-shape goldens. |
| `tests/WriteableFileProviders.AspNetCore.Tests/` | The gateway and the URL service, end to end on TestServer over the in-memory provider. |
| `tests/WriteableFileProviders.<Provider>.Tests/` | A provider's tests: the conformance suite on its store, emulator or fake, its own cases, and a `Live` suite (see Tests). |
| `tests/WriteableFileProviders.Aot/` | The native AOT test: an app over every package but GoogleCloudStorage and Box, which CI publishes with native AOT and runs on the local disk, the gateway, Azurite, MinIO and the Graph fake. Its README says why it is an app. |
| `docs/guide/` | The user guide, one topic a page, each linked from the README: `providers.md` (the comparison matrix), `paths-and-listings.md`, `reading-and-writing.md`, `signed-urls.md` (native URLs and the gateway), `dependency-injection.md`, `errors.md`, `writing-a-provider.md`, `versioning.md` (the versioning policy). |
| `docs/migration-2027.md` | What 2027.0.0 breaks and how to move over; each change of the release adds its part. |
| `docs/releasing.md` | How to release: date the changelog, tag, approve. |
| `docs/assets/` | The logo (`logo.svg`, `logo-dark.svg`), the mark (`icon.svg`) and the social preview, with the PNGs `eng/render-assets.sh` renders from them: `icon.png` is every package's icon. `badges/` holds the README's static badges, served from the repo so they never wait on an outside service. |
| `REVIEW.md` | The questions every review answers. |
| `docs/plans/` | The plan of each non-trivial change, and the template (`docs/plans/README.md`). |
| `docs/reviews/` | Reports of whole-repo or whole-folder reviews. A change's own review goes in its PR. |
| `.claude/skills/` | The work loop: `start-work`, `review-plan`, `review-code`, `finish-work`. |
| `WriteableFileProviders.slnx` | The solution. |
| `Directory.Build.props`, `Directory.Build.targets` | Settings every project shares; `src/` and `tests/` add their own (for `src/`, packaging, package validation, the trim and AOT analyzers, and the upper bound of a packed dependency on one of our packages, `[V, NextMajor.0.0-alpha)`; for `tests/`, test project settings). The root `.targets` file holds the strictness settings, where no project can turn them off. |
| `Directory.Packages.props` | Every package version, once (central package management). A `.csproj` names packages without versions. |
| `global.json`, `nuget.config` | The SDK and the test runner; the package source (nuget.org only). |
| `eng/` | `coverage-gate.cs`, the coverage gate, and `coverage.json`, the floor of each assembly; `package-check.cs`, which checks a release's packages before they are pushed; `render-assets.sh`, which renders the PNGs in `docs/assets/` with Docker. |
| `tests/coverage.runsettings` | What the coverage run measures. |
| `.github/workflows/` | `ci.yml`, CI on every PR and on `main`; `codeql.yml`, code scanning; `live.yml`, the live suites, by hand or nightly on `main`; `release.yml`, the release of a `v*` tag. |
| `.github/dependabot.yml` | Weekly update PRs for the NuGet packages (grouped), the actions and the SDK. |
| `.github/ISSUE_TEMPLATE/`, `.github/pull_request_template.md` | The bug and feature forms; the PR template. |
| `README.md`, `src/*/README.md` | For the packages' users: the root README (what the library is, install, the interface, the packages, links to the guide, whose `getting-started.md` holds the tutorial) and one README per package (install, a short use, the provider's own options, behaviour and limits), packed into its `.nupkg`. A change to what a package does updates its README, the guide page that explains it, and the matrix in `docs/guide/providers.md` when a cell changes. |
| `CHANGELOG.md`, `LICENSE` | What each release changed: a change consumers notice adds its line under the next version, and `release.yml` publishes that version's section as the GitHub release's notes. The MIT licence. |
| `CONTRIBUTING.md`, `SECURITY.md` | A first contribution's short path through this file; how to report a vulnerability, and the supported versions. |

Every project targets `net10.0` and builds strict: nullable analysis, warnings as errors, the analyzers at
`latest-recommended` and the `.editorconfig` style enforced in the build. Every package also runs the trim and AOT
analyzers (`IsAotCompatible`); a member that needs reflection says so with `RequiresUnreferencedCode` and
`RequiresDynamicCode`. Code follows `.editorconfig`, and nothing reformats a file it does not otherwise change.

Package validation (`EnablePackageValidation`, in `src/Directory.Build.props`) runs on every pack: it compares every
package with its baseline, 2027.0.0 on nuget.org, and a breaking change to the public API fails the pack
([docs/releasing.md](docs/releasing.md), "After a major release").

## Build and test

Run everything from the repo root, with a .NET 10 SDK (`global.json`).

- **Once per clone:**
  - Only for the live suites, and only for the accounts you own: each provider's test project reads its live settings
    from `WFP_*` environment variables, or from a `testsettings.json` next to that project, which you copy from the
    `testsettings.sample.json` there and fill in (see Tests, Live suites). It is git-ignored: real credentials go there
    or in the environment, nowhere else. Nothing else needs it.
  - With Claude Code: install the C# language server its plugin runs, `dotnet tool install --global csharp-ls`. Claude
    Code asks before it loads the docs servers in `.mcp.json`; to approve them once for this clone, copy
    `.claude/settings.local.json.example` to `.claude/settings.local.json`.
- **Build:** `dotnet build WriteableFileProviders.slnx`. Providers reference Common as a project, and every package
  is versioned together from git tags (MinVer).
- **Pack:** `dotnet pack WriteableFileProviders.slnx -o <folder>`; an untagged build is `2027.0.0-alpha.0.<height>`.
- **Tests**, everything that needs no cloud account, on MSTest and Microsoft.Testing.Platform (`global.json` selects
  it for `dotnet test`). With Docker running, the emulator suites start their containers themselves:

  ```
  dotnet test --solution WriteableFileProviders.slnx --filter "TestCategory!=Live"
  ```

- **Coverage:** delete `artifacts/TestResults` (the gate keeps the best figure across every report it finds), run the
  same tests with `--coverage --coverage-output-format cobertura --coverage-settings tests/coverage.runsettings
  --results-directory artifacts/TestResults`, then `dotnet run eng/coverage-gate.cs -- artifacts/TestResults`. The gate fails when an assembly under `src/` falls below
  its floor in `eng/coverage.json`, and prints the floors that can rise. Raise them in the change that earns it; never
  lower one to pass. The goal is 90% of lines on every assembly.
- **CI** (`.github/workflows/ci.yml`) runs the build, the tests with coverage, the gate and the pack on Linux, where
  Docker is available and a missing emulator fails the run (`WFP_REQUIRE_DOCKER=1`), and the build and tests on Windows.
  Its Native AOT job publishes `tests/WriteableFileProviders.Aot` with native AOT, which fails on any trim or AOT
  warning in the packages or the SDKs they call (but one, its README says which), and runs it on the local disk,
  the gateway, Azurite, MinIO and the Graph fake.
- **Release:** a maintainer tags `v<version>` on `main`. `release.yml` builds, runs the tests with coverage and the
  coverage gate, packs, checks that every package has that version and depends on our other packages within its major
  (`eng/package-check.cs`), and requires that `ci.yml` concluded success on the tagged commit (its Windows job included);
  then, once a maintainer approves the `nuget` environment, it pushes the packages to nuget.org through trusted
  publishing and creates the GitHub release, with the version's section of `CHANGELOG.md` as its notes.
  [docs/releasing.md](docs/releasing.md) has the steps.

### Tests

`ProviderConformanceTests` (tests/WriteableFileProviders.Testing) is the contract, one test per rule, so a member means
the same thing on every provider. A provider's test project derives a `[TestClass]` from it, says how to build the
provider and what the store can do (real folders, case, signed URLs, the large-file size, the emulator bugs it must step
around), and so runs every rule. Where the fixture can reach the store past the provider, it also stores files under
names the provider refuses (`StoreRawFileAsync`), so the suite checks that listings leave them out and everything listed
reads back. Every fixture builds a provider at a root with a dot segment and expects `ArgumentException`, except where
the root is a folder of the disk (`ChecksItsRoot`, false for Physical). The cases of the error contract
([docs/guide/errors.md](docs/guide/errors.md)) run on a provider whose store throws whatever it is given
(`CreateProviderThatFails`): the in-memory fixtures and every emulator or offline provider fixture wire it, through a
seam of their SDK; a fixture without it (Physical's, the live suites) reports those cases inconclusive. Three tests race
writers: creates of one file with `IfNotExists`, small and large, where exactly one must win, and appends to a missing
file; a fixture whose emulator can't settle such a race says so (`RefusesConcurrentCreates`).
`InMemoryFileProvider`, the reference provider, passes it in `WriteableFileProviders.Common.Tests`.

Each package has a test project of its own, `tests/WriteableFileProviders.<Package>.Tests`:

- **Common:** the conformance suite on the in-memory provider (twice: as an object store, and case-insensitive with
  fixed-lifetime URLs as Box and SharePoint are), the base class, the path rules, the serializer, and the configuration
  shape: `ConfigurationShapeTests` pins that one-way door with the files in `Serialization/Golden/`. `Writeable*.json`
  were written by the 2026.10.10.x serializer and are never regenerated; `Lenient.*.json` and `Config.*.json` are
  hand-written inputs the 2026.10.10.x readers accept (JSON and configuration), which every later reader must accept
  too. A provider's own project may add checks on the same files (`AzureConfigurationShapeTests`,
  `SharePointConfigurationShapeTests`).
- **AspNetCore:** the gateway end to end on TestServer (`Microsoft.AspNetCore.TestHost`), with ephemeral Data Protection
  keys, over `InMemoryFileProvider` and a delegating provider that signs no URLs natively or keeps no version to pin; it
  needs no outside service. It covers every token refusal (tampered, expired, wrong purpose, provider or access, `..`
  and out-of-root payloads), one-time tokens under concurrent `PUT`s, ranges and headers, the size cap and content type
  of uploads, the URL service's choice between native and gateway URLs, the options, and the app pipeline (HTTP logging,
  output caching, a fallback authorization policy). `GatewayTokenGoldenTests` pins the gateway's one-way door with
  tokens issued by a test-only key, for a keyed provider and for the default one: they are never regenerated.
  `TutorialTests` runs step 2 of `docs/guide/getting-started.md` (download URLs for local files) over the Physical provider.
- **Physical:** the suite on the local disk, under a temp folder of its own that the class deletes when it ends;
  `PhysicalProviderTests` and `PhysicalOptionsTests` cover the temp files, the races, links, the rules that differ by OS
  and the stored root forms; `PhysicalLinkAndSweepTests`: `RefuseLinks` and the sweep of leftover temp files. CI runs it
  on Linux and Windows.
- **Azure Blob**, on Azurite: `AzuriteConformanceTests` (providers built from options) and
  `AzuriteStrictConformanceTests` (over a container client whose pipeline, `AzureHandler`, refuses what Azure refuses
  and Azurite allows: replacing an append blob, more blocks than the limit, here lowered to 6), and
  `AzureBlobProviderTests` (2026 append blobs, block restaging, races injected through the pipeline, missing
  containers, signed URLs used over HTTP, failures part of the way); `AzureBlobOptionsTests`, `SignedUrlTests`,
  `AzureErrorTests` and `AzureRootTests` offline.
- **Amazon S3 and MinIO:** each runs the suite and the shared S3 behaviour tests (`tests/Shared/S3`, linked into both)
  against S3Mock 5.2.3 and MinIO (`cgr.dev/chainguard/minio`, pinned by digest, run as root), and offline tests that
  fake S3 behind the AWS SDK's HTTP layer. The S3Mock fixtures declare its three bugs as capabilities: an aborted
  multipart upload deletes the object, a multipart completion with the right `If-Match` answers 404, and creates racing
  with `If-None-Match` all win, as it checks the condition apart from the write. The MinIO fixtures declare that MinIO
  lists only the file where a file and a folder share a name.
- **Google Cloud Storage**, on fake-gcs-server: `GcsConformanceTests` (through the options, with a service account key
  generated for the run and the `Endpoint` option), `GcsClientConformanceTests` (over a `StorageClient` the test built,
  which cannot sign) and `GcsProviderTests` (the missing bucket, bucket creation, signed URLs used for real, races and
  server answers injected through the client's HTTP seam); `GcsOptionsTests` and `GcsErrorTests` offline.
- **SharePoint**, offline against `GraphFake`, a stateful in-memory Microsoft Graph at the end of the SDK's own HTTP
  pipeline: the suite through both constructors (a caller's `GraphServiceClient`, and options with a work folder), and
  unit tests of the requests sent, upload sessions, races the service settles, faults and the options.
- **Box**, offline against `FakeBox`, an in-memory Box API behind the SDK's network client (`FakeNetworkClient`; the SDK
  has no HttpClient seam): `BoxConformanceTests` (the provider as it ships, signing in through the fake's token
  endpoint), `BoxAtomicConformanceTests` (a ready client, with the `If-Match` header on) and the unit tests.

**Emulators** start through Testcontainers, once per run, from each project's fixture (`Azurite`, `S3Emulators`,
`FakeGcs`), and the containers go when the run ends. Without Docker their tests are inconclusive, not failed, unless
`WFP_REQUIRE_DOCKER=1` (CI), which makes them fail.

**Live suites** are the classes marked `[TestCategory("Live")]`, which the filter above leaves out: the conformance
suite (and, where a class says so, checks only a real service can settle) against an account you own. Every one follows
the same rule:

- without `WFP_LIVE=1` its tests are inconclusive;
- with it, it reads its settings from `WFP_*` environment variables, or from a `testsettings.json` next to its test
  project (copy the `testsettings.sample.json` there, which lists the keys), and fails, naming each missing setting,
  when the account is not configured. Each class's docs name its variables.

Run one only against an account you own, and check it is clean afterwards:

```
WFP_LIVE=1 dotnet test --project tests/WriteableFileProviders.<Package>.Tests --filter "TestCategory=Live"
```

Each test works under a folder of its own and deletes it; Box keeps what it deletes in its trash, and SharePoint in the
site's recycle bin. The live classes are `AzureBlobLiveTests`, `AmazonS3LiveTests`, `MinioLiveTests`,
`GcsLiveConformanceTests` and `GcsLiveAtomicityTests` (what fake-gcs-server does not enforce), `SharePointLiveTests` and
`SharePointLiveChecks` (what the Graph fake assumes), `BoxLiveTests`, `BoxLiveAtomicTests` and `BoxLiveChecks` (what the
fake assumes), and `PhysicalLiveTests` (a folder you name, such as a network share). `live.yml` runs them, by hand or
nightly on `main`, where their secrets are set; it writes Box's app configuration from the `WFP_BOX_CONFIG_JSON` secret
to the file `WFP_BOX_CONFIG_FILE` names, and leaves Physical's out.

A change to a provider runs that provider's emulator suite, or its live suite where there is no emulator (Box,
SharePoint), and its PR says which ran, or that it did not run and why. The offline suites of Box and SharePoint run on
top, on every change: a fake is no emulator, so it does not stand in for the live suite.

## How a change lands

`main` takes pull requests only: no direct pushes, no force pushes. The maintainers, @serionist and @NemesLaszlo, merge,
their own PRs included; merging is the approval. Anyone else opens a PR from a fork, and a maintainer merges it. PRs are
squash-merged, so the PR title becomes the commit subject on `main`.

In the skills, *the developer* is the person driving the session: a maintainer, or a contributor from outside. Two
steps belong to a maintainer alone, approving a plan and merging, and the skills say so where they come up.

1. **Start** (`start-work`): an issue for anything beyond a typo, and a branch from `origin/main` named
   `<issue>-<slug>` (`42-sharepoint-paging`). A non-trivial change gets a plan in `docs/plans/`, reviewed with
   `review-plan` and approved by a maintainer before any code.
2. **Build it**, with its tests and its docs in the same change, and a line in `CHANGELOG.md` when consumers notice
   it.
3. **Finish** (`finish-work`): the build and the offline tests pass, `review-code` is clean, and the PR follows the
   template and says `Closes #<n>`.

While you build:

- Before coding against a library API, look it up: Microsoft Learn for .NET, Azure and Microsoft Graph, Context7 for
  the AWS, Google, Box and MinIO SDKs (both in `.mcp.json`). SDKs move faster than any model's memory.
- A question about a symbol (callers, implementations, unused code) goes to the C# language server first; grep is for
  text in config, docs and strings.
- Code, tests, build files and docs explain themselves: no plan or review ids (`D29`, `F5`, `P40`…) outside
  `docs/plans/` and `docs/reviews/`; a comment says the reason itself.

## Commits and PRs

- A subject in the imperative that says what changed, a body that says why.
- Author email linked to your GitHub account, so the commit links to you.
- What lands on `main` carries no tool attribution: no AI co-author trailers, no "Generated with" lines, no session
  links. The maintainer who merges trims them from the squash message.

## One-way doors

Consumers depend on these. Change one only in a PR that says so in its title and description, after the maintainers
agree in its issue. Versions follow the [versioning policy](docs/guide/versioning.md): a change to a one-way
door, a dropped target framework or a dependency change that breaks consumers' builds ships only in the next major; a
feature or a new target framework in a minor; a fix in a patch.

- **Names:** the `WriteableFileProviders.*` package ids and namespaces, published on nuget.org. The spelling
  "Writeable" stays. The DI and endpoint extensions live in `Microsoft.Extensions.DependencyInjection` and
  `Microsoft.AspNetCore.Builder`, as ASP.NET Core's own do.
- **The Common API:** `IWriteableFileProvider` and `IWriteableFileInfo`; `WriteableFileProvider<TOptions>` with its
  protected `...CoreAsync` members and helpers, which third-party providers build on; `ProviderPath`, `ResolvedPath`,
  `WriteableFileInfo`, `DirectoryContents`, `FileProviderOptions`; `UpdateFileMode`, `UpdateFileOptions`,
  `ReadOptions`, `SeekableReadOptions`; `SignedUrl`, `SignedUrlOptions`, `SignedUrlAccess`, `SignedUrlKind`;
  `LocalFileStream`; `PreconditionFailedException`, `FileAlreadyExistsException`, `FileExistsException`; the DI
  extensions; and `WriteableFileProviderCompatibilityExtensions`, the `[Obsolete]` 2026 forms that go in 2028.0.0.
  Every provider and every consumer builds on them; removing a member or changing what it means breaks them.
  docs/migration-2027.md records each break of 2027.0.0.
- **The configuration shape:** `WriteableFileProviderSerializer` builds a provider from its `Type` and the property
  names of its options class, from configuration or JSON (System.Text.Json since 2027.0.0). Consumers keep that in
  configuration files and databases, so renaming a provider class, an options property or the `Type` key breaks what
  they stored. Every later reader reads what an earlier version wrote (the goldens pin it), except a root with a dot
  segment, refused since 2027.0.0 because it reached outside the root (the maintainers agreed on
  [#3](https://github.com/serionist/WriteableFileProviders/issues/3#issuecomment-6108746458)); the reverse is not promised.
- **The gateway's URLs:** the token format `{access}.{provider key}.{payload}`, its Data Protection purpose chain (the
  root purpose, `WriteableFileProviders.Gateway.v1` unless the app sets `DataProtectionPurpose`, then the provider key,
  the access) and the URL shape `{BasePath}/{token}/{fileName?}`.
  A token for the app's default provider leaves the key out: an empty key segment, and no key in the purpose chain. A
  URL issued by one version redeems on the next; a new format gets a new purpose version and the old one keeps
  redeeming.
- **One meaning across providers:** a member does the same thing on every provider. A provider that cannot do something
  says so with an exception, the same way the others do, never by doing something else.

## Never

- Push to `main`, or merge with a failing build or failing tests.
- Commit secrets: keys, tokens, passwords, connection strings or private endpoints, in code, comments, tests or
  settings files. Credentials live in `testsettings.json`, environment variables or the emulators' public defaults.
- Change a one-way door without the maintainers' agreement.
- Check out, build, test or run someone else's code from outside the maintainers (a contributor's PR or branch) on your
  machine: it would run with every credential there. Review it by reading; its build and tests run in CI.
- Weaken a test, an analyzer or a check to make a change pass.
