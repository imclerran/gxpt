# Design: host-level `cd` + per-anchor server pooling (worktree workflow enforcement)

**Date:** 2026-06-26
**Status:** Draft (design only — not scheduled for implementation). A design review
(2026-06-26) resolved the four open questions and surfaced several refinements; see
**Review addenda** at the end. Where the addenda and the original body disagree, the
addenda win.
**Related:** PR #217 (`claude/git-worktrees-mcp-support-0nxat9`) ships the `worktree`
tool and the model-facing `cwd` argument this design builds on. Companion to
`mcp-architecture.md`, `mcp35-servers-spec.md`, and `mcp35-approval-spec.md`.

## Summary

Give a conversation a **current working directory** that can be moved, by the model,
into any subdirectory of its user-chosen workspace (and back), with the constraint
that it can never climb above that workspace. All workdir-scoped MCP servers
(files / git / command) then operate at that current directory. The user-chosen
workspace — the **anchor** — remains the only thing persisted to disk and the hard
floor the current directory can never escape.

The motivating use case is git worktrees: a model can create a worktree as a
subdirectory, "enter" it, and have its entire git/file workflow confined to that
worktree — so a weaker model that forgets to scope an individual call cannot clobber
the main checkout. But the mechanism is general (it is a `cd`, not a git feature),
and the git server stays completely unaware of it.

**Chosen approach (the "minimal" variant):** keep the host's existing
**one-server-set-per-workspace pooling**, and implement `cd` by **reusing the
anchor's already-running server set and injecting the current directory into each
call** as a host-authoritative field — *not* by spawning a new server set per
subdirectory. This solves the actual problem (no process spawn on `cd`) while
preserving per-workspace process isolation and adding no new concurrency burden.

## Background: what exists today

Three facts about the current system shape this design:

1. **Servers are pooled per working directory, not per tab.** `McpHost` keeps one
   set of workdir-scoped servers (files/git/command) per directory string
   (`_scopedByWorkdir`), launched lazily by `EnsureWorkingDir(dir)` with
   `GXPT_WORKDIR=dir`, and torn down by `RetainOnly`/`ReleaseWorkingDir` when no open
   tab still references the folder. **Tabs sharing a folder share its server set.**

2. **A conversation's working directory is already runtime-mutable.** The "Set
   Working Folder…" path (`MainForm.SetWorkingFolderForContext`) sets
   `ctx.WorkingDir`, persists it, and calls `SyncMcpWorkingDirFromActiveTab()` →
   `EnsureWorkingDir(...)`. The "point a conversation at a folder, spin up/reuse that
   folder's servers" motion is existing, working machinery.

3. **Each server confines paths to its launch-time `GXPT_WORKDIR` via `PathSandbox`**
   (canonicalize, then a directory-boundary containment check so `/root` ≠
   `/root-evil`). This is the trust boundary the user consents to when they pick a
   folder.

PR #217 additionally adds, on the git server only:
- a `worktree` tool (`list`/`add`/`remove`/`prune`) that creates worktrees as
  `PathSandbox`-confined subdirectories of `GXPT_WORKDIR`;
- an optional, **model-supplied** `cwd` argument on every git tool, used as the git
  process's working directory (also `PathSandbox`-confined).

The model-facing `cwd` is the *soft* version of what this design makes *enforced*:
it works only if the model remembers to pass it, and the model controls it.

## Problem

A model working in a worktree must scope every git/file operation to that worktree.
With only the model-facing `cwd`, a weaker model can omit it and silently operate on
the main checkout — committing to the wrong branch, staging the wrong tree, etc. We
want "stay in the worktree until the model deliberately steps out" to be a property
of the system, not of the model's diligence.

## Goals / Non-goals

**Goals**
- A per-conversation current directory the model can move within its workspace.
- Hard floor: the current directory can never rise above the user-chosen anchor (it
  may equal the anchor, or descend arbitrarily deep inside it).
- Enforcement for the clobber surface (files + git): when scoped into a subdirectory,
  the model cannot reach outside it via a forgotten or omitted argument.
