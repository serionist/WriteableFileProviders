# Versioning

## One version for every package

Every release tags `v<version>` on `main`, and MinVer gives every package that version. Use the same version of all of
them: a provider of one major doesn't load against the Common of another.

- From 2027 on, each provider depends on Common of its own major only: `[2027.0.0, 2028.0.0-0)` for 2027.0.0, which also
  keeps out the next major's prereleases. Mixing majors makes NuGet warn (NU1608) or fail the restore (NU1107).
- The 2026.10.10.x providers carry no upper bound, so nothing warns when they meet Common 2027. Update every package
  together.

## What the numbers mean

The versions are SemVer. The major is a sequence number, not a date. It continues above the 2018–2026 date versions
(`yyyy.M.d.n`, such as `2026.10.10.4`), so NuGet orders every new release after them.

| Release | Example | Holds |
|---|---|---|
| Major | `2028.0.0` | Breaking changes. Ships whenever it is needed, even years later. |
| Minor | `2027.1.0` | Features, and a new target framework. |
| Patch | `2027.0.1` | Fixes. |
| Release candidate | `2028.0.0-rc.1` | Precedes a major. |
| Untagged build | `2027.0.0-alpha.0.42` | `-alpha.0.<height>`, from a commit with no tag. |

## What counts as breaking

A change to anything consumers depend on: the package ids and namespaces, the Common API, the stored configuration
shape, the gateway's URLs, or one member meaning the same on every provider. Also dropping a target framework, or a
dependency change that breaks consumers' builds (such as a cloud SDK major whose types appear in a public signature).

- **Target frameworks:** the current .NET LTS, `net10.0` today. A new LTS comes in a minor; an out-of-support one goes
  only in a major.
- **Obsolete members** stay for the major that marks them, with a warning that names the replacement, and go in the
  next. The 2026 call shapes marked `[Obsolete]` in 2027 go in `2028.0.0`.

## Support

- The latest major gets every fix.
- The previous major gets security fixes for 6 months after the next major ships.
- `2026.10.10.x` (.NET 8) gets security fixes until a date the maintainers announce in [SECURITY.md](../../SECURITY.md).

What each release changed is in the [changelog](../../CHANGELOG.md). Moving from 2026 is in the
[migration guide](../migration-2027.md).
