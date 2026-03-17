# Review Architecture

## Strategy Layer

Revu supports multiple review strategies, each backed by a different agent runtime. The
`Reviewer` resolves a strategy by key from a DI factory based on `.revu.json` config:

```
.revu.json: "strategy": "core" | "copilot" | "claude-code"
                    │
                    ▼
            ┌───────────────┐
            │    Reviewer    │  resolves strategy, caps comments,
            │                │  filters findings, builds summary
            └───────┬───────┘
                    │ strategyFactory(key)
        ┌───────────┼───────────┐
        ▼           ▼           ▼
  ┌───────────┐ ┌─────────┐ ┌────────────┐
  │  Core     │ │ Copilot │ │ ClaudeCode │
  │ Strategy  │ │Strategy │ │  Strategy  │
  │           │ │         │ │  (future)  │
  │ IChatClient│ │CopilotCli│ │ Claude CLI │
  │ direct API│ │ JSON-RPC│ │            │
  └───────────┘ └─────────┘ └────────────┘
```

All strategies implement `IReviewStrategy.Review()` → `ReviewResult`. Same tools, same prompts,
same output contract. The `Reviewer` doesn't know or care which runtime produced the findings.

## CoreStrategy

The default strategy. Built from scratch using direct LLM API calls via `IChatClient`.

### Goals

- The reviewer does the analysis. It has direct tools to read files, search code, and explore the
  codebase. It does not delegate its thinking.
- Explorers exist for context management. When a cross-file question would pollute the reviewer's
  context with raw file data, an explorer handles it and returns a compressed conclusion.
- Guardrails, explicit budgets, and observability are required because agent behavior is
  non-deterministic.

### Design principles

- The reviewer IS the reviewer, not a dispatcher, not an orchestrator. It reads the diff, analyzes
  the code, and produces findings.
- Explorers are scoped workers for cross-file analytical questions. They have tools, can make
  multiple calls, but return structured output (not raw files, not free-text observations).
- The return format prevents abuse, not the prompt. The structured output schema prevents raw file
  dumps. Enforcement is structural, not prompt-based.
- Explorers are a context-management escape hatch, not the default execution path.

## Architecture

Given a PR, a reasoning agent reads all the changes, analyzes the code with full tool access, spawns
explorers only for cross-file analytical work, then produces a final review.

```
┌──────────────────────────────┐
│         Reviewer Agent       │
│  (reasoning model + tools)   │
│                              │
│  Tools: FetchFile, SearchCode│
│         ListDirectory,       │
│         QueryCodeGraph,      │
│         Explore              │
│                              │
│  1. Analyze visible code     │
│  2. Use direct tools for     │
│     quick lookups            │
│  3. Explore for cross-file   │
│     analytical work          │
│  4. Produce findings         │
└──────┬──┬──┬─────────────────┘
       │  │  │  dynamic, 0-N
       ▼  ▼  ▼
  Explorer agents
  (each answers a specific cross-file
   question, returns structured JSON)
```

The reviewer (`ModelKey.Reasoning`) gets the diff and has full tool access. It analyzes the code
itself, uses direct tools for quick lookups, and spawns explorers only for concrete analytical work
at cross-file scope: questions that require reading and comparing multiple files.

Explorers (`ModelKey.Default`) are agentic. They have the same tools and can make multiple calls.
But they return structured output. If an explorer fails to produce valid structured output, the
result is an error message. Raw text never flows back to the reviewer.

### How it's built

- **Agent-as-tool**: the explorer is a full agent with its own tools, exposed to the reviewer as a
  callable tool wrapped by a `DelegatingAIFunction` guard. The reviewer sees a tool it can call
  with a natural language query; it doesn't know there's an agent behind it. Same MAF pattern as
  before, but now the reviewer also has direct tools, so Explore is one option among
  many, not the only path.
