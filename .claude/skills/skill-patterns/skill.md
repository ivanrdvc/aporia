---
name: skill-patterns
description: "Add patterns to reference skills or create new ones: /skill-patterns [add|create]"
user-invocable: true
disable-model-invocation: true
argument-hint: "[add <skill> | create <name>]"
---

# Reviewer Skill Patterns

Manage pattern reference skills that ship with the Aporia reviewer (`Aporia/Skills/`).

## Argument routing

| Command | What it does |
|---|---|
| `/skill-patterns add <hint>` | Fuzzy-match an existing reference skill, add entries to it |
| `/skill-patterns create <name>` | Create a new skill from scratch |
| `/skill-patterns <hint or description>` | Natural language — infer add vs create from context |
| No argument | Ask the user what they want to do |

### Skill resolution

1. List directories under `Aporia/Skills/`.
2. Match the hint as a case-insensitive substring against directory names.
   - One match → use it.
   - Multiple → ask.
   - Zero → assume `create` with the hint as the new skill name.
3. Hints like "dotnet", "dn", "arch", "di" should match `dotnet-architecture`.
   Hints like "copilot", "ck", "agui" should match `copilotkit`.

## Skill types

There are two kinds of reviewer skills. This skill manages **reference skills** only.

- **Domain skills** teach the reviewer *how to review* a technology (e.g. `copilotkit`, `maf`).
  Self-contained SKILL.md with review instructions. Not covered here.
- **Reference skills** help the reviewer *verify its own assumptions* before reporting.
  SKILL.md is an index pointing to resource files with pattern entries.

## File structure

```
Aporia/Skills/<name>/
  SKILL.md              — index with resource table + description
  <concern>.md          — resource file, one per concern area
  <concern>.md          — ...
```

All files under `Aporia/Skills/` are copied to build output via the csproj Content glob.

## SKILL.md format

```markdown
---
name: <skill-name>
description: "<technology> pattern reference — standard patterns that are frequently
misidentified as bugs. Check before reporting <areas> findings to avoid false positives."
---

Standard <technology> patterns that are frequently misidentified as bugs during review.
Each resource describes how a pattern works and the one check that distinguishes
correct usage from a real bug. Not checking leads to false positives.

## Resources

Use `read_skill_resource("<skill-name>", "<filename>")` to load.

| Resource | Covers |
|---|---|
| `<file>.md` | <brief list of patterns covered> |
```

Key rules for SKILL.md:
- Description MUST signal consequence ("avoid false positives") — not just "reference"
- Description MUST name the technology and concern areas
- Body is an index, not instructions — point to resources
- Keep the resource table, no prose

## Resource file entry format

Each entry follows this exact format — 4 lines, no headers beyond `##`:

```markdown
## `pattern signature or name`
Normal when <how the pattern works in correct usage>.
Bug when <the actual failure mode>.
→ Check: <the one thing to verify — a concrete action>
```

Rules:
- One `##` heading per pattern — the pattern name or code signature
- "Normal when" line — describes correct usage, not "don't flag this"
- "Bug when" line — the real failure mode
- "→ Check:" line — one concrete discriminator the reviewer can verify
- No sub-bullets, no prose paragraphs, no "looks like" section
- Keep entries tight — 4 lines max per pattern
- Group related patterns in the same resource file by concern area

## Adding entries (`/skill-patterns add`)

1. Read the target skill's SKILL.md to see available resources
2. Read the relevant resource file
3. Ask the user to describe the pattern (or infer from conversation context)
4. Write the entry in the 4-line format
5. If no existing resource fits, create a new resource file and add it to the SKILL.md table

## Creating a new skill (`/skill-patterns create`)

1. Ask the user for the technology and concern areas (or infer from context)
2. Create the directory under `Aporia/Skills/<name>/`
3. Write SKILL.md with the index format above
4. Create initial resource files with entries
5. Verify the build picks up the new files: `dotnet build Aporia/Aporia.csproj --no-restore`

## Token budget

Keep resource files dense:
- ~150 tokens per resource file (3-5 entries)
- No filler words, no explanatory paragraphs
- If a resource grows past ~10 entries, split by sub-concern

## Example

`Aporia/Skills/dotnet-architecture/di-patterns.md`:

```markdown
## `new X()` inside a singleton
Normal when X is a value object, DTO, or cheap helper with no dependencies.
Bug when X has injected dependencies, holds resources, or is registered in DI.
→ Check: does X's constructor take services?

## Keyed services (`AddKeyedSingleton`, `AddKeyedScoped`)
Normal for strategy/provider selection — multiple implementations registered with different keys.
Bug when the key string doesn't match between registration and resolution.
→ Check: search for the key string — does it appear in both `AddKeyed*` and `[FromKeyedServices]`?
```
