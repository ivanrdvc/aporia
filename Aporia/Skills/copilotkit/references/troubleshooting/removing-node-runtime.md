---
keywords:
  [
    migration,
    remove runtime,
    direct connection,
    v2 API,
    ExperimentalEmptyAdapter,
    agents__unsafe_dev_only,
    AG-UI,
  ]
last_updated: 2025-01-10
---

# Removing Node.js CopilotRuntime

How to eliminate the CopilotKit Node.js runtime middleware and connect directly to AG-UI backends.

## The Problem

CopilotKit's documented architecture requires Node.js middleware:

```
Frontend → Node.js Runtime (@copilotkit/runtime) → Your Backend
```

Community feedback on [GitHub Issue #2186](https://github.com/CopilotKit/CopilotKit/issues/2186) called managing a separate Node runtime "a show stopper" for enterprise adoption.

## The Solution

Bypass the runtime entirely:

```
Frontend → AG-UI Protocol → Backend
```

This works because CopilotKit v1.50+ is "fully built on AG-UI" internally.

---

## Client Changes (3 Steps)

**1. Update imports to v2 API:**

```tsx
import { CopilotKitProvider, CopilotPopup } from "@copilotkit/react-core/v2";
import { HttpAgent } from "@ag-ui/client";
import "@copilotkit/react-core/v2/styles.css";
```

**2. Replace CopilotKit with CopilotKitProvider + HttpAgent:**

```tsx
const agent = new HttpAgent({ url: 'http://backend:5000/agent' })

<CopilotKitProvider agents__unsafe_dev_only={{ myAgent: agent }}>
  {children}
</CopilotKitProvider>
```

**3. Add agentId to UI components:**

```tsx
<CopilotPopup agentId="myAgent" />
```

---

## Files to Delete

- `src/app/api/copilotkit/route.ts` (Next.js) or `server.ts` (Express)
- Any CopilotRuntime configuration files

## Dependencies to Remove

```bash
pnpm remove @copilotkit/runtime
```

---

## Backend Requirements

Must implement AG-UI protocol. For .NET:

```csharp
app.MapAGUI("/agent", agent);
```

Endpoint accepts POST with JSON, returns SSE stream with AG-UI events.

---

## Why This Isn't Officially Documented

| Feature                   | Runtime Required? |
| ------------------------- | ----------------- |
| Basic chat streaming      | No                |
| Thread persistence        | Yes               |
| Auth/guardrails/analytics | Yes (Cloud only)  |
| Multi-agent coordination  | Yes               |

CopilotKit's business model centers on Cloud/runtime features. `agents__unsafe_dev_only` is an undocumented escape hatch found in type definitions.

---

## Trade-offs

**Gained:** No Node.js middleware, simpler deployment, direct debugging

**Lost:** Thread persistence, Cloud features, official support

---

## References

- [AG-UI Protocol](https://docs.ag-ui.com/)
- [Microsoft Agent Framework AG-UI](https://learn.microsoft.com/en-us/agent-framework/integrations/ag-ui/)
- [GitHub Issue #2186](https://github.com/CopilotKit/CopilotKit/issues/2186) - Community request
- [CopilotKit v1.50 Release](https://github.com/CopilotKit/CopilotKit/releases)
