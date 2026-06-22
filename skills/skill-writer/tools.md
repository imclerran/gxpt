# Built-in tools a skill can use

Read this while interviewing and shaping the skill, whenever the task might involve doing
something - reading or writing files, running commands, searching the web, building, using git.
The point: propose the *right* tool for the job, **even if it isn't in your context right now.**

GxPT ships the servers below. The user can turn any server off, and tools can sit behind
`reveal_tools`, so the set visible to you at this moment may be smaller than the full set here.
**Don't reason only from what you can currently see.** When a skill would benefit from a tool
that's built in but not currently available, name it anyway in the skill's instructions and tell
the user that skill will need the `<server>` server enabled. A skill is written for a future
session where the needed servers may well be on.

Tools are addressed as `server__tool` (for example `files__read`, `git__commit`).

## files - read and change files in the workspace
- `files__read` - read a text file's contents
- `files__list` - list a directory's entries
- `files__search` - search file contents by string or regex
- `files__write` - create or overwrite a text file
- `files__edit` - replace an exact text span in a file
- `files__delete` - delete a file or empty directory

## command - run a shell command
- `command__run` - run a command line on Windows (cmd.exe), capturing stdout, stderr, exit code.
  Runs in the workspace directory. Use this for running existing commands and tools (the build, a
  linter, a script already in the repo) or anything without a dedicated tool. A script *you create
  as part of a skill*, though, lives in the skill and runs via `run_skill_script` (see
  `scripts.md`) - not here.

## git - version control in the workspace
- Read: `git__status`, `git__diff`, `git__log`
- Common: `git__add`, `git__commit`, `git__branch`, `git__stash`, `git__fetch`
- Heavier / history-changing: `git__push`, `git__pull`, `git__checkout`, `git__restore`,
  `git__merge`, `git__rebase`, `git__reset`, `git__rm`, `git__cherry_pick`

## web - reach the internet
- `web__search` - search the web; returns titles, URLs, and snippets
- `web__extract` - fetch the full text of pages you already have URLs for

## msbuild - build .NET projects and solutions
Tool names are **discovered from the toolchains installed on the machine**, so they vary:
- `msbuild__build_<version>` - build with a specific MSBuild (e.g. `build_4_0`, `build_3_5`)
- `msbuild__build_solution_<year>` - build a solution via that year's Visual Studio
If a skill builds something, instruct your future self to pick the appropriate build tool from
what's available rather than hardcoding one name.

## memory - long-term memory across conversations
- `memory__remember`, `memory__read_memory`, `memory__update_memory`, `memory__forget`,
  `memory__consolidate`. A skill rarely needs these directly - memory is usually managed by the
  app, not by a skill - but they exist.

## skills - the skill system itself (host + the extensions server)
- `open_skill(names[])` - open a skill and load its body
- `read_skill_file(slug, relpath)` - read a skill's bundled file
- `run_skill_script(slug, relpath, args[])` - run a skill's declared `.bat` (see `scripts.md`)
- Authoring (what you're using now): `create_skill`, `write_skill_file`, `update_skill`,
  `edit_skill_file`, `list_skill_files`, `delete_skill_file`, `delete_skill`, `validate_skill`.
  These are only available while this skill-writer skill is enabled. (The same server also hosts
  the agent-authoring tools, behind the separate agent-writer skill.)

## Also check your own current tools - the user may have added their own

Beyond the built-ins above, the user can wire up extra MCP servers in their `mcp.json`. Those
tools show up in **your** tool list for this session. So when you're working out what a skill
needs, also look at the tools you actually have right now: if you see a relevant non-built-in
tool (some project-specific or third-party MCP server), it's fair to propose the skill use it -
just tell the user the skill will depend on that server staying configured, since it isn't one of
the built-ins everyone has.

## How to use this when proposing a skill

- Match the task to the smallest, most specific tool - prefer `files__edit` over `command__run`
  with `sed`, `git__commit` over `command__run` with raw git, and so on.
- Name the exact tool in the skill body so your future self reaches for the right one.
- If a needed server might be off, say so in the hand-off: "this skill uses the `web` server -
  enable it if it's not on."
- Don't invent tools. Only propose tools from the built-in list above or ones actually present in
  your current context.
