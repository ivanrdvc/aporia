# .NET Backend (Agent Framework + AG-UI)

.NET AG-UI server using Microsoft Agent Framework. Hosts AI agents as HTTP endpoints that stream responses via SSE to a CopilotKit React frontend.

```
dotnet add package Microsoft.Agents.AI.Hosting.AGUI.AspNetCore --prerelease
dotnet add package Azure.AI.OpenAI --prerelease
dotnet add package Azure.Identity
dotnet add package Microsoft.Extensions.AI.OpenAI --prerelease
```

---

## Minimal Server Setup

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient().AddLogging();
builder.Services.AddAGUI();  // Register AG-UI middleware services

var app = builder.Build();

string endpoint = builder.Configuration["AZURE_OPENAI_ENDPOINT"]!;
string deployment = builder.Configuration["AZURE_OPENAI_DEPLOYMENT_NAME"]!;

ChatClient chatClient = new AzureOpenAIClient(
        new Uri(endpoint), new DefaultAzureCredential())
    .GetChatClient(deployment);

AIAgent agent = chatClient.AsIChatClient().AsAIAgent(
    name: "AGUIAssistant",
    instructions: "You are a helpful assistant.");

app.MapAGUI("/", agent);  // HTTP POST + SSE endpoint
await app.RunAsync();
```

### Core API

| Method | Purpose |
|---|---|
| `builder.Services.AddAGUI()` | Register AG-UI services in DI container |
| `app.MapAGUI(path, agent)` | Map agent to HTTP endpoint — handles POST requests, returns SSE stream |
| `chatClient.AsIChatClient()` | Convert OpenAI `ChatClient` to `IChatClient` (from `Microsoft.Extensions.AI.OpenAI`) |
| `client.AsAIAgent(name, instructions, tools?)` | Create `AIAgent` from `IChatClient` |

### How It Connects to CopilotKit

```
React (CopilotKit)  ──HTTP POST──►  .NET MapAGUI endpoint
                    ◄──SSE stream──  (AgentResponseUpdate events)
```

- Frontend sends messages via HTTP POST to the `MapAGUI` endpoint
- Server streams back events auto-converted to AG-UI protocol (TEXT_MESSAGE_*, TOOL_CALL_*, STATE_SNAPSHOT, etc.)
- `ConversationId` maintains session continuity across requests

```tsx
<CopilotKit runtimeUrl="http://localhost:8888">
  {/* or use useAgent() with agentUrl */}
</CopilotKit>
```

---

## Backend Tools

Tools execute server-side. Results stream to the CopilotKit client automatically.

```csharp
// 1. Define types + JSON serialization context
[JsonSerializable(typeof(SearchRequest))]
[JsonSerializable(typeof(SearchResponse))]
internal sealed partial class ToolJsonContext : JsonSerializerContext;

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Add(ToolJsonContext.Default));

// 2. Define the tool function ([Description] becomes LLM metadata)
[Description("Search for information.")]
static SearchResponse Search(
    [Description("The search parameters")] SearchRequest request)
{
    return new SearchResponse { Results = ["result1", "result2"] };
}

// 3. Create tool with serializer options (required for complex param types)
var jsonOptions = app.Services
    .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value;

AITool[] tools = [
    AIFunctionFactory.Create(Search, serializerOptions: jsonOptions.SerializerOptions)
];

// 4. Create agent with tools
AIAgent agent = chatClient.AsIChatClient().AsAIAgent(
    name: "Assistant", instructions: "...", tools: tools);
```

**Key:** Complex parameter types **require** `serializerOptions` + registered `[JsonSerializable]` context. Simple types (string, int) don't.

### Frontend Tool Support

Frontend tools (defined in CopilotKit via `useCopilotAction`) require **no special server configuration**. The server automatically receives client tool declarations, routes calls back to the client, and processes results.

---

## Human-in-the-Loop (Approval Workflow)

Uses `DelegatingAIAgent` middleware to intercept tool calls and request user approval before execution.

### Flow

1. Wrap sensitive tools with `ApprovalRequiredAIFunction`
2. Server middleware intercepts approval-required function calls
3. Emits `request_approval` tool call to client (via SSE)
4. CopilotKit client renders approval UI (via `useHumanInTheLoop`)
5. Client sends approval/rejection back as `FunctionResultContent`
6. Server processes response — executes or skips the original function

### Key Types

- `ApprovalRequiredAIFunction` — wraps an `AIFunction` to mark it as requiring approval
- `FunctionApprovalRequestContent` — content type for approval requests (intercepted in middleware)
- `FunctionApprovalResponseContent` — client's approve/reject response

See sample: `GettingStarted/AGUI/Step04_HumanInLoop/Server/ServerFunctionApprovalServerAgent.cs`

---

## State Management

Bidirectional state sync between CopilotKit frontend and .NET backend. Uses `DelegatingAIAgent` middleware with a two-phase execution pattern.

### How It Works

1. Client sends current state in `ChatOptions.AdditionalProperties["ag_ui_state"]` (arrives as `JsonElement`)
2. **Phase 1:** Middleware runs agent with `ChatResponseFormat.ForJsonSchema<T>()` to generate structured state update
3. Middleware emits state as `DataContent("application/json")` — auto-converts to `STATE_SNAPSHOT` event
4. **Phase 2:** Middleware runs agent again to generate user-friendly text summary

### Key APIs

```csharp
// Force structured JSON output for state
options.ChatOptions.ResponseFormat = ChatResponseFormat.ForJsonSchema<StateResponse>(
    schemaName: "StateResponse",
    schemaDescription: "Updated application state");

