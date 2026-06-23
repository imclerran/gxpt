# Multimodal Attachments — Design Spec

**Status:** Design (implementation-level).
**Branch:** `claude/multimodal-file-formats-DYqEt`

This document specifies how GxPT will support **image and PDF attachments** sent
to vision/document-capable models via OpenRouter, alongside the existing
text-extraction attachment flow. It also specifies the supporting **model-info
cache** that drives capability-aware decisions.

---

## 1. Goals & non-goals

**Goals**

- Let users attach **images** (and PDFs natively) to messages, sent through
  OpenRouter's multimodal content format.
- Preserve GxPT's core attachment property: **the transcript is the durable,
  self-contained source of truth.** An attached file remains viewable in-app and
  replayable to the model even after the original file is deleted from disk.
- Keep the token-light **text-extraction** path as the default for PDFs.
- Make per-model decisions (can this model take an image? a PDF?) from real
  capability data, not guesses.
- Honor **Zero Data Retention** (ZDR) conservatively.
- Run on **.NET 3.5 / Windows XP** with bundled libraries only.

**Non-goals**

- RAG / embedding / retrieval over documents.
- Audio or video modalities.
- Server-side file hosting (everything rides inline in the request).
- Client-side token budgeting beyond the coarse context/prune hooks in §10.

---

## 2. Current state (baseline)

| Concern | Today |
|---|---|
| Attachment model | `AttachedFile { FileName, Content }` — `Content` is always extracted **text** (`Models/ChatModels.cs:6`). |
| Extraction | Pluggable `IAttachmentExtractor` (`CanHandle`/`Extract`/`GetFileDialogPatterns`/`GetCategoryLabel`): Text / PDF via iTextSharp (`PdfAttachmentExtractor.Extract`) / DOCX via Ionic.Zip. `AttachmentService` whitelists via `IsSupported` (`Services/AttachmentService.cs:57`) + `BuildOpenFileDialogFilter` (`:69`). Attach UI: `btnAttach_Click` (`MainForm.cs:3859`) + drag-drop (`:247`). |
| Transcript | Attachments **are** persisted structured (`ToMessageDto:143` → `MessageDto.Attachments:413`) and restored with legacy-delimiter fallback (`FromDto:226`); Newtonsoft, `NullValueHandling.Ignore`. (The `ChatModels.cs:23` "not serialized" comment is stale.) |
| Request render | `BuildMessagesForModel` (`Forms/MainForm.cs:3479`) **re-inlines** attachment text into the message string with `--- Attached File: … ---` delimiters, on **every** request, via the `RequestMessageTransform` hook (`McpChatOrchestrator.cs:135`, applied at `:410/:629`). Full history is resent each turn. |
| Wire `content` | `ContentValue` (`OpenRouterClient.cs:163`) returns a plain string normally **but already emits a one-part content *array*** carrying `cache_control` when a message is flagged a cache breakpoint — the array shape exists. |
| HTTP | Shells out to **curl**; `BuildCurlArgs` (`:715`) writes the JSON body to a temp file and sends `-d @file` (no command-line-length limit; large base64 is fine), API key off the command line via `-K`, temp files cleaned via `CleanupTempFiles` (`:794`) in `finally`. curl is required for TLS 1.2 on XP. |
| Model info | `ModelCatalogService` (`Services/ModelCatalogService.cs`, namespace `GxPT`) fetches `GET /api/v1/models` via bundled `Lib\curl.exe` on app-open (24h staleness) and via the Settings **"Update Model Info"** button. Keeps only `id → context_length` in `%AppData%\GxPT\model-context.txt`; `TryGetContextLength` feeds the status-bar context meter (`MainForm.cs:5760`). **No modality/pricing data retained yet.** |
| ZDR | `ConversationDto.Zdr` + `ZdrFirstMessageIndex`. **ZDR locks once the first message is sent under it** — it cannot be turned off mid-conversation. |

