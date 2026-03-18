# Node.js CopilotRuntime (Legacy)

> **Being phased out.** Direct AG-UI connection to .NET backend replaces this. See [troubleshooting/removing-node-runtime.md](troubleshooting/removing-node-runtime.md) for migration.

The CopilotRuntime is a Node.js middleware that sits between the React frontend and your backend agents. It handles protocol translation, agent routing, and Cloud features.

```
React (CopilotKit) → Node.js CopilotRuntime → Backend Agent (AG-UI)
```

## v1 Setup (Next.js App Router)

```ts
// src/app/api/copilotkit/route.ts
import { CopilotRuntime, EmptyAdapter, copilotRuntimeNextJSAppRouterEndpoint } from "@copilotkit/runtime";
import { LangGraphHttpAgent } from "@copilotkit/runtime/langgraph";

const runtime = new CopilotRuntime({
  agents: {
    my_agent: new LangGraphHttpAgent({ url: "http://localhost:8000/agents/my_agent" }),
  },
});

const { handleRequest } = copilotRuntimeNextJSAppRouterEndpoint({
  runtime,
  serviceAdapter: new EmptyAdapter(), // no direct LLM — agent handles everything
  endpoint: "/api/copilotkit",
});

export const POST = handleRequest;
```

Frontend points at this endpoint:

```tsx
<CopilotKit runtimeUrl="/api/copilotkit">
```

## v2 Setup

```ts
import { createCopilotEndpoint, BasicAgent, InMemoryAgentRunner } from "@copilotkit/runtime/v2";

const agent = new BasicAgent({ name: "assistant" });
const runner = new InMemoryAgentRunner([agent]);

export default createCopilotEndpoint({ runner });
```

## Why remove it

- Adds a Node.js deployment to manage alongside your .NET backend
- For basic chat + tools + state, the runtime is just a passthrough
- CopilotKit v1.50+ is built on AG-UI internally — frontend can connect directly

## Direct connection (no runtime)

```tsx
import { CopilotKitProvider, CopilotPopup } from "@copilotkit/react-core/v2";
import { HttpAgent } from "@ag-ui/client";

const agent = new HttpAgent({ url: "http://localhost:5000/agent" });

<CopilotKitProvider agents__unsafe_dev_only={{ myAgent: agent }}>
  <CopilotPopup agentId="myAgent" />
</CopilotKitProvider>
```

## What you lose without the runtime

| Feature | Without Runtime |
|---|---|
| Basic chat, tools, state | Works via direct AG-UI |
| Thread persistence | Must implement yourself |
| Cloud features (auth, guardrails, analytics) | Not available |
| Multi-agent coordination | Must implement yourself |
| `@copilotkit/runtime` agent classes (`LangGraphAgent`, etc.) | Not available — use `HttpAgent` from `@ag-ui/client` |

## Packages

| Package | Purpose |
|---|---|
| `@copilotkit/runtime` | CopilotRuntime, adapters, agent classes — **remove when going direct** |
| `@ag-ui/client` | `HttpAgent`, `AbstractAgent` — **use for direct connection** |