// Emit state snapshot (DataContent with application/json → STATE_SNAPSHOT event)
yield return new AgentResponseUpdate
{
    Contents = [new DataContent(stateBytes, "application/json")]
};

// Emit state delta (DataContent with json-patch → STATE_DELTA event)
yield return new AgentResponseUpdate
{
    Contents = [new DataContent(patchBytes, "application/json-patch+json")]
};
```

See sample: `GettingStarted/AGUI/Step05_StateManagement/Server/SharedStateAgent.cs`

---

## DelegatingAIAgent (Middleware Pattern)

Core mechanism for intercepting and transforming agent behavior. Used by approval and state management.

```csharp
internal sealed class MyMiddleware : DelegatingAIAgent
{
    public MyMiddleware(AIAgent innerAgent) : base(innerAgent) { }

    public override async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session,
        AgentRunOptions? options, [EnumeratorCancellation] CancellationToken ct)
    {
        // Pre-process: modify messages, options, inject context
        await foreach (var update in InnerAgent.RunStreamingAsync(messages, session, options, ct))
        {
            // Post-process: transform, filter, or augment updates
            yield return update;
        }
    }
}
```

Compose by wrapping (outermost runs first):

```csharp
AIAgent agent = chatClient.AsIChatClient().AsAIAgent(...);
agent = new ApprovalAgent(agent);
agent = new SharedStateAgent(agent, jsonOptions);
app.MapAGUI("/", agent);
```

---

## Content Types → AG-UI Events

| Content Type | AG-UI Event | Description |
|---|---|---|
| `TextContent` | `TEXT_MESSAGE_*` | Streamed text (auto-wrapped in START/END) |
| `FunctionCallContent` | `TOOL_CALL_START/ARGS/END` | Tool invocation with `Name`, `Arguments` |
| `FunctionResultContent` | `TOOL_CALL_RESULT` | Tool result with `CallId`, `Result` |
| `DataContent("application/json")` | `STATE_SNAPSHOT` | Full state replace |
| `DataContent("application/json-patch+json")` | `STATE_DELTA` | Incremental state patch (RFC 6902) |
| `ErrorContent` | `RUN_ERROR` | Error with `Message` |
| Other (`ImageContent`, etc.) | — | **Silently dropped** |

---

## Protocol Compatibility

.NET supports **12 of 16 core AG-UI events**.

### Supported

| Category | Events |
|---|---|
| Lifecycle | `RUN_STARTED`, `RUN_FINISHED`, `RUN_ERROR` (auto-emitted by `MapAGUI`) |
| Text | `TEXT_MESSAGE_START/CONTENT/END` (auto-wrapped around `TextContent`) |
| Tool | `TOOL_CALL_START/ARGS/END`, `TOOL_CALL_RESULT` |
| State | `STATE_SNAPSHOT`, `STATE_DELTA` |

### Not Supported

| Event | Workaround |
|---|---|
| `STEP_STARTED` / `STEP_FINISHED` | Use text messages or tool calls for progress |
| `MESSAGES_SNAPSHOT` | Client maintains its own history; use `ConversationId` for continuity |
| `RAW` / `CUSTOM` | Wrap in tool results or text messages |

Convenience events (`TEXT_MESSAGE_CHUNK`, `TOOL_CALL_CHUNK`) are client-side shorthands — .NET always emits the expanded form.

### Message Types Not Emitted

| Type | Notes |
|---|---|
| `Activity` | Frontend-only (progress/status) — CopilotKit creates these client-side, no backend support needed |
| `Reasoning` | No .NET mapping. `TextReasoningContent` from M.E.AI is silently dropped |

### CopilotKit Feature Compatibility

| Feature | .NET Support |
|---|---|
| Streaming chat (`CopilotChat`) | Fully supported |
| Backend tools | Fully supported |
| Frontend tools (`useCopilotAction`) | Fully supported (auto-routed) |
| Human-in-the-loop (`useHumanInTheLoop`) | Via middleware |
| Shared state (`useCoAgent` / `useAgent`) | Via middleware |
| Generative UI (`useCoAgentStateRender`) | Via state snapshots |
| Step progress indicators | Not supported |
| Activity messages | N/A (frontend-only) |

---

## Quick Reference

### NuGet Packages

| Package | Purpose |
|---|---|
| `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore` | AG-UI hosting — `MapAGUI`, `AddAGUI` |
| `Microsoft.Agents.AI` | Core agent types — `AsAIAgent()` |
| `Azure.AI.OpenAI` | Azure OpenAI client |
| `Azure.Identity` | `DefaultAzureCredential` |
| `Microsoft.Extensions.AI.OpenAI` | `AsIChatClient()` bridge |

### Project SDK

Server projects require `Microsoft.NET.Sdk.Web` (not `Microsoft.NET.Sdk`).

### Running

```bash
export AZURE_OPENAI_ENDPOINT="https://your-resource.openai.azure.com/"
export AZURE_OPENAI_DEPLOYMENT_NAME="gpt-4o-mini"
dotnet run --urls http://localhost:8888
```
