---
name: finish-work
description: Use when a change is ready ("finish", "wrap up", "we're done", "open the PR", "merge it"). Checks the docs moved with the change, runs the build and tests, commits, opens a draft PR for the developer's look, reviews it with review-code until it is clean, then marks the PR ready or squash-merges it, and cleans up.
---

# Finish a change

Done is defined by a passing build and tests, `REVIEW.md`, and a clean `review-code` pass, not by the sense that the
work is complete. `main` takes PRs only; either maintainer merges (`AGENTS.md`, How a change lands).

## Touch points, in order

1. **Docs in the same change.** List what describes the change and check each moved with it: XML docs on changed
   public members, `AGENTS.md` when a path, a command or a rule changed, the plan when the work departs from it (say
   where and why in the plan).
2. **Build and test**, from the repo root (`AGENTS.md`, Build and test):
   - `dotnet build WriteableFileProviders.sln -p:UseLocalCommon=true`, with no new warnings in the files the change
     touched;
   - the offline tests;
   - for a changed provider, its emulator suite when the emulator can run here, and its live suite only when the
     developer says they have the account. Note which suites ran, and which could not and why.

   Fix every failure; never weaken a test or a check to pass.
3. **Commit:** a subject in the imperative that says what changed, a body that says why, `Refs #<n>`. No tool
   attribution of any kind.
4. **The developer's look**, before the review, in a draft PR. Push the branch (`git push -u origin <branch>`) and open
   it with `gh pr create --draft --title "<plain imperative sentence>" --body-file .local/pr-body.md`. The body follows
   `.github/pull_request_template.md`; keep the body file in `.local/`, which git ignores. Say the draft is open and
   how to see all of it: the PR's Files changed tab, or `git diff origin/main...HEAD`. Wait for their go; their edits
   land as commits before the review starts.
5. **Review** with the `review-code` skill until its verdict is `clean`. Commit each round of fixes with the tests they
   touch passing. Write the verdict and what it fixed into the PR body's Review section
   (`gh pr edit <n> --body-file .local/pr-body.md`).
6. **Ask how to close**, as one question with a recommendation, after offering one more look at the whole change,
   since the review's fixes changed it:
   - **mark it ready** for the other maintainer to look at: best when it touches Common, a one-way door or the
     harness, or they should see it;
   - **merge it** now;
   - **leave it as a draft**.
7. **Merge**, when chosen:
   - bring the branch up to date if `main` moved: `git fetch origin` and `git rebase origin/main`, then
     `git push --force-with-lease`. A rebase that changes code needs the tests again, and another `review-code` pass
     over what changed;
   - `gh pr ready <n>` if it is a draft, then
     `gh pr merge <n> --squash --delete-branch --subject "<PR title>" --body "<what and why, Closes #<n>>"`. The squash
     message is the PR's what and why, without HTML comments or tool attribution.
8. **Clean up:** `git switch main` and `git pull`. A worktree is removed from the main checkout
   (`git worktree remove <path>`); in Claude Code, leave it with the worktree tool. Work that turned up along the way
   becomes an issue now (`gh issue create`).

## Done means

- The build and the tests passed, the developer had their look, and `review-code` is clean for this change.
- One of: the PR is ready for review; it is merged and its issue closed; or the developer chose to leave it as a draft.

## Never

- Open, mark ready or merge a PR with a failing build or failing tests, or with a review of different code.
- Mark a `REVIEW.md` question yes without its evidence.
- Add AI co-author trailers, "Generated with" lines or session links anywhere: commits, PR titles, PR bodies.
- Push to `main`, or merge someone else's PR without asking them.
