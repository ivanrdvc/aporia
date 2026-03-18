# CopilotKitCore API

Cross-framework orchestration layer. Manages agents, tools, context, and runtime connections. Used directly in v2 (`@copilotkitnext/core`) or accessed via `useCopilotKit()` in React.

---

## Overview

```
Framework Layer (React / Angular / Vue)
        ↓
CopilotKitCore  ←  cross-framework orchestration, state management
        ↕
CopilotRuntime  ←  backend agent execution
```

CopilotKitCore handles:
- **Agent orchestration** — running agents, tool execution, follow-up loops
- **Runtime connection** — connecting to CopilotRuntime, discovering remote agents
- **State sync** — consistent state for agents, tools, context across components
- **Framework independence** — same API for React, Angular, etc.

---

## Accessing CopilotKitCore

### React

```tsx
import { useCopilotKit } from "@copilotkitnext/react";

function MyComponent() {
  const { copilotkit } = useCopilotKit();
  // copilotkit is the CopilotKitCore instance
  console.log(copilotkit.runtimeConnectionStatus);
}
```

`useCopilotKit()` returns:

```ts
{
  copilotkit: CopilotKitCore;
  renderToolCalls: ReactToolCallRenderer<any>[];
  currentRenderToolCalls: ReactToolCallRenderer<unknown>[];
  setCurrentRenderToolCalls: React.Dispatch<...>;
}
```

Auto-subscribes to runtime connection status changes.

### Direct instantiation (advanced)

```ts
import { CopilotKitCore } from "@copilotkitnext/core";

const core = new CopilotKitCore({
  runtimeUrl: "/api/copilotkit",
  headers: { Authorization: "Bearer ..." },
  properties: { userId: "123" },
});
```

Most apps don't need this — the provider handles it.

---

## Config

```ts
interface CopilotKitCoreConfig {
  runtimeUrl?: string;                              // CopilotRuntime endpoint
  headers?: Record<string, string>;                 // HTTP headers for every request
  properties?: Record<string, unknown>;             // App data forwarded to agents
  agents__unsafe_dev_only?: Record<string, AbstractAgent>;  // Local dev agents (NOT for production)
  tools?: FrontendTool<any>[];                      // Frontend tools agents can invoke
}
```

- `runtimeUrl` — triggers auto-connect to discover remote agents
- `agents__unsafe_dev_only` — bypasses CopilotRuntime for local prototyping. Agent key becomes the agent ID.
- `properties` — forwarded to agents as `forwardedProps`

---

## Runtime Connection

### Connection Flow

1. CopilotKitCore sends `GET {runtimeUrl}/info`
2. Runtime responds with available agents + metadata
3. For each remote agent, creates a `ProxiedCopilotRuntimeAgent`
4. Remote agents merge with local agents
5. Subscribers notified

### Connection States

```ts
type CopilotKitCoreRuntimeConnectionStatus =
  | "Disconnected"  // No runtimeUrl, or intentionally closed
  | "Connecting"    // Actively connecting
  | "Connected"     // Connected, remote agents available
  | "Error";        // Failed, continues with local agents only
```

### Properties

```ts
copilotkit.runtimeUrl           // string | undefined
copilotkit.runtimeVersion       // string | undefined
copilotkit.runtimeConnectionStatus  // CopilotKitCoreRuntimeConnectionStatus
copilotkit.headers              // Readonly<Record<string, string>>
copilotkit.properties           // Readonly<Record<string, unknown>>
copilotkit.agents               // Readonly<Record<string, AbstractAgent>>
copilotkit.tools                // Readonly<FrontendTool<any>[]>
copilotkit.context              // Readonly<Record<string, Context>>
```

---

## Running Agents

### runAgent()

Executes an agent with automatic tool execution and follow-up loops.

```ts
await copilotkit.runAgent({ agent: AbstractAgent }): Promise<RunAgentResult>
```

Execution flow:
1. Sends messages to the agent
2. Agent may request tool calls
3. CopilotKitCore finds matching tool, executes it, adds result to messages
4. If tool has `followUp: true` (default), re-runs agent with tool results
5. Cycle continues until agent completes without tool requests

Key behaviors:
- **Auto tool execution** — finds tool by name (agent-specific first, then global, then wildcard `*`)
- **Error recovery** — tool errors captured and returned to agent
- **Smart follow-ups** — continues until no more tools requested

### connectAgent()

Establishes a live connection to an existing agent session. For reconnecting after page refresh or subscribing to background processes.

```ts
await copilotkit.connectAgent({
  agent: AbstractAgent,
  agentId?: string,
}): Promise<RunAgentResult>
```

