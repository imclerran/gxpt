# Design: host-level `cd` + per-anchor server pooling (worktree workflow enforcement)

**Date:** 2026-06-26
**Status:** Draft (design only — not scheduled for implementation)
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
  into). Starts equal to the anchor.

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
- Conversation reload → current resets to anchor (not restored).

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

1. **Field name / transport.** `__cwd` as a reserved tool-argument vs. an out-of-band
   request field. Tool-argument is simplest given the existing dispatch; underscore
   marks it host-owned. Confirm the host can reliably strip a same-named model arg.
2. **`cd` resolution base.** Resolve `cd` targets relative to **current** (shell-like,
   composable: `cd a` then `cd b` → `a/b`) or always relative to the **anchor**
   (absolute-within-workspace, simpler to reason about)? Shell-like is more intuitive;
   anchor-relative is harder to misuse. Leaning shell-like with an explicit
   "return to root."
3. **Retire model-facing `cwd`?** Keep both (compose) or drop the shipped `cwd` once
   `cd` lands, to avoid two ways to scope.
4. **Per-server enforcement granularity.** Adopt D2 for files+git uniformly; confirm
   command stays D1 (working-dir only) and is documented as gate-governed, not
   sandboxed.

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