- No process spawn on `cd` (the latency concern that killed the "spawn a server set
  per subdir" idea).
- Per-tab correctness: two tabs in two different worktrees of the same workspace must
  not interfere.
- The git/files/command servers stay stateless per call; the "current directory"
  state lives only in the host.

**Non-goals**
- Confining the **command** server's child processes to the current directory. It has
  no `PathSandbox` (it is the "sharpest edge," contained by the approval gate, not by
  a root). `cd` sets its working directory but does not — and is not claimed to —
  sandbox it.
- Collapsing to one global server instance per type (see *Future work*). This design
  keeps per-workspace pooling.
- Letting the model change the **anchor**. Only the user can move the anchor (via Set
  Working Folder), which is a re-consent action.

## Alternatives considered (and why this won)

The team worked through a sequence of designs; capturing them so the decision isn't
relitigated:

1. **Stateful "active worktree" inside the git server.** Rejected: servers are pooled
   per folder, so two tabs sharing a folder share one git process. A single in-server
   "active worktree" pointer would leak across tabs, and the server can't tell callers
   apart (MCP calls carry no tab id). Server-side state here is necessarily
   per-folder, shared — the wrong semantics. It also couples the git server to a
   worktree concept it shouldn't own.

2. **Second "shadow" git server scoped to the worktree, preferred over the main one.**
   Rejected: requires bespoke preference/shadowing rules and meta-tooling, and pairs
   the git server tightly to the host. The cleaner realization of the same intent is
   "just point the conversation's workdir at the worktree," which needs no shadow.

3. **`cd` that spawns a new server set per target directory** (repoint roots). Correct
   and gives hard isolation, but pays a process-spawn + handshake cost on every cold
   `cd`, and needs a new `cd`-aware retention/eviction policy on top of the
   already-subtle pooling code. Rejected as the *first* step because the cost is
   avoidable.

4. **Host injects the current directory as a soft `cwd` default** (one anchor server,
   model can still omit/override). Rejected: collapses back into "nicer defaults" —
   the model can still escape by omitting or overriding, so it isn't enforcement.

5. **Collapse to one global server per type, current dir injected per call.** Tempting
   simplification (deletes the host pooling entirely), but it forces a server
   concurrency rewrite (one stdio pipe, head-of-line blocking — fatal for long
   `command__run`s), concentrates the failure domain (one crash = all conversations),
   and moves the security clamp entirely host-side onto the hot path. Deferred to
   *Future work* as a separate, larger optimization — not needed to solve `cd`.

**Chosen (this doc):** per-anchor pooling (existing) + host-authoritative current-dir
injection into the anchor's existing server set. It is enforcement (host-authoritative,
not a model hint), costs no spawn on `cd`, keeps per-workspace isolation, and adds no
concurrency burden beyond the shared-folder case the system already handles.

## Design

### Two directories per conversation

- **Anchor** — the user-chosen workspace folder. Persisted to disk (as today). The
  **floor**: the current directory may equal it or descend into it, never rise above
  it. Only the user changes it (Set Working Folder = re-consent).
- **Current** — the conversation's effective working directory. Transient: held in
  memory per conversation, **not persisted**, and **reset to the anchor on conversation
  load** (you reopen at the consented boundary, never at some subdir the model wandered
  into). Starts equal to the anchor. *(Superseded by the 2026-07-31 amendment: `current`
  is now persisted anchor-relative and restored with validation — the transcript's cd
  echoes persist, so the host state must too.)*

`cd` mutates **current** only.

### The `__cwd` convention (host-authoritative)

The host injects the conversation's current directory into every workdir-scoped tool
call as a reserved field — name TBD, e.g. `__cwd` (leading underscore to mark it
host-owned and distinct from any model-facing argument):

- The host **strips any model-supplied `__cwd`** and sets its own. The model cannot
  spoof or widen the root by sending the field itself. This is what makes it
  *enforcement* rather than a default.
- The host computes `__cwd` from the per-conversation current dir, **canonicalized and
  validated to be within the anchor** before injection (defense at the source).
- Injection happens at the host's tool-call dispatch — the point that already resolves
  the connection by workspace (`McpToolRegistry.TryResolve(name, workdir)`). The
  resolved server is the **anchor's** existing set; only the injected `__cwd` differs
  per call.

So a server set is launched once per *anchor*; `cd` never spawns anything. The current
directory rides each call.

### What each server does with `__cwd`