Restores message history, sets up error subscribers, returns immediately without triggering a new run.

### getAgent()

```ts
copilotkit.getAgent(id: string): AbstractAgent | undefined
```

Returns `undefined` if agent doesn't exist or runtime is still connecting.

---

## Tool Management

### addTool()

```ts
copilotkit.addTool<T>(tool: FrontendTool<T>): void
```

Skips if tool with same name + agent scope already exists.

### removeTool()

```ts
copilotkit.removeTool(id: string, agentId?: string): void
```

- With `agentId`: removes tool only for that agent
- Without: removes only global tools with that name

### getTool()

```ts
copilotkit.getTool({
  toolName: string,
  agentId?: string,
}): FrontendTool<any> | undefined
```

Resolution order: agent-specific → global → undefined.

### setTools()

```ts
copilotkit.setTools(tools: FrontendTool<any>[]): void
```

Replaces all tools at once.

### Tool Execution Lifecycle

1. Agent returns tool call request (name, args, call ID)
2. CopilotKitCore resolves: agent-specific → global → wildcard (`*`)
3. Handler invoked with parsed args
4. Result stringified, added to message history
5. If `followUp: true`, agent re-runs with results
6. Errors caught and returned to agent

---

## Context Management

### addContext()

```ts
copilotkit.addContext(context: { description: string; value: any }): string
```

Returns unique ID for later removal.

### removeContext()

```ts
copilotkit.removeContext(id: string): void
```

---

## Configuration Methods

```ts
copilotkit.setRuntimeUrl(url: string | undefined): void   // Change endpoint, triggers reconnect
copilotkit.setHeaders(headers: Record<string, string>): void
copilotkit.setProperties(properties: Record<string, unknown>): void
```

All notify subscribers on change.

---

## Event Subscription

```ts
const subscription = copilotkit.subscribe(subscriber: CopilotKitCoreSubscriber);
// Later: subscription.unsubscribe();
```

### Available Events

```ts
interface CopilotKitCoreSubscriber {
  onRuntimeConnectionStatusChanged?: (event: {
    copilotkit: CopilotKitCore;
    status: CopilotKitCoreRuntimeConnectionStatus;
  }) => void | Promise<void>;

  onToolExecutionStart?: (event: {
    copilotkit: CopilotKitCore;
    toolCallId: string;
    agentId: string;
    toolName: string;
    args: unknown;
  }) => void | Promise<void>;

  onToolExecutionEnd?: (event: {
    copilotkit: CopilotKitCore;
    toolCallId: string;
    agentId: string;
    toolName: string;
    result: string;
    error?: string;
  }) => void | Promise<void>;

  onAgentsChanged?: (event: {
    copilotkit: CopilotKitCore;
    agents: Readonly<Record<string, AbstractAgent>>;
  }) => void | Promise<void>;

  onContextChanged?: (event: {
    copilotkit: CopilotKitCore;
    context: Readonly<Record<string, Context>>;
  }) => void | Promise<void>;

  onPropertiesChanged?: (event: {
    copilotkit: CopilotKitCore;
    properties: Readonly<Record<string, unknown>>;
  }) => void | Promise<void>;

  onHeadersChanged?: (event: {
    copilotkit: CopilotKitCore;
    headers: Readonly<Record<string, string>>;
  }) => void | Promise<void>;

  onError?: (event: {
    copilotkit: CopilotKitCore;
    error: Error;
    code: CopilotKitCoreErrorCode;
    context: Record<string, any>;
  }) => void | Promise<void>;
}
```

---

## Error Codes

```ts
type CopilotKitCoreErrorCode =
  | "RUNTIME_INFO_FETCH_FAILED"      // Can't reach runtime endpoint
  | "AGENT_CONNECT_FAILED"           // Can't connect to agent
  | "AGENT_RUN_FAILED"               // Agent execution failed
  | "AGENT_RUN_FAILED_EVENT"         // Agent reported failure via event stream
  | "AGENT_RUN_ERROR_EVENT"          // Agent emitted error event
  | "TOOL_ARGUMENT_PARSE_FAILED"     // Tool args couldn't be parsed
  | "TOOL_HANDLER_FAILED";           // Tool handler threw an error
```

---

## Local Dev Agents

**Development only.** Production requires CopilotRuntime.

```ts
copilotkit.setAgents__unsafe_dev_only(agents: Record<string, AbstractAgent>): void
copilotkit.addAgent__unsafe_dev_only({ id: string, agent: AbstractAgent }): void
copilotkit.removeAgent__unsafe_dev_only(id: string): void
```

Remote agents from runtime cannot be removed this way.
