# Xtra Powers

A GxPT plugin: a disciplined **idea → design → plan → implementation** workflow, bundled as a
set of skills and sub-agents.

The flow:

1. **brainstorming** — turn an idea into an agreed design spec (text-only, one question at a time).
2. **writing-plans** — turn the spec into a bite-sized, test-driven implementation plan.
3. **using-git-worktrees** — set up an isolated worktree to do the work in.
4. **subagent-driven-development** *(preferred)* or **executing-plans** *(inline)* — implement the
   plan task-by-task, with review gates.
5. **finishing-a-development-branch** — verify, then merge / open a PR / clean up.

It ships three sub-agents the skills dispatch:

- **doc-reviewer** — reviews a spec or plan document for gaps and contradictions.
- **plan-implementer** — implements one plan task in the worktree (TDD, build, test, commit).
- **task-reviewer** — reviews one task's diff (and the final whole branch) for spec compliance and
  quality.

## Attribution

Adapted for GxPT from the **Superpowers** skill collection by obra
(<https://github.com/obra/superpowers>), which is MIT licensed. The prose has been rewritten for
GxPT's tools, sub-agents, git-worktree/`cd` model, effort tiers, and the Windows XP target; the
visual-companion server, cross-platform tool shims, and bundled shell scripts of the original are
intentionally omitted.

Superpowers is © 2025 Jesse Vincent under the MIT License. Per MIT terms, that attribution and the
license notice are retained in `LICENSE.superpowers` in this folder, which should travel with the
published `.gxpl`.
