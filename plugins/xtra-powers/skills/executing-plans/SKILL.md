---
name: Executing Plans
description: Use to execute an implementation plan yourself, inline in this conversation, with review checkpoints. The simpler alternative to subagent-driven-development - use it for small plans or when the agents feature is off. Works in an isolated git worktree.
---

# Executing Plans

Load the plan, review it critically, execute every task in order, and report when complete. This is the inline path — you do the work yourself rather than dispatching sub-agents. For larger plans, or to keep context lean and run cheaper, prefer the subagent-driven-development skill instead.

**Announce at start:** "I'm using the executing-plans skill to implement this plan."

## Step 1: Set up and review

1. **Isolated workspace.** Invoke the using-git-worktrees skill (`open_skill` → `using-git-worktrees`) to create and `cd` into `.worktrees/<branch>`. Never start on `main`/`master` without explicit user consent.
2. **Read the plan and review it critically.** Note the Global Constraints. If you spot conflicts, gaps, or tasks that contradict each other or the constraints, raise them with the user before starting. If it's clean, proceed.

## Step 2: Execute tasks

The plan is your checklist — its steps use `- [ ]` checkboxes, and the plan file on disk is your durable source of truth (it survives a context compaction; your memory does not).

For each task, in order:
1. Work the steps exactly as written — the plan has bite-sized steps for a reason. Write the failing test, run it, write the minimal code, run the tests, commit.
2. Run the verifications the task specifies — prefer an `msbuild__build_*` tool for a .NET solution, otherwise the project's configured command via `command__run`. Don't skip them.
3. **Mark progress on disk:** as each step completes, flip its `- [ ]` to `- [x]` in the plan file with `files__edit`. After a compaction or a fresh start, re-read the plan — checked steps are done; resume at the first unchecked one. Don't re-do completed, already-committed work.

## Step 3: Complete

After all tasks are done and verified, invoke the finishing-a-development-branch skill (`open_skill` → `finishing-a-development-branch`).

## When to Stop and Ask

Stop immediately and ask the user — don't guess — when you:
- hit a blocker (missing dependency, a test that won't pass, an unclear instruction),
- find the plan has a critical gap that prevents starting or continuing,
- don't understand a step, or a verification fails repeatedly.

If the user updates the plan in response, return to Step 1's critical review before continuing.

## Remember

- Review the plan critically first.
- Follow the steps exactly; don't skip verifications.
- Track progress in the plan's checkboxes, on disk.
- Stop when blocked — don't force through.
- Never start on `main`/`master` without explicit consent.