- **Structured explorer output**: the output schema prevents abuse. The model can't use Explore as
  a file reader because the schema doesn't support raw file content. On parse failure, the reviewer
  gets an error, not raw text.
- **Batch tools**: `FetchFile` and `SearchCode` accept `string[]` arrays, multiple paths or
  queries in one call. This reduces LLM roundtrips since reasoning models tend to issue tool calls
  sequentially. The tools execute all items in parallel via `Task.WhenAll`.
- **SearchCode validation**: queries containing code characters (`;{}=<>!&|,`) or more than two
  words are rejected with feedback. A wildcard fallback (`query*`) fires when exact search returns
  nothing, since ADO treats PascalCase identifiers as atomic tokens.
- **Code graph**: at review start, all `FileIndex` documents for the repo are loaded from Cosmos
  into memory. The `QueryCodeGraph` tool runs callers, implementations, dependents, outline, and
  hierarchy queries as in-memory LINQ lookups — zero per-query Cosmos calls. Background-indexed
  via tree-sitter when a repo is registered. Graceful degradation when no index exists.
- **Two-tier model split**: a strong reasoning model reviews (analysis, synthesis), cheap fast
  models explore (follow objectives, fetch context, compress evidence).
- **Parallel tool calling**: `AllowMultipleToolCalls = true` on the reviewer's `ChatOptions`
  requests the model issue multiple tool calls per turn. Combined with
  `AllowConcurrentInvocation = true` on `FunctionInvocationOptions`, tools execute in parallel.
  Effectiveness varies by model. Claude uses it well; reasoning models (o-series) don't support it.
- **Guardrails**: explorer spawn count capped per review (`MaxExplorationsPerReview`), tool
  roundtrips per explorer capped (`ExplorerMaxRoundtrips`), reviewer roundtrips capped
  (`ReviewerMaxRoundtrips`), concurrency bounded (`MaxConcurrentExplorations`). Sessions captured
  for debugging when enabled.

### Skills