| Server | Uses `__cwd` as | Enforcement |
|--------|-----------------|-------------|
| git | the git process `WorkingDirectory` (as today's `cwd` does); path-arg `PathSandbox` re-rooted at `__cwd` | Hard: git operates on the tree at `__cwd`; path args can't escape it |
| files | the `PathSandbox` root for this call (instead of launch-time `GXPT_WORKDIR`) | Hard: reads/writes confined to `__cwd` |
| command | the child-process `WorkingDirectory` only | **Soft** — no sandbox; gate-governed (by design) |

Each server still **re-validates `__cwd` against its launch-time anchor**
(`GXPT_WORKDIR`) and rejects anything outside it — the second, independent layer
(`mcp-architecture.md` §9: gate and sandbox never trust each other to be the only
defense). The host clamps; the server re-checks.

Two enforcement levels exist, and we adopt the stronger:
- **D1 (working-dir only):** inject `__cwd` as the working directory. Already prevents
  the primary clobber — a bare `commit`/`add`/`push` operates on the tree git runs in,
  not main.
- **D2 (working-dir + sandbox re-root):** D1 plus re-rooting files/git `PathSandbox` at
  `__cwd`, which also blocks deliberate path-arg escapes (`paths: ["../main/x"]`).
  **This is the target.**

### Concurrency

Because pooling stays **per anchor**, the only concurrency on a given server is from
tabs sharing that workspace — exactly the situation the system already tolerates today.
We are **not** introducing the global-multiplexing problem from alternative #5. No
server concurrency rewrite is required.

Two invariants the servers must hold to be safe under even today's shared-folder
concurrency, made load-bearing here:
- **Never** call `Directory.SetCurrentDirectory` (process-global; would race across
  concurrent calls). Per-call working directory is passed to each child `Process.Start`
  via `ProcessRequest.WorkingDirectory`, which `ProcessRunner` already does.
- Build `PathSandbox` (and any `__cwd`-derived value) as a **per-call local**, never a
  shared field.

### `cd` host tool

A host tool (e.g. `cd`) the model can call:
- `cd <relative-or-subpath>` — set current to a directory **within the anchor**.
  Resolve against current (or anchor — TBD, see open questions), canonicalize, reject
  if outside the anchor or nonexistent.
- `cd` to anchor / `cd` with no/empty arg / a `reset` affordance — return to the
  anchor (regain full-workspace breadth).
- Returns the new current directory (relative to anchor) so it's visible to the model
  and the transcript.

`cd` touches host state only — no server contact — so it is instant.

### Worktree interaction

- Worktrees are created **inside the anchor** (PR #217's `worktree add`, sandbox-
  confined). To create one you must be able to write at/above its location, i.e. be at
  the anchor (or an ancestor of the target) — typical flow: `cd` to anchor → `worktree
  add .worktrees/feat` → `cd .worktrees/feat`.
- Once current = the worktree, files/git are hard-confined to it. Stepping back to main
  is a deliberate `cd` to the anchor.
- A linked worktree's `.git` is a file pointing at `mainrepo/.git/worktrees/<name>`;
  git run with `WorkingDirectory` = the worktree resolves it correctly. Worth ensuring
  `.worktrees/` (or chosen location) doesn't confuse `status` at the anchor — git
  already excludes registered linked-worktree paths, but the convention should be
  documented.

### UI / observability

- Surface the current directory on the workspace strip (distinct from the anchor) so
  the user can see when a conversation is scoped into a subdir, with a one-click return
  to the anchor.
- Sandbox-rejection errors should be `cd`-aware: *"you are in `<current>`; `cd` to the
  workspace root to reach `<path>`"* — otherwise a model that scoped deep then asks for
  an anchor-level file gets an opaque "escapes the workspace root."

### Interplay with Set Working Folder

- **Set Working Folder** moves the **anchor** (re-consent, re-persist) and **resets
  current to the new anchor**. Changing the workspace tears down/reuses server sets per
  the existing pooling; current can't be left pointing into the old anchor.
- **`cd`** moves **current** within the existing anchor; never persists; never changes
  which server set is used.

### Relationship to PR #217's model-facing `cwd`

`__cwd` (host, authoritative, the *root* for the call) and the model-facing `cwd` (a
*deeper subpath* selection) compose: effective dir = `__cwd` then optional `cwd`
beneath it. Define precedence explicitly, and decide later whether to retire the
model-facing `cwd` once `cd` exists or keep it for one-off sub-scoping. The shipped
`cwd` is a strict subset of `__cwd`'s mechanism, so git can reuse the same plumbing.

## Edge cases

- `cd` above the anchor → reject (floor violation).
- `cd` to a nonexistent dir, or to a file → reject with a clear message.
- `cd` into a worktree that is later removed/pruned → next call's `__cwd` no longer
  exists → **error, never silent fallback to the anchor** (silent fallback is the
  clobber we're preventing). Surface it and let the model `cd` back deliberately.
- Symlink/junction inside the anchor that resolves outside → canonicalize and reject
  (same class as `PathSandbox` today; do it at both host clamp and server re-check).
- Race: `cd` changes current while a call is in flight → each call carries its own
  `__cwd` snapshot; no shared mutable server state to corrupt.
- Conversation reload → current resets to anchor (not restored). *(Superseded by the
  2026-07-31 amendment: restored after validation; falls back to the anchor only when
  the stored dir is invalid or gone, and the ephemeral tail tells the model either way.)*

## Testing strategy

- Host: clamp logic (within-anchor accept; above/escape/symlink reject;
  canonicalization), `__cwd` injection at dispatch, **stripping** a model-supplied
  `__cwd`, reset-on-load, Set-Working-Folder-resets-current.
- Servers: each honors `__cwd` as documented, **re-validates against launch anchor**,
  rejects out-of-anchor `__cwd`; files/git confine path args to `__cwd` (D2); command
  sets working dir only. Reuse the existing fake-process harness style
  (GitMcpServer.Tests) to assert the working directory and rejection paths.
- Concurrency invariants: assert no `SetCurrentDirectory` use; per-call sandbox locals.

## Open questions

*All four are resolved by the 2026-06-26 review — see **Review addenda**. Kept here for
the record with pointers.*

1. **Field name / transport.** `__cwd` as a reserved tool-argument vs. an out-of-band
   request field. **Resolved → out-of-band via `params._meta`** (addendum A4).
2. **`cd` resolution base.** Relative to **current** (shell-like) vs. the **anchor**.
   **Resolved → shell-like, relative to current, with no-arg `cd` = return to anchor**
   (addendum A8).
3. **Retire model-facing `cwd`?** **Resolved → retire it; the host-injected current dir
   replaces it** (addenda A2/A4). The expressiveness tradeoff is noted in A2.
4. **Per-server enforcement granularity.** **Resolved → a per-server `__cwd` policy
   table; not all scoped servers follow `cd`** (addendum A3). `command` stays D1
   (working-dir only, gate-governed).

## Future work (explicitly out of scope here)

**Collapse to one server instance per type, globally.** Once `__cwd` makes the working
root a per-call concern, nothing forces one process per workspace; in principle one
git/files/command process could serve all conversations, deleting the host's
per-workspace pooling. This is a real simplification of the host but is **not** free:
it requires a server concurrency upgrade (concurrent dispatch + serialized stdout +
per-call locals; pure queuing head-of-line-blocks long `command__run`s), concentrates
the failure domain into a single process, and makes the host's per-call clamp the sole
enforcement layer. It is a separate, larger initiative to weigh on its own merits —
likely sensible for files/git, likely *not* for command — and is intentionally
deferred. The design in this doc is forward-compatible with it (the `__cwd` convention
is the same), so adopting per-anchor pooling now does not foreclose collapsing later.

---

## Review addenda (2026-06-26)

A design review pressure-tested the body above against the live code (`McpHost`,
`McpToolRegistry.TryResolve`, `PathSandbox`, the orchestrator's `ResolutionWorkdir`,
PR #217's `GitTools.ResolveCwd`, and the approval gate). The items below resolve the
open questions and amend the body where it was incomplete or inaccurate. **These win on
conflict.**

### A1. `__cwd` ↔ approval/remember store (a gap the body missed)

The body never mentions the approval gate, but `cd` changes its semantics. In
`ToolApprovalPolicy`, remembered approvals for path-scoped tools are keyed by the
**relative** `path` argument (`PrefixMatches(val, pattern, isPath:true)`), and a blanket
`RememberWorkdirWrites` rule covers "all Write-tier path tools for the active
workspace," with an empty pattern matching any relative path under the root. Today those
are interpreted relative to the anchor; once `cd` exists, the same relative path means a
different absolute location per `current`, so a remembered rule silently changes scope as
the model moves.

**Resolution:** resolve every remembered rule (match **and** store) against the
**`__cwd`-derived, canonicalized absolute** path, not the relative arg. Two constraints:
- The strip/overwrite of the host current-dir field must happen **before**
  `_approval.Check(...)` (`McpChatOrchestrator.cs:1056`), so the gate sees the
  host-authoritative value, never a model-spoofed one.
- The canonicalization the gate stores/matches with must be **the same** routine the
  server's `PathSandbox` uses (`PathSandbox.Resolve`), or a stored rule and the server's
  independent re-check can disagree on whether a path matches. Co-locate it.

Add this interaction to the testing strategy.

### A2. `GXPT_WORKDIR` splits into two roles (request-time working dir)

Decision: move to a **request-time working directory** — the server is launched per
anchor but is not told its *current* dir until each call. **`GXPT_WORKDIR` does not go
away;** it changes job. Today it plays two roles at once:

- **(a) the `PathSandbox` floor** — the containment boundary the server enforces
  independently; and
- **(b) the working directory** processes actually run in.

`__cwd` (transported per A4) takes over **(b)** per request. Role **(a) stays**, because
defense-in-depth (`mcp-architecture.md` §9: gate and sandbox never trust each other)
requires the server to re-validate `__cwd` against something it knows at launch. If
`GXPT_WORKDIR` were deleted outright, the server would have nothing to validate against
and the host clamp would become the *sole* enforcement layer — the exact collapse
rejected in alternative #5. So: the server knows its **anchor/floor** at launch; it
learns its **current dir** per call.

Mechanics:
- **Two `PathSandbox` instances per call, both per-call locals** (honoring the
  no-shared-mutable-state invariant): an anchor-sandbox validates `__cwd ∈ anchor`, then
  a fresh sandbox rooted at `__cwd` validates the call's path args.
- **Default when `__cwd` is absent/empty → the anchor** (`GXPT_WORKDIR`). Preserves
  today's behavior for any host-internal call and is the safe floor. (This is what
  `ResolveCwd` already does for an empty `cwd`.)
- **Pool/resolve keys stay the anchor.** `EnsureWorkingDir`/`ReleaseWorkingDir`/
  `RetainOnly`/`TryResolve` remain keyed on `WorkingDir` (the anchor); `__cwd` rides
  orthogonally. The host must call `EnsureWorkingDir(anchor)`, **never** `current` — else
  it spawns a set per `cd` and defeats the design. Three host concepts now exist: anchor
  (pool + resolve + persist), current (injected), and the retired model `cwd`.
- **Expressiveness tradeoff (accepted):** retiring model-facing `cwd` in favor of
  host-injected current means a single turn cannot operate in two worktrees without
  `cd`-A → op → `cd`-B → op (serialized). That is the enforcement working as intended.
  If it ever bites, model `cwd` can return as a *compose-beneath-`__cwd`* subpath (never
  escaping); for now it is retired.
- **Robustness footnote:** because `current` is host-side and rides each call, a crash +
  relaunch of an anchor's server set (a faulted connection in
  `_scopedByWorkdir[anchor]`) does **not** lose the model's place — unlike the rejected
  alternative #1, where current-dir as in-server state would reset on crash.

### A3. Per-server `__cwd` policy — not every scoped server follows `cd`

The body's enforcement table lists three servers; there are in fact **six** workdir-scoped
servers, and `__cwd = current` is wrong for some. The 3-row table is replaced by this
6-row policy table. Each server still re-validates `__cwd` against its launch anchor
(A2) regardless of row.

| Server | `__cwd` role | Floor / sandbox | Enforcement |
|--------|--------------|-----------------|-------------|
| git | git process `WorkingDirectory`; path args re-rooted at `__cwd` | anchor floor + `__cwd` sandbox | Hard (D2) |
| files | per-call `PathSandbox` root | anchor floor + `__cwd` sandbox | Hard (D2) |
| command | child-process `WorkingDirectory` only | no sandbox (gate-governed) | Soft (D1) |
| msbuild | build process `WorkingDirectory` | anchor floor | Hard on working dir |
| **memory** | **ignored — stays anchored** | anchor only | n/a (anchored by nature) |
| **extensions / skills** | **split:** script *process cwd* = `__cwd`; skill *authoring/resolution* paths stay anchored | anchor for file paths; `__cwd` for child cwd | Mixed |

Rationale for the two non-obvious rows:
- **memory** persists to `GXPT_WORKDIR/.gxpt/memory`. If it followed `cd`, memory would
  fragment into per-worktree stores (`.worktrees/feat/.gxpt/memory`) — memory is a
  workspace property, not a worktree one. It ignores `__cwd`.
- **extensions** authors/resolves skills under `GXPT_WORKDIR/.gxpt/{skills,agents}` and
  against bundled/user roots — those are workspace resources and stay anchored — but
  `run_skill_script`'s *child process* plausibly should run in `current`. Hence the split.

So "add `__cwd` to the other scoped servers" really means "decide, per server, whether
`__cwd` moves the working dir or is ignored because the server is anchored by nature."

### A4. Transport: out-of-band via `params._meta` (resolves OQ1)

A `tools/call` is JSON-RPC `{"params":{"name":…,"arguments":{…}}}`; the model only fills
`arguments`. Two ways to carry the host current dir:

- **In-`arguments`** — host writes the field into the model's `arguments` bag post-hoc.
  Requires defensively stripping any model-supplied copy, and pollutes the gate's
  argument view/logging with a host field.
- **Out-of-band (chosen)** — carry it as a **sibling of `arguments`** in
  `params._meta`, the MCP-reserved metadata slot that is **not part of any tool's input
  schema**:
  ```json
  {"params":{"name":"git__commit","arguments":{ …model… },"_meta":{"gxpt.cwd":"<abs>"}}}
  ```
  Because the tool schema only describes `arguments`, `_meta` can never collide with a
  model arg, never appears in the revealed/cached schema, and sits outside the
  model-controlled bag — no strip dance, clean gate view. The win is **trust-domain
  separation**, not schema pollution (the host injects post-hoc, so the field never
  enters the published schema in either approach — an earlier framing overstated that
  risk).

**Plumbing cost (not free in this codebase):** `CallToolParams`
(`src/Mcp35.Core/Protocol/Tools.cs:43`) carries only `name` + `arguments`,
`McpServerConnection.CallTool(name, args, timeoutMs)` has no meta parameter, and
`ToolCallContext` exposes only `Arguments`. OOB therefore requires: add `_meta` to
`CallToolParams`, thread a meta dict through `CallTool`, and expose `ctx.Meta`
server-side. That is protocol + client + server-framework surface (broader than a
leaf-server change) but it is the correct, reusable seam for host metadata. In-`arguments`
remains the pragmatic fallback if the strip/overwrite discipline is owned instead.

### A5. Scratch / folderless conversations (resolves the body's silent assumption)

`ResolutionWorkdir` falls back to `ScratchWorkingDir` when no workspace is set; there is
no anchor in that mode. Behavior:
- `command` (the only scratch-eligible scoped server) still receives `__cwd` — floor and
  current both = the scratch dir; it has no `PathSandbox` anyway, so `__cwd` just sets the
  child working dir.
- **`cd` is absent/disabled** on scratch turns: do not offer the host `cd` tool when the
  turn's `ResolutionWorkdir` came from `ScratchWorkingDir` (no anchor → no floor → nothing
  to scope within).

