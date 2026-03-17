# Hooks — Agents

Working with agents: retrieving agents, shared state, running agents, intermediate state rendering, and LangGraph interrupts.

---

## Quick Reference

| Hook | What it does | Package | Version |
|---|---|---|---|
| `useAgent` | Get agent instance, subscribe to updates | `@copilotkitnext/react` | v2 |
| `useCoAgent` | Typed shared-state agent (wraps useAgent) | `@copilotkit/react-core` | v1 |
| `useCoAgentStateRender` | Render agent intermediate state in chat | `@copilotkit/react-core` | v1 |
| `useLangGraphInterrupt` | Handle LangGraph interrupt events | `@copilotkit/react-core` | v1 |

---

## 1. useAgent (v2)

Primary v2 hook for interacting with agents. Retrieves an agent by ID, subscribes to state changes, and triggers re-renders.

```tsx
import { useAgent } from "@copilotkitnext/react";

function ChatComponent() {
  const { agent } = useAgent({ agentId: "assistant" });

  return (
    <div>
      <div>Messages: {agent.messages.length}</div>
      <div>Running: {agent.isRunning ? "Yes" : "No"}</div>
    </div>
  );
}
```

### Parameters

```ts
{
  agentId?: string;           // Agent ID (default: "default")
  updates?: UseAgentUpdate[]; // Selective re-render subscriptions
}
```

### Selective Updates

By default, subscribes to ALL update types (frequent re-renders). Optimize with `updates`:

```tsx
import { useAgent, UseAgentUpdate } from "@copilotkitnext/react";

// Only re-render on message changes
const { agent } = useAgent({
  agentId: "assistant",
  updates: [UseAgentUpdate.OnMessagesChanged],
});

// Only re-render on run status changes
const { agent } = useAgent({
  agentId: "assistant",
  updates: [UseAgentUpdate.OnRunStatusChanged],
});

// No subscriptions (static access)
const { agent } = useAgent({
  agentId: "assistant",
  updates: [],
});
```

Available update types:
- `UseAgentUpdate.OnMessagesChanged` — messages added or modified
- `UseAgentUpdate.OnStateChanged` — agent state changes
- `UseAgentUpdate.OnRunStatusChanged` — agent starts or stops running

### Return Value

```ts
{ agent: AbstractAgent }
```

The `AbstractAgent` instance exposes:
- `agent.id` — agent identifier
- `agent.messages` — message history array
- `agent.state` — shared state object
- `agent.isRunning` — boolean run status
- `agent.threadId` — conversation thread ID
- `agent.addMessage(msg)` — add a message
- `agent.setState(state)` — update shared state
- `agent.subscribe(subscriber)` — subscribe to events (returns `{ unsubscribe }`)
- `agent.runAgent(params?)` — trigger agent execution
- `agent.abortRun()` — stop current execution

### Sending Messages

Add a message then run the agent:

```tsx
import { useAgent } from "@copilotkitnext/react";
import { useCopilotKit } from "@copilotkitnext/react";

function MessageSender() {
  const { agent } = useAgent({ agentId: "assistant" });
  const { copilotkit } = useCopilotKit();

  const send = async (content: string) => {
    agent.addMessage({
      id: crypto.randomUUID(),
      role: "user",
      content,
    });
    await copilotkit.runAgent({ agent, agentId: "assistant" });
  };

  return <button onClick={() => send("Hello!")} disabled={agent.isRunning}>Send</button>;
}
```

### Shared State

Both user and agent can read/write state. Changes from either side trigger re-renders:

```tsx
function SharedCounter() {
  const { agent } = useAgent({
    updates: [UseAgentUpdate.OnStateChanged],
  });

  const increment = () => {
    agent.setState({
      ...agent.state,
      counter: (agent.state.counter || 0) + 1,
    });
  };

  return (
    <button onClick={increment}>
      Count: {agent.state.counter || 0}
    </button>
  );
}
```

