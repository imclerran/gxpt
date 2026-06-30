# Bundled Graphviz (portable, 2.38)

GxPT renders fenced ` ```dot ` code blocks as images by shelling out to a small,
**portable** Graphviz 2.38 build that lives in this folder. At runtime the app looks for
`Lib\graphviz\dot.exe` next to `GxPT.exe` (see `Services/GraphvizRenderer.cs`); if it isn't
there, `dot` blocks simply fall back to being shown as ordinary highlighted code — the app
keeps working without Graphviz.

The Graphviz binaries are **not** committed to this repository (they're large native files
and licensed separately). To enable graph rendering, drop the files below into this folder.
The build (`<Content Include="Lib\graphviz\*.exe|*.dll" />` in `GxPT.csproj`) copies whatever
is present here into the output directory, and the setup project harvests it into the install.

## Files to place here

Copy these out of a Graphviz 2.38 Windows ZIP/install (`bin/` folder) into this directory:

| File | Purpose |
|---|---|
| `dot.exe` | The layout/render driver GxPT invokes (`dot -Tpng`). |
| `config6` | **Already committed here.** Trimmed plugin config — only loads the core, dot-layout, neato-layout, and GDI+ plugins so no extra dependency DLLs (cairo/pango/gd/iconv/...) are needed. Do not overwrite it with the stock `config6`, or `dot.exe` will try to load plugins whose dependencies aren't shipped and pop missing-DLL dialogs. |
| `gvc.dll` | Graphviz context. |
| `cdt.dll` | Container data types. |
| `cgraph.dll` | Graph library. |
| `Pathplan.dll` | Path planning. |
| `ltdl.dll` | libltdl (plugin loader). |
| `libltdl-3.dll` | What `ltdl.dll` links against. |
| `libexpat.dll` | XML parsing. |
| `zlib1.dll` | Compression. |
| `gvplugin_core.dll` | Core output devices. |
| `gvplugin_dot_layout.dll` | The `dot` layout engine (hierarchical). |
| `gvplugin_neato_layout.dll` | The `neato`/`fdp`/`sfdp`/`twopi`/`circo`/`osage`/`patchwork` engines (force-directed, radial, circular, treemap). Needed for the more compact, square-ish layouts. |
| `gvplugin_gdiplus.dll` | PNG output via the OS GDI+ (`gdiplus.dll`) — no extra image libraries to ship. |

Plus the **Visual C++ 2008 runtime** that these binaries link against — see the next section; it is
**not** a simple drop-in and is not part of the Graphviz distribution.

## Visual C++ 2008 runtime (`msvcr90.dll` / `msvcp90.dll`)

The official Graphviz 2.38 binaries are built with Visual Studio 2008, so they depend on the VC9
C runtime (`msvcr90.dll`) and — for `gvplugin_neato_layout.dll` — the C++ runtime (`msvcp90.dll`).
These are **not** Graphviz files and are redistributed separately by Microsoft. (A dev box usually
already has them in `WinSxS` because VS or some other app installed them, which is why `dot.exe` can
render there with nothing extra. A clean Windows XP machine generally won't.)

Crucially, the Graphviz DLLs carry embedded **side-by-side (SxS) manifests** that require the
`Microsoft.VC90.CRT` assembly, so dropping the bare `msvcr90.dll`/`msvcp90.dll` next to `dot.exe`
is **not** reliable — on a clean machine it raises a side-by-side configuration error. Use one of:

- **Installer (preferred):** add the `Microsoft_VC90_CRT_x86.msm` merge module (plus
  `policy_9_0_Microsoft_VC90_CRT_x86.msm`) to `GxPT.Setup`. Installs the CRT into `WinSxS`
  correctly and satisfies the SxS manifests. Found under
  `…\Common Files\Merge Modules\` on a machine with VS2008.
- **Portable / xcopy build:** flatten the contents of the `Microsoft.VC90.CRT` folder — `msvcr90.dll`,
  `msvcp90.dll`, `msvcm90.dll`, and `Microsoft.VC90.CRT.manifest` — directly into this directory, next
  to `dot.exe` (not in a subfolder, so the `*.dll` / `*.manifest` content globs in `GxPT.csproj` copy
  them to the output). App-local SxS resolves the assembly from the loading module's directory. Get the
  folder from `…\Microsoft Visual Studio 9.0\VC\redist\x86\Microsoft.VC90.CRT\`.
- **Or** install the **VC++ 2008 SP1 Redistributable** (`vcredist_x86.exe`, 9.0.30729) on the target.

Use the **SP1 (9.0.30729)** version; its publisher policy also satisfies binaries that reference the
older RTM (9.0.21022) CRT. If you skip the C++ runtime entirely, also drop the neato plugin (remove
`gvplugin_neato_layout` from `config6`) — `dot` alone needs only the C runtime.

Because rendering degrades gracefully (a `dot.exe` that can't start just makes the fence fall back to
a code block), shipping the CRT is optional; without it, graphs simply won't render on a bare OS.

## Layout engines

GxPT picks the engine from the **code-fence language**, so the model can request whichever layout
fits the graph:

| Fence | Engine | Good for |
|---|---|---|
| ` ```dot ` (also `graphviz`, `gv`) | `dot` | Directed hierarchies / flowcharts. Can get very tall. |
| ` ```neato ` | `neato` | Spring-model undirected graphs; compact, roughly square. |
| ` ```fdp ` | `fdp` | Force-directed; handles `subgraph cluster_*` groupings. |
| ` ```twopi ` | `twopi` | Radial layout around a root node. |
| ` ```circo ` | `circo` | Circular layout. |

The non-`dot` engines come from `gvplugin_neato_layout.dll`; if that plugin (and `msvcp90.dll`)
isn't present, those fences fall back to a normal highlighted code block.

`gvplugin_neato_layout.dll` also provides `sfdp` (large-graph force-directed) and
`osage`/`patchwork` (treemap) — `config6` loads them — but GxPT does not expose those fences,
since they rarely suit programming diagrams. To enable one, add its name to `TryGetGraphEngine`
in `Controls/ChatTranscriptControl.cs`.

PNG output goes through the **GDI+** plugin, which uses the Windows-provided `gdiplus.dll`,
so none of cairo/pango/freetype/fontconfig/libpng/jpeg/iconv are required.

## Why the trimmed `config6`

Stock Graphviz lists every plugin in `config6`. On startup `dot.exe` tries to `dlopen` each
one; the gd/pango/neato plugins drag in `iconv.dll`, `jpeg62.dll`, `libcairo-2.dll`,
`libfreetype-6.dll`, `libpng12.dll`, `msvcp90.dll`, etc. Missing-dependency dialogs result.
The committed `config6` lists only `gvplugin_core`, `gvplugin_dot_layout`,
`gvplugin_neato_layout`, and `gvplugin_gdiplus`, which keeps the shipped file set to the short
list above.

If you ever regenerate the config (`dot -c`), re-trim it to these four plugins.
