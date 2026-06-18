# Skills System — Design Proposal

**Status:** Design (proposal). No implementation yet.
**Branch:** `claude/inspiring-fermat-zHNpw`
**Last updated:** 2026-06-08

On-demand, progressive-disclosure **capabilities** for GxPT: a skill is a folder
of markdown instructions (plus optional bundled files/scripts) that the model can
pull into context when a task calls for it. Skills are *know-how and procedure*
("how to do X well here"), distinct from MCP tools, which are *effects* ("read a
file", "run a command"). They compose: a skill body can tell the model to
`reveal_tools` and call specific tools in a specific order.

The whole design rides GxPT's existing principle — **progressive disclosure to
bound token cost** — which the MCP layer already implements for tools via a
names-only manifest + `reveal_tools` (`mcp-architecture.md` §7). Skills are the
same idea one level up.

---

## 1. Scope & goals

- **Three skill sources: bundled + project + user-global.** First-party skills ship
  beside `GxPT.exe`; project skills live under the conversation's working folder;
  user-global skills live under `%AppData%/GxPT/skills/` (shipped — see S2/S16).
- **Three disclosure levels.** Only the skill **name + one-line description** is
  always in context (the manifest); the **`SKILL.md` body** loads on demand; the
  **bundled files/scripts** are read/run only when used.
- **Host-native catalog, sandboxed execution.** Discovery, `open_skill`, and asset
  reads (`read_skill_file`) live in the GxPT host (host-synthesized meta-tools, like
  `reveal_tools`). Running a skill's bundled **script** goes through a dedicated,
  sandboxed `SkillsMcpServer` (the skills analogue of `CommandMcpServer`),
  always-confirm.
- **Skills can ship runnable scripts.** A bundled `.bat`/`.cmd` entry (which may wrap
  a sibling `.py`/`.ps1`/`.exe`) is invoked **by handle** — `(slug, relpath)`, never
  an absolute path — and runs in the **workspace**, with its own folder reachable
  **read-only** for assets.
- **Two trigger paths into one catalog.** The model can open a skill autonomously
  (`open_skill`), and the user can drive it explicitly with a slash command. Both
  resolve against the same `SkillCatalog`.
- **No settings-page UI.** Skill management (global + per-skill on/off) is done with
  slash commands, scoped per conversation. Bundled skills "just work" by default.

### Non-goals
- A marketplace / remote skill install; a per-skill settings-page UI. (User-global
  skills and the skills-authoring tool surface were initially non-goals but are now
  shipped — see S2 and S16.)

---

## 2. Decision ledger

| # | Decision | Rationale |
|---|----------|-----------|
| S1 | **Catalog & reads are host-synthesized meta-tools** (`open_skill`, `read_skill_file`), not a server; **only execution gets a server** (S11) | Discovery and asset reads are just disk reads, so a stdio process buys nothing — they ride the host registry like `reveal_tools`, never routed to a connection (D11). Spawning a *script* is the one concern that genuinely needs process isolation + a per-call timeout, so it (and only it) is a server. |
| S2 | **Bundled + project scope at launch** (`<exe>/skills/`, `<workdir>/.gxpt/skills/`, and `%AppData%/GxPT/skills` user-global) | Bundled "starter" skills are the main draw, so unlike memory (project-only by choice) skills lead with bundled. Project skills reuse the `.gxpt/` convention the memory system established. |
| S3 | **Model-initiated *and* slash-command triggers**, over one catalog | `open_skill` matches GxPT's `reveal_tools` precedent (the model picks from the manifest mid-task); the slash command gives the user a deliberate "run this now" entry point. Both pre-/open the same `SKILL.md`, so there is one load path, not two. |
| S4 | **`SKILL.md` = minimal frontmatter + markdown body**, parsed by a hand-rolled reader | net35 has no YAML parser and the repo keeps exactly one JSON lib (Newtonsoft, D3). A ~20-line `--- … ---` reader for `name`/`description` keeps authoring familiar to Claude Code skills without a new dependency or a JSON sidecar. |
| S5 | **Folder name = slug = handle**; reuse the memory system's kebab-case `Slug` | No name→path map needed (memory M4). `<workdir>/.gxpt/skills/<slug>/SKILL.md`; the slug is what appears in the manifest, what `open_skill` takes, and what the slash command types. |
| S6 | **No skills UI in `SettingsForm`; enablement is driven entirely by slash commands** at two scopes — **global default** and **this conversation** — default **all-on** | Skills are a discoverable capability, not a privacy-sensitive opt-in like memory, so they default on and need no settings toggle. Global gives an app-wide default; per-conversation matches the `Zdr`/`WorkingDir` grain (different tabs, different skill sets). |
| S10 | **Enablement resolves by "most specific setting wins"** — a 5-rung ladder (skill-here > skill-global > feature-here > feature-global > default-on), **no hard feature gate** (§7). Both the global and conversation per-skill layers are **tri-state** (inherit/on/off); global lives in `skills.json`, conversation on the `Conversation` | A per-skill rule must beat a feature-wide one (specificity) and a conversation rule beat a global one (scope); a hard "feature off" gate violated this (a per-skill *on* couldn't survive `/skills off`). Tri-state on **both** levels so `/skill X on global` can force-on over a global `/skills off`. Dedicated `skills.json` keeps global state out of `settings.json`/the settings form (S6). |
| S7 | **`open_skill` returns the body as the tool result** (not a separate ephemeral injection) | Returning through the normal tool-loop lands the body in history naturally and keeps it available for the rest of the task — no new injection channel. The always-on **manifest** still rides as an ephemeral system message (S8). |
| S8 | **Manifest injected as an ephemeral system message**, in the stable→volatile stack | Same treatment as the names manifest and the memory block: rebuilt each request from the live catalog, never persisted. Cheap (names + one-liners), so it sits in front of history every turn when skills are enabled for the conversation. |
| S9 | **Skill scripts run via a dedicated `run_skill_script(slug, relpath, args[])` tool, *not* `command__run`** | A skill should not make the model compose an arbitrary command or name a path in the user's Program Files. The handle-based tool shrinks the exec surface from "any command string" to "the declared entry of a named skill + literal args" — smaller, auditable, still gated (Destructive/always-confirm). |
| S11 | **Execution lives in a first-party `SkillsMcpServer`** (stdio, launched with the skill roots in its env), reusing `Mcp35.Server`'s process-exec helper | **Decisive reason:** the process-spawn + cmd.exe arg-quoting helper (the `^`/`%` footgun) lives in `Mcp35.Server`, and the host references only `Mcp35.Client` — one-way seam (D8) — so a host meta-tool can't reuse it without breaking the boundary or duplicating it; a server consumes it like the other six do. Plus **process isolation** (arbitrary author code stays out of the WinForms UI process; a wedged server is killable while the UI stays responsive), the existing **per-call timeout + Destructive/remember approval path** for server tools (S15), and the **M1 precedent** (`MemoryMcpServer` chose a server for *weaker* reasons). It's the skills analogue of `CommandMcpServer`. Catalog/reads stay host-native because they're in-process-safe disk reads with no child process, timeout, or quoting hazard — nothing to isolate (S1). |
| S12 | **Entry points are `.bat`/`.cmd` only**; `.py`/`.ps1`/`.exe` are reached *through* the batch via `%~dp0` | One uniform, validatable entry type; the author owns interpreter selection (and graceful failure when it's absent). `%~dp0` (the batch's own dir at runtime) locates bundled siblings install-path-independently, so nothing hardcodes where `<exe>` landed. A user-authored skill with Python/PS installed ships a `.bat` wrapper calling its sibling script. |
| S13 | **Assets are read by handle via `read_skill_file(slug, relpath)`** (ReadOnly), not `files__*`; resolves against **any discovered skill, not just enabled** | `files__*` is hard-sandboxed to the workspace and rejects absolute paths, so it can't reach a bundled skill's files. A relative-handle read tool resolves `slug`+`relpath` host-side (within the skill root), works for bundled *and* project skills uniformly, and auto-allows by the ReadOnly tier — mirroring `read_memory`. It spans the whole catalog (not just the enabled set, which `open_skill` uses) so the authoring flow can read a *disabled* skill's files to edit it. |
| S14 | **cwd is always the workspace; the skill dir is an asset source (by convention read-only), never a working/output dir** | Skills operate on and write to the user's project, not their own install location (which, for bundled skills, is often non-writable Program Files anyway). `%~dp0` / `GXPT_SKILL_DIR` locate assets; all output goes to cwd. **"Read-only" is a convention, not an enforced sandbox** — a spawned `.bat` runs with the user's rights and can write anywhere; the real boundary is the Destructive always-confirm gate (S15), so the user sees and approves each run. `run_skill_script` therefore requires a workspace (like `command__run`); a workspace-less self-contained skill would use a temp scratch cwd — deferred, never the skill dir. |
| S15 | **`run_skill_script` is Destructive but remember-eligible by exact `(slug, relpath)`**; pipelines live *inside* the script | Because the surface is a fixed declared entry (not an arbitrary string), a remembered approval is narrow and auditable — *less* friction than `command__run` while *more* locked down. Args are shown each run but aren't part of the remembered grain. Output filtering belongs inside the batch (`… | findstr …`); a shell pipeline in the tool call would re-import the arbitrary-shell surface the tool exists to remove. |
| S16 | **Skill *authoring* is a dedicated writer surface on `SkillsMcpServer`** (`create_skill`/`write_skill_file`/`update_skill`, then tier-2 `edit_skill_file`/`list_skill_files`/`delete_*`/`validate_skill`), **not `files__*`**, with a `scope` arg (project/user) from day one | Dedicated tools give skill-aware validation (structured frontmatter → a guaranteed-loadable `SKILL.md`) and extend to the `%AppData%` user-global root that workspace-sandboxed `files__*` can't reach (§5). Writes are Write-tier (by `(scope, slug)`); `delete_*` Destructive; writing a `.bat` is only Write — running it stays the `run_skill_script` gate. Powers a bundled `skill-writer` skill (the writer role memory has via `MemoryMcpServer`). |

---

## 3. On-disk layout

### Bundled (first-party, ships beside the exe)
```
<GxPT.exe dir>/skills/
  <slug>/
    SKILL.md             # frontmatter + body
    scripts/
      gen.bat            # entry point (.bat/.cmd only) — may wrap a sibling script
      gen.py             # reached via %~dp0, never invoked directly
    template.md          # read on demand via read_skill_file
    examples/
```
Deployed by the same `AfterBuild` copy that places `mcp-servers\` (and seeded into
the setup `.vdproj`), so bundled skills travel with the install.

### Project (scoped to the conversation's working folder)
```
<workdir>/.gxpt/skills/
  <slug>/
    SKILL.md
    <bundled files…>
```
Reuses the memory system's `.gxpt/` home. Project skills **shadow** a bundled skill
of the same slug (precedence **project > bundled**).

### `SKILL.md` format
```markdown
---
name: Release Notes
description: Draft release notes from the git log since the last tag.
---

1. Use the git tools to list commits since the most recent tag.
2. Group by type (feat/fix/docs/…), one short summary line per group.
3. Fill in `./template.md`; see `./examples/` for tone.
```
- Frontmatter is a leading `--- … ---` block of `key: value` lines; only `name`
  and `description` are read initially (unknown keys ignored, forward-compatible).
- `description` is a **single line** — it is what the model sees in the manifest, so
  it should read as "use this when…".
- The body and any sibling files are Level-2/3 content, paid for only on open.
- **Reference bundled assets by *relative* path only — never absolute.** The install
  dir varies per machine, so the body names assets relatively (`scripts/gen.bat`,
  `template.md`); the host resolves them to this machine's location on open (§5), and
  scripts find their own siblings via `%~dp0` at runtime.

---

## 4. Disclosure levels

| Level | Content | When it enters context | Cost |
|-------|---------|------------------------|------|
| 1 | `slug` + `description` (the **manifest**) | every request, while skills are enabled for the conversation (S8) | tiny |
| 2 | the **`SKILL.md` body** | when `open_skill(slug)` is called (by model or slash) (S7) | medium |
| 3 | **bundled files/scripts** | files read via `read_skill_file` (S13); scripts run via `run_skill_script` (S9/S11) | paid on use |

---

## 5. Host pieces

### Catalog & reads (host-native, `Services/Skills/`)

```
McpChatOrchestrator (RunTurn loop)
  ├─ ephemeral system stack (stable → volatile):
  │     AgentSystemPrompt
  │     WorkspaceSystemMessage(workdir)
  │     [memory.md block]
  │     [SKILLS MANIFEST]          ← new (S8): "slug — description" per enabled skill
  │     names manifest
  │     ...history
  │
  └─ exposed tools: reveal_tools, open_skill(names[]), read_skill_file(slug, relpath),
                    run_skill_script(slug, relpath, args[]), …revealed tools
```

- **`SkillCatalog`** — scans bundled + project skill folders, parses each frontmatter
  into `(slug, description, path)`, applies project-over-bundled shadowing, and
  produces `SkillsManifestSystemMessage(enabledSet)` and `slug → path` resolution.
  Pure logic (no WinForms) → net48 linked-source tests.
- **`open_skill(names[])`** (host-synthesized, like `reveal_tools`) — returns, per
  named skill: the **`SKILL.md` body** (authored with *relative* asset paths), the
  **resolved absolute skill directory on this machine**, and a **listing** of bundled
  assets (relative path → resolved absolute). The author writes once, portably; the
  host supplies the machine-specific location, so no absolute path is ever baked into
  the markdown. Batch (plural) to resolve ambiguity by opening candidates.
- **`read_skill_file(slug, relpath)`** (host-synthesized, **ReadOnly**, S13) — reads a
  Level-3 asset by relative handle, resolved within the skill root (`PathSandbox`
  semantics against that root: no absolute, no `..` escape). Works for bundled and
  project skills alike; auto-allows by tier like `read_memory`. Keeps `open_skill`'s
  result a *listing*, so big templates aren't dumped into context.
- **Optional LRU cap** on how many full bodies persist, reusing the `reveal_tools`
  reveal-cap machinery — only if skill bodies start to crowd context. Not needed
  initially.

### Execution — `SkillsMcpServer` (S11)

A first-party stdio server, launched with the skill roots (bundled dir +
`<workdir>/.gxpt/skills`) in its environment, exposing one tool:

```
run_skill_script(slug, relpath, args[])
```

**Locking, before anything runs:**
1. **slug → skill root** via the catalog; reject unknown or disabled skills.
2. **relpath → file**, resolved within the root (`PathSandbox`: no absolute, no `..`
   escape, no drive/UNC). Must stay inside the root.
3. **Extension allowlist:** `.bat`/`.cmd` only (S12). (Optionally require entries
   under a conventional `scripts/` subdir.)
4. **args** passed as literal argv tokens (`%1..%N`), each quoted host-side — no shell
   metacharacters from the model are honored. *(cmd.exe + batch arg quoting is a known
   footgun — `^`/`%` handling — so this rides a tested quoter, ideally
   `CommandMcpServer`'s.)*

**Runtime contract (S14):**
- **cwd = the workspace** — what the skill operates on and writes to.
- **`%~dp0` / `GXPT_SKILL_DIR` = the skill's own folder** — read-only asset source,
  located install-path-independently (`%~dp0` derives from the absolute path the host
  invokes, not from cwd, so the two never collide). Plus `GXPT_WORKDIR`,
  `GXPT_SKILL_SLUG`.
- Returns exit code + stdout + stderr (capped like other tool results).
- **Requires a workspace** (like `command__run`); no workspace ⇒ the tool isn't
  resolvable (workspace-less self-contained skills → temp scratch cwd, deferred).

### Authoring — `SkillsMcpServer` (S16)

The same server hosts the **writer** surface, so the model can create and maintain
skills (powering a bundled `skill-writer` skill — the writer role memory has via
`MemoryMcpServer`). **Dedicated tools, not `files__*`** — chosen for skill-aware
validation and because they extend to the `%AppData%` user-global root that the
workspace-sandboxed `files__*` can never reach. All writes target the **writable**
roots only (project `<workdir>/.gxpt/skills` and user-global `%AppData%/GxPT/skills`);
the bundled install dir is never a write target.

Every write tool takes a **`scope`** arg — `project` (default) | `user` (`%AppData%`,
shipped). The writer errors only if the relevant root isn't configured (e.g. project
scope with no workspace), not because a scope is unimplemented.

**Tier 1 — the create flow:**
```
create_skill(scope?, slug, name, description, body)   -- new skill; refuse if it exists;
                                                         assembles VALIDATED frontmatter from fields
write_skill_file(scope?, slug, relpath, content)      -- supporting reference files / scripts
update_skill(scope?, slug, name?, description?, body?) -- structured edit of the main file (null = unchanged)
```
**Tier 2 — maintenance:**
```
edit_skill_file(scope?, slug, relpath, old_string, new_string)  -- string-replace (files__edit parity);
                                                                   for SKILL.md the replace is confined to
                                                                   the BODY + frontmatter re-assembled
                                                                   (name/description stay update_skill's)
list_skill_files(scope?, slug)                                  -- ReadOnly enumerate (spans disabled)
delete_skill_file(scope?, slug, relpath) / delete_skill(scope?, slug)
validate_skill(scope?, slug)                                    -- ReadOnly: does the SKILL.md load? (its own tool,
                                                                   so supporting files/scripts aren't validated)
```

**Rules:**
- **Structured frontmatter:** `create_skill`/`update_skill` take `name`/`description`
  as *fields* (not raw markdown); the server assembles a guaranteed-loadable `SKILL.md`.
  This is the whole reason for dedicated tools.
- **Sandbox & text-only:** `relpath` resolved within the skill folder (same containment
  as `read_skill_file`); `content` is a string, so only text files are authorable
  (`SKILL.md`, refs, `.bat`/`.ps1`/`.py`) — binaries are shipped, never authored.
- **Encoding (server-owned):** UTF-8 **no BOM** for `.md`; **CRLF** for `.bat`/`.cmd`
  (XP `cmd.exe`); atomic writes (FileSafe-style).
- **Approval:** writes are **Write** tier, remember-eligible by **`(scope, slug)`**;
  `delete_*` is **Destructive**. Writing a `.bat` is only Write — *running* it is the
  separate Destructive `run_skill_script` gate, so a freshly-written script can't auto-run.
- **No validation on `write_skill_file`** — supporting files/scripts don't need it;
  `validate_skill` is a separate ReadOnly check (and `create_skill`/`update_skill`
  validate the main file they assemble).
- **Refresh timing:** a created/edited skill appears on the **next** user turn (the host
  re-scans per send), so no mid-turn `create_skill → open_skill`; the tool result says so.
- **Authoring spans all writable skills regardless of enablement** (you'll edit
  disabled skills) — paired with the relaxed `read_skill_file` (S13: any *discovered*
  skill, not just enabled).
- **Dual-mode launch (no workspace required for global authoring).** The server is
  workdir-scoped, but the spec is flagged `RunsWithoutWorkdir`, so the host *also*
  launches one workdir-less instance (no `GXPT_WORKDIR`). The registry's existing
  per-workdir resolution routes a folder turn to that folder's instance and a
  folderless turn to the workdir-less one. Each instance tailors its surface to what it
  can do: **with a workspace** → full set, `scope` defaults to `project`; **without** →
  authoring tools only (no `run_skill_script`, which needs a cwd), `scope` defaults to
  `user`. So `/use-skill`-ing skill-writer in a folderless conversation can still author
  user-global skills, while project authoring + `run_skill_script` stay workspace-bound.
- **Deferred:** `rename_skill` (folder rename — delete+recreate covers it for now).

---

## 6. Slash commands (on the existing `ISlashCommand` framework)

GxPT now has a slash-command framework (`Services/Commands/`: `ISlashCommand`,
`SlashCommandRegistry`/`Processor`, `commands.json`, gating, autocomplete) — so
skills register as **commands on it**, not a new router (this supersedes the
original `SlashCommandRouter` plan). The management commands are `Client` kind (run
locally, no LLM send — `WriteInfo` + `Handled()`); `/use-skill` is also `Client` but returns
`Send(...)` with a short ask while attaching the body as hidden context (below).
Registered in `SkillCommands.BuiltIns()` alongside the other built-ins.

| Command | Kind | Effect |
|---------|------|--------|
| `/list-skills` | Client | List skills with effective on/off state and source (global default vs. this conversation) |
| `/toggle-skills <here\|global> <on\|off\|inherit>` | Client | Set the **whole feature** at that scope. `global` takes only `on\|off` (it is the most upstream layer); `here` also takes `inherit` (unset → follow global) |
| `/toggle-skill <slug> <here\|global> <on\|off\|inherit>` | Client | Set **one skill** at that scope (both scopes tri-state, so both take `inherit`); bare `/toggle-skill <slug>` toggles for this conversation |
| `/reset-skills <here\|global>` | Client | Clear skill settings at that scope: `here` drops every conversation override (its feature on/off and all per-skill); `global` removes every per-skill global override. Neither touches the global on/off master (only `/toggle-skills global on\|off` does) |
| `/use-skill <slug> [text]` | Client | **Invoke**: resolve `<slug>`, carry its rendered `SKILL.md` block on the result as `SystemContext` (committed to history as a **hidden system message** at the send, so an early return can't orphan it), and send the short user ask `Use the <slug> skill. [text]` |

- **Scope-first, required.** The scope (`here`/`global`) is the first token and picks the
  layer the change lands on; the verb follows. This lets autocomplete offer only the verbs
  that layer accepts — `global` on the feature toggle is `on|off` (no `inherit`, since the
  global feature flag is a plain bool with nothing upstream to inherit from), every other
  layer adds `inherit`. `global` edits `skills.json`.
- **`inherit` vs. `/reset-skills`.** `inherit` unsets a *single* layer so it falls through
  to the one upstream. The bulk "clear the overrides at this scope" action lives in
  `/reset-skills <here|global>` — that is where the old `reset` verb's behavior moved. It
  clears per-skill overrides (and, for `here`, the conversation feature toggle) but never the
  global on/off master, which only `/toggle-skills global on|off` changes.
- **Invocation is `/use-skill <slug>`**, not a per-skill `/<slug>` command — the framework's
  registry is built once at startup, so one command per dynamically-discovered skill
  doesn't fit (decision in §11). `/use-skill` attaches the rendered skill body as a **hidden
  system message** (sent to the model, never shown in the transcript — the same channel
  `/compact` uses) and sends only a short `Use the <slug> skill. [text]` user message, so
  it pre-loads without an `open_skill` round-trip and without dumping the body into the
  transcript. It works **regardless of enablement**, since it's an explicit user action.
  The model's autonomous `open_skill` path remains the primary, manifest-gated route.
- **Conversation overrides vs. global:** management commands read/write the
  per-conversation tri-state through `ISlashCommandContext`
  (`Get/SetConversationSkill…`), persisted on the `Conversation`; `global`-scope
  changes go straight to `SkillEnablement` (`skills.json`). Both take effect on the
  next message (the manifest/enabled set is recomputed per send).
- **Autocomplete:** `/toggle-skill` and `/use-skill` complete slugs (annotated with state)
  via `IArgumentCompleter`; `/toggle-skills`/`/toggle-skill` then complete `here|global`
  and, scoped to that choice, `on|off[|inherit]`. Each level's accepted value carries a
  trailing space so the popup advances to the next level immediately (no manual space
  needed). Command-name completion is hyphen-aware: a typed prefix anchors at the start of
  a name or just after any `-`, so `/skill` surfaces `/toggle-skill`, `/toggle-skills`, etc.

---

## 7. Enablement & persistence

Four independently-settable controls (a 2×2 of **all-skills / one-skill** × **global /
this-conversation**), resolved per request by **"most specific setting wins"** — there
is **no hard feature gate** (the old gate let a general rule override a specific one,
which was the bug). Everything defaults to **on**.

### The controls (2×2)
|  | **Global** (`skills.json`) | **This conversation** (`Conversation`) |
|---|---|---|
| **All skills** | `/toggle-skills global on\|off` → `feature_off` | `/toggle-skills here on\|off\|inherit` → `SkillsFeatureOff` |
| **One skill** | `/toggle-skill X global on\|off\|inherit` → `skills[X]` | `/toggle-skill X here on\|off\|inherit` → `SkillOverrides[X]` |

Three of the four cells are **tri-state** (set / unset = inherit); `inherit` clears that one
cell so it falls through to the next rule. The exception is **all-skills / global**
(`feature_off`), a plain bool that is the most upstream layer — it is always `on` or `off`,
with no `inherit`. Clearing a whole scope at once is `/reset-skills <here|global>`.

### Resolution ladder (effective state of skill X in conversation C)
Take the first rule that has been set:

| # | Rule | Source |
|---|------|--------|
| 1 | this skill, **here** | `Conversation.SkillOverrides[X]` |
| 2 | this skill, **global** | `skills.json` `skills[X]` |
| 3 | all skills, **here** | `Conversation.SkillsFeatureOff` |
| 4 | all skills, **global** | `skills.json` `feature_off` |
| 5 | **default: ON** | — |

Two sub-rules generate the ladder: a **per-skill** rule beats a **feature-wide** rule
(specificity), and within a level **this conversation** beats **global** (scope). The
one deliberate tiebreak: rung 2 (skill-global) beats rung 3 (feature-here) — an
explicit per-skill setting isn't resurrected/killed by a blanket toggle in the other
scope.

### Persistence
- **Global** — `%AppData%/GxPT/skills.json`, written only by `… global` commands:
  ```json
  { "feature_off": false, "skills": { "noisy-skill": false, "always-pirate": true } }
  ```
  `skills` is tri-state (slug → bool; absent = inherit). An older `"disabled": [..]`
  array is read back-compat as force-off entries.
- **Conversation** — `SkillsFeatureOff` (`bool?`) + `SkillOverrides` (`slug → bool`)
  on the `Conversation`, round-tripped by `ConversationStore` (absent = inherit/empty).

Per-tab by construction; **no `settings.json` key, no checkbox** — all control is the
§6 slash commands. The `/list-skills` list shows each skill's effective state **and the rung
that decided it** (e.g. `on here`, `off globally`, `all skills off here`, `default`),
under a `Default: ON globally · OFF here` header naming the feature toggle (rungs 3–4) —
the default for any skill with no per-skill setting.

---

## 8. Security & approval

- **A loaded `SKILL.md` body is injected instructions.** Bundled (first-party) and
  project-local skills are effectively author-trusted (a project skill carries the
  same trust as the repo the user opened). Treat skill content as untrusted input at
  the parse boundary regardless — the posture the architecture takes toward tool
  output (mcp-architecture §9) — and when user-global / shared skills land, gate a
  first open with the approval prompt so the user sees what's being loaded.
- **Execution is a *narrowed* surface, not `command__run` (S9).** `run_skill_script`
  lets the model run only the **declared entry of a named skill** with **literal
  args** — it cannot compose an arbitrary command or name a path. It is **Destructive
  (always-confirm)** but **remember-eligible by exact `(slug, relpath)`** (S15): a
  remembered allow is narrow and auditable, so it's *less* friction than `command__run`
  while *more* locked down. The prompt is human-readable ("skill `release-notes` wants
  to run `scripts/gen.bat --since v1.2` — *bundled*") and distinguishes bundled vs.
  project source.
- **Skills never operate on their own dir (S14).** cwd is the workspace; the skill
  folder is reached read-only for assets. Bundled skills typically sit in
  non-writable Program Files (an OS-level backstop), but the *write-only-to-workspace*
  rule is the real contract, since project skills live in the writable workspace tree.
- **No path or interpreter handling by the model.** `(slug, relpath)` handles plus the
  batch-only `%~dp0` wrapper convention (S12/S13) keep absolute paths and interpreter
  selection entirely host/author-side — the model never pastes a Program Files path
  into a shell.

---

## 9. Testing strategy

Same dual-world pattern as the rest of the repo (net48 linked-source via
`dotnet test`):

- **`SkillCatalog` + frontmatter parser** — discovery, project-over-bundled
  shadowing, malformed frontmatter, manifest assembly for a given enabled set.
- **Skills slash commands** (`SkillCommands`, against a fake `ISlashCommandContext`) —
  `/list-skills` list; `/toggle-skills` and `/toggle-skill <slug>` set on/off/inherit across
  `here`/`global` scopes (scope-first); `/reset-skills` bulk-clears each scope (leaving the global
  on/off master alone); `global inherit` on the feature toggle is rejected; conversation overrides vs.
  `skills.json`; bare-slug toggle; per-scope verb completion; unknown slug/scope failures;
  `/use-skill` attaches the body as hidden context + sends a short ask, and works even when disabled.
- **Conversation override persistence** — `SkillsFeatureOff` + `SkillOverrides`
  round-trip through `ConversationStore`; missing fields default to inherit/empty.
- **Enablement resolution** — the two-layer precedence: conversation override beats
  global default; **force-on over a global-off**; `reset` falls back to inherit;
  newly-added slugs default on at both layers.
- **Host loop (`GxPT.Tests`)** — `open_skill` resolution + body-as-tool-result (body +
  resolved skill dir + asset listing); manifest injected in the correct stable→volatile
  slot and reflecting the resolved enabled set; feature-off (either layer) ⇒ no
  manifest and no `open_skill` exposed; pre-opened skill equals a model `open_skill`.
- **`read_skill_file`** — relative-handle resolution within the skill root; rejects
  absolute / `..`-escape / drive paths; works for bundled and project skills.
- **`SkillsMcpServer` / `run_skill_script`** — slug+relpath resolution and sandbox
  rejection; `.bat`/`.cmd` extension allowlist; literal-arg quoting (no shell-metachar
  injection); cwd = workspace while `%~dp0` / `GXPT_SKILL_DIR` point at the skill root;
  unknown/disabled slug rejected; no-workspace ⇒ unresolvable.

---

## 10. Phasing / roadmap

1. **`SkillCatalog` + frontmatter parser** + bundled-skill discovery (pure, TDD).
2. **Manifest injection** in the orchestrator's ephemeral stack, gated by the
   per-conversation enabled set.
3. **`open_skill` meta-tool** (sibling to `reveal_tools`): body + resolved skill dir +
   asset listing as the tool result.
4. **`read_skill_file` meta-tool** (ReadOnly) for Level-3 assets.
5. **Skills slash commands** on the existing `ISlashCommand` framework
   (`SkillCommands`: `/list-skills`, `/toggle-skills`, `/toggle-skill <slug>`, `/reset-skills`, `/use-skill <slug>`) + per-conversation
   override fields on `Conversation`/`ConversationStore` + global `skills.json`
   enablement. *(Split: 4a = enablement core + gating; 4b = the commands.)*
6. **`SkillsMcpServer` scaffold + `run_skill_script`**: batch-only entry, handle
   resolution + sandbox, literal args, cwd = workspace, always-confirm
   (remember-eligible by `(slug, relpath)`). Added to `GxPT.sln`, the `AfterBuild`
   copy, and the setup `.vdproj` (like `MemoryMcpServer`).
7. **Authoring tools on `SkillsMcpServer`** (S16): **tier 1** (`create_skill`,
   `write_skill_file`, `update_skill`) — the create flow — then **tier 2**
   (`edit_skill_file`, `list_skill_files`, `delete_*`, `validate_skill`). `scope`
   arg, structured frontmatter, Write/Destructive tiers.
8. **Bundled skills + deploy**: ship first-party skills via a `skills/` source folder
   copied next to the exe (`AfterBuild` + setup `.vdproj`) — including the
   **`skill-writer`** skill, which depends on phase 7.
9. **(Optional/later)** user-global (`%AppData%`) skills + the `scope=user` path; LRU
   cap on opened bodies; the **ephemeral opened-bodies** model (unload `open_skill`
   bodies on disable, instead of persisting them as tool results — see §11).

---

## 11. Open / soft decisions

**Resolved (2026-06-08):**
- ~~`open_skill` host meta-tool vs. `SkillsMcpServer`~~ → catalog & reads are host
  meta-tools; execution is a `SkillsMcpServer` (S1/S11).
- ~~Scope~~ → bundled + project + user-global (`%AppData%`) all shipped (S2).
- ~~Trigger model~~ → model-initiated (`open_skill`) **and** slash command (S3).
- ~~Slash commands: new router vs. existing framework~~ → built as `ISlashCommand`s on
  `main`'s framework (§6); the `SlashCommandRouter` plan is dropped.
- ~~User-initiated invocation under the static registry~~ → a single `/use-skill <slug>`
  command (body attached as hidden context, short ask sent), not per-skill `/<slug>`
  commands (§6).
- ~~Per-skill settings UI~~ → dropped; enablement is slash-command driven, default
  all-on (S6).
- ~~Whether enablement should be promotable to an app-wide default~~ → yes: two
  scopes, `global` (a dedicated `skills.json`) and `here` (the conversation), with
  the conversation overriding the global default (S6/S10, §6/§7).
- ~~How the model invokes bundled scripts~~ → `run_skill_script(slug, relpath,
  args[])` in a sandboxed `SkillsMcpServer`, batch-only entry, handle-based (no
  absolute paths), cwd = workspace (S9/S11–S15, §5/§8).
- ~~Reading bundled assets `files__*` can't reach~~ → `read_skill_file(slug, relpath)`,
  ReadOnly, host-resolved within the skill root (S13).
- ~~Per-machine install path in authored markdown~~ → never author absolute paths;
  `open_skill` returns the resolved skill dir + asset listing, and `%~dp0` locates
  bundled siblings at runtime (S12, §5).
- ~~Skill authoring: dedicated tools vs. reuse `files__*`~~ → dedicated writer tools on
  `SkillsMcpServer` (extends to `%AppData%` which `files__*` can't reach; skill-aware
  validation), `scope` arg from day one, tier 1 then tier 2 (S16, §5).
- ~~`read_skill_file` scope~~ → relaxed to **any discovered skill** (not just enabled)
  so authoring can read a disabled skill to edit it (S13). *Implemented.*
- ~~`validate_skill` baked into writes vs. separate~~ → a separate ReadOnly tool, since
  supporting files/scripts don't need frontmatter validation (S16).
- ~~Line-by-line skill edits~~ → frontmatter edits go through structured `update_skill`;
  a `files__edit`-style `edit_skill_file` (string-replace) is tier 2 for body/large
  files (S16).

**Deferred:**
- User-global (`%AppData%`) skills + the `scope=user` write path; precedence
  (`project > user > bundled`).
- LRU cap on opened bodies, and the **ephemeral opened-bodies** model (unload on
  disable rather than persisting `open_skill` results in history).
- Workspace-less skill execution via a temp scratch cwd (a workspace is required
  today, S14).
- Non-batch direct entry types (`.exe`/`.py`/`.ps1`); today they're reached only
  through a `.bat`/`.cmd` wrapper (S12).
- `rename_skill` (folder rename); delete + recreate covers it for now.
