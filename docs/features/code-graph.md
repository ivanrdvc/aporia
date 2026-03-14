# Code Graph

Structural AST index that gives the reviewer codebase awareness before it fetches files.

## Problem

The reviewer has no picture of the codebase. It burns reasoning tokens and tool roundtrips
discovering callers, implementors, and dependencies via SearchCode + FetchFile before it can
do any actual review work.

## Solution

Tree-sitter parses C# and TypeScript files into symbols (classes, methods, interfaces, enums)
and references (calls, implements, imports). One Cosmos document per file, partitioned by repo.
Background-indexed when a repo is registered. The reviewer queries it via `QueryCodeGraph`
before fetching full source.

## Queries

| Kind | Question | Example |
|---|---|---|
| `callers` | Who calls this symbol? | `QueryCodeGraph("callers", "GetFile")` |
| `implementations` | Who implements this interface? | `QueryCodeGraph("implementations", "IGitConnector")` |
| `dependents` | What files reference symbols from this file? | `QueryCodeGraph("dependents", "Git/IGitConnector.cs")` |
| `outline` | What symbols are defined in this file? | `QueryCodeGraph("outline", "Review/CoreStrategy.cs")` |
| `hierarchy` | What does this type extend / what extends it? | `QueryCodeGraph("hierarchy", "GuardedExploreTool")` |

## How it works

1. **Indexing**: on first repo registration (or provider change), `AdminFunction` enqueues an
   `IndexRequest`. Re-registering an existing repo skips indexing. The queue-triggered
   `IndexFunction` calls `CodeGraphIndexer`, which fetches all files via the git provider,
   parses them with tree-sitter, resolves cross-file references, and upserts `FileIndex`
   documents to Cosmos. SHA256 content hashing skips unchanged files on re-index.

2. **Querying**: at review start, `CoreStrategy` loads all `FileIndex` docs for the repo into
   memory (`GetAllAsync`). All queries are in-memory LINQ lookups — zero per-query Cosmos calls.
   If no index exists, the tool returns a fallback message.

3. **Outline-first pattern**: before fetching a full file with `FetchFile`, the reviewer uses
   `QueryCodeGraph("outline", filePath)` to see its structure (signatures, line ranges). Only
   fetches full source when it needs implementation details.

## Configuration

Code graph is enabled by default. To disable indexing and querying globally:

```
Revu__EnableCodeGraph=false
```

Or in `appsettings.json`:

```json
{ "Revu": { "EnableCodeGraph": false } }
```

When disabled, `QueryCodeGraph` returns a fallback message directing the reviewer to use
`SearchCode` and `FetchFile` instead, and new repo registrations skip index enqueuing.

## Known limitations

Tree-sitter is syntactic only — it cannot resolve overloads, extension methods, partial classes,
or framework types across files. References are matched by name, not by fully-qualified type.
Callers/implementations queries may return false positives (e.g. two unrelated `GetFile` methods).
The graph points the reviewer in the right direction; the LLM disambiguates by reading actual code.

Roslyn semantic analysis would fix this but requires a compilable project (NuGet restore, .csproj).
LSP integration is a future option if local repo cloning is added.