### Error Behavior

After runtime sync (Connected or Error), `useAgent` throws if the agent ID doesn't exist. During sync, returns a provisional agent. Wrap in an error boundary if needed.

### Performance: Split by Update Type

```tsx
// Messages only
function Messages() {
  const { agent } = useAgent({ updates: [UseAgentUpdate.OnMessagesChanged] });
  return <>{agent.messages.map(m => <div key={m.id}>{m.content}</div>)}</>;
}

// Status only
function Status() {
  const { agent } = useAgent({ updates: [UseAgentUpdate.OnRunStatusChanged] });
  return <div>{agent.isRunning ? "Processing..." : "Ready"}</div>;
}

// Parent doesn't subscribe
function Chat() {
  return <><Status /><Messages /></>;
}
```

---

## 2. useCoAgent (v1)

v1 hook for LangGraph-based shared-state agents ("CoAgents"). Internally wraps `useAgent` from v2. Provides typed state, start/stop controls, and LangGraph node tracking.

```tsx
import { useCoAgent } from "@copilotkit/react-core";

type AgentState = {
  count: number;
};

function Counter() {
  const { state, setState, running } = useCoAgent<AgentState>({
    name: "my-agent",
    initialState: { count: 0 },
  });

  return (
    <div>
      <p>Count: {state.count}</p>
      <button onClick={() => setState({ count: state.count + 1 })}>
        Increment
      </button>
      <p>{running ? "Agent running..." : "Idle"}</p>
    </div>
  );
}
```

### Options

Three overloads for state management:

```ts
// Internal state with typed initial value
useCoAgent<AgentState>({
  name: "my-agent",
  initialState: { count: 0 },
});

// Internal state without initial value
useCoAgent({
  name: "my-agent",
});

// External state management (e.g., Zustand, Redux)
useCoAgent<AgentState>({
  name: "my-agent",
  state: externalState,
  setState: setExternalState,
});
```

Common options across all overloads:

```ts
{
  name: string;               // Required — agent ID (maps to agentId in v2)
  config?: {                  // LangGraph config
    configurable?: Record<string, any>;
    [key: string]: any;
  };
  configurable?: Record<string, any>;  // @deprecated — use config.configurable
}
```

### Return Value

```ts
interface UseCoagentReturnType<T> {
  name: string;         // Agent name
  nodeName?: string;    // Current LangGraph node name
  threadId?: string;    // Thread ID
  running: boolean;     // Is agent running
  state: T;             // Current state (typed)
  setState: (newState: T | ((prev: T | undefined) => T)) => void;
  start: () => void;    // Start agent run
  stop: () => void;     // Abort current run
  run: (...args: any[]) => Promise<any>;  // Re-run agent
}
```

### How it works internally

`useCoAgent` is a v1 compatibility wrapper:
1. Calls `useAgent({ agentId: options.name })` from `@copilotkitnext/react`
2. Subscribes to the v2 agent's state changes
3. Maps v2 agent methods to v1 return shape (`start` → `agent.runAgent`, `stop` → `agent.abortRun`)
4. Handles external vs internal state management
5. Tracks LangGraph node name via `useAgentNodeName`

### Bidirectional state sync

State changes from the agent update the UI, and `setState()` calls from the UI update the agent:

```tsx
const { state, setState } = useCoAgent<{ items: string[] }>({
  name: "todo-agent",
  initialState: { items: [] },
});

// UI → Agent
const addItem = (text: string) => {
  setState(prev => ({ items: [...(prev?.items || []), text] }));
};

// Agent → UI happens automatically via subscription
```

---

## 3. Rendering Agent State in Chat

Render custom UI in the chat based on agent intermediate state. Two APIs depending on version.

### v1: useCoAgentStateRender

Hook-based. Register a render function scoped to an agent (and optionally a LangGraph node).

