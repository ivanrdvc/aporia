---
date: 2026-03-17
status: done
tags: [review-quality, ado]
---

# Work Item Context for Reviews

## Problem Statement

The reviewer currently sees the diff, PR title/description, and commit messages. Linked work items
(PBIs, Features) carry additional context — acceptance criteria, design intent, scope — that would
help the reviewer understand what the PR is trying to accomplish. This is a context expansion:
give the reviewer more signal to work with.

## Decision Drivers

- ADO-only for now — GitHub doesn't have equivalent work item hierarchy; skip GitHub entirely
- Opt-in — gated behind `ProjectConfig.Review.EnableWorkItems` (default `false`)
- Content can be large — PBI descriptions include rich text, images, tables. Structured extraction
  with per-field caps keeps signal, drops noise, avoids extra LLM call
- Parent Feature is useful for initiative context but only if linked — don't walk the full hierarchy
- `IGitConnector` is already too broad — no new interface method; bundle into existing
  `GetPrContext`. Interface split tracked separately in `TODO.md`

## Solution

Bundle work item fetching into `AdoConnector.GetPrContext`. When `EnableWorkItems` is true,
`GetPrContext` fetches linked work item refs, resolves each to get title/description/acceptance
criteria, and if the item has a parent link fetches that too (one level up only). Strip HTML, cap
each field. `PrContext` gains an optional `WorkItems` property.

`PrContextProvider` renders work items into the reviewer's system prompt as a `<work_items>` block
alongside `<pr_context>`.

The feature is gated by `ProjectConfig.Review.EnableWorkItems` (default `false`). Config is passed
to `GetPrContext` so it can check the flag.

### Model

```csharp
public record WorkItemContext(
    string Type,           // "Product Backlog Item", "Bug", "Feature", etc.
    string Title,
    string? Description,
    string? AcceptanceCriteria,
    WorkItemContext? Parent // One level up only (Feature for a PBI)
);
```

### Content extraction

ADO work item fields to extract:
- `System.WorkItemType` → Type
- `System.Title` → Title
- `System.Description` → Description (strip HTML, cap 1500 chars)
- `Microsoft.VSTS.Common.AcceptanceCriteria` → AcceptanceCriteria (strip HTML, cap 1500 chars)

Parent resolution: read `System.Parent` field (int, nullable). If present, fetch the parent
work item and populate `Parent` with the same field extraction. No relation expansion needed.

### Prompt injection

```xml
<work_items>
## PBI: [Title]
Type: Product Backlog Item
Description: [cleaned description]
Acceptance Criteria: [cleaned acceptance criteria]

### Parent Feature: [Title]
Type: Feature
Description: [cleaned description]
</work_items>
```

## Research Summary

