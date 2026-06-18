# Sub-Agents System — Design Proposal

**Status:** Design (proposal). No implementation yet.
**Branch:** `claude/compassionate-goldberg-5c9ccz`
**Last updated:** 2026-06-18 (reconciled with PR #154 — workspace-wide file
approval + flag-insensitive command-signature rules, see §7–§8; refined against
OpenMonoAgent.ai prior art read **from source** — `max_turns`, periodic doom-loop
detection, wildcard allowlists, and read/write-aware fan-out, see §13 and A9/A17–A19)

Delegated, context-isolated **agents** for GxPT: a sub-agent is a markdown file
with YAML-style frontmatter (the `SKILL.md` convention, one level up) that defines
a *specialist* — a system prompt, a bounded tool allowlist, and an autonomy
setting. The main agent hands a sub-agent a self-contained task; the sub-agent runs
its **own** `McpChatOrchestrator` loop in a fresh context, does the work through
tools, and returns **only its final answer** as the dispatching tool's result.

Where a **skill** is *know-how* ("how to do X well here") and an **MCP tool** is an
*effect* ("read a file"), a **sub-agent** is a *worker* ("go do X and report
back") — it owns a loop, a context window, and a capability envelope. The three
compose: a sub-agent's prompt can `open_skill`, a skill body can tell the model to
`dispatch_agent`, and both ride the same tool catalog and approval gate.

The design rides three principles GxPT already commits to: **progressive
disclosure to bound token cost** (a names-only manifest, bodies on demand — the
same idea skills and `reveal_tools` use); **the approval gate is the security
backstop** (`mcp35-approval-spec.md` §6); and **the orchestrator is built fresh
per turn, un-shared, and thread-safe to run concurrently** (it already does, across
tabs — `McpChatOrchestrator` RunTurn, MainForm's per-tab `ThreadPool` dispatch).

---

## 1. Scope & goals

- **Three sub-agent sources: bundled + project + user-global.** Identical roots to
  skills — first-party agents ship beside `GxPT.exe` (`<exe>/agents/`); project
  agents live under `<workdir>/.gxpt/agents/`; user-global under
  `%AppData%/GxPT/agents/`. Reuse `SkillRoots`/`SkillFrontmatter` wholesale.
- **A sub-agent is one markdown file** — `<slug>.md` with frontmatter
  (`name`/`description`/`tools`/`model`/`autonomy`/`max_tier`) + a body that is the
  agent's **system prompt**. (Skills use a `<slug>/SKILL.md` folder because they
  bundle assets; agents rarely do — A4.)
- **Context isolation is the point.** A dispatched sub-agent runs in a *fresh*
  history (its system prompt + the parent's task string), never the parent's
  transcript, and returns only its final text. Keeps the parent's context small and
  firewalls the two conversations.
- **Layered security, three independent layers** (§8): a static **tool allowlist**
  in frontmatter (what it *can* reach), the existing **runtime approval gate** (the
  backstop — Destructive still always-confirms), and a per-agent **autonomy dial**
  (how much it prompts within those bounds). The dial can only ever *loosen* inside
  the ceiling the other two layers set.
- **Parallel by default where it's safe.** `dispatch_agent` is batch: several agents
  run on their own worker threads concurrently (the win is overlapping LLM streams),
  with per-connection serialization on shared MCP servers and a bounded fan-out.
- **Host-native dispatch, no new server.** `dispatch_agent` is a host-synthesized
  meta-tool handled in the orchestrator — the skills `open_skill` precedent (S1):
  spawning an in-process loop is not a child process, so it needs no `…McpServer`.
- **Slash commands, no settings-page UI** — `/agents`, `/agent <slug> …`, and an
  explicit `/agent <slug> <task>` dispatch, on the existing `ISlashCommand`
  framework, scoped per conversation, mirroring `/skills`.

### Non-goals
- **Nested fan-out.** A sub-agent never gets `dispatch_agent` in its tool set — no
  agent-spawns-agent (fork-bomb guard, A12). One level deep, always.
- A marketplace / remote agent install; a per-agent settings-page UI; streaming a
  sub-agent's *partial* output into the parent's context (only the final answer
  returns — A7).
- Cross-turn agent persistence (a dispatched agent is one-shot: it runs to
  completion within the parent's turn and is gone — A8).

---

## 2. Decision ledger

| # | Decision | Rationale |
|---|----------|-----------|
| A1 | **Dispatch is a host-synthesized meta-tool** (`dispatch_agent`), handled in `McpChatOrchestrator` like `reveal_tools`/`open_skill` — **not** an MCP server | Spawning a sub-orchestrator is in-process loop reuse, not a child process: there is no exec, timeout, or arg-quoting hazard to isolate (the reasons skills put *only* `run_skill_script` in a server, S1/S11). The dispatcher reuses the host's own `McpChatOrchestrator`, `McpToolRegistry`, and approval policy — all host types — so it cannot live in `Mcp35.*` without breaking the one-way seam anyway. |
| A2 | **A sub-agent runs its own `McpChatOrchestrator` instance** on a fresh `List<ChatMessage>` seeded with `[system: body] + [user: task]` | The orchestrator is already built fresh per turn, documented un-shared/non-racy, and run on `ThreadPool` worker threads concurrently across tabs. Reusing it gives sub-agents the *entire* loop for free — streaming, reveal/skills, cancellation, usage accounting, the cap wrap-up — with zero new loop code. |
| A3 | **Context firewall:** the sub-agent never sees the parent transcript; only its **final assistant message** returns as the `dispatch_agent` tool result | This is the whole value — token isolation (a 30-call research dig costs the parent ~one paragraph, not 30 tool results) and a clean trust boundary. Matches every mainstream sub-agent model (Claude Code's `Task`/agents). |
| A4 | **One file per agent: `agents/<slug>.md`** (flat), not a `<slug>/` folder | Agents are a *prompt + tool list*; they almost never bundle scripts/assets (which is exactly why skills need a folder). A flat file matches the cross-tool `.claude/agents/<name>.md` convention the user already knows, and keeps the catalog a simple glob. An agent that genuinely needs bundled reference files can be paired with a skill it `open_skill`s. (Folder form deferred, A-open.) |
| A5 | **Frontmatter is the security contract**: `tools` (allowlist), `max_tier` (ceiling), `autonomy` (dial), plus `model` — same `--- … ---` reader as skills (`SkillFrontmatter`), extended for list/enum values | net35 has no YAML parser and the repo keeps one JSON lib (D3); the hand-rolled frontmatter reader already ships for skills (S4). Extending it to parse a `[a, b]` inline list + a few enum keys is a few lines, no new dependency, authoring stays familiar. |
| A6 | **`description` is the dispatch trigger** — a single "use this agent when…" line, the only thing in the always-on manifest | Identical to skills' `description` (S4): it is what the parent model reads to decide *whether* to delegate and *which* agent. Names-only-plus-one-liner keeps the manifest cheap (§4). |
| A7 | **Only the final answer crosses back; no streaming into the parent** | Streaming a sub-agent's intermediate tool chatter into the parent re-imports the context cost the firewall exists to remove (A3). The sub-agent's live output still renders in the transcript UI (its own collapsible block, §9) so the user sees it — it just isn't fed to the *parent model*. |
| A8 | **A dispatch is one-shot within the parent's turn** (no resumable agent sessions) | Keeps state trivial: the sub-orchestrator is a local object on a worker thread, joined before the parent's `dispatch_agent` tool result is formed. Resumable agents would need persisted sub-histories and a handle scheme — deferred until a real need appears. |
| A9 | **`dispatch_agent` is batch** (`agents: [{name, task}, …]`); a batch runs **in parallel** when every named agent is **read-only** (`max_tier: readonly`), else **serially** | The parent fans out in one tool call; the host joins. Parallelism overlaps the expensive part (LLM streams). But two *write*-capable children racing the same workspace can corrupt it logically even with per-connection transport serialization (A10), so a batch with any writer serializes — "reads parallel, writes serial" (OpenMono's intra-turn tool rule) lifted to the **agent** grain. (Per the OpenMono source, `AgentTool` defaults non-concurrency-safe and dispatches **serially**; our parallel read-only fan-out is a deliberate, bounded extension, not a port.) |
| A10 | **Shared MCP server connections are serialized per-connection** under a lock; **revealed-tool state is per-sub-agent** | `McpServerConnection` correlates by request id and the server SDK dispatches serially; concurrent `CallTool` on one connection is unsafe, so a per-connection mutex serializes tool I/O while model streams still overlap. Each sub-agent owns its own `RevealedToolNames` list (fresh context), so no agent churns another's tools array (prompt-cache safety, already the per-tab rule). |
| A11 | **Effective tools = `allowlist` ∩ `parent-available` ∩ `max_tier`** — a sub-agent can never exceed the parent's own reach | No privilege escalation: delegating cannot grant a capability the parent itself lacks in this workspace (e.g. a folderless turn's sub-agent still can't reach `files__*`). The allowlist *narrows*; it never widens. |
| A12 | **`dispatch_agent` is never in a sub-agent's tool set** | One level of delegation only — structurally prevents recursive fan-out / fork bombs and unbounded cost. Enforced by the host (the dispatcher strips it from every child's exposed defs), not by author discipline. |
| A13 | **The approval gate is fully in force for every sub-agent tool call**, with the **shared** remembered-allowlist and per-tab prompt host | The sub-agent isn't a trust upgrade: a Destructive call it makes still always-confirms, an unremembered Write still prompts. Prompts marshal to the parent tab's `ToolApprovalPanel` (the existing `Control.Invoke` path), attributed to the agent. This is the backstop that makes autonomy safe (§8, layer 2). |
| A14 | **Autonomy is a per-agent dial** (`gated` \| `auto-readonly`), approved **once at dispatch** (remember by agent name), bounded by `max_tier` | "Leeway to act autonomously" without surrendering the gate: an `auto-readonly` research agent, approved once, runs a long read-only dig unattended; the moment it reaches for Write/Destructive it re-enters the normal gate. The grant can't exceed the tier ceiling, and Write/Destructive never auto-approve from a dial (only an exact remembered rule does). |
| A15 | **Enablement reuses the skills tri-state ladder** (`/agents`, `/agent <slug> on\|off\|reset [here\|global]`), default **OFF** for the feature, opt-in per agent | Same `SkillEnablement`/conversation-override machinery (S10), so the manifest/dispatch surface only appears when the user has turned agents on — agents are more powerful (they spawn loops + cost) than skills, so they lead **off** rather than all-on (the one deliberate departure from S6). |
| A16 | **Catalog & frontmatter are pure logic** (`AgentCatalog`, `Services/Agents/`), no WinForms — net48 linked-source tests, like `SkillCatalog` | Same dual-world test pattern (§10). Discovery, allowlist resolution, tier ceiling, and the manifest are all testable without the UI or a live model. |
| A17 | **Per-agent `max_turns` budget** feeds the child orchestrator's existing `maxIterations` ctor arg | The orchestrator *already* takes `maxIterations` as a constructor parameter — a per-agent budget is free plumbing. OpenMonoAgent.ai assigns distinct budgets per specialist (from source: Explore/Plan 100, Verify 150, general 200, Coder 300 — far higher than its docs imply). Two implications: bounding an explore agent below a coder is the right grain, **and** our `DefaultMaxIterations = 25` is tuned for *interactive* turns (the continuation prompt is its release valve) — an **unattended** child has no continuation prompt, so write-capable bundled agents should set a generous `max_turns`, with doom-loop (A18) + cap-wrap-up as the backstops. |
| A18 | **Periodic doom-loop detection in the orchestrator**: over a short rolling window of recent `name:normalized-args` signatures, abort with a wrap-up when a cycle of period *p* (1–4) repeats (≥3× for *p*=1, ≥2× for *p*=2–4) | The `MaxIterations` cap bounds *total* work but a stuck agent still burns the whole budget on a cycle — worse for an unattended sub-agent (A14). The OpenMono `DoomLoopDetector` (from source: `MaxPeriod=4`, `MaxHistory=12`, `reps = period==1 ? 3 : 2`, args JSON-normalized with sorted keys) is smarter than "N identical in a row": it catches **oscillations** (edit→test-fail→revert→edit…, an A→B→A→B period-2 cycle) a consecutive-only check misses. Cheap (a ~12-entry signature ring in `RunTurn`), benefits the **main** agent too, ends as content not a throw. |
| A19 | **Allowlist `tools` supports glob wildcards** (`files__*`, `mcp__*`, `*__read`, `*`) matched against the qualified catalog | Server-qualified names (`files__read`, `git__status`) make prefix/suffix globs natural and far less brittle than enumerating every tool — "all file tools" is `files__*`, "everything a server exposes" is `mcp__myserver__*`. The OpenMono precedent ("allow-lists support `*`, `mcp__*`"). Still intersected with parent-available and `max_tier` (A11), so a wildcard never widens past the parent or the ceiling. |

---

## 3. On-disk layout

### Bundled (first-party, ships beside the exe)
```
<GxPT.exe dir>/agents/
  code-explorer.md
  test-runner.md
  pr-reviewer.md
```
Deployed by the same `AfterBuild` copy that places `mcp-servers\` and `skills\`
(and seeded into the setup `.vdproj`), so bundled agents travel with the install.

### Project (scoped to the conversation's working folder)
```
<workdir>/.gxpt/agents/
  <slug>.md
```
Reuses the `.gxpt/` home memory and skills established. A project agent **shadows**
a bundled agent of the same slug (precedence **project > user > bundled**, A11-aligned
with skills S5).

### User-global
```
%AppData%/GxPT/agents/
  <slug>.md
```

### `AGENT.md` format
```markdown
---
name: Code Explorer
description: Use to search and summarize unfamiliar code across the workspace before making changes. Read-only.
tools: [files__read, files__list, files__search]
max_tier: readonly
max_turns: 15
autonomy: auto-readonly
model: anthropic/claude-sonnet-4-6
---

You are a code-exploration specialist working inside the user's workspace.

Given a question about the codebase, locate the relevant files, read the parts that
matter, and return a tight written summary: where the thing lives (file:line), how
it works, and anything surprising. Cite paths. Do not modify files. If you can't
find something after a genuine search, say so and name where you looked.
```

- **Frontmatter** is a leading `--- … ---` block of `key: value` lines, parsed by
  the extended `SkillFrontmatter` reader. Keys:
  - `name` *(required)* — display name.
  - `description` *(required)* — the **single-line** "use this agent when…" the
    parent reads in the manifest (A6).
  - `tools` *(optional)* — inline list `[a, b, c]` of server-qualified function
    names the agent may use, **with glob wildcards** (`files__*`, `mcp__*`, `*__read`,
    `*`) matched against the qualified catalog (A19, the OpenMono allowlist-wildcard
    precedent). **Omitted ⇒ inherit a conservative default set** (all ReadOnly-tier
    tools available in the workspace), *not* everything (A5, fail-safe). `tools: [*]`
    opts into the full parent-available set (still capped by `max_tier` and the gate).
  - `max_tier` *(optional)* — `readonly` \| `write` \| `destructive` ceiling,
    classified via the existing `ToolClassifier`. Caps the allowlist regardless of
    what `tools` names. Default `write` (an agent can edit but the gate still
    confirms; `destructive` must be opted into explicitly).
  - `max_turns` *(optional)* — per-agent iteration budget, fed straight into the
    child orchestrator's `maxIterations` ctor arg (A17). Omitted ⇒ the host default
    (`DefaultMaxIterations`). Lets an `explore` agent cap at ~15 while a `coder` runs
    ~30, so an unattended specialist's cost is bounded *to its job*, not the parent's.
  - `autonomy` *(optional)* — `gated` (default) \| `auto-readonly` (§8 layer 3).
  - `model` *(optional)* — model id override; omitted ⇒ the parent turn's model.
- The **body** is the agent's **system prompt** — it replaces `AgentSystemPrompt`'s
  *persona*, but the host still prepends the standing agent guidance and the
  workspace block (§5), so a sub-agent always knows it's tool-using and where it is.
- Unknown frontmatter keys are ignored (forward-compatible, like skills).

---

## 4. Disclosure levels

| Level | Content | When it enters context | Cost |
|-------|---------|------------------------|------|
| 1 | `slug` + `description` (the **agents manifest**) | every request, while the agents feature is enabled for the conversation **and** at least one agent is on | tiny |
| 2 | `dispatch_agent` meta-tool def | same condition (exposed alongside the manifest) | tiny |
| 3 | the agent **body** (its system prompt) + its **work** | only inside the *sub-agent's* context when dispatched — **never** in the parent | paid by the sub-agent, isolated |
| 4 | the agent's **final answer** | returns to the parent as the `dispatch_agent` tool result | one summary |

The parent pays Level 1+2 (a line per agent + one tool def) to *know it can
delegate*, and Level 4 (a summary) per dispatch. Level 3 — the expensive part —
lives entirely in the sub-agent's window. That asymmetry is the design.

---

## 5. Host pieces

### Catalog & manifest (host-native, `Services/Agents/`)

```
Agent                 -- (slug, name, description, ToolSpec, MaxTier, Autonomy, Model, BodyPath)
AgentFrontmatter      -- extends SkillFrontmatter: inline-list + enum keys
AgentCatalog          -- scans bundled+user+project agents/*.md, project>user>bundled
AgentInjection        -- BuildManifestMessage(enabledAgents) -> the Level-1 block
AgentResolve          -- EnabledAgents(...) over the tri-state ladder (reuses SkillResolve shape)
AgentToolResolver      -- effective tool defs for an agent = allowlist ∩ parent-available ∩ max_tier
```

`AgentCatalog` is pure logic (no WinForms) → net48 linked-source tests, exactly like
`SkillCatalog`. `AgentInjection.BuildManifestMessage` produces the same kind of
ephemeral block the skills manifest does, slotted into the orchestrator's
`BuildEphemeralContextText` tail (a new `<agents>` section, after `<skills>`,
before `<available_tools>`):

```
McpChatOrchestrator (parent RunTurn loop)
  ├─ ephemeral tail (Zone C):  <memory> … <skills> … <agents> … <available_tools>
  └─ exposed tools: reveal_tools, open_skill, read_skill_file, dispatch_agent, …revealed
```

A new orchestrator field mirrors the skills hooks exactly:
```csharp
public Func<string> AgentsManifestSystemMessageProvider { get; set; }   // Level-1 block
public AgentDispatcher AgentDispatcher { get; set; }                     // exposes + handles dispatch_agent
```
`dispatch_agent` is added to the `tools` array (like `OpenSkillDef`) when the
dispatcher is set and has agents, and handled in `ExecuteCall` *before* MCP
resolution (like `IsOpenSkill`), so it never hits a server.

### `dispatch_agent` — the meta-tool

```
dispatch_agent(agents: [ { name: string, task: string }, … ])   // batch (A9)
```
Definition shape mirrors `open_skill`/`reveal_tools` so the model treats it the
same way. Description: *"Delegate one or more self-contained sub-tasks to specialist
agents that work in isolation and report back. Pass each agent's slug (from the
agents list) and a complete task description — the agent does not see this
conversation."*

**On call, the `AgentDispatcher` (host):**
1. Resolve each `name` → enabled `Agent` (unknown/disabled ⇒ that entry returns a
   short note, the rest still run — the `open_skill` tolerance, S-style).
2. For each entry, build a **child** `McpChatOrchestrator`:
   - history = `[ system: standing-guidance + workspace-block + agent.Body ]`
     then `[ user: task ]`;
   - `WorkingDir` = parent's (so scoped servers route to the same folder);
   - `model` = `agent.Model ?? parentModel`;
   - **exposed tools restricted** via `AgentToolResolver` (A11) — for a *small*
     declared allowlist the child skips progressive disclosure and is handed those
     defs directly (no `reveal_tools` dance); for `tools: [*]` it gets the normal
     manifest + `reveal_tools`, tier-capped;
   - `dispatch_agent` **stripped** (A12), `open_skill`/`read_skill_file` allowed;
   - `HiddenToolNames` = (parent-available − effective) so a directly-named
     out-of-allowlist tool is refused, the existing gate mechanism (§ `HiddenToolNames`);
   - approval policy = the parent's, **wrapped** with the agent's autonomy
     pre-authorization (§8 layer 3) and tagged with the agent name for the prompt;
   - `Cancellation` = the parent's handle (parent Stop cancels all children, §9);
   - `UsageReported` → aggregated onto the parent conversation.
3. Run the children **in parallel** on `ThreadPool` work items, bounded to
   `MaxParallelAgents` (default 3), joining on a `ManualResetEvent`/countdown.
4. Each child's **final assistant text** (its `RunTurn` result) becomes one labeled
   section of the `dispatch_agent` tool result:
   ```
   ## Agent: code-explorer
   <final answer>

   ## Agent: test-runner
   <final answer>
   ```
   A child that errored/hit its cap returns its wrap-up text in the same slot — never
   throws across the join (failures are content, the loop's existing stance).

### Why reuse the orchestrator wholesale

A child gets, for free: streamed model calls, the names-manifest + `reveal_tools`
flow, `open_skill`/skills, the `MaxIterations` budget + `ContinuationDecider`
(children default to **no** continuation prompt — they wrap up at the cap rather
than block on a user, since the dispatch is meant to be unattended), cancellation,
usage/cost accounting, and the empty-response retry. The dispatcher writes *none* of
that — it only constructs and joins.

---

## 6. Slash commands (on the existing `ISlashCommand` framework)

Register in an `AgentCommands.BuiltIns()` beside the skills/built-ins. `Client`
kind (run locally, no LLM send) except `/agent <slug> <task>`, which dispatches.

| Command | Kind | Effect |
|---------|------|--------|
| `/agents` | Client | List agents with effective on/off state + source (bundled/user/project) |
| `/agents [on\|off\|reset] [here\|global]` | Client | Toggle/reset the **whole feature** at that scope (default `here`) |
| `/agent <slug> [on\|off\|reset] [here\|global]` | Client | Toggle/reset **one agent**; bare `/agent <slug>` toggles for this conversation |
| `/agent <slug> <task…>` | Send | **Explicit dispatch**: send a hidden instruction `Dispatch the <slug> agent with this task: <task>` so the model issues a real `dispatch_agent` call (works regardless of enablement — an explicit user action, like `/use`) |

- **Slug-first**, mirroring `/skill` and `/tool`. Autocomplete completes slugs
  (annotated with state) then `on\|off\|reset` then `here\|global`, via
  `IArgumentCompleter` — the exact pattern `SkillCommands` uses.
- Explicit `/agent <slug> <task>` routes through the model (not a direct host
  spawn) so the dispatch is a normal, gated `dispatch_agent` tool call with a
  transcript record and approval — no second, ungated entry point into spawning.

---

## 7. Enablement & persistence

Reuses the skills 2×2 (all-agents / one-agent × global / this-conversation),
resolved by **most-specific-wins**, with **one deliberate difference: the feature
default is OFF** (A15). An agent is in play only when the feature is on *and* that
agent is on.

### Resolution ladder (effective state of agent X in conversation C)
| # | Rule | Source |
|---|------|--------|
| 1 | this agent, **here** | `Conversation.AgentOverrides[X]` |
| 2 | this agent, **global** | `agents.json` `agents[X]` |
| 3 | all agents, **here** | `Conversation.AgentsFeatureOff` |
| 4 | all agents, **global** | `agents.json` `feature_off` |
| 5 | **default: feature OFF** | — |

### Persistence
- **Global** — `%AppData%/GxPT/agents.json` (a sibling of `skills.json`), written
  only by `… global` commands:
  ```json
  { "feature_off": true, "agents": { "code-explorer": true, "pr-reviewer": true } }
  ```
- **Conversation** — `AgentsFeatureOff` (`bool?`) + `AgentOverrides` (`slug → bool`)
  on the `Conversation`, round-tripped by `ConversationStore` (absent = inherit).

Per-tab by construction; no `settings.json` key, no checkbox — all control is §6.
The autonomy *grant* (A14) is separate persisted state in the **shared approval
store** alongside the existing remember kinds (per-tool, command-signature/path
rules, and the workdir-keyed workspace-write set from PR #154): a remembered "this
agent may run autonomously" keyed by agent name, revocable from the same approvals
affordance that clears tool allowlists and workspace-write grants
(`mcp35-approval-spec.md` §5). Slotting it beside the workdir-write set means one
view/clear surface governs every grant a sub-agent can inherit.

---

## 8. Security & approval — the three layers

The request is "layered security controls **and** leeway to act autonomously." The
resolution is three independent layers where the security layers set a **ceiling**
and the autonomy dial only ever moves *within* it — autonomy can never out-vote a
narrower layer below it.

```
              ┌─────────────────────────────────────────────┐
  Layer 1     │ STATIC CAPABILITY  (frontmatter)            │  what it CAN reach
  (definition)│ tools allowlist ∩ parent-available ∩ max_tier│  — never widens past parent
              └─────────────────────────────────────────────┘
                              ▼ bounds
              ┌─────────────────────────────────────────────┐
  Layer 2     │ RUNTIME GATE  (ToolApprovalPolicy, shared)  │  whether each call is allowed
  (per call)  │ Destructive always-confirm; unremembered     │  — the backstop, unchanged
              │ Write/ReadOnly prompt; narrow remembered rules│
              └─────────────────────────────────────────────┘
                              ▼ within which
              ┌─────────────────────────────────────────────┐
  Layer 3     │ AUTONOMY DIAL  (autonomy:, approved once)    │  how much it prompts
  (per agent) │ auto-readonly ⇒ ReadOnly-tier auto-allows     │  — the LEEWAY, bounded by 1&2
              │ for this run; Write/Destructive still gate     │
              └─────────────────────────────────────────────┘
```

### Layer 1 — static capability scoping (definition-time)
The `tools` allowlist + `max_tier` ceiling are the agent's **maximum reachable
surface**, resolved by `AgentToolResolver` to `allowlist ∩ parent-available ∩
max_tier` (A11). Properties:
- **No escalation** — the effective set is a *subset* of what the parent itself can
  call in this workspace; delegation never unlocks a new capability.
- **Fail-safe default** — an omitted `tools` inherits only the ReadOnly tier, not
  everything (A5). A forgotten allowlist makes an agent *less* capable, never a
  silent wildcard.
- Out-of-set tools are placed in the child's `HiddenToolNames`, so even a model that
  names a tool directly gets `[Unknown tool: …]` — the existing gate-off mechanism.

### Layer 2 — the runtime approval gate (per call, unchanged backstop)
Every tool call a sub-agent makes flows through the **same** `ToolApprovalPolicy`
with the **shared** `IApprovalStore` and the parent tab's prompt host (A13). The
policy is **per-turn `WorkingDir`-aware** (PR #154), so the child must be built with
its `WorkingDir` set to the parent's — which it is (children share the parent's
folder, §5) — and the `AutonomyApprovalPolicy` decorator (layer 3) forwards
`WorkingDir` to the inner policy so the workspace fast-path below still fires for
sub-agents:
- **Destructive always-confirms** — a sub-agent cannot silently `files__delete` /
  `command__run` a new command / `git__push`, even if its allowlist includes them
  and the user is away (the turn simply blocks at the modal — correct, and the same
  semantics as the main agent). `files__delete` stays Destructive and is **excluded**
  from the workspace-write grant (PR #154), so no blanket ever covers a delete.
- **Remembered grants stay narrow / user-vetted** — sub-agents inherit exactly the
  three remember kinds the store holds, no more: per-tool (ReadOnly/Write), the
  argument-scoped **command-signature** and **path** rules, and the session-scoped
  **workspace-write** set (PR #154, keyed by `server + canonical workdir`). An
  injected attempt (a malicious tool result steering the sub-agent) to run a tool
  whose **command signature** or path differs from any remembered grant still
  prompts — the signature is flag-insensitive but *target*-sensitive (a different
  file/subcommand re-prompts, PR #154), so the prompt-injection backstop
  (`mcp35-approval-spec.md` §6) covers sub-agents identically.
- **Attribution** — the prompt header shows which agent is asking
  (`code-explorer · files__read`), so the user always knows *who* wants the effect.

### Layer 3 — the autonomy dial (the leeway, bounded)
`autonomy` grants a sub-agent room to run unattended **inside** layers 1–2:
- `gated` *(default)* — behaves exactly like the main agent: every tier prompts per
  the normal rules. Safe, but a long read-only dig prompts repeatedly.
- `auto-readonly` — the sub-agent's **ReadOnly-tier** calls auto-allow for the
  duration of *this dispatch*; **Write and Destructive still hit the gate** (or are
  absent, if `max_tier: readonly`). This is what lets a fan-out of research/explore
  agents run while the user is away.

The grant is **approved once, at dispatch**: dispatching an `auto-readonly` agent
prompts *"`code-explorer` will run autonomously using read-only tools — allow?"*,
remember-eligible **by agent name** (stored like a tool allowlist entry, A14/§7).
Crucially:
- The dial is **capped by `max_tier`** — `autonomy: auto-readonly` on an agent whose
  ceiling is `readonly` means the whole agent is unattended; on a higher-ceiling
  agent it still only auto-allows the *ReadOnly* slice.
- **A dial never auto-approves Write/Destructive** — only an explicit, narrow
  remembered *rule* (command signature / path) or the user's **workspace-write
  grant** does, exactly as for the main agent. The dial is a tier-scoped convenience
  over already-remember-eligible ReadOnly calls (which the main agent could itself
  "always allow"), not a new bypass.
- Implementation: the dispatcher wraps the shared `IToolApprovalPolicy` in an
  `AutonomyApprovalPolicy` that, for an `auto-readonly` child, returns `Allow`
  without a prompt **iff** the call classifies ReadOnly *and* the one-time
  agent-name grant is present; everything else delegates to the real policy. No
  change to the gate itself — a decorator at the dispatch seam.

**Write-autonomy is delivered by the user's workspace grant, not a per-agent dial
(reinforced by PR #154).** PR #154 added *"Allow all edits in this workspace,"* a
session-scoped, workdir-keyed approval covering every Write-tier `files__` tool in
the current folder (`files__delete` excluded). Sub-agents inherit it through the
shared store, so a `gated`, write-capable agent's `files__write`/`files__edit` calls
pass silently **once the user has made that workspace-level choice** — coarse
write-autonomy that is *user-controlled at the workspace grain*, not declared by an
agent's frontmatter. This is deliberately *not* an `auto-write` autonomy level:
keeping write breadth a user decision (made once, visibly, revocable from the same
approvals affordance) is safer than letting an authored agent self-grant it, and it
composes cleanly — the per-agent dial covers the ReadOnly slice, the workspace grant
covers Writes, and `files__delete` + `command__run` + `git__push` stay on the
always-confirm backstop for every agent regardless. (If a real need for per-agent
write-autonomy appears later, it would be a *fourth* remember kind gated like the
workspace grant — noted, not adopted.)

### Why this is both safer *and* more autonomous
- **Safer than a raw tool allowlist:** the allowlist alone (layer 1) wouldn't stop a
  manipulated agent from misusing an *allowed* Destructive tool; layer 2 does.
- **More autonomous than the main agent:** an `auto-readonly` agent, vetted once,
  runs a long investigation with zero further prompts — leeway the main agent only
  gets by the user manually "always-allowing" each read tool. The dial packages that
  as a per-specialist, revocable grant.
- A loaded agent body is **injected instructions** (the skills stance, §8 there):
  bundled/project agents are author-trusted; treat the body as untrusted at the
  parse boundary, and gate a first dispatch of a *user-global/shared* agent so the
  user sees what persona + capabilities they're about to run.

---

## 9. Parallelism & threading

GxPT already runs each tab's turn on a `ThreadPool` worker thread, with the
orchestrator built fresh and explicitly documented un-shared. Parallel sub-agents
extend that, not rework it.

- **Fan-out, read/write-aware (A9):** a batch where **every** named agent is
  read-only (`max_tier: readonly`) queues N `ThreadPool` work items (bounded to
  `MaxParallelAgents`, default 3 — a named constant), each running a child `RunTurn`
  to completion, joined on a countdown before the tool result forms. A batch that
  includes **any write-capable agent runs serially** instead — two writers racing the
  same workspace can produce a logically inconsistent tree even when individual calls
  are transport-serialized (A10), so concurrency is restricted to the provably-safe
  read-only case (OpenMono's "reads parallel, writes serial," at the agent grain).
  Either way the parent worker thread blocks on the join — the same "turn pauses,
  user present-or-away" model the gate already uses.
- **The win is overlapping LLM streams**, the turn's real latency. Tool I/O to a
  **shared** MCP server connection is *also* serialized by a **per-connection mutex**
  (A10): `McpServerConnection` correlates by id and the server SDK dispatches
  serially, so concurrent `CallTool` on one connection is unsafe — the lock makes two
  read-only agents take turns at the file server while their *model* calls still run
  concurrently. (The mutex protects *transport*; the A9 read-only rule protects
  *workspace logical consistency* — two distinct concerns, both needed.)
- **Prompt caching is unaffected:** each child owns a *fresh* `RevealedToolNames`
  and a fresh transcript, so no child churns another's tools array (the per-tab
  invariant, generalized).
- **Approval prompts serialize naturally:** modal `Control.Invoke` to the one UI
  thread queues concurrent asks; each is agent-attributed (§8). A child that needs
  an unremembered Write blocks just that child until answered.
- **Cancellation propagates:** the parent's `RequestCancellation` is handed to every
  child, so the Stop button cancels the whole fan-out; each child finalizes cleanly
  (keeps partial text, the loop's existing `FinishCancelled`).
- **UI:** each running child renders as a collapsible transcript block ("▸ Code
  Explorer — running… 3 tools"), its tool calls/results shown but *not* fed to the
  parent model (A7). On completion the block collapses to the returned summary.
- **Cost guardrails (three bounds, the unattended-run safety net):**
  `MaxParallelAgents` bounds concurrency; each child carries its **`max_turns`**
  budget (A17) and wraps up at its cap with **no continuation prompt** (the dispatch
  is meant to be unattended — a child can't block the fan-out waiting on a user); and
  **periodic doom-loop detection** (A18) aborts a child stuck in a repeating *cycle*
  (not just a single repeated call) rather than draining the whole budget. Together these make an
  `auto-readonly`, write-granted fan-out safe to leave running: bounded breadth
  (allowlist/tier), bounded depth (`max_turns`), bounded repetition (doom-loop), and
  the always-confirm gate on anything destructive.

---

## 10. Testing strategy

Same dual-world pattern (net48 linked-source via `dotnet test`):

- **`AgentCatalog` + frontmatter parser** — discovery across the three roots,
  project>user>bundled shadowing, inline-list `tools` parsing, `max_tier`/`autonomy`
  enum parsing, malformed/missing frontmatter, manifest assembly for an enabled set.
- **`AgentToolResolver`** — `allowlist ∩ parent-available ∩ max_tier`; omitted
  `tools` ⇒ ReadOnly-only; `tools: [*]` ⇒ full set tier-capped; **wildcard patterns**
  (`files__*`, `mcp__*`, `*__read`) expand against the qualified catalog (A19); an
  allowlisted tool the parent lacks in this workdir is excluded (no escalation);
  `dispatch_agent` always stripped from a child.
- **`max_turns` + periodic doom-loop** (in the orchestrator, so the main agent is
  covered too) — a child built with `max_turns` wraps up at that cap (A17); a cycle of
  period 1–4 over the recent `name:normalized-args` signature window (≥3× for period 1,
  ≥2× for longer — A18) aborts with a wrap-up, catching A→B→A→B oscillations and not
  just A→A→A; the abort returns as content, never a throw.
- **Read/write fan-out gating** — an all-read-only batch runs children concurrently;
  a batch with any write-capable agent serializes (A9), asserted by execution order.
- **Agent slash commands** (`AgentCommands` vs. a fake `ISlashCommandContext`) —
  list/toggle/reset across here/global; conversation override vs. `agents.json`;
  default-OFF feature; `/agent <slug> <task>` sends the hidden dispatch instruction;
  unknown slug/scope failures.
- **Enablement resolution** — the 5-rung ladder with feature-default OFF;
  force-on over a global-off; reset → inherit.
- **`AutonomyApprovalPolicy`** (decorator) — `auto-readonly` + grant ⇒ ReadOnly
  auto-allows; Write/Destructive still delegate to the real gate; no grant ⇒
  everything prompts; the grant is tier-capped and name-scoped.
- **Dispatcher / host loop (`GxPT.Tests`)** with a scripted model stream + fake
  registry: single dispatch → child runs → final answer returns as the tool result;
  **batch dispatch runs concurrently** and aggregates labeled sections; a child that
  errors/caps returns wrap-up text without throwing; `dispatch_agent` handled
  host-side (no MCP round-trip); cancellation propagates to children; an out-of-set
  tool named directly by a child → `[Unknown tool]`; **no `dispatch_agent` exposed
  to a child** (no nesting).
- **Concurrency** — N children sharing one fake connection serialize through the
  per-connection lock (asserted via call interleaving), while their model streams
  overlap; `MaxParallelAgents` bounds in-flight children.

---

## 11. Phasing / roadmap

1. **`AgentCatalog` + extended frontmatter parser** + bundled-agent discovery
   (pure, TDD) — the catalog, `tools`/`max_tier`/`autonomy` parsing, shadowing.
2. **`AgentToolResolver`** — effective-tool resolution (allowlist ∩ parent ∩ tier),
   the no-escalation core (pure, TDD).
3. **Manifest injection** — `AgentInjection.BuildManifestMessage` slotted into the
   orchestrator's ephemeral tail (`<agents>` section), gated by the enabled set.
4. **`dispatch_agent` meta-tool + `AgentDispatcher`** — single-dispatch first:
   build a child orchestrator, run it, return its answer as the tool result
   (handled in `ExecuteCall` like `open_skill`). Context firewall + `HiddenToolNames`
   strip of `dispatch_agent`.
5. **Approval integration** — `AutonomyApprovalPolicy` decorator + the one-time
   agent-name grant in the approval store; agent-attributed prompts.
6. **Agent slash commands** on the `ISlashCommand` framework (`/agents`,
   `/agent <slug> …`, `/agent <slug> <task>`) + per-conversation override fields on
   `Conversation`/`ConversationStore` + global `agents.json` (5-rung ladder).
7. **Parallel fan-out** — batch `dispatch_agent`, bounded `ThreadPool` execution,
   per-connection mutex, cancellation propagation, usage aggregation.
8. **Transcript UI** — collapsible per-child agent blocks (live tool activity shown,
   not fed to the parent), collapsing to the returned summary.
9. **Bundled agents + deploy** — ship a starter suite via an `agents/` source folder
   copied next to the exe (`AfterBuild` + setup `.vdproj`), like bundled skills. The
   OpenMono specialist set is a proven shape to seed with: **explore** (read-only
   discovery, `auto-readonly`, low `max_turns`), **plan** (read-only architecture,
   `max_tier: readonly`), **coder** (write, higher `max_turns`), **verify**
   (read + build/test, adversarial), plus a **general-purpose** catch-all — alongside
   the task-shaped `pr-reviewer`. Each is just an `AGENT.md`; nothing new in code.
10. **(Optional/later)** an agent-authoring surface (an `agent-writer` skill, the
    skill-writer precedent); folder-form agents with bundled assets; user-global
    `scope` parity in any authoring flow.

---

## 12. Open / soft decisions

**Soft (lean noted):**
- **Agent file form** — *lean flat `agents/<slug>.md`* (A4) for parity with the
  cross-tool convention and catalog simplicity; revisit to `<slug>/AGENT.md` folders
  only if agents start needing bundled assets (and even then, pairing with a skill
  may be the better factoring).
- **Default `max_tier`** — *lean `write`* (an agent can edit, the gate still
  confirms), with `destructive` opt-in only. Could argue `readonly` default for
  maximum caution; chosen `write` so the common "fix this" agent works without
  ceremony while Destructive stays explicit.
- **`MaxParallelAgents`** — *lean 3* (named constant, tunable), balancing latency
  win against API/thread pressure on XP-era hardware.
- **Explicit `/agent <slug> <task>` routing** — *lean route-through-the-model*
  (a real gated `dispatch_agent` call) rather than a direct host spawn, so there's
  exactly one spawning path and it's always gated/recorded.

**Deferred:**
- **Nested delegation** (an agent dispatching agents) — structurally blocked (A12);
  revisit only with a hard depth cap and cost budget if a real workflow needs it.
- **Resumable / multi-turn agents** — today a dispatch is one-shot within the
  parent's turn (A8); a handle scheme + persisted sub-histories is a later feature.
- **Streaming a child's output into the parent model** — deferred (A7); only the
  final answer crosses back. The user still *sees* live child activity (§9).
- **Agent-authoring tools** (a writer like `skill-writer`) — phase 10; until then
  agents are authored by hand or via the file tools.
- **Folder-form agents + bundled assets**; **user-global authoring `scope`** parity.
- **Per-agent model *routing policy*** beyond a static `model:` override (e.g. "use
  a cheaper model for explore agents") — the static override covers the common case.

---

## 13. Prior art & external validation — OpenMonoAgent.ai

[OpenMonoAgent.ai](https://github.com/StartupHakk/OpenMonoAgent.ai) is a .NET sub-agent
coding system (local llama.cpp inference). It's a useful mirror because it solved the
same problem in a comparable stack, and most of its choices **independently match**
ours — which raises confidence — while a few sharpened this design (A17–A19, §9). The
findings below come from reading the actual source (`src/OpenMono.Cli/`:
`Tools/AgentTool.cs`, `Agents/AgentDefinition.cs`, `Tools/ToolDispatcher.cs`,
`Tools/ToolBase.cs`, `Session/DoomLoopDetector.cs`, `Permissions/Capability.cs`), which
corrected two things its prose docs overstated (turn budgets; the "12-step pipeline" is
really a 2-stage read-parallel/write-serial dispatcher).

### Where it confirms our choices (convergent design)
| OpenMono | Our design | Note |
|----------|-----------|------|
| `AgentTool` "spawns an isolated session with a restricted tool set and a dedicated system prompt" | `dispatch_agent` → child orchestrator, fresh context, allowlist, body-as-system-prompt (A1–A5) | Nearly verbatim — the isolated-session-with-locked-tools shape is the consensus pattern. |
| Sub-agent **reuses the parent's `PermissionEngine`** (preserves user consent) | Child reuses the parent's `ToolApprovalPolicy`/`IApprovalStore` (A13) | Strong validation of Layer 2: don't re-prompt or fork consent; the gate is shared. |
| Returns **final text + artifacts** as a single tool response; parent never sees inner iterations | Only the final assistant message returns (A3/A7) | The context-firewall value is the agreed core. |
| Sub-agent inherits parent context (`OPENMONO.md`, memory, git status) but its **own message list** | Standing guidance + workspace block + AGENTS.md + memory, in a fresh history (§5) | Same "shared standing context, isolated transcript." |
| Hard-coded allowlists per agent type; **read-only vs write tiers** | `tools` ∩ `max_tier` (A5/A11), classified by `ToolClassifier` tiers | Same tiered-capability instinct; ours is author-declared per agent rather than five fixed types. |
| **Spawn is a capability** (`AgentSpawnCap(string AgentType, string TaskSummary)`, source) routed through the permission engine | `dispatch_agent` itself goes through approval; autonomy grant remembered by agent name (A14) | Both gate the *spawn*, not just the spawned calls. |
| `AgentDefinition` **record**: `Name`, `Description`, `AllowedTools (["*"])`, `MaxTurns`, `SystemPrompt?` (source) | Our `Agent` from frontmatter: `name`, `description`, `tools`, `max_turns`, body-as-prompt — **plus** `max_tier` + `autonomy` | Field-for-field match; our schema is a **superset**, adding the two keys that serve the layered-security goal (§8). |
| No nesting: `if (tool.Name == "Agent") continue;` (source — sub-agents can't see the Agent tool) | `dispatch_agent` stripped from every child (A12) | Identical fork-bomb guard, confirmed in code. |

### Where it sharpened our design (adopted)
- **Distinct turn budgets per specialist** → adopted as **`max_turns`** (A17), free
  plumbing over the orchestrator's existing `maxIterations` arg. Reading the source
  also corrected the numbers — the real budgets are **100–300** (Explore/Plan 100,
  Verify 150, general 200, Coder 300), not the 10–30 the docs imply — which flagged
  that our interactive `DefaultMaxIterations = 25` is too low for an *unattended*
  write agent (no continuation prompt), so bundled write agents set a generous
  `max_turns` (A17).
- **Periodic doom-loop detection** → adopted as A18. The source `DoomLoopDetector` is
  *cycle*-aware (period 1–4, `reps = period==1?3:2`, JSON-normalized signatures, a
  12-entry history), not the "3 identical in a row" the prose says — so it catches
  A→B→A→B oscillations a consecutive-only check misses. We adopt the smarter version.
  A gap in our prior draft, and exactly the valve an unattended agent needs.
- **Allowlist wildcards** (`*`, `mcp__*`, confirmed by `IsToolAllowed` in
  `AgentTool.cs`) → adopted as A19; natural over GxPT's server-qualified names
  (`files__*`), far less brittle than enumerating tools.
- **A proven bundled suite** (explore / plan / coder / verify / general) → seeds our
  phase-9 starter agents — pure `AGENT.md` files, no new code.

### Where we deliberately diverge (and why)
- **Capability-based `PermissionEngine`** (`FileWriteCap`, `ProcessExecCap`,
  `NetworkEgressCap`, …) is richer than our tier + argument-scope classifier. We
  **keep the tier model**: it already ships, the approval spec is built on it, and
  PR #154's workspace-write/command-signature grants extend it cleanly. A
  capability-object refactor is a large change for marginal gain here — noted, not
  adopted.
- **The parallelism axis is opposite, by design.** The source shows OpenMono
  parallelizes **tools within one turn** (`ToolDispatcher` splits calls on
  `tool.IsReadOnly && tool.IsConcurrencySafe` → `Task.WhenAll`, writes after) but
  dispatches **sub-agents serially** (`AgentTool` extends `ToolBase`, which defaults
  both flags `false`, so a spawn lands in the serial write-group). GxPT can't do the
  intra-turn part — net35 has no `async`/`Task` and the loop is *deliberately serial*
  (`mcp35-toolloop-spec.md` §6) — so we take the **agent**-grain version instead:
  parallel read-only *children* (§9, A9). Same governing principle ("reads parallel,
  writes serial"), applied one level up — and our read-only fan-out is genuinely
  *more* concurrent than OpenMono's serial agents, which the read/write gate (A9) +
  per-connection mutex (A10) keep safe.
- **Agents are files, not hardcoded records.** OpenMono's agents are C# records in a
  static `BuiltInAgents.All` dictionary — type-safe and simple, but only the authors
  can add one. We keep the **markdown-file** model (the skills/Claude-Code convention,
  A4): bundled *and* user/project-authored, discovered at runtime, enabled per
  conversation (§7). The cost is a frontmatter parser (already ours, A5); the gain is
  user-extensibility, which for a skills-parity product is the whole point.
- **Global "plan mode"** as a session state that forces read-only. Our per-agent
  `max_tier: readonly` covers the same need for a *plan* agent without a new global
  mode; a conversation-level plan toggle is a possible separate feature, not part of
  the sub-agent system.

### Worth tracking (not yet adopted)
- **Context compaction inside a long-running child** (OpenMono: checkpoint at 65%,
  compact at 80%). GxPT has `/compact` + the context meter; wiring auto-compaction
  into a child that nears its window would help deep autonomous runs. Folds into the
  existing compaction machinery — a §11 phase-8/UI follow-on, not new architecture.
- **Artifact storage for large tool results** (>10 KB off to disk, referenced by
  handle) to keep a child's context lean — orthogonal to sub-agents but a clean
  companion to `FormatResult`'s existing length cap.
