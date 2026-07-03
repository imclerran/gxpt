# Krypton Chrome Theming — Design

**Status:** In progress — DLL referenced; `KryptonThemeBridge` wired into the
theme-apply path. Form/control migration to `Krypton*` is the remaining work.
**Last updated:** 2026-06-30

This note describes adopting the **Krypton Toolkit** as the engine for *window
chrome* (forms, buttons, inputs, menus, status strip, dialogs) while keeping the
custom `ChatTranscriptControl` and the Catppuccin code highlighting exactly as
they are. The goal is a cohesively styled UI on Windows XP / .NET 3.5, including
chrome that finally participates in dark mode.

---

## 1. Goal & scope

**In scope**

- Theme all the *stock WinForms* chrome via Krypton so it stops rendering as
  un-themed system light-grey (the current state in "dark mode").
- Let the user pick a theme that drives **both** the Krypton chrome **and** the
  transcript palette from one switch.
- Keep dark mode **orthogonal** to color: the user independently chooses an
  accent color and light/dark.

**Out of scope (deliberately unchanged)**

- `ChatTranscriptControl` stays a custom-painted `UserControl`. Krypton has no
  equivalent for markdown + selectable text + inline diffs + custom scrollbar.
- Code highlighting stays **Catppuccin** (Latte light / Macchiato dark) in
  `Highlighting/SyntaxHighlighter.cs`. Krypton has no concept of token colors.
- The custom content panels (`QuestionPanel`, `ToolApprovalPanel`,
  `AgentActivityPanel`, `DiffPreviewPanel`) keep their custom *content* painting;
  their *chrome* may adopt Krypton later, per-panel.

---

## 2. Background: how theming works today

- `Services/ThemeService.cs` loads named themes (`blue`/`red`/`orange`, from
  JSON in `resources/Themes` + built-ins) into a `ThemeColors` struct. Each
  `ThemeDefinition` carries **both** a `Light` and a `Dark` `ThemeColors`, and
  `Get(bool dark)` picks between them. **The content side is already orthogonal
  (color × dark).**
- The transcript reads a flat `ThemeColors` + a `dark` bool in
  `ChatTranscriptControl.ApplyThemeFromSettings()` (~line 623). A few colors
  (error red, diff +/- green/red) are *derived* from `dark` there, not stored in
  `ThemeColors`.
- Syntax colors are keyed purely off `dark` via
  `SyntaxHighlighter.GetTokenColorForTheme(tokenType, dark)`.
- Two settings keys exist today: legacy `theme` (`dark`/light) and `color_theme`
  (`blue`/`red`/`orange`).

**The gap:** none of the *chrome* (MenuStrip, StatusStrip, TabControl,
SplitContainer, dialogs, buttons, inputs) is themed. Dark mode is applied
piecemeal by manual `BackColor`/`ForeColor` assignments in a handful of forms;
most chrome stays system light-grey even in dark mode.

---

## 3. Decision

Adopt Krypton as the **chrome engine + single theme switch**, but keep the
authored content palette as the source of truth for everything Krypton can't
represent. Authority inverts to Krypton; the *data* stays ours.

- A **theme** = `(accent, mode)` where `accent ∈ {Blue, Orange, Red, …}` and
  `mode ∈ {Light, Dark}`. Orthogonal, by generation — not a hand-authored
  matrix.
- One generator produces, for any `(accent, mode)`:
  1. a custom **`KryptonPalette`** → drives all chrome via
     `KryptonManager.GlobalPalette`;
  2. a **`ThemeColors`** → drives the transcript (unchanged intake path);
  3. the **Catppuccin variant** (`Macchiato` if dark, else `Latte`).

We do **not** expose Krypton's stock palettes as the picker: they are
pre-combined (color+mode fused) points, so they can't vary the two axes
independently, and Krypton 4.x/5.5x ships no true-dark stock palette anyway.

---

## 4. Library

- **Package:** `Krypton.Toolkit`, the **5.500/5.5xx** line (e.g. `5.550.2108.1`)
  — the legacy line that ships a **`net35`** build. **NOT** the modern
  `105.x` line (that is .NET 4.6.2+ and will not load on XP/3.5). Pin the
  version; never "update to newest."
