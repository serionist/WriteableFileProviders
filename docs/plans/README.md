# Plans

A non-trivial change gets a plan here before any code: a new capability, a change to Common's contract or to a one-way
door, work across several providers, a dependency upgrade that changes behaviour, the modernization. A fix or a change
inside one provider that a reviewer can follow from the PR alone needs none; its PR says `Trivial: <why>` instead.

- One file per plan: `YYYY-MM-DD-<slug>.md`, the date it was started.
- Written with `start-work`, reviewed with `review-plan` until approved, approved by a maintainer, and committed as the
  first commit of its branch.
- A plan is the record of what was decided and why at the time. It changes while its branch is open, when the work
  departs from it, and not after the merge.
- Fill every section. "None" is an answer; a blank is not.

## Template

```markdown
---
title: <a plain imperative sentence, which can become the PR title>
issue: <#n>
status: draft
approved_by:
approved_at:
---

# <title>

## Goal

What is true when this is done, and why it matters. In scope; out of scope.

## Design

How it works: the types and members it adds or changes, and how each provider does it. The alternatives considered and
why this one.

## Compatibility

The one-way doors it touches (AGENTS.md), what breaks for consumers (code, stored configuration, behaviour), and how
they move over. Or "none".

## Steps

Ordered, each one buildable and testable on its own, each naming the test that proves it.

## Tests

The new and changed tests, and which suites run: offline, emulator (which), live (which provider).

## Docs

The docs that change with it: XML docs, AGENTS.md, READMEs.

## Risks

What could go wrong, and whether each risk is resolved or accepted.

## Plan review

Written by `review-plan`: tier, model, verdict, and the findings with their status.
```
