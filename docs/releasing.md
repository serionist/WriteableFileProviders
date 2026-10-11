# Releasing

How a release reaches nuget.org: a maintainer tags a commit of `main`, `release.yml` builds, tests and packs it, a
maintainer approves the push, and the packages and the GitHub release appear. No API key exists anywhere: nuget.org's
trusted publishing trades the job's GitHub token for a key that lives an hour. Versions follow the
[versioning policy](guide/versioning.md).

## Once: set it up

In this order. A workflow that names an environment creates it, unprotected, the first time it runs, so protect the
environment before it holds a secret or nuget.org trusts it.

1. **Protect the `nuget` environment** (Settings, Environments, New environment, `nuget`):
   - Required reviewers: the maintainers, so a person approves every push to nuget.org after the tests pass. Leave
     "Prevent self-review" off if one maintainer releases alone.
   - Deployment branches and tags: Selected branches and tags, with the tag rule `v*` only.
2. **A tag ruleset on `v*`** (Settings, Rules, Rulesets, New tag ruleset): target tags matching `v*`, with the rules
   that restrict creations, updates and deletions, and only the maintainers in its bypass list, so no one else can
   create, move or delete a release tag.
3. **The `NUGET_USER` secret** of the `nuget` environment: the nuget.org profile name (not the email) of the account
   that owns the `WriteableFileProviders.*` packages.
4. **Trusted publishing on nuget.org**, signed in as that account: the account menu, Trusted Publishing, and a new
   policy with repository owner `serionist`, repository `WriteableFileProviders`, workflow file `release.yml` (the file
   name only) and environment `nuget`. The policy covers every package the account owns.
5. **Reserve the `WriteableFileProviders.` prefix** on nuget.org, so no one else can publish a package under it and
   ours show the verified mark: email `account@nuget.org` from the owning account, naming its profile and the prefix
   `WriteableFileProviders.*`. nuget.org answers with its decision.

## Every release

1. **Make `main` ready.** CI is green on the commit to release, the live suites of any provider that changed have run
   against the maintainers' accounts (`live.yml`), and `CHANGELOG.md` has the release's section: its heading is the
   version with the date, such as `## 2027.0.0 (2026-10-20)`, replacing `(unreleased)`. That section becomes the
   GitHub release's notes. Merge it in a pull request like any change.
2. **Optionally, a release candidate:** tag `v2027.0.0-rc.1` on that commit and push it. It runs the whole release
   and publishes prereleases, `2027.0.0-rc.1`, to nuget.org; its notes are the `2027.0.0` section.
3. **Tag the release** on the commit of `main`, annotated:

   ```
   git fetch origin
   git tag -a v2027.0.0 -m "WriteableFileProviders 2027.0.0" origin/main
   git push origin v2027.0.0
   ```

4. **Watch `release.yml`** (Actions, Release). It checks the tag is on `main`, builds, runs the tests with coverage and
   the coverage gate, packs every package at the tag's version, and checks them (`eng/package-check.cs`: every
   package at that version, each depending on our other packages within its major). It also waits for `ci.yml` to
   conclude success on the tagged commit, Windows job included.
5. **Approve the deployment** when the `nuget` job asks (Review deployments, `nuget`, Approve). It pushes every
   `.nupkg` and `.snupkg`; then the GitHub release is created with the packages attached.

If a step fails before the approval, nothing was published: fix `main`, then tag a new commit (a moved tag needs the
ruleset's bypass), or re-run the workflow once CI is green. If the push fails halfway, re-run the failed jobs:
`--skip-duplicate` pushes only what is missing.

## Afterwards: check nuget.org

Within an hour or so of the push (validation and indexing take a while):

- Each of the nine packages, `WriteableFileProviders.Common`, `.Physical`, `.AzureBlob`, `.AmazonS3`, `.Minio`,
  `.GoogleCloudStorage`, `.Box`, `.SharePoint` and `.AspNetCore`, is listed at the version, with its README, the icon,
  the MIT licence and the release notes link.
- Each provider's dependencies show `WriteableFileProviders.Common (>= 2027.0.0 && < 2028.0.0-0)`.
- The symbols: open a package in NuGet Package Explorer (its nuget.org page links it), whose Health section shows
  Source Link, a deterministic build and the compiler flags as valid.
- The GitHub release has the notes and the packages.
- A new project restores the packages from nuget.org and runs the root README's tutorial.

Then, for the first 2027 release only, unlist the versions before the 2026.10.10 line (each package's Manage page on
nuget.org; the modernization plan's list for the maintainers names them), keeping `2026.10.10.x` listed for .NET 8
consumers.

## Afterwards: move the public API to shipped

Each package's `PublicAPI.Unshipped.txt` lists what is new since the last release; once a release ships, its lines move
to `PublicAPI.Shipped.txt`, so the next change shows only what it adds or removes. In a pull request after the release:

```
for dir in src/WriteableFileProviders.*/; do
  shipped="$dir/PublicAPI.Shipped.txt"; unshipped="$dir/PublicAPI.Unshipped.txt"
  removed=$(grep '^\*REMOVED\*' "$unshipped" | sed 's/^\*REMOVED\*//' || true)
  { grep -v '^#nullable enable$' "$shipped"; grep -v -e '^#nullable enable$' -e '^\*REMOVED\*' "$unshipped"; } \
    | grep -v -x -F -e "$removed" | grep -v '^$' | LC_ALL=C sort -u > "$shipped.new"
  { echo '#nullable enable'; cat "$shipped.new"; } > "$shipped"
  echo '#nullable enable' > "$unshipped"
  rm "$shipped.new"
done
dotnet build WriteableFileProviders.slnx
```

A `*REMOVED*` line in `PublicAPI.Unshipped.txt` drops that member's line from `PublicAPI.Shipped.txt`. The build checks
the result: a member missing from both files fails it (RS0016), and so does a line with no member (RS0017).

## Afterwards: turn on the package validation baseline

Package validation already runs on every pack. After the first release on nuget.org, it can also compare each new
package with that release, to catch a breaking change before it ships. In `src/Directory.Build.props`, beside
`EnablePackageValidation`:

```xml
<PackageValidationBaselineVersion>2027.0.0</PackageValidationBaselineVersion>
```

Every pack then downloads each package's 2027.0.0 from nuget.org and fails on a change that breaks it (`CP0002` and
the like). The pull request that sets it shows CI's locked restore and pack passing with it. A minor release keeps the baseline at the major's first release, or moves it to the latest release. A new
major breaks on purpose: generate the suppressions once with `dotnet pack -p:ApiCompatGenerateSuppressionFile=true`,
review the `CompatibilitySuppressions.xml` each project gets in its pull request, and after the major ships, delete
those files and set the baseline to it.
