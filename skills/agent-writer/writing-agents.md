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

Tools are addressed as `server__tool`; globs are allowed (`files__*`, `git__*`, `*`).

Every tool has a fixed **tier** - ReadOnly, Write, or Destructive - that the host assigns from an
authoritative internal table; it is not something you can query at authoring time, and
`validate_agent` does not report it. The `max_tier` you set is a **ceiling**: at dispatch the host
classifies each tool the agent would get and **drops any whose tier exceeds the ceiling** (so a
`readonly` agent never gains a Write tool even if its allowlist names one - it is silently dropped,
leaving the agent under-powered). So you must know each tool's tier to choose both the allowlist and
the ceiling. The full first-party table follows - keep `max_tier` at or above the highest tier in
the allowlist, and pick the lowest ceiling that still covers the job.

- **files** - `files__read`, `files__list`, `files__search` (read-only); `files__write`,
  `files__edit` (write); `files__delete` (destructive).
- **git** - `git__status`, `git__diff`, `git__log`, `git__fetch` (read-only - `fetch` only updates
  remote-tracking refs, it does not touch the working tree); `git__add`, `git__commit`,
  `git__branch`, `git__stash` (write - they stage/record but don't discard work); `git__push`,
  `git__pull`, `git__checkout`, `git__restore`, `git__reset`, `git__merge`, `git__rebase`,
  `git__rm`, `git__cherry_pick` (destructive - can lose uncommitted work, move HEAD, or rewrite
  history).
- **command** - `command__run` runs a shell command (destructive - it can do anything). This is the
  only tool in the server.
- **web** - `web__search`, `web__extract`, `web__get` (read-only - `get` is an HTTP GET, `extract`
  fetches page content); `web__http` (destructive - issues state-changing requests like POST/PUT/
  DELETE, a remote-mutation and data-egress surface).
- **memory** - `memory__read_memory` (read-only); `memory__remember`, `memory__update_memory`,
  `memory__forget`, `memory__consolidate` (write - low-risk local `.gxpt` edits). Rarely needed by a
  sub-agent.
- **msbuild** - builds .NET projects. Tool names are discovered per machine (`msbuild__build_4_0`,
  `msbuild__build_solution_2022`, …), so match them with a glob like `msbuild__*`; every build tool
  is destructive (a build runs arbitrary targets/Exec tasks). Tell a build agent to pick an
  available one.
- **extensions** - skill and agent authoring (`.gxpt/skills`, `.gxpt/agents`). Read-only:
  `extensions__list_skill_files`, `extensions__validate_skill`, `extensions__read_agent`,
  `extensions__list_agents`, `extensions__validate_agent`. Write: `extensions__create_skill`,
  `extensions__write_skill_file`, `extensions__update_skill`, `extensions__edit_skill_file`,
  `extensions__create_agent`, `extensions__update_agent`, `extensions__edit_agent`. Destructive:
  `extensions__delete_skill_file`, `extensions__delete_skill`, `extensions__delete_agent`,
  `extensions__run_skill_script`. Rarely granted to a sub-agent (an agent cannot author agents in a
  way that escalates - the same tier rules apply).

Third-party MCP servers the user has configured (commonly **github** - `github__*` for reading PRs
and issues) are **not** in the table. The host classifies them from each tool's advisory
annotations: a `readOnlyHint` makes it read-only, a `destructiveHint` makes it destructive, and a
tool with neither (or an unknown one) is treated as **write**. You can't see these hints at
authoring time, so for a read-only agent that should reach a third-party server, set
`max_tier: readonly` and trust the ceiling to drop anything that isn't actually read-only rather
than assuming a `github__*` glob is safe.

Guidance:
- Match the allowlist to the job, and let `max_tier` enforce the ceiling. A read-only reviewer:
  `tools: [files__read, files__list, files__search, git__status, git__diff, git__log]` +
  `max_tier: readonly`.
- A writer that fixes code: add `files__write`, `files__edit` and `max_tier: write`.
- Don't grant `command__run`, `web__http`, or the destructive git tools unless the job needs them -
  and then set `max_tier: destructive` deliberately.
- After writing, run `validate_agent` to confirm the contract parses (it checks the `max_tier`
  spelling and that `tools` is well-formed - it does not check that the tier matches the allowlist,
  so that judgment is yours).
- The agent runs in the same workspace; it cannot dispatch other agents (one level of delegation
  only), so don't bother listing `dispatch_agent`.

## Writing the system prompt (the body)

The body is the agent's system prompt. The built-in agents (`code-explore`, `plan`, `verify`,
`web-research`, `code-review`, `general-purpose`) are the model to follow - read one with
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
   *what the result contains*, not the *procedure to produce it*. E.g. the code-explore agent
   returns "where the thing lives (file path + line), how it works at a high level, anything
   surprising."

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

This is the `code-explore` agent's body - the canonical shape. Notice how short each part is:

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
