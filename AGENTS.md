# WriteableFileProviders

Writeable `IFileProvider` implementations for .NET: one interface, `IWriteableFileProvider`, over the local disk, Azure
Blob Storage, Amazon S3, MinIO, Box, SharePoint and Google Cloud Storage, each shipped as its own NuGet package. This
file is for anyone who changes the repo, person or agent: where things are, how to build and test, how a change lands,
and what never to do.

## Where things are

| Path | What |
|------|------|
| `src/WriteableFileProviders.Common/` | `IWriteableFileProvider`; `WriteableFileProvider<TOptions>`, the base class every provider derives from; `FileProviderOptions` (the root folder); `LocalFileStream`; `WriteableFileProviderSerializer`, which builds a provider from configuration or JSON. Package `WriteableFileProviders.Common`. |
| `src/WriteableFileProviders.<Provider>/` | One provider each: `Physical`, `AzureBlob`, `AmazonS3`, `Minio`, `Box`, `SharePoint`, `GoogleCloudStorage`. Package `WriteableFileProviders.<Provider>`. |
| `src/WriteableFileProviders.AspNetCore/` | The signed-URL gateway for ASP.NET Core: `IWriteableFileUrlService` (a provider's native URL, else a gateway URL), the `GET`/`HEAD`/`PUT` endpoints on Data Protection tokens, and the one-time token store. Package `WriteableFileProviders.AspNetCore`. |
| `tests/WriteableFileProviders.Testing/` | Shared test code: `ProviderConformanceTests`, the contract every provider passes, and `InMemoryFileProvider`, the reference provider that passes it. |
| `tests/WriteableFileProviders.Common.Tests/` | Common's tests: the conformance suite on the in-memory provider, unit tests, and the configuration-shape goldens. |
| `tests/WriteableFileProviders.AspNetCore.Tests/` | The gateway and the URL service, end to end on TestServer over the in-memory provider. |
| `tests/WriteableFileProviders.Tests/` | The provider tests from before the modernization; each provider's rewrite moves its tests to a project of its own on the conformance suite. |
| `tests/WriteableFileProviders.<Provider>.Tests/` | A rewritten provider's tests: the conformance suite on its emulator or fake, its own cases, and a `Live` suite (see Tests). |
| `docs/migration-2027.md` | What 2027.0.0 breaks and how to move over; each change of the release adds its part. |
| `REVIEW.md` | The questions every review answers. |
| `docs/plans/` | The plan of each non-trivial change, and the template (`docs/plans/README.md`). |
| `docs/reviews/` | Reports of whole-repo or whole-folder reviews. A change's own review goes in its PR. |
| `.claude/skills/` | The work loop: `start-work`, `review-plan`, `review-code`, `finish-work`. |
| `WriteableFileProviders.slnx` | The solution. |
| `Directory.Build.props`, `Directory.Build.targets` | Settings every project shares; `src/` and `tests/` add their own (packaging; test project settings). The `.targets` file holds the strictness switch. |
| `Directory.Packages.props` | Every package version, once (central package management). A `.csproj` names packages without versions. |
| `global.json`, `nuget.config` | The SDK and the test runner; the package source (nuget.org only). |
| `eng/` | `coverage-gate.cs`, the coverage gate, and `coverage.json`, the floor of each assembly; `package-check.cs`, which checks a release's packages before they are pushed. |
| `tests/coverage.runsettings` | What the coverage run measures. |
| `.github/workflows/` | `ci.yml`, CI on every PR and on `main`; `codeql.yml`, code scanning; `live.yml`, the live suites, by hand or nightly on `main`; `release.yml`, the release of a `v*` tag. |
| `.github/dependabot.yml` | Weekly update PRs for the NuGet packages (grouped), the actions and the SDK. |
| `.github/ISSUE_TEMPLATE/`, `.github/pull_request_template.md` | The bug and feature forms; the PR template. |
| `CONTRIBUTING.md`, `SECURITY.md` | A first contribution's short path through this file; how to report a vulnerability, and the supported versions. |

Every project targets `net10.0`. A project still holding code from before the modernization (#3) sets
`<WfpLegacy>true</WfpLegacy>`, which turns off nullable analysis, implicit usings, warnings as errors and the style
checks in the build, lowers the analysis level to the minimum and silences missing-docs warnings; its rewrite removes the
flag. New code follows `.editorconfig` either way, and nothing reformats a file it does not
otherwise change.

## Build and test

Run everything from the repo root, with a .NET 10 SDK (`global.json`).

- **Once per clone:**
  - Only for the live suites: copy `tests/WriteableFileProviders.Tests/testsettings.sample.json` to `testsettings.json`
    next to it and fill in the accounts you own. It is git-ignored: real credentials go there and nowhere else. Nothing
    else needs it; without it the Physical suite works under a temp folder. The Azure Blob live suite reads its own,
    `tests/WriteableFileProviders.AzureBlob.Tests/testsettings.json` (sample next to it), or the `WFP_AZURE_*`
    variables.
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
- **Release:** a maintainer tags `v<version>` on `main`. `release.yml` builds, tests and packs, checks that every
  package has that version (`eng/package-check.cs`), and once a maintainer approves the `nuget` environment, pushes
  the packages to nuget.org through trusted publishing and creates the GitHub release.

### Tests

`ProviderConformanceTests` (tests/WriteableFileProviders.Testing) is the contract: one test per rule of "The behaviour
contract" in the modernization plan. A provider's test project derives a `[TestClass]` from it, says how to build the
provider and what the store can do (real folders, case, signed URLs, the large-file size), and so runs every rule. It
passes today against `InMemoryFileProvider` in `WriteableFileProviders.Common.Tests`; each provider joins it in its own
step of #3, in a test project of its own:

- **Azure Blob**, `tests/WriteableFileProviders.AzureBlob.Tests`. On Azurite, which `Azurite` starts once per run through
  Testcontainers (inconclusive without Docker, unless `WFP_REQUIRE_DOCKER=1`): `AzuriteConformanceTests`, the suite on
  providers built from options; `AzuriteStrictConformanceTests`, the suite again over a container client whose pipeline
  (`AzureHandler`) refuses what Azure refuses and Azurite allows (replacing an append blob, more blocks than the limit,
  here lowered to 6); and `AzureBlobProviderTests`: 2026 append blobs, block restaging, races injected through the
  pipeline, missing containers, signed URLs used over HTTP. Offline: `AzureBlobOptionsTests`, `SignedUrlTests`,
  `AzureConfigurationShapeTests`. Live: `AzureBlobLiveTests`, only with `WFP_LIVE=1` (inconclusive without it) and an
  account in the `WFP_AZURE_*` variables or in `testsettings.json` next to the project (see `testsettings.sample.json`
  there).
- **SharePoint**, `tests/WriteableFileProviders.SharePoint.Tests`: the suite runs offline against `GraphFake`, a stateful
  in-memory Microsoft Graph at the end of the SDK's own HTTP pipeline, through both constructors (a caller's
  `GraphServiceClient`, and options with a work folder). Unit tests cover the requests sent, upload sessions, races the
  service settles, faults and the options; `SharePointConfigurationShapeTests` adds the 2027 sign-in keys to the
  configuration-shape checks. `SharePointLiveTests` (`Live`) runs the suite against a real library: it is
  inconclusive without `WFP_LIVE=1`, and with it fails, naming what is missing, unless an account is configured in the
  `WFP_SHAREPOINT_*` variables or a `testsettings.json` next to its project (`testsettings.sample.json` lists the
  keys).

`WriteableFileProviders.AspNetCore.Tests` runs the gateway end to end on TestServer (`Microsoft.AspNetCore.TestHost`),
with ephemeral Data Protection keys, over `InMemoryFileProvider` and a delegating provider that signs no URLs natively
or keeps no version to pin; it needs no outside service. It covers every token refusal (tampered, expired, wrong
purpose, provider or access, `..` and out-of-root payloads), one-time tokens under concurrent `PUT`s, ranges and
headers, the size cap and content type of uploads, the URL service's choice between native and gateway URLs, the
options, and the app pipeline (HTTP logging, output caching, a fallback authorization policy).
`GatewayTokenGoldenTests` pins the gateway's one-way door with tokens issued by a test-only key: they are never
regenerated.

`TestBase`, in tests/WriteableFileProviders.Tests, is the older shared suite each provider still runs until then: create,
overwrite, append, move, delete, listing, local stream, download URL and 100 MB tests. Its classes fall in four groups:

- **Unit tests**, no outside service: `SegmentValidationTests`, `PhysicalReadTests`, `AmazonS3PathTests`,
  `BoxOptionsTests` and `BoxOfflineTests` (with a fake Box API).
- **Physical**, `PyhsicalFileProvider`: the shared suite on the local disk, under `PhysicalFileProvider_RootPath`, or a
  temp folder when it is not set.
- **Emulator tests**: `Emulators` starts each emulator once per run through Testcontainers, and the containers go when
  the run ends. Without Docker they are inconclusive, not failed, unless `WFP_REQUIRE_DOCKER=1` (CI).
  - MinIO against S3Mock: `MinioEndpointFileProvider` and `MinioProviderTests`, or another endpoint through the
    `WFP_MINIO_*` variables (`MinioTestEndpoint`).
  - S3: `AmazonS3LocalStackSuite`, `AmazonS3LocalStackRootSuite` and `AmazonS3ProviderTests`, against the S3-compatible
    endpoint in `AWS_ENDPOINT_URL_S3`, which must have the bucket `wfp-test` (or `WFP_S3_TEST_BUCKET`) and accept the
    access key and secret `test`. They check only that the variable is set, not that the endpoint answers, so with it
    set and the endpoint down they fail; and the AWS SDK sends the live `AmazonS3FileProvider` suite there too. Set it
    only for the run: `AWS_ENDPOINT_URL_S3=<url> dotnet test ...` in bash, or `$env:AWS_ENDPOINT_URL_S3 = '<url>'` in
    a PowerShell window you close afterwards. With LocalStack, which needs a free auth token from localstack.cloud:
    `docker run -d --name localstack -p 4566:4566 -e LOCALSTACK_AUTH_TOKEN=<token> localstack/localstack`, then
    `docker exec localstack awslocal s3 mb s3://wfp-test`; the endpoint is `http://s3.localhost.localstack.cloud:4566`.
  - Google Cloud Storage against fake-gcs-server, which `FakeGcs` starts the same way, in
    `tests/WriteableFileProviders.GoogleCloudStorage.Tests`: the conformance suite twice (`GcsConformanceTests`
    through the options, with a service account key generated for the run and the `Endpoint` option;
    `GcsClientConformanceTests` over a `StorageClient` the test built, which cannot sign), and `GcsProviderTests` (the
    missing bucket, bucket creation, signed URLs used for real, and races and server answers injected through the
    client's HTTP seam). `GcsOptionsTests` there runs offline.
- **Live suites**, `[TestCategory("Live")]`, which the filter above leaves out: the shared suite against a real
  account, with the credentials from `testsettings.json`. They fail without them. Run one only against an account you
  own (`--filter "TestCategory=Live&FullyQualifiedName~<Provider>"`, with the package's name such as `AzureBlob`,
  `GoogleCloudStorage` or `SharePoint`), and check it is clean afterwards. Google Cloud Storage's
  (`GcsLiveConformanceTests`, and `GcsLiveAtomicityTests` for what the emulator does not enforce) run only with
  `WFP_LIVE=1` and a bucket and service account key in `WFP_GCS_*` variables or a `testsettings.json` next to its test
  assembly (see `GcsLive`); otherwise they are inconclusive. Azure Blob's (`AzureBlobLiveTests`) runs only with
  `WFP_LIVE=1`, and is inconclusive without it; with it, it needs an account in the `WFP_AZURE_*` variables or a
  `testsettings.json` next to its project, and fails without one. SharePoint's, `SharePointLiveTests`, is described
  with its project above.
- **Configuration shape**: `ConfigurationShapeTests`, in WriteableFileProviders.Common.Tests, pins the one-way door with
  the files in `Serialization/Golden/` (a provider's own project may add its checks on the same files, as
  `SharePointConfigurationShapeTests` does):
  `Writeable*.json` were written by the 2026.10.10.x serializer and are never regenerated; `Lenient.*.json` and
  `Config.*.json` are hand-written inputs the 2026.10.10.x readers accept (JSON and configuration), which every later
  reader must accept too.

A change to a provider runs that provider's emulator suite, or its live suite where there is no emulator (Box,
SharePoint), and its PR says which ran, or that it did not run and why.

## How a change lands

`main` takes pull requests only: no direct pushes, no force pushes. The maintainers, @serionist and @NemesLaszlo, merge,
their own PRs included; merging is the approval. Anyone else opens a PR from a fork, and a maintainer merges it. PRs are
squash-merged, so the PR title becomes the commit subject on `main`.

In the skills, *the developer* is the person driving the session: a maintainer, or a contributor from outside. Two
steps belong to a maintainer alone, approving a plan and merging, and the skills say so where they come up.

1. **Start** (`start-work`): an issue for anything beyond a typo, and a branch from `origin/main` named
   `<issue>-<slug>` (`42-sharepoint-paging`). A non-trivial change gets a plan in `docs/plans/`, reviewed with
   `review-plan` and approved by a maintainer before any code.
2. **Build it**, with its tests and its docs in the same change.
3. **Finish** (`finish-work`): the build and the offline tests pass, `review-code` is clean, and the PR follows the
   template and says `Closes #<n>`.

While you build:

- Before coding against a library API, look it up: Microsoft Learn for .NET, Azure and Microsoft Graph, Context7 for
  the AWS, Google, Box and MinIO SDKs (both in `.mcp.json`). SDKs move faster than any model's memory.
- A question about a symbol (callers, implementations, unused code) goes to the C# language server first; grep is for
  text in config, docs and strings.

## Commits and PRs

- A subject in the imperative that says what changed, a body that says why.
- Author email linked to your GitHub account, so the commit links to you.
- What lands on `main` carries no tool attribution: no AI co-author trailers, no "Generated with" lines, no session
  links. The maintainer who merges trims them from the squash message.

## One-way doors

Consumers depend on these. Change one only in a PR that says so in its title and description, after the maintainers
agree in its issue.

- **Names:** the `WriteableFileProviders.*` package ids and namespaces, published on nuget.org. The spelling
  "Writeable" stays.
- **The Common API:** `IWriteableFileProvider` and `IWriteableFileInfo`; `WriteableFileProvider<TOptions>` with its
  protected `...CoreAsync` members and helpers, which third-party providers build on; `ProviderPath`, `ResolvedPath`,
  `WriteableFileInfo`, `DirectoryContents`, `FileProviderOptions`; `UpdateFileMode`, `UpdateFileOptions`,
  `ReadOptions`, `SeekableReadOptions`; `SignedUrl`, `SignedUrlOptions`, `SignedUrlAccess`, `SignedUrlKind`;
  `LocalFileStream`, `RangeReadStream`; `PreconditionFailedException`, `FileExistsException`; and the extension and
  DI classes. Every provider and every consumer builds on them; removing a member or changing what it means breaks
  them. docs/migration-2027.md records each break of 2027.0.0.
- **The configuration shape:** `WriteableFileProviderSerializer` builds a provider from its `Type` and the property
  names of its options class, from configuration or JSON (System.Text.Json since 2027.0.0). Consumers keep that in
  configuration files and databases, so renaming a provider class, an options property or the `Type` key breaks what
  they stored. Every later reader reads what an earlier version wrote (the goldens pin it); the reverse is not promised.
- **The gateway's URLs:** the token format and its Data Protection purpose chain (`WriteableFileProviders.Gateway.v1`,
  the provider name, the access) and the URL shape `{BasePath}/{token}/{fileName?}`. A URL issued by one version
  redeems on the next; a new format gets a new purpose version and the old one keeps redeeming.
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
