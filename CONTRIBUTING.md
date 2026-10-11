# Contributing

Thanks for helping. This is the short path for a first contribution. [AGENTS.md](AGENTS.md) has the full rules, for
people and agents alike.

## Before you start

- **Open an issue** for anything beyond a typo, with the bug or feature form. Say there that you want to work on it.
- **Write a plan first** for a non-trivial change: a new capability, a change to Common's API or to how every provider
  behaves, a dependency upgrade that changes behaviour. Use the template in [docs/plans/README.md](docs/plans/README.md)
  and open it as a draft PR. A maintainer approves it before any code.
- **Leave the one-way doors alone** unless the maintainers agree in the issue first: the package ids and namespaces,
  the Common API, the configuration shape, the gateway's URLs, and one meaning for each member on every provider. Such
  a change ships only in the next major.
- **Don't set versions.** Every package releases together, at the release tag's version. A break waits for the next
  major, a feature or new target framework goes in a minor, a fix in a patch
  ([versioning policy](docs/guide/versioning.md)).
- **Report security issues privately**, never in an issue: see [SECURITY.md](SECURITY.md).
- **Your contribution is MIT licensed.** Opening a pull request means you agree to license it under the repository's
  [MIT licence](LICENSE).

## Build and test

You need the .NET 10 SDK (`global.json`), and Docker for the emulator suites.

```
dotnet build WriteableFileProviders.slnx
dotnet test --solution WriteableFileProviders.slnx --filter "TestCategory!=Live"
```

- The emulator suites start their own containers. Without Docker they are inconclusive locally; CI runs them.
- Coverage and its gate: AGENTS.md, "Build and test".
- `ProviderConformanceTests` (`tests/WriteableFileProviders.Testing`) is the contract, one test per rule, and every
  provider runs it. New behaviour in Common adds its rule's test there. Every provider passes it, or throws the same
  exception where it can't.
- A bug fix comes with a test that failed before it.
- The live suites (`TestCategory=Live`) need real cloud accounts. Run one only against an account you own; a PR never
  needs one from you. If a provider change didn't run its live suite, say so and why in the PR's "Tested by". The
  maintainers run it with `live.yml`.

## Open the pull request

1. Fork, and branch from `main` as `<issue>-<slug>`, such as `42-sharepoint-paging`.
2. Make the change with its tests and docs:
   - XML docs on public members;
   - the package README, and the guide page in `docs/guide/` that describes the behaviour;
   - a line in [CHANGELOG.md](CHANGELOG.md) when consumers notice the change;
   - AGENTS.md when a path, a command or a rule changes.
3. Commit with an imperative subject that says what changed, and a body that says why. No AI co-author trailers or
   "Generated with" lines.
4. Open the PR with the template filled in, ending `Closes #<issue>`. Its title becomes the commit on `main`.

CI builds and tests every PR on Linux and Windows, with the emulator suites and the coverage gate, and CodeQL scans it.
The maintainers review a PR from outside by reading it: they never check it out, build or run it on their machines,
where it would run next to their credentials. Your PR is ready for review when CI is green. A maintainer squash-merges
it.
