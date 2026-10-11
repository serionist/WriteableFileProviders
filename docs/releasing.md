# Releasing

1. In a pull request, date the version's section in `CHANGELOG.md`: `## 2027.1.0 (2027-03-01)`. A stable tag without
   a dated section fails.
2. Tag the merged commit on `main` and push the tag:

   ```
   git fetch origin
   git tag -a v2027.1.0 -m "WriteableFileProviders 2027.1.0" origin/main
   git push origin v2027.1.0
   ```

   A `-rc.N` suffix (`v2027.1.0-rc.1`) publishes prereleases.
3. Approve the `nuget` deployment when `release.yml` asks. It publishes every package and creates the GitHub release.

If the run fails before the approval, nothing was published: fix `main` and tag again. If the push fails partway,
re-run the failed jobs; packages already pushed are skipped.

## After a major release

Set the package validation baseline in `src/Directory.Build.props` to the release, so a pack fails on a breaking
change:

```xml
<PackageValidationBaselineVersion>2027.0.0</PackageValidationBaselineVersion>
```
