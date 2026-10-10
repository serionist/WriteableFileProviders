---
name: review-plan
description: Use when a plan in docs/plans/ is drafted and needs checking before any code ("review the plan", "is this plan ready", "poke holes in this", step 5 of start-work). Reviews the plan against AGENTS.md, REVIEW.md and the code it changes through the lenses in lenses.md, tries to refute every finding, writes the result into the plan and loops with the developer until it is approved. Say "deep" for the full fan-out, or name a model instead of the default Opus.
---

# Review a plan

A plan review catches a design defect while it is still a one-line edit to the plan. It reads the plan against the
rules and the code it changes, applies every lens in `lenses.md` (this folder), tries to refute each finding, writes
the result into the plan's "Plan review" section, and loops with the developer until the plan is approved.

## Inputs

- **The plan**, by path: `docs/plans/<date>-<slug>.md`. Never review "the latest plan".
- **The tier:** standard by default; deep when asked for, or when the standard pass recommends it.
- **The model:** Opus, unless the request names another.

## Touch points, in order

1. **The ground truth** the reviewer reads: `AGENTS.md`, `REVIEW.md`, the plan in full, the issue it names
   (`gh issue view <n>`), and the code it changes: every file the plan names, and for a Common change, each provider's
   implementation of what changes.
2. **Review at the tier:**
   - **Standard:** quick by design, about ten minutes. Start one fresh agent on the chosen model with the plan's path,
     the ground-truth list above and `lenses.md`. It reads and nothing else, applies every lens, refutes each finding
     as it goes by quoting the plan and the code or rule it is judged against, and returns the confirmed and refuted
     findings, a verdict, and whether a deep pass is worth it. Do not read the ground truth into this session yourself.
     Then give each confirmed blocker, major or sensitive finding to its own fresh refuter agent, in parallel. A refuter
     that fails leaves its finding unverified, never refuted.
   - **Deep:** run `.claude/skills/review-code/scripts/review.workflow.js` with the Workflow tool and
     `args: { kind: "plan", label: "<slug>", plan: "<plan path>", model: "<model>" }`. Every lens runs as its own agent,
     a reconcile pass merges duplicates, each blocker, major and sensitive finding meets a solo refuter, and three
     voters judge the rest. Recommend it for a plan that changes Common, a one-way door, or many providers, or when the
     standard pass says so; the developer decides.
   - Without agents (another tool): apply every lens yourself, one at a time, then try to refute each finding as
     `lenses.md` says. Say in the verdict that the author reviewed their own plan.
3. **Write the findings** into the plan's "Plan review" section as `P1`, `P2`, ...: severity, plan section, what it is
   judged against, the evidence, the proposed plan edit, status (`open`, `question`, `fixed`, `verified`, `wontfix`),
   and how it was verified. Refuted findings go under a "Refuted" line with their reasons. Add the tier, the model and
   the date.
4. **Propose, apply, ask:**
   - Apply the clear edits that do not change the design yourself: a missing test, a missing docs step, the order of
     steps, a vague check, an explicit out-of-scope line. Mark them `fixed`.
   - Ask the developer about every edit that changes the design or the scope, needs their intent, or touches a
     sensitive area. Ask as a yes or no with your proposal, and record the answer.
   - Ask `[owner call]` findings one at a time, with both sides. A no closes the finding as accepted.
5. **Re-review:** review only the edited sections and what they touch, and flip `fixed` to `verified` after reading
   the edited text. Repeat 4 and 5 until a pass turns up nothing new; every round gets its agent pass.
6. **Set the verdict** in the "Plan review" section:
   - `approved` when every question has its answer, every blocker and major is `verified` or a `wontfix` the developer
     approved (never on a sensitive area), the last pass added no blocker or major, and the plan schedules its tests
     and docs and ends with `review-code`;
   - otherwise `needs-fixes` or `needs-answers`.

## Done means

- The verdict is `approved`, or the developer stopped the loop, with the open findings recorded as accepted or deferred,
  never dropped.

## Never

- Change the plan's design or scope without a yes.
- Drop a finding silently: refuted, accepted and unverified findings all stay in the plan.
- Approve a plan with an open blocker on a sensitive area.
