---
name: Writing Plans
description: Use when you have an agreed spec or requirements for a multi-step task, before touching code. Turns the spec into a bite-sized, test-driven implementation plan with exact files, code, and commands - then hands off to execution.
---

# Writing Plans

## Overview

Write a complete implementation plan for an engineer who has zero context for this codebase and questionable taste. Document everything: which files to touch for each task, the actual code, how to test it, what to check. Hand them the whole thing as small, ordered tasks. DRY. YAGNI. TDD. Frequent commits.

Assume they're a capable developer who knows almost nothing about this toolset or problem domain, and doesn't know good test design well. The plan is also what a **plan-implementer sub-agent** will execute one task at a time, so each task must stand on its own.

**Announce at start:** "I'm using the writing-plans skill to create the implementation plan."

**Save plans to:** `docs/plans/YYYY-MM-DD-<feature-name>.md` (use today's date; honor any project preference).

## Scope Check

If the spec covers several independent subsystems, it should have been split during brainstorming. If it wasn't, suggest splitting it into one plan per subsystem — each plan should produce working, testable software on its own.

## File Structure First

Before defining tasks, map out which files will be created or modified and what each is responsible for. This is where decomposition gets locked in.

- Give each file one clear responsibility and a well-defined interface. Files that change together live together; split by responsibility, not by technical layer.
- Prefer smaller, focused files — they're easier to reason about and edits are more reliable. In an existing codebase, follow established patterns; if a file you're modifying has grown unwieldy, a targeted split is reasonable, but don't restructure unrelated code.

## Task Right-Sizing

A task is the smallest unit that carries its own test cycle and is worth a fresh reviewer's gate. Fold setup, configuration, scaffolding, and docs into the task whose deliverable needs them; split only where a reviewer could meaningfully reject one task while approving its neighbor. Each task ends with an independently testable deliverable.

## Bite-Sized Steps

Each step is one action (2–5 minutes):
- "Write the failing test" — step
- "Run it and confirm it fails" — step
- "Write the minimal code to pass it" — step
- "Run the tests and confirm they pass" — step
- "Commit" — step

## Plan Document Header

Every plan starts with this header:

```markdown
# [Feature Name] Implementation Plan

> **For implementers:** execute this plan task-by-task with the subagent-driven-development
> skill (preferred) or executing-plans. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** [one sentence — what this builds]

**Architecture:** [2–3 sentences on the approach]

**Tech stack:** [key technologies/libraries]

## Global Constraints

[The spec's project-wide requirements — version floors, dependency limits, naming and copy
rules, platform requirements — one line each, exact values copied verbatim from the spec.
Every task implicitly includes this section.]

---
```

## Task Structure

````markdown
### Task N: [Component Name]

**Files:**
- Create: `exact/path/to/File.cs`
- Modify: `exact/path/to/Existing.cs:123-145`
- Test: `tests/exact/path/to/FileTests.cs`

**Interfaces:**
- Consumes: [what this task uses from earlier tasks — exact signatures]
- Produces: [what later tasks rely on — exact names, parameter and return types. The
  implementer sees only their own task; this block is how they learn neighboring names.]

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Does_The_Specific_Thing()
{
    var result = Subject.Method(input);
    Assert.Equal(expected, result);
}
```

- [ ] **Step 2: Run the test, confirm it fails**

Run: [the project's test command for this test]
Expected: FAIL — method not implemented

- [ ] **Step 3: Write the minimal implementation**

```csharp
public static Result Method(Input input) { ... }
```

- [ ] **Step 4: Run the test, confirm it passes**

Run: [the project's test command]
Expected: PASS

- [ ] **Step 5: Commit**

Commit with a clear message (e.g. `feat: add <thing>`).
````

> The code blocks above are illustrative (C#/xUnit). Use whatever language and test framework the
> project uses, and write each task's real build/test command — an `msbuild__build_*` tool for a
> .NET solution, otherwise the project's configured command via `command__run`. Don't hardcode a
> framework the project doesn't use.

## No Placeholders

Every step contains the actual content the engineer needs. These are plan failures — never write them:
- "TBD", "TODO", "implement later", "fill in details"
- "Add appropriate error handling" / "add validation" / "handle edge cases"
- "Write tests for the above" (without the actual test code)
- "Similar to Task N" (repeat the code — tasks may be read out of order)
- A step that says what to do without showing how (code steps need code blocks)
- References to types/functions/methods not defined in any task

## Self-Review

After writing the full plan, check it against the spec with fresh eyes:

1. **Spec coverage** — skim each spec requirement; can you point to a task that implements it? List gaps and add tasks for them.
2. **Placeholder scan** — search for the red flags above; fix them.
3. **Type consistency** — do the names, signatures, and types used in later tasks match what earlier tasks defined? (`clearLayers()` in Task 3 but `clearFullLayers()` in Task 7 is a bug.)

Fix issues inline. For a large or high-stakes plan, you may dispatch the **doc-reviewer** agent (`dispatch_agent`) with the plan path and the spec path, asking it to check completeness, spec alignment, and task decomposition; address what it raises.

## Execution Handoff

After saving the plan, offer the two execution paths:

> "Plan complete and saved to `docs/plans/<filename>.md`. Two ways to execute:
>
> **1. Sub-agent driven (recommended)** — I work in an isolated worktree and dispatch a fresh
> implementer sub-agent per task, with a review gate after each. Keeps context lean and cheap.
>
> **2. Inline** — I execute the tasks myself in this conversation, with checkpoints.
>
> Which would you like?"

- **Sub-agent driven** → invoke the subagent-driven-development skill (`open_skill` → `subagent-driven-development`).
- **Inline** → invoke the executing-plans skill (`open_skill` → `executing-plans`).

Either way, the work happens in a git worktree — both execution skills set one up via using-git-worktrees first.