Skills are domain-specific review knowledge that the reviewer can load on demand. Each skill is a
`SKILL.md` file (YAML frontmatter + markdown instructions) under `Revu/Skills/`, following the
[Agent Skills](https://agentskills.io/) open standard.

MAF's `FileAgentSkillsProvider` handles discovery, parsing, and tool registration. It's added to the
reviewer agent as an `AIContextProvider` and uses progressive disclosure:

```
Advertise:   skill names + descriptions injected into instructions   (~100 tokens/skill, cached)
Load:        reviewer calls load_skill(name)                         (tool use)
Delivery:    full instructions returned as tool result                (messages layer, per-review)
Resources:   reviewer calls read_skill_resource(name, file)          (supplementary files, on demand)
```

Since skills ship with Revu (not per-repo), the advertisement and tools are identical across all
reviews, so the tools + system prompt layers stay cacheable.

Skills are reviewer capabilities, not project configuration. They represent what the reviewer
*knows*, not what the project requests. A project doesn't opt into security review. The reviewer
loads the security skill when it sees security-relevant code.

There are two kinds of skills:

**Domain skills** (`maf`, `copilotkit`, `react`) teach the reviewer *how to review* a specific
technology — what patterns matter, what to look for, what the framework expects. They add review
capability the reviewer wouldn't otherwise have. The `SKILL.md` is self-contained instructions.

**Reference skills** (`dotnet-architecture`) help the reviewer *verify its own assumptions* before
reporting. The reviewer already pattern-matches against known anti-patterns — reference skills
give it a way to check whether a match is actually a bug or a standard pattern. The `SKILL.md`
is an index pointing to resource files the reviewer drills into on demand.

#### Reference skills

A reference skill describes how standard patterns work so the reviewer can tell whether code
follows or deviates from them. The description signals that not checking leads to false positives,
which creates urgency to load the skill rather than treating it as optional reference.

Structure uses all three disclosure tiers:

```
Advertise:   "patterns frequently misidentified as bugs — check to avoid false positives"
Load:        SKILL.md is an index — resource table, not instructions  (signals "there's more")
Resource:    each resource file covers one concern area               (drill into specific pattern)
```

Each resource entry follows a compact format: **normal when** (how the pattern works),
**bug when** (the actual failure mode), **→ Check** (the one thing to verify). This gives the
reviewer a discriminator — one check that separates correct usage from a real bug — rather than
a list of things to flag or suppress.

The workflow requires skill loading before producing findings (step 4 in `Prompts.cs`). This is
enforced via prompt instruction ("you MUST call load_skill"), not structurally — the model can
still skip it. The MUST instruction moved skill loading from 0% to ~50% in testing with Haiku.
Structural enforcement (e.g. two-pass review) would be more reliable but doubles cost.

Why reference skills instead of prompt rules: prompt space is finite and cached. Skills use the
messages layer (per-review, not cached) and only load when relevant. A reference skill for .NET
patterns costs nothing on a React PR. The resource structure adds a second engagement point
(`read_skill_resource` after `load_skill`), which matters because the core problem is getting the
model to use tools at all — more tool-call opportunities mean more chances for engagement.

### Reviewer context layers

The reviewer agent's input is composed from multiple independent sources via MAF's
`AIContextProvider` pipeline. Each provider injects its own instructions, tools, or messages.
`BuildReviewPrompt` only handles the diff — all metadata flows through providers.

| Layer                 | Source                                      | Content                                |
|-----------------------|---------------------------------------------|----------------------------------------|
| Instructions          | `Prompts.BuildReviewerInstructions()`       | How to review (rules, severity, format)|
| Instructions (merged) | `PrContextProvider`                         | PR title/desc/commits, linked work items (ADO, `EnableWorkItems`), project context + rules from `.revu.json` |
| Instructions (merged) | `LearningsProvider` (planned)               | Learnings from past review feedback    |
| Tools (advertised)    | `FileAgentSkillsProvider`                   | Skill names + descriptions             |
| User message          | `BuildReviewPrompt(diff)`                   | The diff                               |

Providers read per-review data from `AgentSession.StateBag`, populated by the strategy before
`RunAsync`. Adding a new context source = new provider class + register in `AIContextProviders`.
No signature changes to `IReviewStrategy`, `Reviewer`, or `BuildReviewPrompt`.

### System prompt structure

Prompt text lives in `Review/Prompts.cs`, separate from strategy wiring.

The reviewer prompt is built by `BuildReviewerInstructions()` (static, no parameters) and contains
only review methodology. All dynamic context — PR metadata, project context, rules — flows through
`PrContextProvider` via `StateBag`. Each concern is wrapped in its own XML tag so the model parses
sections unambiguously.

Ordering follows primacy/recency attention patterns:
- **Primacy** (top): `<role>` and `<workflow>`: identity and the process.
- **Middle**: `<exploration_guidance>`, `<finding_format>`, `<severity_definitions>`,
  `<summary_format>`: reference material the model consults as needed.
- **Recency** (bottom): `<code_fix>`, then provider-injected context (`<pr_context>`,
  `<project_context>`, `<additional_rules>`).

Each finding carries a `Severity` (critical / warning / info) defined by impact type — security,
logic bug, suggestion. `Reviewer.cs` sorts findings by severity, takes the top `maxComments` as
inline PR comments, and pushes the rest into a collapsible summary section. Within a severity tier,
findings preserve model order; a secondary sort (e.g. feedback-driven reranking) may be added later.

Explorer instructions are a separate constant (`ExplorerInstructions`), different agent, different
job. Nothing is shared between reviewer and explorer prompts. The explorer prompt uses XML tags
(`<role>`, `<workflow>`, `<output>`) and is ordered: identify files via paths or SearchCode first,
fetch them, compare patterns, then output ONLY the JSON result (no tool calls in the same response
because the parser can't extract JSON from a message that also contains tool calls).