- **License:** BSD-3-Clause (the entire suite is open source). The built DLL is
  freely redistributable — commit it to `lib/` and bundle it in
  `GxPT.Setup.msi`. Only `Krypton.Toolkit` is needed; Docking/Navigator/Ribbon/
  Workspace are equally free but unused for now (and Workspace has no net35
  build).
- **No build required:** download the `.nupkg`, rename to `.zip`, extract the
  assembly from `lib\net35\`, drop it (and its `.xml`) into `GxPT/lib/`.
- **Reference** in `GxPT.csproj`, mirroring the existing `DotNetZip`/`itextsharp`
  entries:
  ```xml
  <Reference Include="Krypton.Toolkit">
    <SpecificVersion>False</SpecificVersion>
    <HintPath>lib\Krypton.Toolkit.dll</HintPath>
  </Reference>
  ```
- **Namespace / assembly:** the 5.5xx open-source fork **renamed** the namespace
  and assembly from `ComponentFactory.Krypton.Toolkit` to **`Krypton.Toolkit`**.
  Confirmed via the XML doc that ships alongside the DLL: `<name>Krypton.Toolkit</name>`.
  All `using` statements and type references use `Krypton.Toolkit`; the DLL
  filename is `Krypton.Toolkit.dll`.

---

## 5. Krypton API facts (5.5xx, verified against source)

All types in namespace `Krypton.Toolkit` (confirmed from the shipped XML doc).

- Apply a custom palette globally:
  ```csharp
  KryptonManager.GlobalPalette = myPalette;                 // IPalette
  KryptonManager.GlobalPaletteMode = PaletteModeManager.Custom;
  ```
  Setting `GlobalPalette` updates all Krypton controls in one action.
- `KryptonManager.GlobalApplyToolstrips` (bool, default true) — when set, Krypton
  applies palette colors to **all** stock WinForms `ToolStrip`-derived controls
  (MenuStrip, StatusStrip, ToolStrip) automatically. The existing controls do **not**
  need to be replaced with Krypton equivalents; this is why `SafeToolStripRenderer`
  and `StatusStripTooltipFix` can be retired once the palette is applied.
- `KryptonPalette` is the custom palette component. Key members:
  - `BasePaletteMode` (enum **`PaletteMode`** — *different enum* from the
    manager's `PaletteModeManager`): inherit a base scheme, then override.
  - "Populate from base" copies base values into the palette; styles prefixed
    `Custom` are excluded from inheritance (stay local).
  - Style tree groups: `ButtonStyles`, `HeaderStyles`, `FormStyles`,
    `ControlStyles`, `InputControlStyles`, `PanelStyles`, `LabelStyles`,
    `TabStyles`, `SeparatorStyles`, `GridStyles`, `Common`, `ToolMenuStatus`
    (for MenuStrip / StatusStrip / ToolStrip colors), `ContextMenu`, `Images`, …
  - Import/export palette definitions as XML.
- **`PaletteMode` enum** (used by `KryptonPalette.BasePaletteMode`): includes
  `Office2007Blue/Silver/White/Black`, `Office2010Blue/Silver/White/Black`,
  `Office2013`, `Office365Black/Blue/Silver/White`, `SparkleBlue/Orange/Purple`,
  **`VisualStudioDark`**, **`VisualStudioLight`**, `Custom`. The two VS modes are the
  built-in dark/light flat themes — `VisualStudioDark` is a viable dark base.
- **`PaletteModeManager` enum** (used by `KryptonManager.GlobalPaletteMode`):
  mirrors `PaletteMode` (same members) but lacks the `Global` sentinel. Use
  `PaletteModeManager.Custom` when setting a custom `GlobalPalette`.
- **Renderer vs palette:** the *renderer* controls shape/gloss (Office-2007
  glass, Sparkle, Office-2010 flat); the *palette* controls colors. Basing on a
  mode inherits its renderer; `Renderer` can also be set explicitly to mix a
  renderer with custom colors. This is what lets us keep an Office/Sparkle
  *style* with our own *colors*.

> Note: 5.5xx has **no single base-hue knob** (that's a modern-fork feature).
> Recoloring = overriding the specific style groups (form/header/button/input).
> A little Office-2007 glass sheen is baked into the renderer and won't fully
> recolor; Sparkle recolors more uniformly and is better for custom dark bases.
> `VisualStudioDark` (flat, no gloss) is the cleanest dark starting point.

---

## 6. The generator

```
Generate(accentSeed, mode):
    (neutrals, baseMode) = ModeTemplate[mode]
        # Light → Office2007Blue base + light neutral backgrounds/text
        # Dark  → VisualStudioDark base + #242424-family neutrals (flattest dark)
    palette = new KryptonPalette { BasePaletteMode = baseMode }
    apply neutrals to: FormStyles, ControlStyles, PanelStyles, InputControlStyles
    inject accentSeed into: ButtonStyles (standalone),
                            HeaderStyles, TabStyles,
                            selection/checkmark colors, link color
    return palette

