# Plugin Required-Tools — Design Spec

**Status:** Design (approved). No implementation yet.
**Branch:** `claude/bulk-skill-agent-import-ti6e0r`
**Last updated:** 2026-06-26

## Summary

A `.gxpl` plugin bundles skills and agents. Agents declare the MCP tools they
use (`tools:` frontmatter, capped by `max_tier`), but those tools — `msbuild`,
PowerShell (`command__pwsh` / `command__powershell` / `command__powershell_v1`),
custom MCP servers — may be **absent on the machine that installs the plugin**.

This feature lets a plugin **declare the tools its agents require** so the
installer can flag missing capabilities. The required set is **derived from the
bundled agents only**, computed with the *same* resolver the dispatcher uses
(`AgentToolResolver.Resolve`), presented to the author in a grouped checkbox
dialog for refinement, stored in the manifest, and re-evaluated against the
installer's live tool catalog at install time.

## Goals

- Capture, in the plugin manifest, the tools the bundled **agents** need.
- At install, warn (never block) when required tools are unavailable.
- Make the requirement **exactly** what the agents can actually call — glob ∩
  `max_tier` ∩ available — with no drift from dispatch behavior.
- Keep authoring a deliberate, author-confirmed step (detection seeds; author
  decides).

## Non-goals

- **No skill-script scanning.** A skill's `.ps1` does *not* contribute a
  requirement. Skills declare no tools; a script is run by whatever tool the
  caller invokes, and a skill used by the main conversation already runs with
  the user's own installed tools. Requirements come from **agents only**. (An
  optional `.ps1`→"any PowerShell" seed can be added later; it is out of scope.)
- No PowerShell version/host inference. If an agent needs a specific host it
  already names it (`command__pwsh` vs `command__powershell`); a `command__*`
  glob means "any PowerShell/command tool." We carry the declaration through, we
  never guess.
- No change to skill/agent frontmatter or the agent/skill catalogs. Requirements
  live in the plugin manifest only.

## Background (grounding)

- **Agents** declare `tools:` (server-qualified ids or `*`-globs) and `max_tier`
  (`AgentMaxTier`: `ReadOnly | Write | Destructive`) — `AgentFrontmatter`.
