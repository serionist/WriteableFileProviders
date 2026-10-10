# WriteableFileProviders

Writeable `IFileProvider` implementations for .NET: one interface, `IWriteableFileProvider`, over the local disk, Azure
Blob Storage, Amazon S3, MinIO, Box, SharePoint and Google Cloud Storage, each shipped as its own NuGet package. This
file is for anyone who changes the repo, person or agent: where things are, how to build and test, how a change lands,
and what never to do.

## Where things are

| Path | What |
|------|------|
| `WriteableFileProviders.Common/` | `IWriteableFileProvider`; `WriteableFileProvider<TOptions>`, the base class every provider derives from; `FileProviderOptions` (the root folder); `LocalFileStream`; `WriteableFileProviderSerializer`, which builds a provider from configuration or JSON. Package `WriteableFileProviders.Common`. |
| `WriteableFileProviders.<Provider>/` | One provider each: `Physical`, `AzureBlob`, `AmazonS3`, `Minio`, `Box`, `SharePoint`, `GoogleCloudStorage`. Package `WriteableFileProviders.<Provider>`. |
| `WriteableFileProviders.Tests/` | The one MSTest project, for every provider (see Tests below). |
| `REVIEW.md` | The questions every review answers. |
| `docs/plans/` | The plan of each non-trivial change, and the template (`docs/plans/README.md`). |
| `docs/reviews/` | Reports of whole-repo or whole-folder reviews. A change's own review goes in its PR. |
| `.claude/skills/` | The work loop: `start-work`, `review-plan`, `review-code`, `finish-work`. |
| `Directory.Build.props`, `global.json`, `nuget.config` | Settings every project shares, the SDK, the package source (nuget.org only). |

The code predates the style in `.editorconfig`: block-scoped namespaces, no nullable annotations, `net8.0`. The
modernization brings it over in one pass. Until then, new code follows `.editorconfig`, and nothing reformats a file it
does not otherwise change.

## Build and test

Run everything from the repo root, with a .NET 10 SDK (`global.json`) and the .NET 8 runtime for the tests.

- **Once per clone:**
  - Copy `WriteableFileProviders.Tests/testsettings.sample.json` to `testsettings.json` next to it, and set
    `PhysicalFileProvider_RootPath` to a scratch folder. The build fails without the file. It is git-ignored: real
    credentials go there and nowhere else.
  - With Claude Code: install the C# language server its plugin runs, `dotnet tool install --global csharp-ls`. Claude
    Code asks before it loads the docs servers in `.mcp.json`; to approve them once for this clone, copy
    `.claude/settings.local.json.example` to `.claude/settings.local.json`.
- **`UseLocalCommon`:** each provider builds against the *published* `WriteableFileProviders.Common` package, at the
  version its `.csproj` pins, unless `-p:UseLocalCommon=true` puts the Common project in this repo in its place. Pass it
  to every build and test while you work, or a change to Common is not what the providers and tests run against.
- **Build:** `dotnet build WriteableFileProviders.sln -p:UseLocalCommon=true`
- **Offline tests**, everything that needs no cloud account:

  ```
  dotnet test WriteableFileProviders.sln -p:UseLocalCommon=true --filter "ClassName!=WriteableFileProviders.Tests.AmazonS3FileProvider&ClassName!=WriteableFileProviders.Tests.AzureBlobFileProvider&ClassName!=WriteableFileProviders.Tests.BoxFileProvider&ClassName!=WriteableFileProviders.Tests.GoogleCloudStorageFileProvider&ClassName!=WriteableFileProviders.Tests.MinioFileProvider&ClassName!=WriteableFileProviders.Tests.SharePointFileProvider"
  ```

### Tests

`TestBase` is the shared suite: every provider runs the same create, overwrite, append, move, delete, listing, local
stream, download URL and 100 MB tests. The classes fall in four groups:

- **Unit tests**, no outside service: `SegmentValidationTests`, `PhysicalReadTests`, `BoxOptionsTests`,
  `GcsOptionsTests`, `SharePointPathTests` (with a fake Graph), and the options tests in `AzureBlobProviderTests`.
- **Physical**, `PyhsicalFileProvider`: the shared suite on the local disk, under `PhysicalFileProvider_RootPath`.
- **Emulator tests**, inconclusive, not failed, when their emulator is not running (S3: while its variable is unset):
  - Azure Blob against Azurite: `AzuriteFileProvider` and the rest of `AzureBlobProviderTests`.
    `docker run -d -p 10000:10000 mcr.microsoft.com/azure-storage/azurite azurite-blob --blobHost 0.0.0.0`
  - MinIO against s3mock: `MinioEndpointFileProvider` and `MinioProviderTests`.
    `docker run -d -p 9090:9090 -e COM_ADOBE_TESTING_S3MOCK_STORE_INITIAL_BUCKETS=wfp-test adobe/s3mock`, or another
    endpoint through the `WFP_MINIO_*` variables (`MinioTestEndpoint`).
  - S3: `AmazonS3LocalStackSuite`, `AmazonS3LocalStackRootSuite` and `AmazonS3ProviderTests`, against the S3-compatible
    endpoint in `AWS_ENDPOINT_URL_S3`, which must have the bucket `wfp-test` (or `WFP_S3_TEST_BUCKET`) and accept the
    access key and secret `test`. They check only that the variable is set, not that the endpoint answers, so with it
    set and the endpoint down they fail; and the AWS SDK sends the live `AmazonS3FileProvider` suite there too. Set it
    only for the run: `AWS_ENDPOINT_URL_S3=<url> dotnet test ...` in bash, or `$env:AWS_ENDPOINT_URL_S3 = '<url>'` in
    a PowerShell window you close afterwards. With LocalStack, which needs a free auth token from localstack.cloud:
    `docker run -d --name localstack -p 4566:4566 -e LOCALSTACK_AUTH_TOKEN=<token> localstack/localstack`, then
    `docker exec localstack awslocal s3 mb s3://wfp-test`; the endpoint is `http://s3.localhost.localstack.cloud:4566`.
- **Live suites**, the six classes the filter above leaves out: the shared suite against a real account, with the
  credentials from `testsettings.json`. They fail without them. Run one only against an account you own, and check it
  is clean afterwards.

A change to a provider runs that provider's emulator suite, or its live suite where there is no emulator (Box,
SharePoint, Google Cloud Storage), and its PR says which ran.

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
- **The Common API:** `IWriteableFileProvider`, `WriteableFileProvider<TOptions>`, `FileProviderOptions`,
  `UpdateFileMode`, `LocalFileStream`. Every provider and every consumer builds on them; removing a member or changing
  what it means breaks them.
- **The configuration shape:** `WriteableFileProviderSerializer` builds a provider from its `Type` and the property
  names of its options class. Consumers keep that in configuration files and databases, so renaming a provider class,
  an options property or the `Type` key breaks what they stored.
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
