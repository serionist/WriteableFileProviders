# Releasing

A maintainer tags a commit of `main`. `release.yml` builds, tests and packs it, a maintainer approves the push, and the
packages and the GitHub release appear. There is no API key: nuget.org trusted publishing trades the job's OIDC token
for a key that lives one hour. Versions follow the [versioning policy](guide/versioning.md).

## Once: set it up

Do these in order, before the first tag. A workflow creates the environment it names, unprotected, on its first run:
protect `nuget` before it holds a secret or nuget.org trusts it.

1. Reserve the package prefix on nuget.org: email `account@nuget.org` from the account that owns the packages. Ask
   before the first tag: the new id `WriteableFileProviders.AspNetCore` is free until it is published or reserved.
   - Profile: `serionist`
   - Prefix: `WriteableFileProviders.*`
2. Protect the `nuget` environment (Settings > Environments > New environment).
   - Name: `nuget`
   - Required reviewers: the maintainers. Leave "Prevent self-review" off if one maintainer releases alone.
   - Deployment branches and tags: Selected branches and tags, with the tag rule `v*` only.
3. Add a tag ruleset (Settings > Rules > Rulesets > New tag ruleset).
   - Target: tags matching `v*`
   - Rules: restrict creations, restrict updates, restrict deletions
   - Bypass list: the Repository admin and Write roles (today the two maintainers; anyone given write access later can
     tag too)
4. Add the `NUGET_USER` secret to the `nuget` environment.
   - Value: `serionist`, the nuget.org profile name (not the email)
5. Add a trusted publishing policy on nuget.org, signed in as `serionist` (your user name > Trusted Publishing).
   - Owner: `serionist`
   - Repository Owner: `serionist`
   - Repository: `WriteableFileProviders`
   - Workflow File: `release.yml` (the file name only)
   - Environment: `nuget`
   - Scopes: new packages and new versions of existing packages, glob `WriteableFileProviders.*` (new packages matter:
     `WriteableFileProviders.AspNetCore` is new in 2027.0.0)

## Every release

1. Make `main` ready, in a pull request like any change:
   - CI is green on the commit to release.
   - The live suites (`live.yml`) have run for every provider that changed.
   - `CHANGELOG.md` has the release's section, dated: `## 2027.0.0 (2026-10-20)`, not `(unreleased)`. It becomes the
     GitHub release notes, and `release.yml` refuses a stable tag without it.
2. Optional: tag a release candidate, `v2027.0.0-rc.1`, on that commit. It runs the whole release and publishes
   `2027.0.0-rc.1` prereleases. Its notes are the `2027.0.0` section, or notes GitHub generates when there is none.
3. Tag the release on the commit of `main`, annotated:

   ```
   git fetch origin
   git tag -a v2027.0.0 -m "WriteableFileProviders 2027.0.0" origin/main
   git push origin v2027.0.0
   ```

4. Watch `release.yml` (Actions > Release). It:
   - checks the tag is on `main` and the changelog has the version's section;
   - builds, and runs the tests with coverage and the coverage gate;
   - packs every package at the tag's version and checks them (`eng/package-check.cs`: every package at that version,
     each depending on our other packages within its major);
   - waits for `ci.yml` to succeed on the tagged commit, Windows job included.
5. Approve the deployment when the `nuget` job asks (Review deployments > `nuget` > Approve). It pushes every `.nupkg`
   and `.snupkg`, then creates the GitHub release with the packages attached.

If it fails:

- Before the approval, nothing was published. Fix `main` and tag a new commit (moving a tag needs the ruleset's
  bypass), or re-run the workflow once CI is green.
- During the push, re-run the failed jobs. `--skip-duplicate` pushes only what is missing.

## Afterwards: check nuget.org

Give validation and indexing an hour or so, then check:

- All nine packages are listed at the version, each with its README, the icon, the MIT licence and the release notes
  link: `WriteableFileProviders.Common`, `.Physical`, `.AzureBlob`, `.AmazonS3`, `.Minio`, `.GoogleCloudStorage`,
  `.Box`, `.SharePoint`, `.AspNetCore`.
- Each provider depends on `WriteableFileProviders.Common (>= 2027.0.0 && < 2028.0.0-0)`.
- The symbols: open a package in NuGet Package Explorer (linked from its nuget.org page). Its Health section shows
  Source Link, deterministic build and compiler flags as valid.
- The GitHub release has the notes and the packages.
- A new project restores the packages from nuget.org and runs the getting-started guide
  (`docs/guide/getting-started.md`).

After the first 2027 release only: unlist every version below `2026.10.10` of each package (its Manage page on
nuget.org). Keep `2026.10.10.x` listed for .NET 8 apps.

## Afterwards: turn on the package validation baseline

Package validation runs on every pack. After the first release on nuget.org, add the baseline to
`src/Directory.Build.props`, next to `EnablePackageValidation`:

```xml
<PackageValidationBaselineVersion>2027.0.0</PackageValidationBaselineVersion>
```

Restore then downloads each package's 2027.0.0 from nuget.org, and the pack fails on a change that breaks it (`CP0002`
and the like). The pull request that sets it shows CI's locked restore and pack passing.

- **A package first released after 2027.0.0:** its project sets an empty `PackageValidationBaselineVersion`, which
  turns the check off, until its first version ships; then that version.
- **A minor release:** keep the baseline at the major's first release, or move it to the latest release.
- **A new major:** generate the suppressions with `dotnet pack -p:ApiCompatGenerateSuppressionFile=true`, and review
  each project's `CompatibilitySuppressions.xml` in its pull request. After the major ships, delete those files and set
  the baseline to it.
