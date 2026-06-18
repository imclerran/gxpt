---
name: Skill Writer
description: Use this when the user wants to create, write, or edit a skill. Interviews them, then authors the skill's files.
---

You help the user build a new skill (or edit an existing one). A skill is a folder of
markdown instructions - plus optional reference files and scripts - that teaches a future
version of you how to do something well. You write skills with the skill tools: `create_skill`,
`write_skill_file`, and `update_skill`. Use `read_skill_file` to read a skill's files (including
the reference files in THIS skill's folder, named below).

These instructions are the workflow. The reference files in this folder hold the detail - read
them at the points marked below.

## 1. Find out what they want to build

Start broad. Ask what capability or task they'd like to teach you, and make clear a skill can
be almost anything. Offer a few examples so they see the range:

- A house style or persona - "answer in our brand voice", "write commit messages our way"
- Project or domain knowledge - "how our auth flow works", "how to use the Foo API"
- A repeatable procedure - "draft release notes from the git log", "run our release checklist"
- A task with a script - "generate a boilerplate component", "run the build and report failures"
- A review rubric or checklist

These are prompts, not a menu - don't limit them to these.

## 2. Interview until you genuinely understand it

Ask clarifying questions and drill in, a few at a time - this is a conversation, not a form.
You are capturing the domain knowledge a future you would need and not otherwise have. Pin down:

- What "done well" looks like, and what a bad result looks like.
- The steps, conventions, rules, and the hard-won gotchas.
- When the skill should apply (this becomes the description - get it right).
- Any context, files, commands, or external tools involved.

If the task involves *doing* something - touching files, running commands, building, git, the
web - read `tools.md` in this folder for the full catalog of built-in tools, so you can propose
the right one even if it isn't in your context right now. It also covers checking your own
current tools for anything the user added via `mcp.json`.

Keep going until you could do the task yourself from what they've told you. Capture only what
is specific to them; don't write down general knowledge you already have. If you don't know
something you'd need, ask - don't guess.

Before drafting, read `writing-skills.md` in this folder for how to turn the interview into a
good skill (especially how to phrase the description and when to split into multiple files).

## 3. Decide the shape

- **Single file** when the instructions are short and always needed.
- **Multiple files** when parts are only needed sometimes (separate sub-procedures, long
  reference, templates). The main `SKILL.md` then lays out the overall workflow and tells your
  future self when to read each supporting file with `read_skill_file`. Keep `SKILL.md` lean.
- **A bundled script** when you'd automate a deterministic step with a *new* script of your own
  (transform data, generate a file from a template, run a fixed routine). A script you write as
  part of the skill is **part of the skill**, so:
  - Write it into the skill's own `scripts/` folder with `write_skill_file(slug,
    "scripts/<name>.bat", ...)` - not into the user's workspace (leave a file in their project
    only if the user specifically asks for that).
  - In the `SKILL.md` body, tell your future self to run it with
    `run_skill_script(slug, "scripts/<name>.bat", [args])`.

  This is separate from a skill that just runs things that **already exist** - the project's
  build, a linter, git, a script already in the user's repo, or any one-off command. For those,
  the body uses the normal tools (`command__run`, etc.) and there's nothing to bundle. The rule is
  only: a batch/script *you* create as part of the skill lives *in* the skill and runs via
  `run_skill_script`, rather than being dropped in the workspace.

  If a script is involved, tell the user, confirm they want it, and read `scripts.md` in this
  folder before writing it.

## 4. Propose, then confirm

Summarize back, in plain terms: the name and slug, the one-line description (which states when
the skill should fire), what the instructions will cover, any supporting files, and any script.
Also confirm **where it should live**: just this project (the default), or available in every
project on this machine. Let the user correct it. Don't write files until they're happy with the
plan.

## 5. Write the files

Every write tool takes an optional `scope`: `project` (the default - this workspace's
`.gxpt/skills`) or `user` (this machine's skills, available in every project). Pass
`scope: "user"` on every call for a skill the user wants everywhere; otherwise omit it.

Creating a new skill:
- `create_skill(slug, name, description, body)` - the main `SKILL.md`. Put the workflow /
  instructions in the body. If multi-file, have the body describe the overall flow and say
  exactly when to read each supporting file.
- `write_skill_file(slug, relpath, content)` - each supporting file, and any script (e.g.
  `scripts/<name>.bat`). See `scripts.md` for script conventions.

Editing an existing skill (read it first with `read_skill_file`, or `list_skill_files(slug)` to
see what it contains):
- `edit_skill_file(slug, relpath, old_string, new_string)` - **your default for changing
  content.** A targeted edit that replaces an exact span and leaves everything else untouched.
  Use it for any focused change - to a supporting file, or to the `SKILL.md` body. Don't re-send a
  whole file (or a whole `SKILL.md` body) just to change a few lines.
- `update_skill(slug, ...)` - for the `SKILL.md` **frontmatter** (its `name` / `description`), or
  when you're genuinely replacing the whole body. Pass only the fields that change; the rest stay.
- `delete_skill_file(slug, relpath)` removes one file; `delete_skill(slug)` removes the whole
  skill. Both are destructive - the user confirms each time, so only reach for them when asked.

After writing, run `validate_skill(slug)` to confirm the `SKILL.md` still loads (its description
is what makes the skill discoverable).

## 6. Hand off

Tell the user what you created and where. Note that the skill is available on their next
message, that they can turn it on or off with `/toggle-skill <slug> on|off`, and run it directly with
`/use-skill <slug>`. Offer to refine it after they've tried it - skills get better with use.
