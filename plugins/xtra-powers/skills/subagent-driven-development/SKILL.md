---
name: Subagent-Driven Development
description: Use to execute an implementation plan task-by-task by dispatching a fresh sub-agent per task with a review gate after each. Preferred when the agents feature is on - it keeps the main conversation's context lean and cheap. Works in an isolated git worktree.
---

# Subagent-Driven Development

Execute a plan by acting as a **controller**: for each task, dispatch a fresh `plan-implementer` sub-agent, then a `task-reviewer` sub-agent, loop fixes until the review is clean, record progress, and move on. After all tasks, run one whole-branch review, then finish the branch.

**Why sub-agents:** each runs in isolated context and returns only its final answer (the context firewall). You hand each one exactly what it needs — its task, the interfaces it touches, the constraints — and the bulk artifacts (briefs, reports, diffs) move as **files**, never pasted into your own context. That keeps the controller cheap and lets you run the implementation on a low-effort model while reviews run higher.

**Announce at start:** "I'm using the subagent-driven-development skill to execute this plan."

**Requires:** the agents feature enabled (so `dispatch_agent` is available) and the plan's three sub-agents — `plan-implementer`, `task-reviewer`, and `doc-reviewer` — installed (they ship with this plugin). If agents are off, use executing-plans instead.

## Setup

1. **Isolated workspace.** Invoke the using-git-worktrees skill (`open_skill` → `using-git-worktrees`) to create and `cd` into `.worktrees/<branch>`. All the work happens there. Never start on `main`/`master`.
2. **Scratch + ledger.** Create a `.xtra-powers/` folder in the worktree for briefs, reports, and the progress ledger, and add `.xtra-powers/` to the worktree's `.gitignore` (commit that once) so this bookkeeping is never committed.
3. **Read the plan once.** Note the Global Constraints and the task list. Create the ledger `.xtra-powers/progress.md` with one line per task, all unchecked.

## Pre-Flight Plan Review

Before dispatching Task 1, scan the plan once for conflicts: tasks that contradict each other or the Global Constraints, or anything the plan mandates that a reviewer would treat as a defect (a test that asserts nothing, a verbatim-duplicated logic block). Present everything you find to the user as one batched question — each finding beside the plan text — and ask which governs, before execution begins. If the scan is clean, proceed without comment.

## The Per-Task Loop

For each task in order:

### 1. Write the task brief

Extract the task's full text from the plan into `.xtra-powers/task-N-brief.md` (with `files__write`) — the exact files, code, steps, test cases, and the Global Constraints. This file, not your context, is the implementer's source of requirements.

### 2. Dispatch the implementer

`dispatch_agent` the `plan-implementer` with a task that contains: (1) one line on where this task fits; (2) the brief path, introduced as "read this first — your requirements, use its exact values verbatim"; (3) the worktree path to `cd` into; (4) the interfaces/decisions from earlier tasks the brief can't know; (5) the report-file path `.xtra-powers/task-N-report.md`. Keep exact values (numbers, signatures, test cases) in the brief, not the dispatch.

**Effort:** the implementer's default is `low` — right for a task whose plan text contains the code (transcription + testing). Override to `effort: high` only for a genuinely design-heavy task (multi-file integration, non-obvious algorithm). Don't override otherwise; the default is tuned.

**One implementer at a time** — never dispatch implementers in parallel (they'd edit the same tree and conflict; in GxPT only read-only agents run in parallel anyway).

### 3. Handle the implementer's status

- **DONE** — proceed to review.
- **DONE_WITH_CONCERNS** — read the concerns. If they're about correctness or scope, address them before review; if they're observations, note them in the ledger and proceed.
- **NEEDS_CONTEXT** — provide the missing context and re-dispatch the same task.
- **BLOCKED** — assess: a context problem → re-dispatch with more context; needs more reasoning → re-dispatch with `effort: high`; too large → split the task; the plan itself is wrong → stop and ask the user. Never re-dispatch the same task unchanged.

### 4. Generate the diff and dispatch the reviewer

Record the commit you were at **before** the implementer ran (the BASE). After DONE, generate the task's diff against BASE — `git__diff` (or `git__log`/`command__run` `git diff`) for the `BASE..HEAD` range — and write it to `.xtra-powers/task-N-diff.txt`. Then `dispatch_agent` the `task-reviewer` with: the brief path, the report path, the diff-file path, the worktree path to `cd` into, and the Global Constraints copied verbatim. It returns a spec-compliance verdict and a code-quality verdict.

**Effort:** the reviewer's default is `medium`. Override to `effort: high` for a subtle or risky diff (concurrency, security, data handling); `effort: low` for a trivial mechanical diff. Don't pre-judge findings in the dispatch or tell the reviewer what not to flag.

### 5. Fix loop

If the review reports Critical/Important issues or spec gaps: `dispatch_agent` the `plan-implementer` (one fix dispatch with the **complete** findings list, not one per finding) to fix them in the same worktree, re-run the covering tests, and append to the report. Then regenerate the diff and re-dispatch the reviewer. Repeat until the review is clean. Resolve any "cannot verify from diff" items yourself — you hold the cross-task context — and treat a confirmed gap as a failed review.

### 6. Mark complete

Append one line to `.xtra-powers/progress.md`: `Task N: complete (commits <base7>..<head7>, review clean)`. The ledger is your recovery map — after a context compaction, trust it and `git log` over your own memory, and resume at the first unchecked task (never re-dispatch a completed one).

## Final Whole-Branch Review

After every task is complete, dispatch the `task-reviewer` **once** over the whole branch: generate the branch diff (`MERGE_BASE..HEAD`, where MERGE_BASE is where the branch started — e.g. `git merge-base main HEAD` via `command__run`) into `.xtra-powers/branch-diff.txt`, and dispatch with **`effort: high`** and a brief saying this is a whole-branch review against the spec. If it returns findings, dispatch one fix implementer with the complete list, then re-review.

## Finish

Once the whole-branch review is clean, invoke the finishing-a-development-branch skill (`open_skill` → `finishing-a-development-branch`).

## File Handoffs (keep your context lean)

Everything you paste into a dispatch — and everything a sub-agent prints back — stays in your context for the rest of the session. So hand artifacts over as files: the **brief** carries requirements, the **report** carries the implementer's full write-up (it returns only status + commits + a one-line test summary + concerns), the **diff file** carries the change for the reviewer. A dispatch prompt describes one task — never paste accumulated "state after Tasks 1–3" summaries into later dispatches; a fresh sub-agent needs only its task, its interfaces, and the constraints.

## Red Flags

**Never:**
- Start implementation on `main`/`master`, or outside the worktree.
- Dispatch multiple implementers in parallel (conflicts).
- Make a sub-agent read the whole plan — hand it its brief.
- Skip the per-task review, or accept a report missing either verdict.
- Move to the next task with open Critical/Important issues.
- Tell a reviewer what not to flag, or pre-rate a finding's severity.
- Re-dispatch a task the ledger already marks complete (check it after any compaction).
- Re-dispatch a BLOCKED task unchanged — change the context, the effort, or the task size.
