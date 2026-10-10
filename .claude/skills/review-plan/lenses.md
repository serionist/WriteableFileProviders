# Plan review lenses

Every tier of `review-plan` applies every lens below. A lens that does not apply (its "Applies when" names nothing the
plan does) returns no findings at once.

A finding has: a title, a severity (blocker, major, minor, nit), the plan section it anchors to, the rule or code it is
judged against (an `AGENTS.md` or `REVIEW.md` rule, a file and line, or "none: propose one"), the evidence (quote the
plan), the proposed plan edit, and `sensitive` (true when it touches a sensitive area, below). Prefer a few real
findings over many nits.

## Sensitive areas

Security (credentials, download URLs, path validation and root protection), the one-way doors in `AGENTS.md` (names,
the Common API, the configuration shape, one meaning across providers), anything that can lose or half-write a
consumer's data (delete, move, overwrite, the local stream's write-back), and the harness itself (`AGENTS.md`,
`REVIEW.md`, `.claude/`, `.github/`, the build settings). A plan edit there is never made without a yes.

## rules-conflict

Does the plan contradict a rule in `AGENTS.md` or `REVIEW.md`, or change a one-way door without saying so in
Compatibility and pointing at the maintainers' agreement? A change stated openly is not a conflict; a silent one is a
blocker.

## omissions

Read `REVIEW.md` and the code the plan changes, and list what they require that the plan does not mention: a provider
left out of a Common change, a test suite that should run, a docs update, a consumer migration, cleanup of what the
tests create.

## completeness

Is every section of the template in `docs/plans/README.md` filled, not waved at? Goal with in and out of scope;
design with the alternatives; compatibility; ordered steps, each verifiable; tests per step; docs; risks resolved or
accepted by name. Flag "etc.", TODOs dressed as decisions, and steps whose how is undefined.

## provider-parity

Applies when: the plan changes Common, shared behaviour, or more than one provider.

Does each of the seven providers get the change, or does the plan say why one cannot (and how it fails: the same
exception the others use)? Does each member keep one meaning on every provider? Does the plan account for what each
provider's SDK and service can actually do (paging limits, eventual consistency, append support, folder semantics)?

## compatibility-and-blast-radius

What existing behaviour, public API, stored configuration (`WriteableFileProviderSerializer`'s `Type` and options
property names) or package dependency does the plan change, and for whom? Each collision needs a line in the plan:
kept compatible, migrated, or broken on purpose with the agreement. A dependency bump that drags a breaking change
into consumers' builds counts.

## security-design

Bias fail-closed and name the concrete attack. Paths validated and kept inside the provider's root; credentials never
logged, never in exception messages; download URLs scoped to what was asked; input from configuration validated. Any
gap on a sensitive area is a blocker. A costly attack for little gain, whose fix would cost users something, is put to
the developer as a choice with both sides, never designed away silently.

## simplicity

Two axes. Speculation: seams with no caller, generality ahead of a second use, scope beyond the goal. Cost against
importance: for each planned mechanism, weigh what it costs (moving parts, options a consumer must understand, test
surface, failure modes) against how much it matters. These are the owner's decisions: title them `[owner call] `,
write them as a choice ("X could become Y, which drops Z: worth it?") with what is lost and who notices, severity minor
or major, never blocker. Complexity that carries security or data integrity is not a finding.

## sequencing

Can each step be built, tested and committed on its own? Do foundations (Common, shared helpers) land before the
providers that depend on them? For a long change, could it ship as several PRs?

## testability

Does each step name the test that proves it, including failure cases? Which suites must run (offline, emulator, live),
and can they run where the work happens? Are the acceptance checks objective?

## premortem-critic

Assume the plan shipped and caused a consumer's data loss or a painful rework: what was the likeliest cause, and what
plan edit prevents it? Then list the plan's load-bearing assumptions, each marked reversible or one-way; an unstated
one-way assumption is a finding.

## Refuting

Every finding gets a refutation attempt before it is reported. Rule it not real when the plan already covers it
elsewhere (read the whole plan: this is the usual false positive), it is deferred openly, it is taste, or it rests on a
misread rule or misread code. Quote the plan and the rule or code; a refutation without quoted text leaves the finding
real. A refuted finding stays in the record, with its reason. A finding whose refuter failed is unverified, never
refuted.