ThemeColors  = ThemeService theme.Get(mode == Dark)   # already exists
Catppuccin   = (mode == Dark) ? Macchiato : Latte     # already keyed on dark
```

- **Mode** selects base renderer + neutral scheme (built once per mode, reused
  across all accents).
- **Accent** is injected on top (one seed per color; light+dark for free).
- Adding a color = one accent seed. No 2N hand-authoring.

---

## 7. Integration plan (low blast radius)

The transcript intake and syntax path **do not change**. Work concentrates in:

1. ✅ **New `Services/KryptonThemeBridge.cs`** — generates a `KryptonPalette` for
   `(accent, mode)` and applies it via `KryptonManager.GlobalPalette` +
   `GlobalPaletteMode = Custom` + `GlobalApplyToolstrips = true`. The dark-mode
   neutral backdrop is pulled straight from `ThemeService.GetColors(true)` so
   chrome and transcript share the exact same background — no new color
   authoring. Accent seeds (blue/red/orange) are injected into button + header
   style groups. Base mode: `VisualStudioDark` (dark) / `Office2007Blue` (light).
2. ✅ **`Managers/ThemeManager.cs`** — `ApplyThemeToAllTranscripts` now calls
   `KryptonThemeBridge.Apply()` first, so the one existing apply path (startup +
   every theme change) updates chrome + transcript together. The bridge reads the
   existing `theme` + `color_theme` settings directly, so **no settings-schema
   change** was needed yet; unifying the two keys into a single `(accent, mode)`
   switch can follow when the picker UI is reworked.
3. ⏳ **Forms** — migrate `Form` → `KryptonForm` and stock controls → `Krypton*`
   incrementally, leaf dialog first (About or FileViewer), MainForm chrome last.
   Until this happens, only the stock `MenuStrip`/`StatusStrip` re-theme (via
   `GlobalApplyToolstrips`); other stock controls stay system-drawn. Once forms
   migrate, `SafeToolStripRenderer` / `StatusStripTooltipFix` can retire. Custom
   `ToolStripItem`s (`ContextMeterItem`, `StopGenerationItem`) keep working; point
   their paint at the active palette.

> **`ThemeService` extension not needed for v1.** The generator lives entirely in
> `KryptonThemeBridge` and reuses `ThemeService.GetColors(dark)` for neutrals, so
> `ThemeService` itself is untouched. A future richer accent model can move there.

Unchanged: `ChatTranscriptControl.ApplyThemeFromSettings`, the derived
error/diff colors, `SyntaxHighlighter`.

---

## 8. Open questions / decisions

- **Light base renderer:** Office 2007 (glossy) vs Office 2010 (flatter)? Affects
  the period aesthetic.
- **Accent axis source:** our own seed hues (recommended) vs borrowing Krypton
  scheme colors.
- **Per-control accent strategy:** button-style variants (`Custom1/2/3`) vs
  per-control `StateCommon` overrides for the few accent-colored controls.
- **Dark legibility QA:** verify injected accents stay legible on `#242424`
  across disabled/border states — centralized in the dark mode template.

---

## 9. Build / packaging checklist

- DLL in `GxPT/lib/`, referenced via HintPath, Copy Local = True (default) so it
  lands in `bin\` next to `GxPT.exe`.
- Confirm `GxPT.Setup` picks up the DLL as a detected dependency of the primary
  output; add manually if not.
- The `net35` assembly must be the .NET 3.5-targeted build (from the 5.5xx
  `lib\net35\` folder), or it will not load on XP.
