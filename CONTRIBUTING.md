# Contributing

Thank you for helping. [AGENTS.md](AGENTS.md) holds the rules every change follows, for people and agents alike:
where things are, how to build and test, how a change lands, the one-way doors and what never to do. This page is the
short path through it for a first contribution.

## Before you start

- **Open an issue** for anything beyond a typo, with the bug or feature form, and say there that you want to work on
  it.
- **A non-trivial change gets a plan first:** a new capability, a change to Common's API or to how every provider
  behaves, a dependency upgrade that changes behaviour. Write it in `docs/plans/` from the template in
  [docs/plans/README.md](docs/plans/README.md), and put it up as a draft PR. A maintainer approves it before any code.
- **The one-way doors** (AGENTS.md) are what consumers depend on: the package ids and namespaces, the Common API, the
  configuration shape, the gateway's URLs, and one meaning for every member on every provider. A change to one needs
  the maintainers' agreement in its issue first, and ships only in the next major version.
- **Versions** are not yours to set: every package is released together, at the version of the release tag. A change
  that breaks consumers (a one-way door, a dropped target framework, a dependency change that breaks their builds)
  waits for the next major; a new feature or target framework goes into a minor, a fix into a patch. See the README's
  [Versioning](README.md#versioning) section.
- **Security issues** never go in an issue: see [SECURITY.md](SECURITY.md).

## Build and test

You need the .NET 10 SDK (`global.json`), and Docker for the emulator suites.

```
dotnet build WriteableFileProviders.slnx
dotnet test --solution WriteableFileProviders.slnx --filter "TestCategory!=Live"
```

The emulator suites start their own containers through Testcontainers; without Docker they are inconclusive locally,
and CI runs them. Coverage and its gate are in AGENTS.md, Build and test.

**The conformance suite** is the contract. `ProviderConformanceTests`, in `tests/WriteableFileProviders.Testing`, has
one test per rule of the behaviour contract, and every provider's test project runs it. New behaviour in Common comes
with the rule's test there, and every provider passes it, or throws the same exception where it cannot. A bug fix comes
with a test that failed before it.

**Live suites** (`TestCategory=Live`) run against real cloud accounts. Run one only against an account you own; a PR
never needs one from you. When a change to a provider has not run its live suite, say so in the PR's "Tested by", and
why (no account, for example); the maintainers run it with the `live.yml` workflow.

## Opening the pull request

1. Fork, and branch from `main` as `<issue>-<slug>`, for example `42-sharepoint-paging`.
2. Make the change with its tests and its docs: XML docs on public members, the package README, AGENTS.md when a path,
   a command or a rule changes.
3. Commit with a subject in the imperative that says what changed, and a body that says why. No AI co-author trailers
   or "Generated with" lines.
4. Open the PR with the template filled in, ending `Closes #<issue>`. Its title becomes the commit on `main`.

CI builds and tests every PR on Linux and Windows, with the emulator suites and the coverage gate, and CodeQL scans it.
That is the only place your code runs on our side: the maintainers review a PR from outside by reading it, and never
check it out, build or run it on their own machines, where it would run next to their credentials. So a PR is ready for
review when CI is green. A maintainer squash-merges it.
