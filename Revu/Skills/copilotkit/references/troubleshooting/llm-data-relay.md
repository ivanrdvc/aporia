---
keywords: [latency, tokens, show_data, streaming, filtering, useRenderToolCall]
status: resolved
---

# LLM Data Relay Bottleneck

## Problem

When user asks "get all employees", 5+ second delay despite backend returning instantly (<100ms).

## Root Cause

LLM acts as data relay: receives data from backend tool, then must re-output that same data as tokens through `show_data` frontend tool.

```
Backend returns 15 employees (68ms)
  ↓
LLM generates show_data({ data: [...15 employees...] })
  ↓
~1,773 tokens at ~100 tok/sec = 17+ seconds
```

**Key insight:** LLM *receives* data instantly (into context), but *outputting* that data requires token generation.

## What Didn't Work

| Approach | Why it failed |
|----------|---------------|
| Backend optimization | Backend was already instant - not the bottleneck |
| Network/SSE tuning | Not the issue - streaming was fine |
| Caching | Doesn't help - bottleneck is LLM output, not data fetch |

## Solutions

### Solution 1: useRenderToolCall (partial)

Render backend tool results directly without `show_data`:

```tsx
useRenderToolCall({
  name: "get_all_employees",
  render: ({ result }) => <EmployeesTable data={result} />
});
```

**Problem:** Loses filtering ability. User says "show expiring certs" but UI shows all certs.

### Solution 2: ID-based filtering (implemented)

LLM outputs IDs instead of full data:

```tsx
show_data({
  source: "get_certifications_by_person",
  ids: ["uuid1", "uuid2"],  // ~50 tokens vs ~1,773
  title: "Expiring Certs"
})
```

Frontend stores tool results, filters by IDs.

**Result:** Response time dropped from 17s to ~2s.

### Solution 3: CoAgent state (proper way)

Backend emits `StateSnapshot` with filtered data directly. Frontend just renders state. No LLM token generation for data.

Best for new projects. More backend work.

## Key Takeaway

Filtering must happen BEFORE LLM outputs data. Options:
- Backend filters (cleanest)
- LLM outputs filter spec / IDs, frontend applies (compromise)
- Never: LLM outputs filtered data (token bottleneck)
