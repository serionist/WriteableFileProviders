# Code review lenses

Every tier of `review-code` applies every lens below, to a change or to a path. `REVIEW.md` holds the questions every
review must answer; these lenses say how to look. A lens that does not apply (its "Applies when" names nothing in the
scope) returns no findings at once.

A finding has: a title, a severity (blocker, major, minor, nit), `file:line`, what it is judged against (an `AGENTS.md`
or `REVIEW.md` rule, the plan, an SDK's documented behaviour, or "none"), the evidence, the fix, and `sensitive` (true
when it touches a sensitive area, below). Prefer a few real findings over many nits.

## Sensitive areas

Security (credentials, download URLs, path validation and root protection), the one-way doors in `AGENTS.md` (names,
the Common API, the configuration shape, one meaning across providers), anything that can lose or half-write a
consumer's data (delete, move, overwrite, the local stream's write-back), and the harness itself (`AGENTS.md`,
`REVIEW.md`, `.claude/`, `.github/`, the build settings). A finding there is never fixed without a yes, and a blocker
there is never waived.

## plan-and-intent

Applies when: the change has a plan in `docs/plans/` or an issue.

Does the change do what its plan and issue say? Find behaviour nobody asked for, a planned step missing, and a
departure from the plan the plan does not explain (major).

## implementation

Real bugs in C#: wrong logic; edge cases (empty, null, root, trailing or doubled separators, duplicates, zero-length
and very large files); error handling that loses the cause or swallows it; streams, SDK clients and responses not
disposed on every path; sync over async, async void, blocking waits; missing or dropped cancellation; races between
reading and writing the same object; paths that break on Windows or Linux (separators, case, encoding); misuse of the
provider's SDK (paging not followed, eventual consistency assumed away, a call that loads a whole file into memory).
Ignore style: `.editorconfig` and `dotnet format` own it.

## provider-parity

Applies when: the scope touches Common, shared behaviour, or more than one provider; or the scope is a path covering
several providers.

Does each member mean the same on every provider: what `UpdateFileMode.Append` does to a missing file, whether a
listing is one level, what deleting a folder deletes, what an unsupported operation throws, how the root folder and
path segments are validated, what `IFileInfo` reports for a folder? A provider that differs from the others is a
finding against the one-meaning rule in `AGENTS.md`.

## security

Bias fail-closed and name the concrete attack. Secrets in code, comments, settings files, tests or logs; credentials in
exception messages or `ToString`; a path that escapes the provider's root (traversal, encoded separators, absolute
paths); a download URL that lives longer or reaches more than asked; TLS validation turned off; input from
configuration trusted without validation; a query or path built by string concatenation where the SDK has a safe way
(Graph paths, OData filters); a new package without a reason or with an advisory. Anything on a sensitive area is at
least major. Weigh the attacker's cost against the gain: a costly attack for little gain, whose fix would cost users
something, is put to the developer as a choice with both sides, never hardened silently.

## api-compatibility

Does the scope change a one-way door in `AGENTS.md`: a public type or member removed, renamed or given a new meaning;
a provider class, an options property or the `Type` key renamed, which breaks stored configuration; a package id or
namespace? Without the maintainers' agreement linked in the PR, it is a blocker. For a path scope: is anything public
that should not be, which now cannot be taken back without a breaking change?

## dependencies

Each package: needed, at a version without known advisories, not deprecated by its vendor, its licence fine to
redistribute. Duplicate versions of one package across projects; a package that drags a large or conflicting
dependency tree into consumers; an SDK used through an API its vendor marks obsolete.

## modernization

Applies when: the scope is a path, or the change adds code in an idiom this lens names.

What the current .NET and C# offer that the code does not use, where it matters: nullable annotations, `IAsyncDisposable`
and `await using`, `CancellationToken` through the public API, `ArrayPool` or `Stream.CopyToAsync` over manual buffers,
`System.Text.Json` against `Newtonsoft.Json`, obsolete APIs, and the `net8.0` target (.NET 8 support ends in November
2026). Each finding names what it gains; a change for its own sake is a nit.

## simplicity

Two axes. Structure: needless abstraction or indirection, a second implementation of one thing (each provider copying
what Common could hold), generality with one caller, dead or commented-out code. Cost against importance: for each
mechanism, weigh what it costs (moving parts, options a consumer must understand, test surface, failure modes) against
how much it matters. These are the owner's decisions: title them `[owner call] `, write them as a choice ("X could
become Y, which drops Z: worth it?") with what is lost and who notices, severity minor or major, never blocker.
Complexity that carries security or data integrity is not a finding.

## tests

Would each new test fail without the change? Is a fix pinned by a test that reproduced the bug? Are failure paths
tested, not only the happy path? Do the tests clean up what they create, on disk and in the cloud? Are they
deterministic (no sleeps, no clock or order dependence)? Does a provider that can run on an emulator have an emulator
suite, and does the shared suite in `TestBase` cover what every provider must do?

## docs-freshness

Did the docs that describe the scope move with it: XML docs on public members (and do they match what the code does),
`AGENTS.md` when a path, a command or a rule changed, the plan? Does anything cite a path, a class or a command that no
longer exists?

## harness-integrity

Applies when: the scope touches `AGENTS.md`, `REVIEW.md`, `.claude/`, `.github/`, `.editorconfig`, the build settings
or the tests' setup.

Anything that weakens a check: an analyzer or a rule turned off, a warning suppressed without its reason, a test
skipped or loosened, a review step dropped, tool attribution let through. Blocker-class.

## Refuting

Every finding gets a refutation attempt before it is reported. Rule it not real when it is a nit dressed as a bug,
already handled elsewhere (read further), contradicted by the source or the SDK's documented behaviour, or uncertain.
Settle it with evidence: in the standard tier, quote the line that proves or kills it; in the deep tier and for a
blocker's refuter, run the test or apply the change where that can run here. A refutation you could not check leaves
the finding real. A refuted finding stays in the record, with its reason. A finding whose refuter failed is unverified,
never refuted.
