---
name: Finishing a Development Branch
description: Use when implementation is complete and tests pass, to integrate the work. Verifies the build/tests, then presents clear options - merge locally, open a pull request, keep as-is, or discard - and carries out the choice, including cleaning up the git worktree.
---

# Finishing a Development Branch

Verify the work, then present clear options and carry out the chosen one — including worktree cleanup.

**Announce at start:** "I'm using the finishing-a-development-branch skill to complete this work."

## Step 1: Verify the build and tests

Before offering any options, confirm the work is green — run from inside the worktree:
- Prefer an `msbuild__build_*` tool for a .NET solution; otherwise the project's configured build/test command via `command__run`.

**If it fails:** report the failures and stop. Don't present options — the branch isn't ready to merge or PR until it's green.

**If it passes:** continue.

## Step 2: Determine the base branch

Find where the branch started — `command__run` `git merge-base HEAD main` (or `master`), or just ask: "This branch started from `main` — correct?"

## Step 3: Present options

Present exactly these four, with no extra explanation:

```
Implementation complete and tests pass. What would you like to do?

1. Merge into <base> locally
2. Push and open a Pull Request
3. Keep the branch as-is (I'll handle it later)
4. Discard this work

Which option?
```

## Step 4: Carry out the choice

All git below runs via the git tools / `command__run`. Cleaning up a worktree must happen from the **workspace root**, not from inside the worktree — so `cd` to the root (bare `cd`) before any `worktree remove`.

**Option 1 — Merge locally.**
1. `cd` to the workspace root.
2. Check out `<base>`, pull, and merge the feature branch (`git__checkout`, `git__pull`, `git__merge`).
3. Re-run the build/tests on the merged result; if they fail, stop and report.
4. Only after a clean merge: remove the worktree — `git__worktree(action: "remove", path: ".worktrees/<branch>")` — then delete the branch (`git__branch` delete). Report done.

**Option 2 — Push and open a PR.**
1. Push the branch: `git__push` (set upstream).
2. Open the PR with the GitHub tools (`github__*`) against `<base>`, with a title and a body summarizing the change. (Needs the `github` server configured with a token; if it isn't, push and give the user the compare URL to open the PR manually.)
3. **Do NOT remove the worktree** — the user needs it alive to iterate on PR feedback. Report the PR link.

**Option 3 — Keep as-is.**
Report: "Keeping branch `<branch>`. Worktree preserved at `.worktrees/<branch>`." Don't remove anything.

**Option 4 — Discard.** Confirm first — this is destructive:

```
This permanently deletes:
- Branch <branch>
- Commits: <short list>
- Worktree at .worktrees/<branch>

Type 'discard' to confirm.
```

Wait for the exact word `discard`. Only then: `cd` to the workspace root, remove the worktree (`git__worktree` remove), and force-delete the branch.

## Quick Reference

| Option | Merge | Push/PR | Keep worktree | Delete branch |
|--------|-------|---------|---------------|---------------|
| 1. Merge locally | yes | — | no (remove) | yes |
| 2. Open PR | — | yes | yes | — |
| 3. Keep as-is | — | — | yes | — |
| 4. Discard | — | — | no (remove) | yes (force) |

## Red Flags

**Never:**
- Present options before the build/tests pass.
- Merge without re-verifying tests on the merged result.
- Run `git__worktree remove` from inside the worktree — `cd` to the workspace root first.
- Force-push, or discard work, without an explicit request (and a typed `discard` for Option 4).
- Remove the worktree for Option 2 — the user needs it for PR iteration.