```tsx
import { useCoAgentStateRender } from "@copilotkit/react-core";

useCoAgentStateRender<ResearchState>({
  name: "research-agent",
  nodeName: "search_node",  // optional — omit for all nodes
  render: ({ state, status, nodeName }) => (
    <div>
      <h3>Research Progress ({status})</h3>
      {state.logs?.map((log, i) => (
        <div key={i}>{log.done ? "done" : "..."} {log.message}</div>
      ))}
    </div>
  ),
  handler: async ({ state, nodeName }) => {
    // optional side-effect callback (runs alongside render)
  },
});
```

| Prop | Type | Purpose |
|---|---|---|
| `name` | `string` | Agent name (must match `useCoAgent` name) |
| `nodeName?` | `string` | Scope to a specific LangGraph node |
| `render?` | `(props: { state: T; nodeName: string; status: "inProgress" \| "complete" }) => ReactElement` | UI injected into chat |
| `handler?` | `(props: { nodeName: string; state: T }) => void` | Side effect callback |

Duplicate detection: two hooks for the same agent + node show a warning toast; the second overrides the first.

### v2: renderCustomMessages (CopilotKitProvider prop)

Provider-prop-based. Pass an array of renderers that receive the agent state snapshot per run.

```tsx
import { CopilotKitProvider } from "@copilotkitnext/react";

<CopilotKitProvider
  renderCustomMessages={[
    {
      agentId: "research-agent",  // optional — omit to match all agents
      render: ({ message, position, runId, agentId, stateSnapshot }) => {
        if (!stateSnapshot?.logs?.length) return null;
        return (
          <div>
            <h3>Research Progress</h3>
            {stateSnapshot.logs.map((log, i) => (
              <div key={i}>{log.done ? "done" : "..."} {log.message}</div>
            ))}
          </div>
        );
      },
    },
  ]}
>
```

| Render Prop | Type | Purpose |
|---|---|---|
| `message` | `Message` | The chat message this render is attached to |
| `position` | `"before" \| "after"` | Render before or after the message |
| `runId` | `string` | Agent run ID |
| `agentId` | `string` | Agent identifier |
| `stateSnapshot` | `any` | Agent state for this run (from `StateManager.getStateByRun()`) |
| `messageIndex` | `number` | Index in full message list |
| `messageIndexInRun` | `number` | Index within this run's messages |
| `numberOfMessagesInRun` | `number` | Total messages in this run |

### v1 vs v2 comparison

| | v1 `useCoAgentStateRender` | v2 `renderCustomMessages` |
|---|---|---|
| Registration | Hook call in component | Array prop on `CopilotKitProvider` |
| Scoping | By agent name + LangGraph node | By agent ID (no node-level scoping) |
| State access | `state` (live agent state) | `stateSnapshot` (state snapshot per run) |
| Positioning | Injected as chat message | `before` or `after` each message |
| Side effects | `handler` callback | No built-in handler (use `agent.subscribe` separately) |

---

## 4. useLangGraphInterrupt (v1)

Handle LangGraph `interrupt_after` / `interrupt_before` events. Renders UI for human approval and sends the resolution back to resume the graph.

```tsx
import { useLangGraphInterrupt } from "@copilotkit/react-core";

function DeleteApproval() {
  useLangGraphInterrupt({
    render: ({ event, resolve }) => (
      <div>
        <p>Agent wants to delete: {JSON.stringify(event.value)}</p>
        <button onClick={() => resolve("approved")}>Approve</button>
        <button onClick={() => resolve("denied")}>Deny</button>
      </div>
    ),
  });

  return null;
}
```

### How LangGraph interrupts work

1. LangGraph graph uses `interrupt_after: ["node_name"]` in compile config
2. When the graph hits the interrupt point, it emits an `on_interrupt` custom event
3. CopilotKit queues the event and renders UI via matching `useLangGraphInterrupt` hooks
4. User responds → `resolve(response)` is called
5. CopilotKit re-runs the agent with `command: { resume: response }` in forwarded props
6. The graph resumes from where it was interrupted

