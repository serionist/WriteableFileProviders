# Review

What every review of a change to this repo checks, whoever does it: a maintainer, Claude (`review-plan`,
`review-code`) or another tool. Each point is a yes-or-no question. A "no" blocks the change, or the PR says why it is
acceptable.

## Always

1. **Build and tests.** Does the solution build with `-p:UseLocalCommon=true`, and do the offline tests pass
   (`AGENTS.md`, Build and test)? For a change to a provider, did its emulator or live suite run, and does the PR say
   which?
2. **One-way doors.** Does the change leave the one-way doors in `AGENTS.md` alone, or does its PR name the one it
   changes and link the issue where the maintainers agreed?
3. **Every provider.** Is a change to Common's contract or to shared behaviour made in all seven providers, tested on
   each that can run, with each member still meaning the same thing on every provider?
4. **Tests.** Is new behaviour covered by a test that fails without it? Is a bug fix pinned by a test that reproduced
   the bug? Do the tests clean up what they create, on disk and in the cloud?
5. **Secrets.** No keys, tokens, passwords, connection strings or private endpoints in code, comments, settings files,
   tests or logs?
6. **Docs.** Did the docs that describe the change move with it: the XML docs of public members, `AGENTS.md` when a
   path, a command or a rule changed, the plan when the change departs from it?
7. **Dependencies.** Is each new or bumped package needed, free of known advisories (the build's NuGet audit), and
   under a licence that lets consumers redistribute it?
8. **The PR.** Does the title read as a plain imperative sentence (it becomes the commit on `main`), does the
   description follow the template, and is it free of tool attribution?

## When the change touches them

- **Streams and I/O:** is every stream and SDK client disposed on every path, is the async path async all the way
  down, is a file streamed rather than held in memory where the SDK can stream, and do large files (the 100 MB test)
  still work?
- **Paths and the root folder:** does every entry point still go through Common's path segment validation and root
  protection, so no path reaches outside the provider's root, and do separators, case and empty segments behave the
  same on Windows and Linux?
- **Deleting, moving, overwriting:** can a failure halfway leave data lost or half-written, and is a folder delete
  limited to that folder?
- **Credentials and download URLs:** are credentials never logged or put in exception messages, and does a download
  URL live exactly as long as the caller asked?
- **The harness:** a change to `AGENTS.md`, `REVIEW.md`, `.claude/`, `.github/`, the build settings or the tests'
  setup is a change to how every other change is checked. Does it keep every check at least as strict?