### A6. Home of `current` + prompt placement

`current` lives on the **conversation context**, beside `WorkingDir`/`ScratchWorkingDir`
(making explicit what the body left implicit). The per-request workspace block must show
`current`, not just the anchor, or the model computes paths against the wrong dir. Move
that block into the **ephemeral tail** (alongside the volatile per-turn manifest): `cd`
mutates it, so keeping it in the cached head would either bust the prompt cache on every
`cd` or show a stale dir.

### A7. `worktree remove` of the current directory

Reject `worktree remove` when the target is `current` or an ancestor of it; require a
`cd` to the anchor first. The alternative (auto-`cd` to anchor on remove) couples the git
server to host current-dir state — the git server would have to instruct the host to move
`current` — which is exactly the coupling this design avoids.

### A8. `cd` host-tool argument surface (resolves OQ2)

- `cd <relpath>` — resolved relative to **current** (shell-like, composable),
  canonicalized, must resolve to an **existing directory within the anchor**. **Absolute
  paths rejected** (mirror `PathSandbox`) to keep one mental model.
- **Return to anchor:** no-arg `cd` → the anchor (shell's "`cd` → home"; the anchor *is*
  home).
- `cd ..` above the anchor → **error**, not a silent clamp — keep the floor visible to
  the model rather than masking the intent.
