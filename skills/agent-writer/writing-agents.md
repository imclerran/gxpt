# Writing a good sub-agent

Read this while shaping an agent. It covers the frontmatter contract (the part that matters most),
how to choose the tool allowlist and tier, and how to write the system prompt.

## The file

An agent is one flat `<slug>.md`: a `--- … ---` frontmatter block, then the body (its system
prompt). You never hand-format the frontmatter - the agent tools assemble it from fields. Example:

```
---
name: Explorer
description: Read-only code explorer. Dispatch it to locate and summarize how something works.
tools: [files__read, files__list, files__search, git__status, git__diff, git__log]
max_tier: readonly
max_turns: 25
---
You are a code-exploration specialist working inside the user's workspace.
...
```

## The contract (frontmatter)

- **description** (required) - the single line the main assistant reads to decide whether to
  delegate. Phrase it as *when to dispatch this agent* and what it returns: "Read-only code
  reviewer. Dispatch it to review a diff or PR; reports bugs and risks with file references." A
  vague description means the agent is never picked, or picked for the wrong job. Get this right.
- **tools** - the allowlist of tools the agent may use, as server-qualified names or glob
  patterns. Omit the key to leave it unspecified (a conservative default); pass `[]` for an agent
  that should call no tools at all. Give the agent only what its job needs.
- **max_tier** - the capability ceiling, applied on top of `tools`:
  - `readonly` - can only use read-only tools. Use for explorers, reviewers, researchers,
    planners. They cannot change anything even if a write tool slips into the allowlist.
  - `write` (the default) - can also edit files (each write still goes through the approval gate).
  - `destructive` - can also run irreversible tools (delete, git push/reset, command__run, …).
    Opt into this only when the job truly needs it.
  Pick the **lowest** tier that does the job.
- **model** - an optional model id override. Omit it to inherit the parent turn's model. Set it
  only when the user wants a specific (often cheaper/faster) model for this agent.
- **max_turns** - an optional iteration budget (tool-call rounds). Keep explorers and reviewers
  low (e.g. 20-30); allow a builder more. A tight budget is also a safety bound on an unattended
  run. Omit for the host default.

## Choosing the tool allowlist

Tools are addressed as `server__tool`; globs are allowed (`files__*`, `git__*`, `*`). The common
built-in servers and what an agent might want from them:

- **files** - `files__read`, `files__list`, `files__search` (read-only); `files__write`,
  `files__edit` (write); `files__delete` (destructive).
- **git** - `git__status`, `git__diff`, `git__log` (read-only); `git__add`, `git__commit`,
  `git__branch` (write); `git__push`, `git__reset`, `git__checkout`, … (destructive).
- **command** - `command__run` runs a shell command (destructive tier - it can do anything).
- **web** - `web__search`, `web__extract` (read-only) for research agents.
- **msbuild** - `msbuild__build_*` for agents that build .NET projects (names are discovered per
  machine; a build agent should be told to pick an available one).
- **github** - `github__*` for agents that read PRs/issues.

Guidance:
- Match the allowlist to the job, and let `max_tier` enforce the ceiling. A read-only reviewer:
  `tools: [files__read, files__list, files__search, git__status, git__diff, git__log]` +
  `max_tier: readonly`.
- A writer that fixes code: add `files__write`, `files__edit` and `max_tier: write`.
- Don't grant `command__run` or git history-rewriting tools unless the job needs them - and then
  set `max_tier: destructive` deliberately.
- The agent runs in the same workspace; it cannot dispatch other agents (one level of delegation
  only), so don't bother listing `dispatch_agent`.

## Writing the system prompt (the body)

The body is the agent's system prompt. The built-in agents (`explore`, `plan`, `verify`,
`web-research`, `code-reviewer`, `general-purpose`) are the model to follow - read one with
`read_agent` before you draft, and match its shape.

**How prescriptive to make it depends on what the user told you in the interview** (step 2 of the
skill), so settle that first. The built-in agents are deliberately lean - a handful of short
paragraphs - because they are general specialists dispatched with a fresh, specific brief each
time; for that kind of agent, do not script the method step by step ("1. fetch the README, 2. then
search code, 3. then…"), since an over-prescribed prompt fights the dispatcher's brief and makes
the agent rigid. But lean is not always right: an agent the user wants to perform the *same*
procedure identically every time, or one fired off with only a one-line ask, may genuinely need a
detailed, opinionated script. Match the altitude to the user's stated goal rather than defaulting
either way. When in doubt, lean - and confirm with the user.

Whatever the altitude, the four-part structure below still applies: state the role, the shape of
the result, and the guardrails, and always close with the final instructions.

Every built-in agent body follows the same four parts, in this order. Use them as your template:

1. **Identity line** - one sentence in the second person naming the specialty: "You are a
   code-exploration specialist working inside the user's workspace." / "You are an adversarial
   verification specialist."

2. **The job + what to return.** One short paragraph (or a lead-in sentence) on the job, then a
   bullet list of what the written result must contain. The agent returns text that the main
   assistant folds back in, so this is the whole value - make the deliverable concrete. Describe
   *what the result contains*, not the *procedure to produce it*. E.g. the explorer returns "where
   the thing lives (file path + line), how it works at a high level, anything surprising."

3. **Rules and boundaries.** A short paragraph of the guardrails and gotchas: what it must NOT do
   (state read-only limits explicitly - "Do NOT modify any files" - even though `max_tier` already
   enforces them, because the prompt is what the agent actually reads), conventions to follow, and
   what to do when it can't finish ("if you can't find it after a genuine search, say so and name
   where you looked"; "if part of the task is unclear, call it out rather than guessing").

4. **Final instructions.** Close with the standing instruction that EVERY built-in agent ends on -
   the agent's run loop depends on it, so do not omit it. It tells the agent to act by calling
   tools rather than narrating, and that a turn with no tool call is taken as its final answer (so
   it must keep going until ready to report). The canonical wording, which you should reuse and
   adapt only the last clause to the agent's deliverable:

   > Work by calling tools, not by narrating. A message with no tool call is treated as your final
   > answer, so only stop once you are ready to give your summary.

   Adapt the tail - "…to present the plan", "…to give your review" - but keep the two sentences.
   This is the single most important line to get right and the easiest to forget; an agent missing
   it tends to narrate, stop early, or never produce its report.

Keep the whole thing focused. An agent with one clear job, a prompt pitched at the right altitude
for how the user will use it, the final instructions at the end, and the lowest tier that does the
work is safer, cheaper, and gets picked for the right tasks.

### A complete body, annotated

This is the `explore` agent's body - the canonical shape. Notice how short each part is:

```
You are a code-exploration specialist working inside the user's workspace.      ← 1. identity

Given a question about the codebase, locate the relevant files, read the parts   ← 2. job + what
that matter, and return a tight written summary covering:                            to return
- where the thing lives (file path + line where useful),
- how it works at a high level,
- anything surprising or worth flagging.

Cite paths so the user can jump to them. Do NOT modify any files. If you can't    ← 3. rules &
find something after a genuine search, say so and name where you looked.              boundaries

Work by calling tools, not by narrating. A message with no tool call is treated   ← 4. final
as your final answer, so only stop once you are ready to give your summary.           instructions
```
