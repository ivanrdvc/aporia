---
keywords: [potential-issue, DataContent, STATE_SNAPSHOT, useCoAgentStateRender, agent-framework, CopilotKit]
status: investigating
---

# DataContent vs STATE_SNAPSHOT Mismatch

## Problem

`useCoAgentStateRender` does not receive state updates when using C# middleware that emits `DataContent`.

## Observed Behavior

- Backend emits state via `DataContent(stateJson, "application/json")`
- `useCoAgent` receives final state correctly
- `useCoAgentStateRender` never triggers (no in-chat rendering)

## C# Code Emitting State

```csharp
private static AgentRunResponseUpdate CreateStateUpdate(ShowYourWorkState state, JsonSerializerOptions jsonOptions)
{
    var stateJson = JsonSerializer.SerializeToUtf8Bytes(state, jsonOptions);
    return new AgentRunResponseUpdate
    {
        Contents = [new DataContent(stateJson, "application/json")]
    };
}
```

## Suspected Root Cause

`DataContent` with JSON MIME type is not interpreted by CopilotKit as an AG-UI `STATE_SNAPSHOT` event.

AG-UI protocol expects specific event types:

```
STATE_SNAPSHOT event:
{
  "type": "STATE_SNAPSHOT",
  "snapshot": { ... state data ... }
}
```

But `DataContent` emits raw JSON bytes without the AG-UI event wrapper.

## Scope

Affects any .NET agent using `DataContent` to emit custom state for `useCoAgentStateRender` consumption.
