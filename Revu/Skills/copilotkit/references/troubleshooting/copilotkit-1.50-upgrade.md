---
keywords: [copilotkit, upgrade, 1.50, graphql, useAgent, messages, backend, tool, results]
---

# CopilotKit 1.50 Upgrade Fix

Fix for backend agent tool results not appearing in messages after upgrading to CopilotKit 1.50.

---

## Root Cause

CopilotKit 1.50 removed GraphQL. The old `useCopilotMessagesContext()` no longer includes backend agent tool results in the messages array.

In the old GraphQL version, backend MCP tool calls appeared in the frontend messages array as `ActionExecutionMessage` and `ResultMessage`. In 1.50, `useCopilotMessagesContext()` returns an empty array, and backend agent tool results are only available via `useAgent()`.

---

## The Fix

### 1. Backend server.js: Fix endpoint parameter

**Problem:** Express `app.use("/api/copilotkit", handler)` strips the path prefix before passing to the handler, but the handler was looking for the full path.

```js
// Wrong - causes 404
app.use("/api/copilotkit", (req, res, next) => {
  return copilotRuntimeNodeHttpEndpoint({
    endpoint: "/api/copilotkit",  // ❌ Path already stripped by Express
    runtime,
    serviceAdapter,
  })(req, res, next);
});

// Correct
app.use("/api/copilotkit", (req, res, next) => {
  return copilotRuntimeNodeHttpEndpoint({
    endpoint: "/",  // ✅ Matches the stripped path
    runtime,
    serviceAdapter,
  })(req, res, next);
});
```

### 2. Frontend: Use useAgent instead of useCopilotMessagesContext

**Problem:** `useCopilotMessagesContext()` no longer includes backend agent tool results.

```tsx
// Wrong - messages array is empty
import { useCopilotMessagesContext } from "@copilotkit/react-core";

export function CopilotIntegration() {
  const { messages } = useCopilotMessagesContext();  // ❌ Empty array
  // ...
}

// Correct - use useAgent
import { useAgent } from "@copilotkit/react-core/v2";

export function CopilotIntegration() {
  const { agent } = useAgent({ agentId: "Teammate" });  // ✅ Has tool results
  const messagesRef = useRef(agent.messages);
  messagesRef.current = agent.messages;
  // ...
}
```

---

## Result

With these changes, the original design works perfectly:
- Display tool takes tool name as parameter (not data)
- Looks up result from messages using `findToolResult()`
- Agent just passes tool name, not data (fast, no LLM overhead)
- All employees show up correctly in preview cards

---

## Key Takeaway

In CopilotKit 1.50+:
- Use `useAgent({ agentId: "YourAgentName" })` to access backend agent tool results
- Don't use `useCopilotMessagesContext()` for agent messages - it's for general conversation context, not agent-specific tool results
- For thread restoration, see [thread-restoration.md](thread-restoration.md)
