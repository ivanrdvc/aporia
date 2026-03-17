# AG-UI Client SDK (`@ag-ui/client`)

Agent connectivity SDK. Provides `AbstractAgent` (base class) and `HttpAgent` (HTTP/SSE transport).

```
npm install @ag-ui/client
```

Full SDK docs: https://docs.ag-ui.com/sdk/js/client/abstract-agent

## AbstractAgent

Base class for all agent implementations. Handles event stream processing, state management, message history.

### Config

```ts
interface AgentConfig {
  agentId?: string           // Agent identifier
  description?: string       // LLM-facing description
  threadId?: string          // Conversation thread ID
  initialMessages?: Message[] // Starting messages
  initialState?: State       // Initial state
}
```

Extend via `interface MyConfig extends AgentConfig { ... }` + `super(config)` in constructor.

### Core Methods

| Method | Signature | Purpose |
|---|---|---|
| `runAgent()` | `(params?: RunAgentParameters, subscriber?: AgentSubscriber) => Promise<RunAgentResult>` | Execute agent, get result + new messages |
| `connectAgent()` | `(params?: RunAgentParameters, subscriber?: AgentSubscriber) => Promise<RunAgentResult>` | Persistent connection (agent must implement `connect()`) |
| `subscribe()` | `(subscriber: AgentSubscriber) => { unsubscribe() }` | Register event handler across multiple runs |
| `use()` | `(...middlewares) => this` | Chain middleware for event processing |
| `abortRun()` | `() => void` | Cancel current execution |
| `clone()` | `() => AbstractAgent` | Deep copy of agent instance |

```ts
interface RunAgentParameters {
  runId?: string
  tools?: Tool[]
  context?: Context[]
  forwardedProps?: Record<string, any>
}

interface RunAgentResult {
  result: any
  newMessages: Message[]
}
```

### Properties

- `agentId`, `description`, `threadId` — config values
- `messages` — conversation history array
- `state` — current agent state object
- `events$` — `ReplaySubject<BaseEvent>`, late subscribers get historical events

### Protected Methods (for subclasses)

| Method | Purpose |
|---|---|
| `run(input): RunAgent` | **Abstract** — must implement. Returns observable event stream |
| `connect(input): RunAgent` | Override for persistent connections. Default throws `ConnectNotImplementedError` |
| `apply(input): ApplyEvents` | Processes run events, updates agent state |
| `prepareRunAgentInput(params?)` | Transforms params to internal input format |
| `onError(error)` | Lifecycle hook for error handling |
| `onFinalize()` | Lifecycle hook for cleanup |

## HttpAgent

Extends `AbstractAgent` for HTTP/SSE transport. POST request → SSE event stream back.

```ts
interface HttpAgentConfig extends AgentConfig {
  url: string                        // Agent endpoint
  headers?: Record<string, string>   // Custom headers (auth, etc.)
}

const agent = new HttpAgent({
  url: "https://api.example.com/v1/agent",
  headers: { Authorization: "Bearer token" }
})
```

### Override `requestInit()` for custom request config:

```ts
// Default sends POST with JSON body, Accept: text/event-stream
protected requestInit(input: RunAgentInput): RequestInit
```

Properties: `url`, `headers`, `abortController`.

## Middleware

Transform/filter event streams. Added via `agent.use()`. Executes in order added, each wrapping the next. **Runs in `runAgent()` only** — `connectAgent()` bypasses middleware.

### Function middleware (stateless):

```ts
type MiddlewareFunction = (input: RunAgentInput, next: AbstractAgent) => Observable<BaseEvent>

agent.use((input, next) => {
  return next.run(input).pipe(tap(e => console.log(e.type)))
})
```

### Class middleware (stateful):

```ts
abstract class Middleware {
  abstract run(input: RunAgentInput, next: AbstractAgent): Observable<BaseEvent>
  protected runNext(input, next): Observable<BaseEvent>           // normalizes chunk events
  protected runNextWithState(input, next): Observable<EventWithState> // + accumulated messages/state
}
```

### Built-in: `FilterToolCallsMiddleware`

```ts
agent.use(new FilterToolCallsMiddleware({ allowedToolCalls: ["search", "calculate"] }))
// or: { disallowedToolCalls: ["dangerous_tool"] }
```

Filters emitted `TOOL_CALL_*` events. Does NOT prevent tool execution in upstream model/runtime.

## compactEvents

Utility to reduce verbose streaming sequences for storage/debugging.

```ts
import { compactEvents } from "@ag-ui/client"
const compacted: BaseEvent[] = compactEvents(events)
```

Groups `Start→Content*→End` into `Start→SingleContent→End` (concatenates deltas). Same for tool call args. Non-streaming events pass through unchanged.