### Options

```ts
function useLangGraphInterrupt<TEventValue = any>(
  action: {
    handler?: (props: {
      event: LangGraphInterruptEvent<TEventValue>;
      resolve: (resolution: string) => void;
    }) => any | Promise<any>;

    render?: (props: {
      event: LangGraphInterruptEvent<TEventValue>;
      result: unknown;     // Return value of handler, if any
      resolve: (resolution: string) => void;
    }) => string | React.ReactElement;

    enabled?: (args: {
      eventValue: TEventValue;
      agentMetadata: AgentSession;  // { nodeName, ... }
    }) => boolean;
  },
  dependencies?: any[],
): void;
```

### Filtering interrupts with `enabled`

When multiple interrupt handlers are registered, use `enabled` to filter by event content or agent metadata:

```tsx
// Only handle delete-related interrupts
useLangGraphInterrupt({
  enabled: ({ eventValue, agentMetadata }) => {
    return agentMetadata.nodeName === "delete_node";
  },
  render: ({ event, resolve }) => (
    <DeleteConfirmation items={event.value} onConfirm={() => resolve("confirmed")} />
  ),
});

// Handle all other interrupts
useLangGraphInterrupt({
  render: ({ event, resolve }) => (
    <GenericApproval event={event} onResolve={resolve} />
  ),
});
```

**Note:** A handler without `enabled` matches ALL interrupt events. If another handler also exists, CopilotKit may warn about conflicts.

### With handler (pre-processing)

The `handler` runs before `render`, and its return value is passed as `result` to the render function:

```tsx
useLangGraphInterrupt({
  handler: async ({ event }) => {
    // Fetch additional data before rendering
    const details = await fetchDetails(event.value.id);
    return details;
  },
  render: ({ event, result, resolve }) => (
    <div>
      <p>Action: {event.value.action}</p>
      <p>Details: {JSON.stringify(result)}</p>
      <button onClick={() => resolve("approved")}>Approve</button>
    </div>
  ),
});
```

---

## 5. v1 → v2 Migration

| v1 | v2 Equivalent |
|---|---|
| `useCoAgent({ name })` | `useAgent({ agentId })` |
| `useCoAgent.state` | `agent.state` |
| `useCoAgent.setState(s)` | `agent.setState(s)` |
| `useCoAgent.running` | `agent.isRunning` |
| `useCoAgent.start()` / `.run()` | `agent.runAgent()` or `copilotkit.runAgent({ agent })` |
| `useCoAgent.stop()` | `agent.abortRun()` |
| `useCoAgent.nodeName` | No direct equivalent (LangGraph-specific) |
| `useCoAgentStateRender` | `renderCustomMessages` prop on `CopilotKitProvider`. Renderer receives `{ message, position, runId, agentId, stateSnapshot }`. Different API shape but same capability — renders custom UI in chat based on agent state per run. |
| `useLangGraphInterrupt` | `useHumanInTheLoop` for general HITL; v1 hook for LangGraph-specific interrupts |

**Key difference:** `useCoAgent` uses `name` (the agent name), while `useAgent` uses `agentId`. Internally, `useCoAgent` calls `useAgent({ agentId: options.name })`.

### When to use which

- **New v2 projects:** Use `useAgent` + `useCopilotKit` directly
- **Existing LangGraph integrations:** `useCoAgent` still works (wraps v2 internally)
- **Intermediate state rendering in chat:** v1: `useCoAgentStateRender` (per-agent/node render callback). v2: `renderCustomMessages` prop on `CopilotKitProvider` (receives `stateSnapshot` per run — different API but same capability)
- **LangGraph interrupts:** `useLangGraphInterrupt` (v1 hook, LangGraph-specific)
- **General HITL:** `useHumanInTheLoop` (see hooks-tools.md)
