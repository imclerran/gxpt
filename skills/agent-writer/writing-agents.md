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

- Address the agent in the second person: "You are a …".
- State its ONE job and the boundaries ("Do NOT modify files").
- Describe exactly what its final answer should contain - the agent returns text that the main
  assistant folds back in, so a tight, structured result (with file paths where useful) is the
  whole value.
- Tell it to work by calling tools, and that a message with no tool call is its final answer, so
  it should only stop when ready to report.
- Keep it focused. An agent with one clear job and a low tier is safer, cheaper, and gets picked
  for the right tasks.
