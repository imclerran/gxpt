---
name: Using Git Worktrees
description: Use before implementing a plan, to set up an isolated git worktree to work in. Creates a linked worktree under .worktrees/ and changes into it, so the feature's work never touches the main checkout - and a second conversation can work a different feature in parallel.
---

# Using Git Worktrees

Do feature work in an isolated worktree, not on the main checkout. A worktree is a second working tree of the same repository on its own branch, living in a subdirectory of the workspace. GxPT confines file, git, and command operations to your **current directory** (set with `cd`), so once you `cd` into a worktree, everything you do is scoped to it — the main checkout cannot be clobbered, and another GxPT tab can work a different worktree of the same repo at the same time.

**Announce at start:** "I'm using the using-git-worktrees skill to set up an isolated workspace."

## Step 0: Are you already in a worktree?

Run `git__status`. If the current directory is already a linked worktree under `.worktrees/` on a feature branch, you're set — skip to Step 3 (baseline). Only create a worktree from the main checkout.

If you have no workspace (a folderless conversation), `cd` and worktrees aren't available — tell the user this skill needs a workspace folder and stop.

## Step 1: Prepare

- **Return to the workspace root** so the worktree is created at the anchor: call `cd` with no argument.
- **Choose a branch name** from the feature (kebab-case, e.g. `add-export-button`). State it.
- **Make sure `.worktrees/` is ignored** so worktree contents never get committed to the repo. Read `.gitignore`; if it doesn't already ignore `.worktrees/`, add a `.worktrees/` line, then `git__add` + `git__commit` that change. (git already excludes registered linked-worktree paths from `status`, but the ignore keeps it unambiguous.)

## Step 2: Create and enter the worktree

- Create it with the git worktree tool:
  `git__worktree(action: "add", path: ".worktrees/<branch>", branch: "<branch>")`
  This carves out `.worktrees/<branch>` on a new branch `<branch>`.
- Enter it: `cd .worktrees/<branch>`.
- Confirm with `git__status` that you're in the worktree on `<branch>` before doing any work. From here on, all file/git/build operations run inside the worktree automatically — you don't pass paths into the main checkout, and you don't need to scope individual calls.

## Step 3: Verify a clean baseline

Build and run the tests once, so you start from a known-good state and can tell new breakage from pre-existing:

- Prefer an `msbuild__build_*` tool for a .NET solution; otherwise run the project's configured build/test command via `command__run`.
- If the project has no build/test step, skip this.

**If the baseline fails:** report the failures and ask whether to proceed or investigate first. **If it passes (or there's nothing to run):** report ready.

## Report

```
Worktree ready at .worktrees/<branch> (branch <branch>)
Baseline: <N tests passing | build OK | nothing to run>
Ready to implement <feature>.
```

The worktree is cleaned up later by the finishing-a-development-branch skill — don't remove it here.

## Red Flags

- **Never** create a worktree without first ensuring `.worktrees/` is ignored.
- **Never** start work before `git__status` confirms you're inside the worktree on the right branch — if you skipped the `cd`, you're about to modify the main checkout.
- **Never** `worktree remove` the directory you're currently in — `cd` back to the workspace root first (the finishing skill handles removal).
