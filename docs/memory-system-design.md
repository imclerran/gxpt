# Memory System — Design Proposal

**Status:** Design (proposal). No implementation yet.
**Branch:** `claude/sleepy-pascal-7Tiuo`
**Last updated:** 2026-06-04

A persistent, project-scoped memory for GxPT: the model can record durable facts
about a workspace and recall them in later conversations. Built as a first-party
MCP server for the tool surface, plus host-side injection of a light index.

---

## 1. Scope & goals

- **Project-scoped only.** Memory lives in a `.gxpt/` directory under the
  conversation's working folder. No global/cross-project tier — memory is
  available **only when the conversation has a working directory**.
- **Light always-on context + detail on demand.** A small primary index is
  injected every request; richer notes live in separate files the model reads
  by name when relevant (mirrors the `reveal_tools` progressive-disclosure model).
- **One tool pathway.** Reuse the existing MCP host, manifest, approval tiers,
  and routing — no parallel tool channel.

### Non-goals (initially)
- Global / user-level memory (`%AppData%`); cross-conversation sync; automatic
  summarization or eviction beyond a soft size cap.

---

## 2. Decision ledger

| # | Decision | Rationale |
|---|----------|-----------|
| M1 | **Standalone `MemoryMcpServer`** (stdio, workdir-scoped), not a loopback/in-process server | Reuses the tested `StdioTransport` + `McpServer` loop; process isolation + per-call timeout; just one more `NewBuiltIn(...)`. Disk is the source of truth, so in-process state sharing buys nothing. |
| M2 | **Host owns injection; server owns writes** | One-way contract over a known path (`.gxpt/`), exactly like the `GXPT_WORKDIR` convention. Host reads the primary file each request; the server is the only writer. |
| M3 | **Memory-semantic tools, not raw file I/O** | The server keeps the primary index consistent; the model supplies content, not file edits. Raw read/write would just re-skin `FilesMcpServer`. |
| M4 | **Name = handle, summary = description** | Each index entry is `name: summary` (+ optional `→ detail.md`). The caller supplies the **name** (a handle, max 5 words; normalized to **kebab-case** — lowercase `[a-z0-9]` words joined by hyphens, which is also the detail filename); the **summary** is a single line of prose and never a key. |
| M5 | **Single enable flag, surfaced in the main settings tab** | Memory is a feature (with context cost), not server plumbing. One `MemoryEnabled` bool gates **both** server launch and injection, so they can't desync. |
| M6 | **No memory text in the constant `AgentSystemPrompt`** | All memory framing lives inside the omittable injected block, so the OFF state leaves zero trace — no phantom capability. |
| M7 | **Compaction is model-driven; `consolidate` is a mechanical, no-LLM atomic replace** | The cap bounds the index, not detail files. The model prunes/consolidates; the server only nudges. `consolidate` just does the all-or-nothing file ops — keeping the server provider-free (M1/M2) and the merged content visible at approval. |

---

## 3. On-disk layout (`<workdir>/.gxpt/memory/`)

```
.gxpt/memory/
  memory.md        # primary index: light, injected every request
  <slug>.md        # detail files, read on demand
  .gitignore       # seeded automatically ("*") — personal memory not committed unless opted in
```

Memory lives in its own `memory/` subfolder of the shared `.gxpt/` project home,
alongside the `skills/` and `agents/` subfolders.

`memory.md` is a flat list of entries:

```
- auth-flow: JWT in httpOnly cookies; refresh via POST /auth/refresh  → auth-flow.md
- db-conventions: snake_case tables, soft-delete via deleted_at
- build-quirks: net35 servers built by the solution, deployed beside GxPT.exe
```

Each entry is one line: a `name` (caller-supplied handle, **max 5 words**) and a
**single-line** `summary`. The server normalizes the name to **kebab-case** (lowercase `[a-z0-9]` words joined
by single hyphens; word boundaries from whitespace, separators, and camelCase, so
`Auth Flow`, `authFlow`, and `auth_flow` all become `auth-flow`). That slug is the
canonical handle shown in `memory.md` and the detail filename (`<slug>.md`), so no
separate name→file map is needed.

Soft size cap on `memory.md`, **default 40 lines** and **user-configurable**; on
exceed the server nudges the model to compact (it never compacts on its own).

---

## 4. Tool surface (`MemoryMcpServer`)

- `remember(name, summary, detail?)` — **adds a new entry.** Appends a `name: summary`
  line to `memory.md`; if `detail` given, writes `<slug>.md` and links it. `name` is
  **required** (max 5 words), `summary` is a **single line**; the server normalizes the
  name to **kebab-case** and **rejects a slug that collides** with an existing entry (use
  `update_memory` to change one). *(Write tier — remember-eligible.)*
- `read_memory(name)` — returns a detail file's contents. *(ReadOnly.)*
- `update_memory(name, summary?, detail?)` — **replaces** the named entry's fields,
  it does **not** append. The provided `summary` overwrites that entry's line; the
  provided `detail` **fully overwrites** `<name>.md` (the caller must pass the
  complete new contents, not a fragment). Omitted fields are left unchanged; other
  entries are untouched. *(Write.)*
- `forget(name)` — remove an entry and its detail file. *(Write.)*
- `consolidate(names[], new_name, summary, detail)` — **atomic multi-entry replace**:
  writes the new `new_name` entry + `<slug>.md`, then removes every entry in `names[]`,
  all-or-nothing. Purely mechanical — it makes **no LLM calls**; the model composes
  `summary`/`detail` and passes them in (see §7). *(Write.)*

The append-vs-replace distinction between `remember` and `update_memory` must be
spelled out in each tool's `description` (what the model sees), so it never
overwrites when it means to add or vice versa.

