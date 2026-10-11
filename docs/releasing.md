# Releasing

How a release reaches nuget.org: a maintainer tags a commit of `main`, `release.yml` builds, tests and packs it, a
maintainer approves the push, and the packages and the GitHub release appear. No API key exists anywhere: nuget.org's
trusted publishing trades the job's OIDC token for a key that lives an hour. Versions follow the
[versioning policy](guide/versioning.md).

## Once: set it up

In this order, before the first tag. A workflow that names an environment creates it, unprotected, the first time it
runs, so protect the environment before it holds a secret or nuget.org trusts it.

1. **Reserve the `WriteableFileProviders.` prefix** on nuget.org, so no one else can publish a package under it and
   ours show the verified mark: email `account@nuget.org` from the account that owns the packages, `serionist`, naming
   that profile and the prefix `WriteableFileProviders.*`. nuget.org answers with its decision; ask before the first
   tag, since the new `WriteableFileProviders.AspNetCore` id is free until it is published or reserved.
2. **Protect the `nuget` environment** (Settings, Environments, New environment, `nuget`):
   - Required reviewers: the maintainers, so a person approves every push to nuget.org after the tests pass. Leave
     "Prevent self-review" off if one maintainer releases alone.
   - Deployment branches and tags: Selected branches and tags, with the tag rule `v*` only.
3. **A tag ruleset on `v*`** (Settings, Rules, Rulesets, New tag ruleset): target tags matching `v*`, with the rules
   that restrict creations, updates and deletions. Its bypass list takes roles, not people: add the Repository admin
   and Write roles. Today that is exactly the two maintainers; anyone given write access later can tag too.
4. **The `NUGET_USER` secret** of the `nuget` environment: `serionist`, the nuget.org profile name (not the email) of
   the account that owns the `WriteableFileProviders.*` packages.
5. **Trusted publishing on nuget.org**, signed in as `serionist`: your user name, Trusted Publishing, and add a policy:
   - owner: `serionist`, the account that owns the packages;
   - Repository Owner `serionist`, Repository `WriteableFileProviders`, Workflow File `release.yml` (the file name
     only), Environment `nuget`;
   - scopes: publishing new packages and new versions of existing packages, for the glob `WriteableFileProviders.*`.
     New packages matter: `WriteableFileProviders.AspNetCore` is a new id in 2027.0.0.

## Every release

1. **Make `main` ready.** CI is green on the commit to release, the live suites of any provider that changed have run
   against the maintainers' accounts (`live.yml`), and `CHANGELOG.md` has the release's section: its heading is the
   version with the date, such as `## 2027.0.0 (2026-10-20)`, replacing `(unreleased)`. That section becomes the
   GitHub release's notes, and `release.yml` refuses a stable tag without it, or whose heading still says
   "unreleased", before anything is pushed. Merge it in a pull request like any change.
2. **Optionally, a release candidate:** tag `v2027.0.0-rc.1` on that commit and push it. It runs the whole release
   and publishes prereleases, `2027.0.0-rc.1`, to nuget.org; its notes are the `2027.0.0` section, or notes GitHub
   generates when there is none yet.
3. **Tag the release** on the commit of `main`, annotated:

   ```
   git fetch origin
   git tag -a v2027.0.0 -m "WriteableFileProviders 2027.0.0" origin/main
   git push origin v2027.0.0
   ```

4. **Watch `release.yml`** (Actions, Release). It checks the tag is on `main` and the changelog has the version's
   section, builds, runs the tests with coverage and the coverage gate, packs every package at the tag's version, and
   checks them (`eng/package-check.cs`: every package at that version, each depending on our other packages within its
   major). It also waits for `ci.yml` to conclude success on the tagged commit, Windows job included.
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
- A new project restores the packages from nuget.org and runs the getting-started guide (`docs/guide/getting-started.md`).

Then, for the first 2027 release only, unlist every version below `2026.10.10` of each package (its Manage page on
nuget.org lists them), keeping `2026.10.10.x` listed for .NET 8 consumers.

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

The SDK then adds each package's 2027.0.0 as a `PackageDownload` at restore, from nuget.org, and the pack fails on a
change that breaks it (`CP0002` and the like). The pull request that sets it shows CI's locked restore and pack passing
with it. A package first released after 2027.0.0 has no such baseline: its project sets its own
`PackageValidationBaselineVersion` once its first version ships, and an empty one until then, which turns the baseline
check off for it.

A minor release keeps the baseline at the major's first release, or moves it to the latest release. A new major breaks
on purpose: generate the suppressions once with `dotnet pack -p:ApiCompatGenerateSuppressionFile=true`, review the
`CompatibilitySuppressions.xml` each project gets in its pull request, and after the major ships, delete those files
and set the baseline to it.