- Always echo the new `current` as an **anchor-relative** string (`.` at the anchor,
  `.worktrees/feat` deep) into the result and transcript; this is also what feeds the
  ephemeral workspace block (A6).
- Not offered on scratch/folderless turns (A5).
- Touches host state only — no server contact — so it stays instant.

### A9. Testing additions

Beyond the body's list:
- Approval store: remembered rules match/store against canonicalized `__cwd`-absolute
  paths; field strip happens before the gate; gate canonicalization == server
  `PathSandbox` canonicalization (A1).
- Per-server policy: `memory` ignores `__cwd` (no per-worktree store); `extensions` runs
  the script child in `__cwd` while authoring paths stay anchored (A3).
- Transport: `_meta` carries the current dir; a model-supplied `arguments.__cwd`/`_meta`
  is ignored/overwritten by the host (A4).
- Scratch: `cd` tool absent on scratch turns; `command` still honors `__cwd` (A5).
- Default: absent `__cwd` falls back to the anchor on every scoped server (A2).

---

## Amendment (2026-07-31): persist `current` and advertise it per request

Two decisions above are revised in light of shipped experience. **This section wins on
conflict** with the body and the 2026-06-26 addenda.

### What went wrong

The body made `current` transient — "reset to the anchor on conversation load" (§Design,
§Edge cases) — reasoning that you reopen at the consented boundary. But the model's
belief about `current` lives in the *persisted transcript*: the `cd` tool's success echo
("Current directory is now `src/foo` …") is saved with the history. On reopen the host
silently reset its half of the state while the transcript kept claiming the subdir, so
the model resolved subdir-relative paths against the anchor — a host-*induced* variant of
the path doom loop `pwd` was added to cure. The same divergence occurred mid-session when
the user clicked the strip's **Return to root**, which moves `current` without leaving
any transcript trace. Separately, addendum A6's requirement that the per-request context
show `current` ("or the model computes paths against the wrong dir") was only half
shipped: the workspace block stayed anchor-only in the cached head, and nothing carried
`current` at all.

