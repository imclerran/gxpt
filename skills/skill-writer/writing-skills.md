# Writing a good skill

Read this once you understand what the user wants, just before you draft. It is the
craft behind step 5 of `SKILL.md`: how to turn the interview into a skill that your
future self will actually find and follow.

## What a skill is for

A skill teaches a future you to do one thing well - something you would *not* already
know or would do inconsistently. It is leverage, not documentation. The best skills
capture the specific, hard-won knowledge of this user and this project: their
conventions, their gotchas, the steps that are easy to get subtly wrong. If a competent
assistant would already do it right without being told, it does not belong in the skill.

## The description is the most important line you write

The description is the *only* part of the skill loaded before it opens. It is the trigger:
a future you, mid-task, scans one-line descriptions and decides whether to open this skill.
If the description does not match the moment, the skill never fires - no matter how good the
body is. Spend real care here.

- Lead with **when to use it**, phrased as the situation, not the mechanism. Start with
  "Use this when ...".
- Name the concrete cues - the verbs, nouns, and file types that signal the moment. "Use
  this when the user wants to write release notes or a changelog from git history" beats
  "Release notes helper".
- Keep it to one or two sentences. It rides in front of every message while skills are on;
  it has to be cheap.
- Write it for a reader who cannot see the body. If two skills could plausibly match the
  same request, make each description say what makes *this* one the right pick.

Test it: imagine the user's request a month from now, worded differently than today. Would
this description still obviously match? If not, broaden the cues.

## The body is instructions to yourself

Write the body as imperative instructions a future you will follow, not prose about the
topic. "Read the changelog template, then fill one section per merged PR" - not "This skill
is about release notes."

- **Be specific and ordered.** Number the steps when order matters. State the conventions and
  the rules, including the non-obvious ones ("always group by component", "never include
  internal ticket numbers").
- **Capture the gotchas.** The single most valuable thing a skill holds is the mistake that is
  easy to make and the user already knows about. Write those down explicitly.
- **Say what "done well" looks like** - and, where it helps, what a bad result looks like - so
  your future self can check its own work.
- **Don't pad with general knowledge.** Leave out what you already know how to do. Every line
  should be something specific to this user, project, or task.
- **Point at the real artifacts.** Name the files, commands, and tools involved by their
  relative path or exact name (see "Bundled files" below, and `tools.md` for the built-in tool
  catalog and exact tool names).
- Keep `SKILL.md` lean. If the body is getting long because parts are only sometimes needed,
  that is the signal to split (next section).

## Name and slug

- **name**: a short, human title in title case ("Release Notes", "Brand Voice"). Shown in the
  skill list.
- **slug**: the folder name, kebab-case, derived from the name. It is how the user runs the
  skill (`/use-skill release-notes`) and how files are addressed. `create_skill` derives a slug from
  the name; keep the name simple so the slug is clean, or pass the slug you want.

## When to split into multiple files

Default to a single `SKILL.md`. Split only when it earns its keep - when parts of the skill are
**needed only some of the time**. The whole point is progressive disclosure: the body is paid
for on every open, but a supporting file costs nothing until your future self reads it.

Split when you have:

- **Distinct sub-procedures** used on different branches of the work ("if migrating, read
  `migrations.md`").
- **Long reference** that is only occasionally consulted (a lookup table, an API surface, a
  style guide).
- **Templates or examples** that get read and copied, not woven into the steps.

When you split, `SKILL.md` becomes a map: it lays out the overall workflow and tells your
future self *exactly when* to read each supporting file with `read_skill_file`. Name the file at
the point of need ("Before drafting, read `template.md`"), the way this very skill tells you to
read `writing-skills.md` here. Don't make your future self guess which file to open - that is
your job now. Keep each file focused on one thing.

## Bundled files

Supporting files live in the skill's own folder beside `SKILL.md`. Your future self reads them
with `read_skill_file(slug, relpath)`.

- **Reference them by relative path only - never an absolute path.** The skill is installed to a
  different place on every machine; a relative path (`template.md`, `examples/good.md`) resolves
  correctly everywhere.
- Group related assets in subfolders (`examples/`, `scripts/`) when there are several.
- A skill can read its own bundled files but **operates on the user's workspace**, never on its
  own folder. The skill folder is a read-only asset source.

If the skill needs a script, read `scripts.md` in this folder for the conventions.

## Common mistakes

- **A vague description** that won't match the request later. The most common failure - the skill
  is good but never fires. Fix the trigger first.
- **Writing a topic essay** instead of instructions. If a line isn't telling your future self to
  do something or to know something specific, cut it.
- **Restating general knowledge.** Keep only what is specific to this user/project.
- **Splitting too early.** A short, always-needed skill is one file. Don't scatter it.
- **Splitting without a map.** If you add supporting files but `SKILL.md` never says when to read
  them, your future self won't.
- **Absolute paths** to bundled assets. Always relative.
- **Over-asking the user.** You are capturing what you *don't* already know. Don't interview them
  about things you can figure out yourself.
