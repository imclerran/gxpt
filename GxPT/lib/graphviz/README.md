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
| `config6` | **Already committed here.** Trimmed plugin config — only loads the core, dot-layout, and GDI+ plugins so no extra dependency DLLs (cairo/pango/gd/iconv/...) are needed. Do not overwrite it with the stock `config6`, or `dot.exe` will try to load plugins whose dependencies aren't shipped and pop missing-DLL dialogs. |
| `gvc.dll` | Graphviz context. |
| `cdt.dll` | Container data types. |
| `cgraph.dll` | Graph library. |
| `Pathplan.dll` | Path planning. |
| `ltdl.dll` | libltdl (plugin loader). |
| `libltdl-3.dll` | What `ltdl.dll` links against. |
| `libexpat.dll` | XML parsing. |
| `zlib1.dll` | Compression. |
| `gvplugin_core.dll` | Core output devices. |
| `gvplugin_dot_layout.dll` | The `dot` layout engine. |
| `gvplugin_gdiplus.dll` | PNG output via the OS GDI+ (`gdiplus.dll`) — no extra image libraries to ship. |
| `msvcr90.dll` | VC++ 2008 CRT. May already be present system-wide; ship it to be safe on a clean machine. |

PNG output goes through the **GDI+** plugin, which uses the Windows-provided `gdiplus.dll`,
so none of cairo/pango/freetype/fontconfig/libpng/jpeg/iconv are required.

## Why the trimmed `config6`

Stock Graphviz lists every plugin in `config6`. On startup `dot.exe` tries to `dlopen` each
one; the gd/pango/neato plugins drag in `iconv.dll`, `jpeg62.dll`, `libcairo-2.dll`,
`libfreetype-6.dll`, `libpng12.dll`, `msvcp90.dll`, etc. Missing-dependency dialogs result.
The committed `config6` lists only `gvplugin_core`, `gvplugin_dot_layout`, and
`gvplugin_gdiplus`, which keeps the shipped file set to the short list above.

If you ever regenerate the config (`dot -c`), re-trim it to these three plugins.