### Revision 1 — `current` is persisted and restored (supersedes reset-on-load)

- `Conversation.CurrentDir` mirrors the tab's current dir and rides every normal
  conversation save. On disk it is stored **anchor-relative** ('/'-separated,
  omitted at the anchor), so a conversation file cannot express a current dir outside
  its own anchor and the subpath survives the anchor being re-picked at a moved
  location (`ConversationStore.SerializeCurrentDir` / `DeserializeCurrentDir`).
- Because the mirror is written when `cd` fires and saved when the turn finalizes, the
  host state and the transcript's cd echo **persist or are lost together** — the two
  can no longer diverge through a save/load cycle.
- Restore validates twice: structurally on deserialize (containment via
  `PathSandbox.Resolve`; malformed/escaping values → null), then at adoption
  (`MainForm.ApplyLoadedWorkingDir`): containment re-checked + the directory must still
  **exist**, else fall back to the anchor and drop the stored value. The load-time
  fallback is deliberate and differs from the mid-session rule (§Edge cases: in-flight
  stale `__cwd` still errors, never silently falls back) — at load there is no in-flight
  operation to mis-target, and Revision 2 makes the fallback visible to the model.
- Consent is unchanged: `current` only ever narrows within the consented anchor, the
  strip shows the restored subdir immediately with one-click Return to root, and **Set
  Working Folder** still resets it (re-consent). Return to root now also clears the
  persisted value (and saves), so an explicit user reset can never resurrect on reopen.

### Revision 2 — A6 completed: `current` rides the ephemeral tail every request

The per-request tail (Zone C, rebuilt after the cache breakpoints) now carries a
`<current_directory>` block (`McpChatOrchestrator.CurrentDirContextBlock`) on **every
workspace turn, including at the anchor**, stating the host's authoritative current dir
anchor-relative. Always-on is the point: the block exists to correct a model whose
transcript says otherwise (failed restore, user Return-to-root), and those are exactly
the turns an "only when scoped" block would omit. The workspace block in the cached head
(Zone A) deliberately keeps showing only the anchor — it must stay byte-identical while
the workspace does; the volatile half lives in the tail, completing A6 without the
cache-bust it warned about. Sub-agent children do not inherit `current` (they run at the
anchor) and their own tails say so.

### Testing

- Store: relative round-trip, at-anchor omission, legacy files → null, malformed /
  escaping / absolute stored values → null (never a load failure).
- Tail: `<current_directory>` present at anchor and when scoped, correct
  anchor-relative rendering, absent without a workspace, ordered before `<memory>`.
- Adoption: within-anchor + existence enforced; fallback clears the stored value.
