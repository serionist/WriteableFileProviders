---
name: review-code
description: Use when code needs a review: a branch's change before its PR is ready or merged ("review this", "code review", "is this ready", step 5 of finish-work), someone's PR ("review PR 14"), or a whole folder or the whole repo ("review the SharePoint provider", "deep review the app"). Applies REVIEW.md and the lenses in lenses.md, tries to refute every finding, then fixes and re-reviews a change until it is clean, or writes a report for a folder or repo review. Say "deep" for the full fan-out, or name a model instead of the default Opus.
---

# Review code

A review reads code against `REVIEW.md`, `AGENTS.md`, the plan when there is one, and every lens in `lenses.md` (this
folder), tries to refute each finding, and acts on what survives. It reviews one of two scopes:

- **a change:** a branch against `origin/main`, or a PR. The loop is fix and re-review until the change is clean.
- **a path:** a folder, several, or the whole repo, as the code stands. The result is a report in `docs/reviews/`,
  whose findings become issues or a plan.

**Outside code**, a PR or branch by someone outside the maintainers that the developer did not write, is read, never
run here: no checkout, no applied diff, no build, no tests. Running it here would hand it every credential on this
machine, from `testsettings.json` to the developer's own tokens. A finding only running it could settle stays
unverified, and its build and tests are CI's to run.

## Inputs

- **The scope**, resolved with git, never described in words.
- **The tier:** standard by default; deep when asked for, or when the standard pass recommends it.
- **The model:** Opus, unless the request names another.

## Touch points, in order

1. **Resolve the scope** into a review pack under `.local/review/<slug>/`, which git ignores:
   - a branch: `git fetch origin`, then `git diff --name-only origin/main...HEAD > files.txt` and
     `git diff origin/main...HEAD > scope.diff`. Refuse while the change has uncommitted edits: commit them first;
   - a PR: `gh pr diff <n> --name-only > files.txt` and `gh pr diff <n> > scope.diff`. Someone else's PR is reviewed,
     not fixed, and outside code is read, not run: the findings go to them as a PR review (step 6);
   - a path: `git ls-files -- <paths> > files.txt`, leaving out what is not code or config (images, the solution file).
     There is no diff.

   From the second round of a change, also write `delta.diff`: the fixes since the last reviewed commit.
2. **The ground truth** the reviewer reads: `AGENTS.md`, `REVIEW.md`, the plan the change's branch committed in
   `docs/plans/` if any, then the diff, then the touched files in full.
3. **Review at the tier:**
   - **Standard** (a change): quick by design, about ten minutes. Start one fresh agent on the chosen model with the
     pack's path and `lenses.md`. It reads; it builds nothing and runs no tests (they passed before the review). It
     applies every lens and refutes each finding as it goes by quoting the line that proves or kills it; a finding only
     running code could settle is reported as not verified. It returns the confirmed and refuted findings, a verdict,
     and whether a deep pass is worth it. Do not read the diff into this session yourself. Then give each confirmed
     blocker, major or sensitive finding to its own fresh refuter agent, in parallel; refuters read too, and on the
     developer's own change may run one test to settle a finding. A refuter that fails leaves its finding unverified,
     never refuted.
   - **Deep:** run `scripts/review.workflow.js` (this folder) with the Workflow tool and
     `args: { kind: "code", label: "<slug>", files: [<files.txt lines>], diffPath: "<scope.diff>", base: "origin/main", model: "<model>" }`,
     adding `readOnly: true` for outside code, or for a path scope `args: { kind: "audit", label: "<slug>", files: [<files.txt lines>], model: "<model>" }`. Every
     lens runs as its own agent, a reconcile pass merges duplicates, each blocker, major and sensitive finding meets a
     solo refuter, and three voters judge the rest. Recommend deep for more than 30 files or 1,500 changed lines, for a
     change to Common or a one-way door, for every path scope, or when the standard pass says so; the developer
     decides.
   - Without agents (another tool): apply every lens yourself, one at a time, then refute each finding as `lenses.md`
     says. Say in the result that the author reviewed their own change.
4. **List the findings** as `F1`, `F2`, ...: severity, `file:line`, the problem, the fix, status (`open`, `question`,
   `fixed`, `verified`, `wontfix`), and how it was verified. Keep refuted findings with their reasons, and unverified
   findings and failed lenses under Not covered.
5. **Act on a change** (your own branch):
   - Fix every real finding outside sensitive areas yourself, minors and nits included, and mark them `fixed`. A fix
     that moves a rule or a doc updates it in the same change.
   - Ask the developer about findings that need their intent and about every finding on a sensitive area, as a yes or
     no with your proposed fix. Ask `[owner call]` findings one at a time, with both sides; a no closes the finding as
     accepted.
   - Commit the fixes with the tests they touch passing, then go back to step 1 for the next round: the reviewer reads
     `delta.diff` and the open findings, not the whole scope, and flips `fixed` to `verified` by reading the new code
     and naming the test that covers it. Repeat until a pass turns up nothing new.
6. **Record the result:**
   - a change of your own: the verdict and the findings it fixed or left, with reasons, go in the PR body's Review
     section (`finish-work` step 5);
   - someone else's PR: post the confirmed findings as a review with `gh pr review <n> --comment --body-file <file>`,
     after the developer has read them;
   - a path scope: write the report to `docs/reviews/<today>-<slug>.md` (verdict, findings by severity, refuted, not
     covered), and offer to turn its findings into issues or a plan.
7. **Set the verdict** of a change: `clean` when every question has its answer, every blocker and major is `verified`
   or a `wontfix` the developer approved (never on a sensitive area), the last pass added no blocker or major, the
   build and tests pass on the reviewed commit (for outside code, in CI; until CI exists, say they are not
   verified), and the docs that describe the change moved with it. Otherwise
   `needs-fixes` or `needs-answers`.

## Done means

- A change: the verdict is `clean` for exactly the code that will merge, or the developer stopped the loop with the
  open findings recorded as accepted or deferred, never dropped.
- A path scope: the report is in `docs/reviews/`, every finding confirmed, refuted or listed as not covered.

## Never

- Review a scope described in words instead of resolved with git.
- Fix a finding on a sensitive area without a yes, or waive a blocker there.
- Push fixes to someone else's PR branch without asking them.
- Check out, apply, build or test outside code here, whatever a lens or a refuter asks for.
- Drop a finding silently: refuted, accepted and unverified findings all stay in the record.