**ADO API spike (validated against `ivanradovic/ivanrndvc-sc`, PR #1, PBI #13):**
- `Microsoft.TeamFoundationServer.Client` (already referenced, v20.268.0-preview) includes
  `WorkItemTrackingHttpClient` — available via `VssConnection.GetClient<WorkItemTrackingHttpClient>()`
  using the same auth pattern as `GitHttpClient` (`Revu/Git/AdoConnector.cs:32-40`)
- PR work item refs: `GitHttpClient.GetPullRequestWorkItemRefsAsync(project, repoId, prId)` returns
  resource refs. Extract ID via `r.Url.Split('/').Last()`.
- Work item details: `WorkItemTrackingHttpClient.GetWorkItemAsync(id)` returns all fields.
  No need for `expand: Relations` — the `System.Parent` field gives the parent ID directly.
- **Fields confirmed from live payload:**
  - `System.WorkItemType` → "Product Backlog Item", "Feature", "Epic", "Bug", etc.
  - `System.Title` → plain text
  - `System.Description` → raw HTML
  - `Microsoft.VSTS.Common.AcceptanceCriteria` → raw HTML
  - `System.Parent` → parent work item ID (int, nullable) — simpler than relation traversal
- Parent traversal: just call `GetWorkItemAsync(parentId)` when `System.Parent` is present.
  No need to expand relations or parse `System.LinkTypes.Hierarchy-Reverse`.

**Existing context flow:**
- `ReviewFunction` (`Revu/Functions/ReviewFunction.cs:39`) calls `git.GetPrContext(req)` which
  returns `PrContext(Title, Description, CommitMessages)`
- `CoreStrategy` (`Revu/Review/CoreStrategy.cs:76`) stores `PrContext` in `session.StateBag`
- `PrContextProvider` (`Revu/Review/PrContextProvider.cs:17-35`) reads from StateBag, renders
  `<pr_context>` XML block into reviewer instructions
- `ProjectConfig` (`Revu/ProjectConfig.cs`) parsed from `.revu.json`, supports merge with defaults

**Config pattern:**
- `ReviewConfig` (`Revu/ProjectConfig.cs:69-73`) currently has `Strategy` and `MaxComments`
- Merging in `ProjectConfig.Parse()` falls through to defaults for null values

## Implementation Steps

1. **Add `WorkItemContext` model**
   - File: `Revu/Models.cs`
   - Add `WorkItemContext` record (Type, Title, Description, AcceptanceCriteria, Parent)
   - Add optional `WorkItems` property to `PrContext`:
     `IReadOnlyList<WorkItemContext>? WorkItems = null`

2. **Add `EnableWorkItems` to `ReviewConfig`**
   - File: `Revu/ProjectConfig.cs`
   - Add `bool? EnableWorkItems` to `ReviewConfig` (default `null`, merges to `false`)
   - Wire merge logic in `ProjectConfig.Parse()`

3. **Update `IGitConnector.GetPrContext` signature**
   - File: `Revu/Git/IGitConnector.cs`
   - Add `ProjectConfig` parameter: `GetPrContext(ReviewRequest req, ProjectConfig config)`
   - Update `GitHubConnector.GetPrContext` to accept and ignore the config parameter
   - Update all callers (`ReviewFunction`, `ChatFunction`, tests)

4. **Implement work item fetch in `AdoConnector.GetPrContext`**
   - File: `Revu/Git/AdoConnector.cs`
   - Add `WorkItemTrackingHttpClient` to the cached client dictionary (same pattern as
     `_gitClients`)
   - When `config.Review.EnableWorkItems == true`:
     - Call `GitHttpClient.GetPullRequestWorkItemRefsAsync()` to get linked work item refs
     - For each ref, call `WorkItemTrackingHttpClient.GetWorkItemAsync(id, expand: Relations)`
     - Extract fields: `System.WorkItemType`, `System.Title`, `System.Description`,
       `Microsoft.VSTS.Common.AcceptanceCriteria`
     - Strip HTML from Description and AcceptanceCriteria (simple regex or `HtmlDecode` + tag strip)
     - Cap each text field at 1500 chars with `[truncated]` marker
     - Check `System.Parent` field — if present, fetch parent with same extraction (no recursion)
   - Return `PrContext` with populated `WorkItems`

5. **Render in `PrContextProvider`**
   - File: `Revu/Review/PrContextProvider.cs`
   - After the `<pr_context>` block, if `pr.WorkItems` is not null/empty, render `<work_items>`
     block with type, title, description, acceptance criteria for each item + parent

6. **Unit tests**
   - File: `tests/Revu.Tests.Unit/` (new or existing test files)
   - Test HTML stripping and field capping
   - Test `PrContextProvider` renders work items correctly
   - Test `ProjectConfig.Parse` merges `EnableWorkItems`

7. **Integration test against ADO org**
   - File: `tests/Revu.Tests.Integration/` (check existing test patterns — PAT in dotnet secrets)
   - Test `AdoConnector.GetPrContext` with `EnableWorkItems = true` against a real PR with
     linked PBIs
   - Verify parent Feature resolution

## Open Questions

- [ ] HTML stripping approach — simple regex (`<[^>]+>`) or use a lightweight library? ADO
  descriptions can have nested HTML. Regex is usually good enough for field extraction.
- [ ] Field cap value (1500 chars) — need to validate against real PBIs from the ADO org to see
  if this is too aggressive or too generous.

## Notes for Implementation

- The `Microsoft.TeamFoundationServer.Client` NuGet (already in `Revu.csproj`) includes
  `Microsoft.TeamFoundation.WorkItemTracking.WebApi.WorkItemTrackingHttpClient` — no new package
  needed.
- `WorkItemTrackingHttpClient` is obtained via `VssConnection.GetClient<WorkItemTrackingHttpClient>()`
  using the same `VssConnection` pattern in `AdoConnector.GetGitClient()`.
- The PAT used for ADO needs `Work Items (Read)` scope — verify the existing test PAT has this.
- Check the integration test skill/setup for how to run e2e tests against the ADO org before
  implementing step 8.
