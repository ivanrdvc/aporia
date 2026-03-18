# AG-UI Protocol

The Agent–User Interaction Protocol. Open, event-based spec for agent ↔ frontend communication. Sits alongside MCP (agent ↔ tools) and A2A (agent ↔ agent) in the agentic protocol stack.

Core contract: `run(input: RunAgentInput) -> Observable<BaseEvent>`

Full spec: https://docs.ag-ui.com

## Events

16 event types across 5 categories. Events stream over SSE or binary transport.

### Lifecycle

| Event | Key Fields | Purpose |
|---|---|---|
| `RUN_STARTED` | `threadId`, `runId`, `parentRunId?`, `input?` | Begins an agent run |
| `RUN_FINISHED` | `result?` | Signals successful completion |
| `RUN_ERROR` | `message`, `code?` | Terminates run with error |
| `STEP_STARTED` | `stepName` | Begins a processing subtask |
| `STEP_FINISHED` | `stepName` | Completes a subtask (must match start) |

### Text Messages

Streaming pattern: **Start → Content* → End**

| Event | Key Fields | Purpose |
|---|---|---|
| `TEXT_MESSAGE_START` | `messageId`, `role` | Initializes a message |
| `TEXT_MESSAGE_CONTENT` | `messageId`, `delta` | Streams text chunks |
| `TEXT_MESSAGE_END` | `messageId` | Completes message |
| `TEXT_MESSAGE_CHUNK` | `messageId`, `role`, `delta` | Convenience: auto-expands to Start→Content→End |

### Tool Calls

Streaming pattern: **Start → Args* → End**

| Event | Key Fields | Purpose |
|---|---|---|
| `TOOL_CALL_START` | `toolCallId`, `toolCallName`, `parentMessageId?` | Agent invokes a tool |
| `TOOL_CALL_ARGS` | `toolCallId`, `delta` | Streams JSON argument fragments |
| `TOOL_CALL_END` | `toolCallId` | Argument streaming complete |
| `TOOL_CALL_RESULT` | `messageId`, `toolCallId`, `content`, `role?` | Tool execution output |
| `TOOL_CALL_CHUNK` | (all start+args fields) | Convenience: auto-expands to Start→Args→End |

### State Management

| Event | Key Fields | Purpose |
|---|---|---|
| `STATE_SNAPSHOT` | (full state object) | Complete state replace — used at init, after reconnect, or major changes |
| `STATE_DELTA` | (RFC 6902 JSON Patch ops) | Incremental update — ops: `add`, `replace`, `remove`, `move`, `copy` with JSON pointer `path` |
| `MESSAGES_SNAPSHOT` | (message array) | Full conversation history for init/sync |

### Special

| Event | Key Fields | Purpose |
|---|---|---|
| `RAW` | (external event data) | Container for events from external systems |
| `CUSTOM` | `name`, `value` | Extension point for app-defined events |

## Messages

6 message types, vendor-neutral. Role-discriminated.

| Type | Key Fields | Notes |
|---|---|---|
| **User** | `role: "user"`, `content` (text or `InputContent[]` for multimodal) | End-user input — text, images, audio, files |
| **Assistant** | `role: "assistant"`, `content?`, `toolCalls?`, `encryptedContent?` | AI response, may include tool invocations |
| **System** | `role: "system"`, `content` | Instructions/context for the agent |
| **Tool** | `role: "tool"`, `toolCallId`, `content`, `error?` | Execution result linked to original call |
| **Activity** | `messageId`, `activityType`, `content` | Frontend-only — progress/status, never sent to agent |
| **Reasoning** | `messageId`, `content?`, `encryptedContent?` | Agent's internal thought process, supports encrypted values |

## Shared State

Bidirectional state sync between agent and frontend. State persists across interactions.

- **StateSnapshot**: full replace. Frontend discards existing state, replaces entirely. Used at start, after reconnect, or for major changes.
- **StateDelta**: incremental RFC 6902 JSON Patch. Applied atomically via `fast-json-patch`. Ops: `{ op: "add"|"replace"|"remove"|"move"|"copy", path: "/json/pointer", value? }`.

Both sides can read and write. Maps to CopilotKit's coagent shared state (`useCoAgent`).

## Frontend Tools

Tools defined in frontend, passed to agent at execution time via `RunAgentParameters.tools`. JSON Schema for params.

**Lifecycle:**
1. Agent emits `TOOL_CALL_START` (`toolCallId`, `toolCallName`)
2. Agent streams args via `TOOL_CALL_ARGS` (`delta` — JSON fragments)
3. Agent emits `TOOL_CALL_END`
4. Frontend accumulates args, executes tool
5. Frontend sends back `TOOL_CALL_RESULT` as a Tool message (`role: "tool"`, `toolCallId`, `content`)

Enables human-in-the-loop: agent can request confirmation/input before proceeding. Maps to CopilotKit's `useFrontendTool`.

## Generative UI Specs

AG-UI is not itself a gen-UI spec — it carries them. Three supported:

- **A2UI** (Google) — declarative, JSONL-based streaming, platform-agnostic rendering
- **Open-JSON-UI** (OpenAI) — open standardization of OpenAI's internal declarative UI schema
- **MCP-UI** (Microsoft + Shopify) — iframe-based, extends MCP for user-facing experiences

Custom specs also supported. Delivered through AG-UI's event stream.