- **`AgentToolResolver.Resolve(agentTools, maxTier, parentAvailable, tierOf)`**
  returns the agent's *effective* allowlist: for each available tool, included
  iff it matches the agent's globs **and** its tier ≤ the agent's ceiling. This
  is the same function the dispatcher uses, so "required" == "what the agent can
  call." `MatchesAny`/`WildcardMatch` do the glob match; `tierOf` is the host
  `ToolClassifier` (reads each tool's `readOnlyHint`/`destructiveHint`).
- **`McpToolRegistry`** holds the live catalog keyed by `server__tool` function
  names, **per working directory**, with per-tool annotations. Useful members:
  `NamesForWorkdir(workdir)`, `HasServer(serverName)`, the `Changed` event.
  Group by the `__` prefix to get the server.
- **Server scope** (`McpConfig` / `McpServerSpec.RunsInScratch`):
  - `web` / `github` / custom-global are **workdir-independent** — connected once
    if enabled, already in the registry.
  - `command` is **workspace-scoped but runs in a scratch dir** (`RunsInScratch`).
  - `files` / `git` / `msbuild` / custom-workdir servers require a **real**
    workspace.
- **`McpHost`** is multi-workdir: `EnsureWorkingDir(workdir)` /
  `ReleaseWorkingDir(workdir)`, `ActiveWorkingDirs`. `MainForm` holds `_mcpHost`
  and `_mcpRegistry`.

## The probe workspace

To expand globs, classify tiers, and group tools, the export flow needs the
author's **live tool catalog including workspace-scoped servers**. A scratch dir
only runs `command`, so we use a **temporary real workspace**:

1. Create a temp dir and `git init` it (so `git`/`files`/`msbuild` accept it as a
   real workdir).
2. `_mcpHost.EnsureWorkingDir(tempDir)` — connects all *enabled* servers for that
   workdir, non-disruptively (the user's real workspaces are untouched).
3. Wait (async, spinner, bounded ~10–15s) for connections: listen for
   `McpToolRegistry.Changed` and/or poll `NamesForWorkdir(tempDir)` until it
   stabilizes or the timeout elapses.
4. **Snapshot** the catalog: `NamesForWorkdir(tempDir)` (workdir-scoped) ∪ the
   workdir-independent names (global servers), plus a `name → ToolTier` map from
   the registry annotations via `ToolClassifier`.
5. `_mcpHost.ReleaseWorkingDir(tempDir)`; delete the temp dir.

Notes / caveats:
- **Latency:** server spin-up + discovery probes (PowerShell/MSBuild version
  probes are up to ~4s each) cost a few seconds. Run async; let the author
  proceed on whatever has resolved.
- **Auth-gated / unconfigured servers** (`github` logged out, a server the author
  never enabled) won't appear. They are handled by the *union-with-flag* rule
  below, not by the probe.
- **Fast path:** if the active conversation already has a connected workdir, seed
  immediately from its `NamesForWorkdir` while the probe completes in the
  background.

## Detection (seed)

For the set of agents the author included in the plugin, with `catalog` =
probe snapshot names and `tierOf` = classifier over the snapshot:

```
seed = ⋃ over included agents:  AgentToolResolver.Resolve(agent.Tools,
                                  agent.MaxTier, catalog, tierOf)
```

`Resolve` already handles every declaration form: `tools:` omitted → ReadOnly
inherit; explicit ids; `*` / `server__*` globs — all tier-capped.

**Union-with-flag for unresolved declarations.** An agent may name a tool/glob
whose server isn't in the probe catalog (not enabled, or auth-gated). Resolve
drops it (not in `parentAvailable`), but we must not silently lose it. So also
collect each agent's *raw* declared concrete ids and globs; any that did not
resolve are surfaced in the dialog under their server (derived from the `__`
prefix), **flagged "(not connected)"**, carrying the agent's `max_tier`.

## The grouped picker dialog (`RequiredToolsForm`)

A separate modal dialog (the list can be long), opened from the export flow after
detection. Mirrors the screenshot the author sketched: a scrollable panel of
**per-server groups**.

Each group:
- **Heading:** the server name (e.g. `command`, `msbuild`, `github`, a custom
  server), with a small **mode control** — an `Any of` / `All` radio pair.
  Default **`Any of`**.
- **Rows:** a `CheckedListBox` (or checkbox rows) of that server's tools, each row
  a concrete tool id. Tools in the `seed` are **pre-checked**. Not-connected
  agent-declared tools appear too, labeled `(not connected)`.
- A **`(whole server — any tool)`** row that, when checked, records a *glob*
  requirement (`<server>__*`) rather than enumerating tools — the easy way to say
  "needs this server" and the only option for a not-connected server.

Bundled-but-unavailable note: bundled built-in servers are always relevant;
custom servers only appear if an agent referenced them or they are in the probe.

The dialog's output is the author's **final** selection (not the raw seed),
written to the manifest.

## Manifest schema

`PluginManifest` gains an optional `requiredTools`: a list of **groups**, one per
server the author marked. (Absent/empty ⇒ no requirements; fully backward
compatible — older readers ignore it, and `PluginManifest.Parse` already ignores
unknown keys.)

```json
"requiredTools": [
  {
    "server": "command",
    "mode": "anyOf",
    "tools": ["command__pwsh", "command__powershell", "command__powershell_v1"]
  },
  {
    "server": "msbuild",
    "mode": "anyOf",
    "globs": [ { "pattern": "msbuild__*", "maxTier": "write" } ]
  },
  {
    "server": "github",
    "mode": "all",
    "tools": ["mcp__github__create_pull_request"],
    "globs": [ { "pattern": "mcp__github__*", "maxTier": "readonly" } ]
  }
]
```

- `server` — the `__`-prefix the group covers (display + grouping).
- `mode` — `anyOf` | `all` (see check semantics).
- `tools` — concrete required tool ids (optional).
- `globs` — `{ pattern, maxTier }` entries (optional). `maxTier` is the **highest**
  `max_tier` among the agents that contributed this glob (union semantics), so the
  install-time re-resolve admits the superset the agents could collectively reach.

## Install-time check

On `ImportPlugin`, after the existing validation, evaluate `requiredTools`
against the installer's live `McpToolRegistry` (workdir-independent names ∪
`NamesForWorkdir` of the install context; tiers via `ToolClassifier`):

Per group, an **entry** is satisfied when:
- a `tools` id ⇒ that function name is present in the catalog;
- a `glob` ⇒ ≥1 catalog tool matches `pattern` with tier ≤ `maxTier` (i.e. the
  same `Resolve`-style intersection, re-run on the installer's catalog — robust to
  the server's tool set having changed).

A **group** is satisfied when:
- `mode == anyOf` ⇒ at least one entry is satisfied;
- `mode == all` ⇒ every entry is satisfied.

Unsatisfied groups are aggregated into a single **warning** surfaced at install
("This plugin's agents need tools not available here: …") and recorded so the
**Details** dialog can show per-group availability. **The install is never
blocked** — tool availability is environment-dependent and can change (install
pwsh later, log into github). The Details dialog re-evaluates live each time it
opens.

## Implementation outline

Core (WinForms-free, net48-testable):
- `RequiredTool` model + (de)serialization on `PluginManifest` (group: server,
  mode, tools[], globs[{pattern,maxTier}]). `JavaScriptSerializer`, mirroring the
  existing manifest fields.
- A pure `RequiredToolsCheck.Evaluate(requiredTools, availableNames, tierOf)` →
  list of unmet groups. Reuses `AgentToolResolver.WildcardMatch` and the tier
  comparison. Unit-tested directly.
- A pure `RequiredToolsDetect.Seed(agents, availableNames, tierOf)` →
  per-server grouped candidates (resolved + not-connected), via
  `AgentToolResolver.Resolve`. Unit-tested with a fake catalog/classifier.

App / WinForms:
- A **probe-workspace** helper around `_mcpHost.EnsureWorkingDir` /
  `ReleaseWorkingDir` + a temp `git init` dir, returning a snapshot (names +
  tier map). Async with a progress state.
- `RequiredToolsForm` — the grouped dialog (built in code, like the other plugin
  dialogs; adopts the app icon via `PluginImportExportManager.ApplyOwnerIcon`).
- Wire into `PluginExportForm` / `PluginImportExportManager.ExportInteractive`:
  after the author picks skills/agents, run detection → show `RequiredToolsForm`
  → pass the selection into `ExportPlugin`.
- `ExportPlugin` writes `requiredTools` into the manifest;
  `ExportInstalledPlugin` round-trips it.
- `ImportPlugin` runs `RequiredToolsCheck.Evaluate`; the manager surfaces the
  warning; `GetPluginDetails` / the Details dialog show per-group status.

Constraints:
- App assembly is **C# 3.0 / .NET 3.5** — no string interpolation, no
  null-conditional, no expression-bodied members. Tests are xUnit on `net48`
  linking the source files (add the new core files to `GxPT.Tests.csproj`).

## Decisions log

- **Agents only** are the requirement source; skills/`.ps1` do not contribute.
- Requirements are computed with `AgentToolResolver.Resolve` so they equal the
  agents' real (tier-capped, glob-expanded) allowlist — no drift.
- Globs are **stored, not frozen-expanded** (`{pattern, maxTier}`); the installer
  **re-resolves** against its own catalog, so requirements survive tool-set drift
  and degrade gracefully for servers we couldn't connect at author time.
- Whole-server granularity (`<server>__*`) is the fallback when a server can't be
  enumerated at author time; per-tool checkboxes are the default when it can.
- A **temporary real (git-init'd) workspace** is the probe, via the existing
  multi-workdir `McpHost` API — scratch is insufficient (it runs only `command`).
- The install check **warns, never blocks**, and is re-checkable live in Details.

## Open questions / deferred

- **Manual-add** of a tool id the author's machine lacks entirely — deferred;
  the union-with-flag + whole-server glob covers the common cases.
- **Version-aware checks** (require PowerShell ≥ 7.4 by comparing against the
  discovered `$PSVersionTable`) — deferred; needs the registry to expose
  discovered host versions to the app. Presence/tier is the v1.
- **`.ps1` → "any PowerShell" optional seed** — deferred (non-goal for v1).
