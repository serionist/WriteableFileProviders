# Versioning

- **SemVer, from git tags, every package in lockstep.** Each release tags `v<version>` on `main`, and MinVer gives
  every package that version. Use the same version of every WriteableFileProviders package: a provider of one major
  does not load against the Common of another. Each provider's package depends on Common of its own major
  (`[2027.0.0, 2028.0.0-0)` for 2027.0.0, which also keeps out the next major's prereleases), so NuGet warns (NU1608)
  or fails the restore (NU1107) when packages of different majors from 2027 on meet; the 2026.10.10.x providers carry
  no upper bound, so nothing warns there: update every package together.
- **The major is a sequence number, not a date.** It continues above the 2018–2026 date-stamped versions
  (`yyyy.M.d.n`, such as `2026.10.10.4`), so NuGet orders every new release after them. `2027.0.0` is the first
  SemVer release; `2027.1.0` adds features and `2027.0.1` fixes bugs; the next breaking release is `2028.0.0`, whenever
  it ships, even years later. The number does not date the release.
- **Breaking means a new major:** any change to what consumers depend on (the package ids and namespaces, the Common
  API, the stored configuration shape, the gateway's URLs, one meaning for every member on every provider), dropping a
  target framework, or a dependency change that breaks consumers' builds (such as a cloud SDK major whose types are in
  a public signature). Adding a target framework or a feature is a minor; a fix is a patch.
- **Target frameworks:** the current .NET LTS, `net10.0` today. A new LTS is added in a minor; an out-of-support one is
  dropped only in a major.
- **Support:** the latest major gets every fix. The previous major gets security fixes for 6 months after the next
  major ships. `2026.10.10.x` (.NET 8) gets security fixes until a date the maintainers announce in
  [SECURITY.md](../../SECURITY.md).
- **Prereleases:** a build from an untagged commit is `-alpha.0.<height>` (such as `2027.0.0-alpha.0.42`); a major is
  preceded by `-rc.N` tags.
- **Obsolete members** stay for the major that marks them, with a warning that names what replaces them, and go in the
  next major: the 2026 call shapes marked `[Obsolete]` in 2027 go in `2028.0.0`.

What each release changed is in the [changelog](../../CHANGELOG.md); moving from the 2026 versions is in the
[migration guide](../migration-2027.md).
