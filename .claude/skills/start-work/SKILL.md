---
name: start-work
description: Use when someone wants to work on something ("let's do #12", "pick up the SharePoint paging issue", "let's add X", "fix Y"), before any code or docs change. Finds or creates the issue, decides whether it needs a plan, writes the plan and reviews it with review-plan until it is approved, then starts the branch from origin/main.
---

# Start a change

Every change to `main` arrives through a PR (`AGENTS.md`, How a change lands). This skill gets a change to the point
where code can start: an issue, a plan when the change needs one, the plan approved, and a branch. `finish-work` closes
it.

## Touch points, in order

1. **Find the issue.**
   - A number (`#12`): `gh issue view 12`. Read it in full, comments included. If it is closed, ask before going on.
   - A sentence: look for an open issue first (`gh issue list --search "<words>"`); if none fits, create one with
     `gh issue create --title "<title>" --body "<what and why>"`. A typo or comment fix needs no issue.
2. **Read before planning:** the code of every folder the change touches, the tests that cover it, `REVIEW.md`, and,
   for Common, how each of the seven providers implements what changes.
3. **Decide whether it needs a plan**, by the rule in `docs/plans/README.md`. When it is unclear, ask; recommend a plan
   for anything that touches Common or a one-way door.
4. **Write the plan** at `docs/plans/<today>-<slug>.md` from the template in `docs/plans/README.md`, with `issue` set.
   Fill every section; "none" is an answer, a blank is not. A plan that changes a one-way door says so in Compatibility,
   and needs the maintainers' agreement in the issue before it is approved.
5. **Review the plan** with the `review-plan` skill until its verdict is `approved`.
6. **Get it approved.** Give the plan's path and the review's verdict, and ask the developer to approve it. Then set
   `status: approved`, `approved_by: <GitHub login>` and `approved_at: <date>` in its frontmatter. No code before this.
7. **Start the branch** `<issue>-<slug>` from `origin/main`, after `git fetch origin`. Ask where, as one question with
   a recommendation:
   - **a new branch** in this checkout (`git switch -c <branch> origin/main`): best when the checkout is clean and
     nothing else runs here. The plan file, not yet committed, comes along;
   - **a new worktree** (`git worktree add .claude/worktrees/<branch> -b <branch> origin/main`, then copy the plan
     there): best when this checkout has other work in flight or several sessions run at once.
8. **Commit the plan** as the branch's first commit: `Plan: <title>`, with `Refs #<n>` in the body.
9. **Ask how to execute**, as one question with a recommendation: directly in this session, or in forked agents (best
   for independent steps, such as the same change across several providers). This session stays the anchor either way.

## Claude Code

- Plan in plan mode if you like, but the file in `docs/plans/` is the plan, not plan mode's scratch file.
- After a new worktree, move the session there with the worktree tool, or start a new session in it.
- A forked agent's brief names the plan's path, its step, and the folders it may touch.

## Done means

- The issue exists (unless the change is a typo or comment fix).
- A change that needs a plan has one, approved, committed first on its branch, with its plan review in it.
- The branch exists from `origin/main`, and how to execute is chosen.

## Never

- Write code before the plan is approved, or on `main`.
- Plan from memory of the code instead of reading it.
- Keep a plan only in the session, an issue comment or a PR description: the plan is a file.