Because `memory.md` is in context every turn, the names are always in front of
the model — it addresses entries by reading the name beside the summary it
recognizes. Writes are atomic (temp file + rename). The server is sandboxed to
`.gxpt/memory/` (reuse `PathSandbox`).

---

## 5. Context injection (host-side)

When memory is enabled and the conversation has a workdir, the orchestrator emits
`memory.md` as an **ephemeral, non-persisted system message**, rebuilt from disk
each request — the same pattern as the names manifest. Benefits: edits take effect
next turn; exports / message-editing / replay stay clean; no double-storage.

Order ephemeral system messages stable → volatile for prompt-cache reuse:

```
[system] AgentSystemPrompt        (constant)
[system] memory.md                (changes rarely within a turn)
[system] names manifest           (rebuilt each request)
...history
```

---

## 6. Enable / disable

A single `MemoryEnabled` bool, stored with the other first-party toggles in
`BuiltInOptions` (so `McpHost` launches the server through the normal path), but
its **toggle is rendered in the main settings tab**, not the MCP/tools tab. Two
consumers read the one value: server launch and injection gate.

- **ON:** server launched + registered (manifest lists its tools), `memory.md`
  injected.
- **OFF:** server not launched, no memory tools in the manifest, no injection —
  the model sees no memory tools and makes no memory claims.

Memory is not surfaced as an independent toggle in the MCP tab (avoids a second,
desync-prone switch); at most a read-only "managed in Settings" indicator.

---

## 7. Compaction

The 40-line cap constrains the **index** (`memory.md`), not the detail files
(read on demand, uncounted). So compaction reduces the number of index *lines*
while preserving as much information as possible. It is **model-driven**; the
server only **nudges** — when `memory.md` exceeds the cap it returns a notice
(e.g. on the next write) telling the model to compact. The server never compacts
on its own.

The model has two complementary moves:

- **Prune.** `forget` entries that are stale, superseded, or no longer earning
  their line. Often enough on its own, and intentionally lossy.
- **Consolidate.** Collapse several *related* entries into one. The new entry's
  **detail file holds the sub-memories as sections** (keyed by the original
  names as headings); the new index line is a single **rolled-up** summary. The
  enumeration of what was merged lives in the detail file's headings, **not** in
  the index line — that keeps the always-injected index light. Same
  progressive-disclosure pattern as index→detail, one level deeper.

Consolidation is the `consolidate(...)` tool: an **atomic multi-entry replace**
(write the merged entry, delete the originals, all-or-nothing). Its only value
over `remember` + N×`forget` is atomicity — it prevents the failure where the
model writes the merged entry but forgets to remove the originals, leaving the
index *larger*. It is **purely mechanical and makes no LLM calls**: the
orchestrating model — already in the loop, with the index in context and able to
`read_memory` the details — composes the rolled-up `summary` and merged `detail`
and passes them as arguments. Keeping the server free of provider coupling
(no API key, no network egress) preserves the M1/M2 sandbox boundary and keeps
the consolidated content visible in the approval prompt. *Tools are
deterministic effects; the model in the loop is the only thing that thinks.*

**Tradeoff:** consolidation trades per-fact addressability for index space — an
absorbed memory is no longer its own entry (edit it by rewriting the detail via
`update_memory`). That suits cold, related facts; leave hot, frequently-revised
memories as standalone entries.

---

## 8. Resolved

- **`forget` is Write tier** (remember-eligible), not Destructive.
- **Compaction is a server nudge** — the server warns when `memory.md` exceeds the
  soft cap; it never compacts on its own.
- **Soft cap defaults to 40 lines**, exposed as a user-configurable setting
  (alongside the `MemoryEnabled` toggle in the main settings tab).
- **`.gxpt/memory/.gitignore` is seeded automatically** (`*`) on first write.
- **Names are normalized to kebab-case** (lowercase, hyphen-joined; camelCase/underscore/
  whitespace boundaries); the slug is the index handle and the `<slug>.md` filename, so
  no name→file map is needed.

---

## 9. Implementation status

Built (branch `claude/sleepy-pascal-7Tiuo`):

- **`servers/MemoryMcpServer/`** — standalone stdio server: `Slug`, `MemoryStore`
  (canonical rewrite, atomic temp+replace writes, auto-seeded `.gitignore`),
  `MemoryTools` (the five tools), reads `GXPT_WORKDIR` + `GXPT_MEMORY_MAX_LINES`.
- **Host wiring** — `McpConfig` (`MemoryName`, `MemoryEnabled`, `MemoryMaxLines`,
  workdir-scoped spec + cap env), `ToolClassifier` (read = ReadOnly, the rest =
  Write), `McpChatOrchestrator.MemorySystemMessageProvider` injected stable→volatile,
  `MemoryInjection` (host-side read of `memory.md`), `MainForm` wiring.
- **Build/deploy** — added to `GxPT.sln`, the `AfterBuild` copy, and the setup
  `.vdproj`.

Configured via `settings.json` keys **`mcp_memory_enabled`** (default `false`) and
**`mcp_memory_max_lines`** (default `40`), persisted in `SettingsData`. Surfaced in
the **main settings tab** (`tblSettings`) as an enable checkbox + a size-limit
spinner (the spinner greys out when memory is off), and still editable via the JSON
editor.

Approval: `read_memory` auto-allows by the **ReadOnly tier** (the approval policy now
allows any ReadOnly-tier tool by tier, not a name list); the write tools are Write
tier (prompt once, remember-able).

**Note:** these projects target .NET 3.5 / WinForms and must be built in Visual
Studio on Windows — build the **whole solution** so `MemoryMcpServer.exe` is compiled
and copied to `mcp-servers\`. The source here is convention-matched but unverified by
a compiler.
