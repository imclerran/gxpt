# files__read Truncation & Chunked Reads — Implementation Spec

> **Addendum (post-review).** A code review revised several details after this plan was written;
> `docs/mcp35-servers-spec.md` is authoritative. Deltas: the output cap is **32 KiB** (~8K
> tokens), not 1 MiB; a **boundary cut returns `next_start_line` AND `next_offset`** so a resume
> passing both seeks **O(1)** (no rescan from the top) while continuing line numbering — `offset`
> alone is still the raw byte window for a single over-cap line; the line scanner is **lone-CR
> aware** (matches `SplitLines`); a **malformed/negative `offset` errors**; and the host's
> `FormatResult` was fixed so a `ToolResults.Json` envelope is no longer sent to the model twice.

**Goal:** Make `files__read` (FilesMcpServer's `read` tool) handle files larger than the
output cap by **truncating with a continuation token** instead of erroring, and make
**minified / zero-newline files readable** by paginating with a byte offset when a single line
is itself larger than the cap.

**Tech stack / constraints:** `servers/FilesMcpServer/FilesTools.cs` compiles under **C# 3 /
.NET 3.5** (VS2008 toolchain) — no `StringBuilder.Clear()` (use `.Length = 0`), no
`string.IsNullOrWhiteSpace`, match the file's explicit-type / `delegate(...)` style. Tests are
xUnit on `net48` in `tests/FilesMcpServer.Tests/` driven through the scripted-stream `Harness`.

---

## Background — current behavior (the dead end)

The 1 MiB cap (`MaxReadBytes`) is a **context** guard. Today it is enforced by **erroring**:

- Whole-file read over cap → `Error("file too large … read a line range instead")`.
- Ranged read whose rendered output exceeds the cap → `Error("requested range is too large …")`.

For a **minified file** (a multi-MiB JSON/JS with zero newlines) *every* path fails with no
content: the whole file is one line, so a line range can't subdivide it, and `start_line=1`
just re-hits the cap. The file is unreadable.

## Design

### 1. Two continuation coordinates (Option A — explicit dual fields)

Line ranges stay the ergonomic front door. The continuation token's **type is decided by where
the read stops**:

- Stop on a **line boundary** (≥1 complete line emitted, next line won't fit) → report
  **`next_start_line`**. Resume in line-world.
- Stop **inside a single line** that alone exceeds the cap (zero complete lines emitted) →
  report **`next_offset`** (an absolute **byte** offset). Resume in byte-world.

`next_offset` is byte-only; `next_start_line` is line-only. We never overload one field to mean
both — the model must know which `read` parameter to feed it back into.

**Per-call rule:** *Did at least one complete line fit before the cap?* Yes → boundary cut
(`next_start_line` = the line we stopped before). No → byte cut (emit a byte-bounded prefix of
that line, `next_offset` = byte position where we stopped). A line is only ever split when it
alone can't fit — exactly the minified case.

### 2. Result shape

- **Not truncated** → bare text, exactly as today (whole-file verbatim when no range/numbering;
  line-joined for ranged/numbered). The verbatim, byte-exact guarantee is preserved for the
  untruncated whole-file read.
- **Truncated** → `ToolResults.Json` envelope (sets `structuredContent`), built as a `JObject`
  like `list`/`search`:
  ```json
  { "content": "…", "truncated": true, "next_start_line": 343, "hint": "…" }
  // or, for a single over-cap line:
  { "content": "…", "truncated": true, "next_offset": 1048576, "hint": "…" }
  ```
  The model loops while it keeps getting `truncated: true`, feeding `next_start_line` →
  `start_line` or `next_offset` → `offset`, until a read comes back not truncated (bare text).

### 3. New `read` parameter: `offset`

`offset` (integer, byte, 0-based). When present, the read is a **byte window**: O(1) seek to the
offset, read up to the cap, return the slice. Ignores `start_line`/`end_line`/`line_numbers`
(byte-window reads are raw — there is no whole line to number). Truncates with `next_offset`;
reaches EOF within the cap → not truncated (bare text), which terminates the loop.

### 4. Mode transition (one-way, deliberately)

`line-mode → byte-mode` happens within a line-mode call when the first line overflows. A
**byte-mode read stays in byte-mode** (continues reporting `next_offset`) until it completes,
because recovering line numbers after an O(1) byte seek would require an O(n) rescan from the
start — not worth it. The model still receives every byte; only the trailing chunks of a file
that contained an over-cap line come back unnumbered. (A no-arg whole-file over-cap read starts
in **line-mode from line 1**, so ordinary multi-line files truncate with `next_start_line` and
only genuinely single-line files fall to `next_offset`.)

### 5. Streaming & memory (why not `ReadLine()`)

`StreamReader.ReadLine()` buffers an entire line, so a 5 MiB single line materializes fully —
blowing the budget we're enforcing. The line-mode reader therefore reads **fixed char chunks**
(like `Edit`'s 64K loop) and scans for `\n` itself, tracking:

- the running line number,
- the absolute **byte** offset (incremental UTF-8 byte count of consumed chars; `\r` of a CRLF
  is counted in the line content, only the `\n` is added as the terminator; BOM adds 3),
- emitted content, capped at the budget.

An **overflow guard** runs per emitted char: when the tentative rendered total (content bytes +
`\n` separators + line-number prefixes, same exact-UTF-8-byte accounting as today) exceeds the
cap, it cuts immediately — boundary cut if any line was already emitted, else byte cut — so it
never finishes reading an over-cap line. Memory stays ≈ one chunk + ≤ budget.

Byte-mode reads raw bytes with an O(1) seek and, when truncating, **backs the cut up to a UTF-8
lead-byte boundary** so a multi-byte codepoint is never split (line-mode cuts on char
boundaries, so it's inherently safe). A leading UTF-8 BOM is stripped only when `offset == 0`.

### 6. Documented limitations

- Truncated reads are **line-normalized** (CRLF/CR rendered as the read tools already do); the
  byte-exact guarantee holds only for the untruncated whole-file read.
- The streaming line scanner treats **`\n`** as the separator (CRLF handled); a **lone CR**
  (classic-Mac) is not a line break on the truncation path. Rare, and the small-file fast paths
  are unchanged.
- `line_numbers` is ignored once a read cuts into a single line (byte cut) — there is no whole
  line to number, and this only happens for single-line files where numbering is meaningless.

---

## Implementation tasks

### Task 1 — `read` schema + handler (`FilesTools.cs`)

- [ ] Add `.Int("offset", false, "Byte offset to resume a truncated read from (for a single line longer than the cap)")` to the `read` schema; update the tool description to explain truncation, `next_start_line` / `next_offset`, and `offset`.
- [ ] Rewrite `Read`: keep the untruncated whole-file fast path (verbatim / numbered) for files ≤ cap; route `offset` → `ReadByteWindow`; route `start_line`/`end_line` and the **over-cap whole-file** case → `ReadLineWindow`. Replace the two over-cap `Error`s with truncation.
- [ ] Add `RenderRead(ReadResult)`: `Error` → `ToolResults.Error`; not truncated → `ToolResults.Text`; truncated → `JObject` envelope → `ToolResults.Json`.

### Task 2 — streaming readers (`FilesTools.cs`)

- [ ] `ReadLineWindow(full, startLine, endLine, lineNumbers, budget)` — chunked char scanner with the overflow guard producing boundary cut (`next_start_line`) or byte cut (`next_offset`); `start_line` past EOF → `Error`.
- [ ] `ReadByteWindow(full, offset, budget)` — binary sniff, O(1) seek, UTF-8-safe cut, BOM strip at offset 0, `next_offset` on truncation; `offset > length` → `Error`, `offset == length` → empty/not truncated.
- [ ] Helpers: `LongArg`, `Utf8ByteCount`, `HasUtf8Bom`, `Utf8SafeCut`, `IsUtf8Continuation`, `ReadFull`, and a private `ReadResult` holder. Remove the old `ReadRange`.

### Task 3 — tests (`tests/FilesMcpServer.Tests/FilesToolsTests.cs`)

- [ ] **Update** the now-truncating cases: `Oversize_read_is_error` (single 2 MiB line → `next_offset`), `Read_range_rejects_an_oversize_selection` (→ `next_start_line`), `Read_range_cap_counts_utf8_bytes_not_chars` (→ truncated), the "just over" half of `Read_range_at_cap_boundary…` (→ `next_start_line=61682`, same content length), and part 2 of `Read_range_works_on_oversize_file…` (whole-file over cap → `next_start_line`).
- [ ] **Add:** resume-via-`next_offset`-until-complete on a minified single-line file (reassembles to original); UTF-8 byte cut never splits a codepoint (all-`€` line); short-line-then-giant-line yields `next_start_line` then `next_offset`; `offset` past EOF → error; `offset == length` → empty, not truncated.

### Task 4 — spec doc

- [ ] Update `docs/mcp35-servers-spec.md` `read` row + bullet to describe truncation, `offset`, and the two continuation coordinates.

### Verification

- [ ] `dotnet test tests/FilesMcpServer.Tests` green; whole-file verbatim / numbering / BOM / binary / sandbox tests still pass unchanged.
