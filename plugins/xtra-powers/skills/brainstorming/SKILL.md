---
name: Brainstorming
description: Use this before any creative or building work - new features, components, behavior changes, or non-trivial fixes. Explores intent, requirements, and design through dialogue, then writes an agreed spec before any implementation begins.
---

# Brainstorming Ideas Into Designs

Turn an idea into a fully-formed design and spec through natural, collaborative dialogue. Understand the project context, ask questions one at a time to refine the idea, then present the design and get approval before anything gets built.

<HARD-GATE>
Do NOT write code, scaffold a project, create a worktree, or invoke any implementation skill until you have presented a design and the user has approved it — on EVERY project, regardless of how simple it looks. The terminal step of this skill is invoking the writing-plans skill, nothing else.
</HARD-GATE>

## Anti-Pattern: "This Is Too Simple To Need A Design"

Every project goes through this. A todo list, a one-function utility, a config change — all of them. "Simple" projects are exactly where unexamined assumptions cause the most wasted work. The design can be short — a few sentences for a truly simple change — but you MUST present it and get approval.

## The Checklist

Work these in order:

1. **Explore project context** — read the relevant files, `AGENTS.md`, docs, and recent commits (`git__log`). Follow existing patterns.
2. **Assess scope first** — if the request describes several independent subsystems (e.g. "a platform with chat, file storage, billing, and analytics"), say so immediately and help decompose it into sub-projects before refining details. Each sub-project gets its own spec → plan → implementation cycle.
3. **Ask clarifying questions** — one per message, multiple-choice when you can. Focus on purpose, constraints, and success criteria.
4. **Propose 2–3 approaches** — with trade-offs; lead with your recommendation and why.
5. **Present the design in sections** — scale each section to its complexity; ask after each whether it looks right.
6. **Write the spec** — to `docs/specs/YYYY-MM-DD-<topic>-design.md`, and commit it.
7. **Self-review the spec** — fix placeholders, contradictions, ambiguity, scope inline.
8. **User reviews the spec** — ask them to read the file and approve before proceeding.
9. **Hand off** — invoke the writing-plans skill.

## The Process

**Understanding the idea.** Check the current project state first. Then ask questions one at a time — don't overwhelm with a list. A topic that needs more exploration becomes several questions across several messages, not one big one. You're capturing purpose, constraints, and what success looks like.

**Exploring approaches.** Once you understand the goal, propose 2–3 different approaches with their trade-offs, lead with your recommendation, and explain the reasoning. Let the user steer.

**Presenting the design.** Present it in sections scaled to complexity — a few sentences when straightforward, a few paragraphs when nuanced. Cover architecture, components, data flow, error handling, and testing. Ask after each section whether it's right so far, and be ready to go back and clarify.

**Design for isolation and clarity.** Break the system into small units that each have one clear purpose, communicate through well-defined interfaces, and can be understood and tested on their own. For each unit you should be able to say what it does, how it's used, and what it depends on. Smaller, focused files are also easier to implement reliably — when a file is growing to do many things, that's a signal to split it.

**Working in existing codebases.** Explore the current structure before proposing changes and follow its conventions. Where existing code genuinely gets in the way of the work (a file grown too large, tangled responsibilities), fold a targeted improvement into the design — but don't propose unrelated refactoring.

## After the Design

**Write the spec.** Save the agreed design to `docs/specs/YYYY-MM-DD-<topic>-design.md` (use today's date; honor any project preference for a different location) and commit it with the git tools.

**Self-review** the written spec with fresh eyes:
1. **Placeholders** — any "TBD", "TODO", or vague requirement? Fill it in.
2. **Consistency** — do any sections contradict each other or the architecture? Fix it.
3. **Scope** — is this focused enough for a single plan, or does it need decomposition?
4. **Ambiguity** — could any requirement be read two ways? Pick one and state it explicitly.

Fix issues inline — no need to re-review. For a larger or higher-stakes spec, you may dispatch the **doc-reviewer** agent (`dispatch_agent`) to check it independently; pass it the spec path and ask it to review for completeness, contradictions, and scope. Address what it raises.

**User review gate.** Then ask the user to review the file:

> "Spec written and committed to `<path>`. Please review it and tell me if you want any changes before I write the implementation plan."

Wait for their response. If they request changes, make them and re-run the self-review. Only once they approve do you continue.

**Hand off to planning.** Invoke the writing-plans skill — call your `open_skill` tool with `writing-plans`. Do NOT invoke any other skill; writing-plans is the only next step.

## Key Principles

- **One question at a time** — don't overwhelm.
- **Multiple choice when possible** — easier to answer than open-ended.
- **YAGNI ruthlessly** — cut features that don't serve the goal.
- **Explore alternatives** — always 2–3 approaches before settling.
- **Incremental validation** — present, get approval, then move on.
- **Stay flexible** — go back and clarify whenever something doesn't fit.
