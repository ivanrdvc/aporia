---
source: https://github.com/ag-ui-protocol/ag-ui (apps/dojo) + https://github.com/microsoft/agent-framework (dotnet/samples/AGUIClientServer/AGUIDojoServer)
extracted: 2026-02-14
scope: full-app
demonstrates: All 7 AG-UI capabilities end-to-end — agentic chat, backend tool rendering, HITL, agentic generative UI, tool-based generative UI, shared state, predictive state updates. DelegatingAIAgent middleware pattern, DataContent for state events, JSON Patch for deltas.
---

## What is the Dojo

Interactive demo/test app at [dojo.ag-ui.com](https://dojo.ag-ui.com/). Three-panel layout: **Preview** (live demo), **Code** (source), **Docs** (specs). Each panel covers one of 7 AG-UI capabilities. Works as both a learning tool and an integration test harness.

Frontend is a Next.js app (in `ag-ui-protocol/ag-ui` repo under `apps/dojo`). Backend agents can be any framework — the .NET `AGUIDojoServer` is one implementation.

## .NET DojoServer structure

```
AGUIDojoServer/
  Program.cs                          # 7 MapAGUI endpoints
  ChatClientAgentFactory.cs           # Factory — creates all 7 agents
  AGUIDojoServerSerializerContext.cs   # Source-gen JSON context
  BackendToolRendering/
    WeatherInfo.cs                    # Tool response model
  AgenticUI/
    AgenticUIAgent.cs                 # DelegatingAIAgent — intercepts plan tools, emits state
    AgenticPlanningTools.cs           # create_plan, update_plan_step
    Plan.cs, Step.cs, StepStatus.cs   # Models
    JsonPatchOperation.cs             # RFC 6902 patch model
  SharedState/
    SharedStateAgent.cs               # DelegatingAIAgent — two-phase state + summary
    Recipe.cs, RecipeResponse.cs, Ingredient.cs
  PredictiveStateUpdates/
    PredictiveStateUpdatesAgent.cs    # DelegatingAIAgent — chunks document into streamed state
    DocumentState.cs
```

## Endpoint mapping

```csharp
app.MapAGUI("/agentic_chat",              ChatClientAgentFactory.CreateAgenticChat());
app.MapAGUI("/backend_tool_rendering",    ChatClientAgentFactory.CreateBackendToolRendering());
app.MapAGUI("/human_in_the_loop",         ChatClientAgentFactory.CreateHumanInTheLoop());
app.MapAGUI("/tool_based_generative_ui",  ChatClientAgentFactory.CreateToolBasedGenerativeUI());
app.MapAGUI("/agentic_generative_ui",     ChatClientAgentFactory.CreateAgenticUI(jsonOptions));
app.MapAGUI("/shared_state",              ChatClientAgentFactory.CreateSharedState(jsonOptions));
app.MapAGUI("/predictive_state_updates",  ChatClientAgentFactory.CreatePredictiveStateUpdates(jsonOptions));
```

Simple scenarios (1-4) are plain `ChatClientAgent` — no middleware. Advanced scenarios (5-7) wrap with `DelegatingAIAgent`.

## The 7 capabilities

### 1. Agentic Chat

Bare minimum — `ChatClient` → `AsIChatClient()` → `AsAIAgent()`. No tools, no middleware.

### 2. Backend Tool Rendering

Server-side tool with typed response. Frontend renders the tool result via `useRenderToolCall` or `useDefaultTool`.

```csharp
[Description("Get the weather for a given location.")]
static WeatherInfo GetWeather([Description("The location")] string location) => new()
{
    Temperature = 20, Conditions = "sunny", Humidity = 50, WindSpeed = 10, FeelsLike = 25
};

AITool[] tools = [AIFunctionFactory.Create(GetWeather, name: "get_weather",
    description: "Get the weather for a given location.",
    AGUIDojoServerSerializerContext.Default.Options)];
```

### 3. Human-in-the-Loop

Agent created with no special config — HITL handled by CopilotKit frontend via `useHumanInTheLoop`. The dojo frontend renders approval UI when the agent emits tool calls that match configured approval patterns.

### 4. Tool-Based Generative UI

Same as agentic chat — no server-side tools. The frontend defines tools via `useFrontendTool` that return custom React components. Agent calls these tools, frontend renders the generated UI.

### 5. Agentic Generative UI (plan steps)

**Pattern:** Agent uses `create_plan` and `update_plan_step` tools. `AgenticUIAgent` middleware intercepts results and emits them as state events.

```csharp
// AgenticUIAgent intercepts tool results:
if (matchedCall.Name == "create_plan")
    stateEventsToEmit.Add(new DataContent(bytes, "application/json"));       // STATE_SNAPSHOT
else if (matchedCall.Name == "update_plan_step")
    stateEventsToEmit.Add(new DataContent(bytes, "application/json-patch+json")); // STATE_DELTA
```

Tool models:

```csharp
// create_plan returns:
class Plan { List<Step> Steps; }
class Step { string Description; StepStatus Status; }

// update_plan_step returns JSON Patch operations:
class JsonPatchOperation { string Op; string Path; object? Value; }
// e.g. { "op": "replace", "path": "/steps/0/status", "value": "completed" }
```

**Key detail:** `create_plan` → `DataContent("application/json")` → `STATE_SNAPSHOT`. `update_plan_step` → `DataContent("application/json-patch+json")` → `STATE_DELTA`. The frontend reads the plan state and renders step progress UI.

Agent instructions enforce tool-only output (no chat messages while planning):
```
When planning use tools only, without any other messages.
Use the `create_plan` tool to set the initial state of the steps.
Use the `update_plan_step` tool to update the status of each step.
Do NOT repeat the plan or summarise it in a message.
```

### 6. Shared State (two-phase)

**Pattern:** `SharedStateAgent` middleware does two LLM calls per request:
1. **Phase 1** — Structured output via `ChatResponseFormat.ForJsonSchema<RecipeResponse>()`. Reads `ag_ui_state` from request, generates updated state. Emits as `DataContent("application/json")` → `STATE_SNAPSHOT`.
2. **Phase 2** — Freeform text summary of the state changes. Streams as normal `TEXT_MESSAGE_*`.

```csharp
// Phase 1: read client state from request
if (!props.TryGetValue("ag_ui_state", out JsonElement state)) { /* pass through */ }

// Generate structured state
firstRunOptions.ChatOptions.ResponseFormat = ChatResponseFormat.ForJsonSchema<RecipeResponse>(...);

// Emit snapshot
yield return new AgentResponseUpdate { Contents = [new DataContent(stateBytes, "application/json")] };

// Phase 2: summary
var secondRunMessages = messages.Concat(response.Messages).Append(
    new ChatMessage(ChatRole.System, "Please provide a concise summary..."));
await foreach (var update in InnerAgent.RunStreamingAsync(secondRunMessages, ...))
    yield return update;
```

**Frontend side:** `useCoAgent` / `useAgent` sends state as `ag_ui_state` in the request. State snapshots update the hook's `state` object. UI re-renders on state change.

### 7. Predictive State Updates (streaming document)

**Pattern:** `PredictiveStateUpdatesAgent` middleware detects `write_document` tool calls and chunks the document content into progressive state snapshots with 50ms delays — creates a typing effect.

```csharp
// Chunk document into 10-char pieces, emit each as STATE_SNAPSHOT
for (int i = startIndex; i < documentContent.Length; i += ChunkSize)
{
    string chunk = documentContent.Substring(0, i + length);
    var stateUpdate = new DocumentState { Document = chunk };
    byte[] stateBytes = JsonSerializer.SerializeToUtf8Bytes(stateUpdate, ...);

    yield return new AgentResponseUpdate(
        new ChatResponseUpdate(role: ChatRole.Assistant,
            [new DataContent(stateBytes, "application/json")]));

    await Task.Delay(50, cancellationToken);
}
```

**Key detail:** Only streams the _new_ portion if the document was previously emitted (diff-aware via `lastEmittedDocument`).

## Wiring patterns

- **Simple agents** (1-4): `ChatClient` → `AsIChatClient()` → `AsAIAgent(name, description, tools?)` → `MapAGUI(path, agent)`. No middleware.
- **Advanced agents** (5-7): Same base agent, wrapped with `DelegatingAIAgent` subclass → `MapAGUI(path, wrappedAgent)`.
- **DelegatingAIAgent** always overrides `RunCoreStreamingAsync`. `RunCoreAsync` delegates to streaming via `ToAgentResponseAsync()`.
- **State events** always use `DataContent`:
  - `"application/json"` → `STATE_SNAPSHOT` (full state replacement)
  - `"application/json-patch+json"` → `STATE_DELTA` (RFC 6902 JSON Patch)
- **Factory pattern**: `ChatClientAgentFactory` centralizes Azure OpenAI client creation. `Initialize(config)` called once at startup.
- **JSON serialization**: Single `AGUIDojoServerSerializerContext` registered via `ConfigureHttpJsonOptions`. All tool models and state models included.

## Gotchas

- `AllowMultipleToolCalls = false` on the agentic UI agent — prevents the LLM from calling `create_plan` and `update_plan_step` in the same turn, which would break the sequential plan flow.
- `update_plan_step` has a `Task.Delay(1000)` — intentional slowdown so the frontend has time to animate step transitions.
- Status values must be lowercase strings (`"pending"`, `"completed"`) not enum names — the AG-UI frontend expects lowercase.
- The predictive state agent always emits the full document up to the current chunk (not just the delta) because each emission is a `STATE_SNAPSHOT`, not a patch.
- `SharedStateAgent` suppresses `TextContent` from Phase 1 (the JSON schema output) — only non-text content (tool calls) gets yielded. Phase 2 text streams normally.