**Key architectural insight we build on:** GxPT already separates *durable
structured storage* (attachments on the message) from *request-time rendering*
(`BuildMessagesForModel` re-emits them each turn). Multimodal attachments slot
into the exact same seam — stored once, re-rendered every request — so they do
**not** drop out of context. And the content-part **array** shape already exists
in `ContentValue` (for `cache_control`), so the wire work is *extending* an
existing array path with `image_url`/`file` parts — not inventing one. The one
genuinely new piece of plumbing is carrying the structured `Attachments` through
the request path into `ContentValue` (today nothing downstream reads them: the
transform inlines their text and they're dropped). See §8.

---

## 3. Model-info cache — extend `ModelCatalogService`

Capability-aware handling needs each model's input modalities — and that data
already arrives in the **existing** OpenRouter model fetch. We widen what it
keeps rather than building anything new.

### 3.1 What already exists (reuse as-is)

`ModelCatalogService` (`Services/ModelCatalogService.cs`, namespace `GxPT`):

- Fetches `GET https://openrouter.ai/api/v1/models` via the bundled `Lib\curl.exe`
  (`HttpGetModels`: `-sS --fail-with-body --max-time 60`, public / no-auth) on a
  **background thread**.
- `RefreshIfDue()` on app open (24h staleness) and `ForceRefresh(onDone)` behind
  the Settings **"Update Model Info"** button (`SettingsForm.BtnUpdateModelInfo_Click`).
- `ParseModelsJson` keeps only `id → context_length`; persists a sorted
  `id<TAB>tokens` file at `%AppData%\GxPT\model-context.txt`.
- `TryGetContextLength(model, out ctx)` lookup ladder (verbatim → strip `~` →
  strip `:variant`); raises `CatalogUpdated`; consumed by the status-bar context
  meter (`MainForm.cs:5760`). Tested in `GxPT.Tests/ModelCatalogServiceTests.cs`.

The fetch, curl transport (mandatory — .NET 3.5 on XP can't do the TLS 1.2
OpenRouter requires), refresh cadence, button, threading, and routing-suffix
ladder are **already built and tested** — we reuse all of it.

### 3.2 The extension

The current cache keeps a flat `int` per model; it can't hold modalities or
pricing. Widen it to retain the **full model objects**:

- In `FetchAndStore`, keep the raw `data[]` objects and persist them as JSON at
  `%AppData%\GxPT\models.json` (Newtonsoft, same settings as `ConversationStore`).
  `model-context.txt` is either derived from the same fetch (kept for back-compat /
  Notepad-readability) or retired in favor of deriving context length from the
  JSON — implementer's call, but **`TryGetContextLength` semantics must not
  change**. This is a deliberate step away from the original tab-file minimalism
  (which was scoped to the context meter), justified by the new modality/pricing
  requirement.
- Add a typed read-only view + capability accessors (same lookup ladder):
  - `bool TryGetModelInfo(string model, out ModelInfo info)`
  - `ModelInfo.SupportsImageInput` ⇐ `architecture.input_modalities` contains `"image"`
  - `ModelInfo.SupportsFileInput`  ⇐ `architecture.input_modalities` contains `"file"`
  - `ModelInfo.Raw` (full `JObject`) for pricing/etc., read lazily — no migration

```csharp
// Layered over the retained raw JObject; only what we consume is typed.
public sealed class ModelInfo
{
    public string Id { get; }
    public long? ContextLength { get; }              // context_length
    public IList<string> InputModalities { get; }    // architecture.input_modalities
    public JObject Raw { get; }                       // full record, for pricing/etc.
    public bool SupportsImageInput { get { return InputModalities.Contains("image"); } }
    public bool SupportsFileInput  { get { return InputModalities.Contains("file"); } } // PDF
}
```

Capability source of truth: `architecture.input_modalities` — `"image"` ⇒ vision,
`"file"` ⇒ native PDF/document.

### 3.3 Consumption & fallback

Attachment gating (§6) and render (§8) call `TryGetModelInfo` for the selected
model, reusing the routing-suffix ladder so `~provider/model-latest`, `:free`,
and `:nitro` resolve. On a miss (model not in the catalog yet, or no fetch has
ever succeeded), treat rich modalities as **unsupported** → text/placeholder
fallback (§6.4); never emit a block that might 400.

---

## 4. Attachment data model

Generalize `AttachedFile` to carry a kind and an optional binary payload, while
staying backward-compatible with existing transcripts.

```csharp
// Kind = wire CARRIAGE (how the attachment is sent), NOT source format.
// A .docx extracts to text ⇒ carried as Text. Binary bytes live in Data,
// never in Content (Content is inlined as text by the transform — see §8).
public enum AttachmentKind { Text, Image, Pdf }

public sealed class AttachedFile
{
    public string FileName { get; set; }
    public string Content  { get; set; }   // extracted TEXT (text files; also PDFs)

    // New (all omitted via NullValueHandling.Ignore when absent):
    public AttachmentKind? Kind { get; set; }   // null ⇒ infer Text (legacy)
    public string MediaType { get; set; }       // e.g. "image/png", "application/pdf"
    public string Data { get; set; }            // base64 of the (normalized) bytes
    public int? Width { get; set; }             // images, for the viewer/UX
    public int? Height { get; set; }

    public AttachedFile Clone() { /* deep copy of ALL fields */ }
}
```

**Representations by kind**

| Kind | `Content` (text) | `Data` (bytes) |
|---|---|---|
| Text | ✅ extracted | — |
| Pdf  | ✅ extracted via iTextSharp | ✅ original PDF bytes |
| Image | — (no faithful text form) | ✅ normalized PNG/JPEG/GIF bytes |

PDFs are **dual-representation**: we keep both the cheap extracted text *and* the
original bytes. Which one is sent is a render-time choice (§8). Images keep bytes
only; their fallback is a placeholder note (§6.3).

**Carriage, not format.** `Kind` is the wire representation, not the file type —
DOCX (and any future extractor) extracts to text and is carried as `Text`. The
load-bearing rule: **binary bytes go in `Data`; `Content` is text only.** If an
image's base64 ever lands in `Content`, the transform (§8) inlines it as text.
Provide `AttachedFile.Clone()` (deep copy of all fields) and use it wherever
attachments are copied — notably the edit/resend path (`MainForm.cs:2376`),
which today does `new AttachedFile(FileName, Content)` and would silently drop
`Data`/`Kind`/`MediaType` (an image, whose `Content` is empty, would vanish on
edit — see §12).

**Backward compatibility:** old transcripts have `{FileName, Content}` with no
`Kind` → treated as `Text`. The legacy delimiter parser
(`ConversationStore.TryExtractAttachmentsFromContent:428`) is unchanged.

---

## 5. Storage & portability

- **Inline base64 in the transcript** (`AttachedFile.Data`). This preserves the
  "one file = the whole self-contained conversation" property GxPT relies on for
  view-after-delete and export/import. Sidecar blob stores are explicitly **not**
  chosen here (they break single-file portability); revisit only if transcript
  sizes become a real problem.
- **Size guards (mandatory on XP):**
  - Hard cap: **500 KB per image** (configurable). Reject/warn at attach.
  - **Downscale images** to a max long-edge of **1568 px** before encoding (see §7).
  - Oversized PDFs: prefer the text-only path; warn before embedding large bytes.
- **Memory discipline:** transcripts load fully into memory and re-serialize
  atomically (`FileSafe.WriteAllTextAtomic`). Caps keep this bounded.
- **Cumulative request limits:** because every rich attachment replays each turn
  (§2), a long conversation can accumulate enough images to exceed a
  provider's per-request limits. Per-item caps don't bound the total — see §10 for
  oldest-first pruning that keeps the *replayed* set within limits while the durable
  bytes stay in the transcript. **Threshold: 10 replayed images OR ~4 MB cumulative
  attachment bytes triggers prune-oldest-first** (global, not per-provider — see §10).

---

## 6. Capability gating & representation choice

At **attach time** and at **render time**, consult
`ModelCatalogService.TryGetModelInfo(model, out info)`.

### 6.1 Attach-time UI gating

- Resolve the conversation's selected model → `ModelInfo`.
- Image attach offered only if `SupportsImageInput` (or unknown → see §6.4).
- PDF attach always offered (text path is universal); native escalation offered
  only if `SupportsFileInput`.

**Implemented as a hard gate** (decided over soft warn-and-allow): the gate is a
`AttachmentService.ImageAttachmentsEnabled` flag the attach UI sets from
`MainForm.CurrentModelSupportsImages()` before each operation. When off, the image
extractor is skipped everywhere — the **Open dialog** omits the *Image Files*
category, **drag-drop** rejects image files (with a clear "needs a vision-capable
model" message rather than a generic "unsupported"), and even an image chosen via
*All Files* is skipped by extraction. `CurrentModelSupportsImages()` is conservative
on a catalog miss (unknown model / first-run) → returns false, so images stay gated
until vision is positively confirmed.

**Scope — attach action only.** The hard gate governs *adding* an image. It does
**not** retroactively purge images already staged (pending) or already sent under a
prior vision model: those are durable and handled by the render path (§8), which
re-resolves representation against the *current* model every request — a staged or
historical image silently becomes a placeholder under a non-vision model and goes
native again when a vision model is reselected. WebP is always rejected regardless
of model (GDI+ cannot decode it locally — a capability gate, not a model gate).

### 6.2 Images

| Condition | Sent as |
|---|---|
| Model supports image + within budget/ZDR | `image_url` data-URL block (§8) |
| Otherwise | Placeholder text: `[image attached: photo.png — current model has no vision]` |

### 6.3 PDFs — default text, escalate to native

| Condition | Sent as |
|---|---|
| Default | Extracted text (current behavior; token-light; universal) |
| User opts into "Send as full PDF" **and** model `SupportsFileInput` **and** not ZDR | Native `file` data-URL block (§8) |
| Scanned PDF (extraction empty, page count > 0), model `SupportsFileInput`, not ZDR | **Auto-escalated to native, no prompt** (the text path is useless for it) |
| Scanned PDF, ZDR or no file support | Inline placeholder note; a one-time message explains why it can't be read here |

iTextSharp stays the **default workhorse**. For a clean digital PDF, native
gives little quality edge at much higher cost; native earns its keep only on
scanned/visual PDFs (OCR, tables, figures) that the text layer can't represent.
A newer iTextSharp isn't an option anyway (newer versions drop .NET 3.5 / move
to AGPL), and wouldn't fix scanned PDFs.

**Implemented mechanism.** PDFs are dual-representation: `PdfAttachmentExtractor`
stores the extracted text in `Content` *and* the original bytes (base64) in `Data`
with `Kind = Pdf`. A durable per-attachment `SendNativePdf` flag selects the wire
representation; `BuildMessagesForModel` re-resolves it every request against the
current model's `SupportsFileInput` and ZDR (native is forced off under ZDR, §9).

- **Manual opt-in:** right-click a PDF chip in the pending banner → *Send as
  extracted text / Send as full PDF*. "Full PDF" is enabled only when the model
  supports file input and ZDR is off; the chip label shows `(full PDF)` when set.
- **Scanned auto-escalation:** a PDF whose text extraction is empty while it has
  pages (`IsLikelyScanned`, a transient attach-time hint) is switched to native
  automatically when eligible — no prompt. When not eligible (ZDR, or the model
  lacks file support) a one-time message box explains why and, under ZDR, points
  the user to start a non-ZDR conversation.

### 6.4 Capability-unknown fallback

If the model is absent from the cache (fetch failed, unlisted model): treat rich
modalities as **unsupported** — fall back to text/placeholder, with a soft note
("couldn't confirm this model supports images"). Never silently send a block
that may 400.

**First run:** on a fresh install the catalog is empty until the first
background `/models` fetch completes (`RefreshIfDue` on app open, usually a few
seconds), so `TryGetModelInfo` misses and image attach is gated off. Surface a
brief "fetching model info…" affordance (or attach-but-warn) rather than
silently hiding the option; the Settings "Update Model Info" button covers the
impatient case.

---

## 7. Image normalization (attach time)

Accept broadly; normalize to a wire-safe format using GDI+ (`System.Drawing`,
in-box on .NET 3.5).

OpenRouter image wire types: **png, jpeg, gif, webp**. GDI+ on XP cannot *decode*
WebP, so WebP is rejected on input despite being a valid wire type.

| Source | Action |
|---|---|
| `image/png`, `image/jpeg`, `image/gif` — within size/dimension caps | Send as-is (original bytes) |
| `image/png`, `image/jpeg` — exceeds size/dimension cap | Downscale then re-encode: JPEG if no alpha, PNG if alpha |
| `image/gif` — exceeds size/dimension cap | Downscale then re-encode as PNG (GIF has no JPEG path; animation flattened) |
| `image/bmp`, `image/tiff`, other GDI+-decodable | Decode, downscale if needed, re-encode: JPEG if no alpha, PNG if alpha |
| `image/webp` | Reject at attach (GDI+ on XP cannot decode WebP) |

```csharp
static byte[] NormalizeImage(byte[] source, out string mediaType)
{
    using (var inMs = new MemoryStream(source))
    using (var img = Image.FromStream(inMs))
    {
        bool needsDownscale = img.Width > 1568 || img.Height > 1568;
        bool isSupportedWireType = /* png/jpeg/gif */ ...;
        if (!needsDownscale && isSupportedWireType)
        {
            mediaType = /* original */; return source;
        }
        var target = needsDownscale ? Downscale(img, 1568) : img;
        bool hasAlpha = Image.IsAlphaPixelFormat(target.PixelFormat);
        using (var outMs = new MemoryStream())
        {
            target.Save(outMs, hasAlpha ? ImageFormat.Png : ImageFormat.Jpeg);
            mediaType = hasAlpha ? "image/png" : "image/jpeg";
            return outMs.ToArray();
        }
    }
}
```

Rules:
- **Downscale before encode** to the §5 long-edge cap (**1568 px**).
- **Re-encode strategy (alpha-based):** after downscaling, use **JPEG** when the
  image has no alpha channel (`!Image.IsAlphaPixelFormat(img.PixelFormat)`), **PNG**
  when it does. Photographs (typically no alpha) shrink dramatically as JPEG;
  screenshots and logos with transparency stay lossless PNG. This is deterministic
  from `PixelFormat` — no fuzzy photo heuristic needed.
- **Do not re-encode GIF** — it's already a wire type, and re-encoding flattens
  animation to one frame. GIF is sent as-is if already within the size cap; if it
  needs downscaling, convert to PNG (GIF has no JPEG path).
- **Do not re-encode png/jpeg/gif** that are already within the size and dimension
  caps — pass the original bytes straight to `Data`.
- Transcode **once** at attach time; store the normalized bytes in `Data`. The
  viewer, transcript, and wire payload all use the same clean bytes.
- **Encode once, reuse the exact bytes — never re-encode per request.** The
  stored `Data` base64 must be byte-identical on every replayed turn. Re-running
  transcode/downscale at request time would change the bytes and break the cached
  prefix, re-billing the whole transcript on caching providers
  (`prompt-caching-design.md:162` requires byte-deterministic transforms).

---

## 8. Request rendering (`content` as parts)

This is a **two-stage seam**, not a single change — and the array machinery
already exists (`ContentValue`, for `cache_control`). Stage 1 *decides* the
representation; stage 2 *emits* it.

**Stage 1 — decide (transform, `BuildMessagesForModel`).** The transform already
runs per request and knows the selected model + ZDR state, so it resolves
`ModelCatalogService.TryGetModelInfo` and picks each attachment's representation
(§6): native block, text, or placeholder. It then:
- inlines **text/placeholder** representations into the message `Content` string,
  exactly as today (text files, PDF-as-text, image placeholders); and
- leaves **binary** representations (image, native PDF) on the request-scoped
  message's `Attachments` for stage 2 to emit.

**New plumbing:** today the transform's output `Attachments` are dropped before
`BuildRequestBody` (nothing downstream reads them). Binary attachments chosen for
native carriage must now survive from the transform through the orchestrator into
`BuildRequestBody`/`ContentValue`. (`WithCacheControl` already preserves
`Attachments` on its request-scoped clone — `ChatModels.cs:52` — so the field is
carried; verify the orchestrator passes it through, and that the transform itself
doesn't drop it.) The transform must continue to preserve `tool_calls` /
`tool_call_id` (`McpChatOrchestrator.cs:195`).

**Stage 2 — emit (`ContentValue`, `OpenRouterClient.cs:163`).** Extend the
existing string-or-array logic to emit a **multi-part array** when the message
carries binary attachments:

```jsonc
"content": [
  { "type": "text", "text": "<user message + any text-rendered attachments>" },
  { "type": "image_url", "image_url": { "url": "data:image/png;base64,…" } },
  { "type": "file", "file": { "filename": "report.pdf",
                              "file_data": "data:application/pdf;base64,…" } }
]
```

- The `image_url.url` field accepts a **base64 data URL** — no hosting needed.
- **Compose with `cache_control`:** `ContentValue` today emits *either* a string
  *or* a one-part `cache_control` array. With binary parts present it must build
  the multi-part array and, when `m.CacheControl` is set, attach `cache_control`
  to the **last part** (so the heavy image/PDF sits inside the cached prefix),
  respecting the 4-breakpoint cap. Plain string stays the path when there are no
  binary parts and no cache flag — no behavior change for text-only turns.
- Keep the existing `text.Length == 0` guard (Anthropic rejects empty text
  parts): omit the text part when the message text is empty but binary parts
  exist.

**Auxiliary requests use text-only.** Title generation
(`Conversation.RequestTitleWithRetry:293`) and any future summarization/compaction
send a derived copy of the user message; these must use the **text/placeholder**
representation only — never binary parts (cost, and some endpoints reject images
on a title-sized request).

Tests: cover the multi-part array shape, the `cache_control`-on-last-part
composition, and text-only fallback in `OpenRouterClientTests`.

---

## 9. ZDR rules

ZDR risk is about **who processes the raw file**, not the transport. base64 in
the body is the same trust boundary as text in the body.

ZDR is enforced by the `provider: { zdr: true }` request flag
(`ClientProperties.Zdr` → `BuildRequestBody`; one-way latched per `Conversation.Zdr`
/ `ZdrFirstMessageIndex`). That flag constrains the **model endpoint** OpenRouter
routes to — it does **not** govern OpenRouter's file-parser plugins, which is
exactly why the `mistral-ocr` / `cloudflare-ai` engines need separate gating
below. (The request body, including base64 bytes, transits a local temp file
that `CleanupTempFiles` deletes in `finally` — it never persists.)

| Path | Processor | ZDR |
|---|---|---|
| iTextSharp extraction | **Local machine** | ✅ Cleanest — file never leaves the box |
| Image as base64 | The model endpoint only | ✅ Inherits endpoint policy (ZDR routing already enforces) |
| Native PDF (`native` engine) | Model provider's own handling | ⚠️ Inherits endpoint policy; residual uncertainty re OpenRouter buffering |
| `mistral-ocr` / `cloudflare-ai` plugins | **Third-party processor** | ❌ Unverified retention — block under ZDR |

**When `conversation.Zdr == true`:**
- PDF → force local iTextSharp text path. **Disable all OpenRouter PDF plugin
  engines** (`mistral-ocr`, `cloudflare-ai`). The engine selection is hard-wired
  off, not merely defaulted off.
- Image → allowed (rides to the ZDR-routed endpoint; same boundary as text).
- Native PDF → permitted only against a confirmed-ZDR endpoint; conservative
  default is to disable it too and keep processing local.

**ZDR is locked once set** — it cannot be disabled mid-conversation. The
scanned-PDF message must therefore point users to a *new* conversation, not a
toggle:

> This PDF appears to be scanned (no extractable text). Reading it requires OCR,
> which routes through a third-party processor whose data-retention policy can't
> be confirmed — so it's disabled in Zero-Data-Retention conversations. To OCR
> this document, **start a new conversation without ZDR**.

Because the default was already local extraction, the common case needs no
special handling under ZDR — only the escalations are pruned.

---

## 10. Context & cost management

The binary is replayed every turn (like text today). Both levers below **reuse
existing machinery** rather than adding new systems.

- **Prompt caching is already implemented — reuse it.** `ChatMessage.CacheControl`
  + `ContentValue`'s ephemeral array, the per-provider `ModelSupportsPromptCaching`
  gate (`OpenRouterClient.cs:184`), request-scoped `WithCacheControl` cloning, and
  sticky provider routing (`CacheWarmProvider` / `provider.order`) already exist.
  The only new work is the multimodal **composition** from §8 — place the
  `cache_control` breakpoint on the last/heaviest part so the image/PDF prefix
  caches at ~0.1× on replay. No new caching subsystem.
- **Prune off the numbers we already have — no client tokenizer.** The context
  meter is driven entirely by the API's `usage.prompt_tokens` (`LastPromptTokens`)
  vs `TryGetContextLength` (`MainForm.cs:5750`); there is no client-side token
  estimator and we won't add one. Drive pruning off those same two values: when
  the **previous** turn's `LastPromptTokens` approaches the model's context length
  (or the §5 cumulative request limit is near), prune before the next send. This
  is reactive (one turn behind) but accurate and free — images/PDFs are counted by
  the API once sent, so the meter stays correct with zero tokenizer work.
- **Prune-with-placeholder, never delete.** When pruning, swap the *rendered*
  representation for a text placeholder (`[image earlier in conversation:
  photo.png]`) while keeping the durable bytes in the transcript. The file stays
  viewable and re-activatable; only its wire form is trimmed. Prune oldest-first.
  **Prune state is transient** — recomputed each session from the live token
  numbers; no new persisted fields needed. (Pinning deferred to a later iteration.)
- **Pruning triggers (v1 — global-conservative, not per-provider):**
  - *Primary:* prior turn's `LastPromptTokens` approaches `TryGetContextLength`.
  - *Safety net:* replayed image count exceeds **10** OR cumulative replayed
    attachment bytes exceed **~4 MB** — whichever fires first. These bounds are
    safe across all providers without per-provider catalog data. Refinement to
    per-provider limits is a later enhancement.

This resolves the "drops out after one question" worry: the file persists and
replays until a deliberate budget/prune decision, and even then survives for
viewing.

**Implemented mechanism.** `AttachmentPruner.ComputePruned` (pure, unit-tested)
runs inside `BuildMessagesForModel` each request and returns the reference set of
native-eligible attachments to demote. It walks the eligible attachments
**newest-first**, keeping each while within the active caps and demoting the rest:

- *Caps:* **10 images** and **~4 MB** cumulative base64 payload normally; under
  context pressure they tighten to **3 images / ~1 MB**. Pressure is
  `priorPromptTokens ≥ 0.85 × contextLength` (both from the existing usage meter;
  pass `0` to disable the token trigger — the count/byte safety net still applies).
- *Current turn is exempt:* attachments on the newest message that carries binary
  are never pruned, so the image/PDF the user just attached always rides native.
- *Demotion render:* a pruned image becomes `[image earlier in conversation: name]`;
  a pruned native PDF falls back to its **extracted text** when it has a layer
  (lighter than the bytes, still faithful) and only to `[PDF earlier in
  conversation: name]` when scanned. The durable bytes stay in the transcript.

---

## 11. In-app image viewer

**The click→viewer path already exists** — don't build new plumbing. Attachment
pills render in the transcript (`ChatTranscriptControl.DrawAttachmentPills:1650`)
and in the pending banner (`MainForm.CreateAttachmentChip`). The two surfaces use
different interaction models:

- **Transcript pills** — **single left-click**: `OnMouseDown` (`ChatTranscriptControl.cs:2873`)
  sets the pressed visual state; `OnMouseUp` (`:2789`) calls `OpenAttachmentInViewer`.
  `HitTestAttachmentPill` provides the pill→attachment index mapping.
- **Pending-banner chips** — **double-click**: `panel.DoubleClick` in
  `MainForm.CreateAttachmentChip`.

Images reuse the **same** transcript-pill + single-click path (no inline
thumbnail in v1 — a possible later enhancement).

The only change: extend `FileViewerForm` to branch on attachment kind — when
`Kind == Image`, decode `Data` into a docked `PictureBox` (`SizeMode = Zoom`)
instead of the existing `RichTextBox` text view. Decode as below:

```csharp
byte[] bytes = Convert.FromBase64String(att.Data);
using (var ms = new MemoryStream(bytes))
using (var tmp = Image.FromStream(ms))
    pictureBox.Image = new Bitmap(tmp);   // own pixels; stream can go
```

GDI+ gotchas to respect:
- **MemoryStream lifetime:** `Image.FromStream` reads lazily — either keep the
  stream alive for the image's life, or copy into a standalone `Bitmap` (above)
  and free the stream.
- **Dispose on close** (Image + stream) — XP is sensitive to GDI handle leaks.
- **Strip any `data:` prefix** before `FromBase64String` (our `Data` stores raw
  base64 + separate `MediaType`, so there's none — by design).
- **WebP unviewable** (and unattachable, §7).
- Large images materialize full uncompressed pixels — §5 caps keep this safe.

---

## 12. Affected components (change map)

| Area | File / symbol | Change |
|---|---|---|
| Attachment model | `Models/ChatModels.cs` `AttachedFile` | Add `Kind`, `MediaType`, `Data`, `Width/Height`, `Clone()` |
| Image extractor | **new** `Services/ImageAttachmentExtractor.cs` + register in `AttachmentService` | `CanHandle` png/jpeg/gif/bmp/tiff (reject webp); `Extract` → normalize/transcode to PNG, downscale, base64 into `Data` |
| PDF extractor | `Services/PdfAttachmentExtractor.cs` | Also retain original bytes in `Data` (keep extracted text in `Content`) |
| Transcript | `Data/ConversationStore.cs` `MessageDto` | New `AttachedFile` fields round-trip (Newtonsoft, ignore-null); legacy parser untouched |
| Render — decide | `Forms/MainForm.cs` `BuildMessagesForModel` (`:3479`) | Choose representation per `ModelInfo`+ZDR; inline text/placeholder; **keep binary on `Attachments`**; apply `AttachmentPruner` demotions |
| Prune budgeting | **new** `Services/AttachmentPruner.cs` | Pure §10 oldest-first prune-with-placeholder; linked into the test project (`AttachmentPrunerTests`) |
| Render — passthrough | orchestrator path (`McpChatOrchestrator` → `OpenRouterClient`) | Carry transform-output `Attachments` into `BuildRequestBody` (today dropped) |
| Wire — emit | `Services/OpenRouterClient.cs` `ContentValue` (`:163`) | Extend existing array path: multi-part (text + `image_url`/`file`); compose `cache_control` on last part |
| Edit/resend | `Forms/MainForm.cs` (`:2370`, `AreAttachmentsEqual`) | Deep-copy via `AttachedFile.Clone()` (today drops `Data`/`Kind`); compare new fields |
| Auxiliary requests | `Models/Conversation.cs` `RequestTitleWithRetry` (`:293`) | Use text-only representation; never send binary parts |
| Model fetch | `Services/ModelCatalogService.cs` | **Exists** — reuse curl `GET /models`, 24h refresh, "Update Model Info" button, background thread |
| Model cache | `Services/ModelCatalogService.cs` (**extend**) + `models.json` | Retain full model objects; add `ModelInfo` + `TryGetModelInfo` / `SupportsImageInput` / `SupportsFileInput`; keep `TryGetContextLength` |
| Capability gate | attach UI (`btnAttach_Click` / drag-drop) + render | Consult `ModelCatalogService.TryGetModelInfo`; unknown / first-run → conservative |
| Viewer | `Forms/FileViewerForm.cs` (**extend**, not new) | Branch on `Kind == Image` → `PictureBox`; click path already wired |
| Tests | `OpenRouterClientTests`, `ConversationStoreTests` | Multi-part array + `cache_control` composition; new-field round-trip; model-cache modality parse; `Clone()` / edit round-trip |

---

## 13. Suggested phasing

1. **Extend `ModelCatalogService`** — retain full `/models` objects (`models.json`),
   add `ModelInfo` + modality accessors; keep `TryGetContextLength`. Independently
   useful; unblocks gating.
2. **Data model + storage** — extend `AttachedFile`/`MessageDto`; round-trip
   tests; backward-compat verification.
3. **Images end-to-end** — attach + normalize/transcode + content-array render +
   capability gate + viewer.
4. **PDF native escalation** — opt-in toggle, scanned detection, ZDR gating.
5. **Context/cost** — `cache_control`, prune-with-placeholder.

---

## 14. Decisions log

All design questions are resolved. The table below records what was decided and
the §-reference where it is implemented.

| Question | Decision | Where |
|---|---|---|
| Native PDF under ZDR | **Disable entirely** — PDF is always local iTextSharp text under ZDR; scanned PDFs prompt the user to open a new non-ZDR conversation. | §9 |
| Image downscale target | **1568 px** long edge (Anthropic's no-auto-downscale threshold; max fidelity). | §5, §7 |
| Per-image byte cap | **500 KB** (configurable). | §5 |
| Re-encode strategy | **Alpha-based:** JPEG when no alpha channel, PNG when alpha present. Deterministic from `PixelFormat`; no photo heuristic. GIF is sent as-is or converted to PNG (never JPEG). | §7 |
| Prune/pin state | **Transient** — recomputed each session; no persisted fields. Pinning deferred to a later iteration. | §10 |
| Pruning threshold | **Global-conservative:** 10 replayed images OR ~4 MB cumulative bytes (safety-net); token trigger tightens to 3 images / ~1 MB when `LastPromptTokens ≥ 0.85 × TryGetContextLength`. Newest turn exempt; pruned native PDFs fall back to extracted text. Per-provider refinement deferred. | §5, §10 |
