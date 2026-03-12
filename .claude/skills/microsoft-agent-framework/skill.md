---
name: ms-agent-framework
description: Use when working with the Microsoft Agent Framework (MAF) — building agents, workflows, tools, MCP, or any question about the framework. Trigger on mentions of Microsoft Agent Framework, MAF, Microsoft.Agents.AI, or agent-framework NuGet packages.
---

# Microsoft Agent Framework

The next-gen open-source SDK from Microsoft for building AI agents and multi-agent workflows (.NET & Python). Successor to both Semantic Kernel and AutoGen — same teams, merged into one framework. Currently in **public preview**.

## Key concepts

| Concept | What it is |
|---|---|
| **Agent** | Single agent that calls an LLM, uses tools, returns responses. `AIAgent` in .NET. |
| **Workflow** | Graph-based multi-agent orchestration — sequential, concurrent, conditional, human-in-the-loop, checkpointing. |
| **Tools** | Functions the agent can call. Supports native functions, MCP servers, OpenAPI. |
| **Middleware** | Intercept/modify agent actions (logging, auth, guardrails). |
| **Providers** | LLM backends — Azure OpenAI, OpenAI, Anthropic, Ollama, GitHub Models, Foundry. |
| **Session** | State management for multi-turn and long-running conversations. |
| **MCP** | Model Context Protocol — connect to external tool servers. |

## .NET packages (all `--prerelease`)

Core: `Microsoft.Agents.AI`, `Microsoft.Agents.AI.Abstractions`
Providers: `Microsoft.Agents.AI.OpenAI`, `Microsoft.Agents.AI.Anthropic`, `Microsoft.Agents.AI.AzureAI`
Workflows: `Microsoft.Agents.AI.Workflows`
Hosting: `Microsoft.Agents.AI.Hosting`, `Microsoft.Agents.AI.Hosting.AzureFunctions`
State: `Microsoft.Agents.AI.CosmosNoSql`, `Microsoft.Agents.AI.Mem0`
Other: `Microsoft.Agents.AI.DevUI`, `Microsoft.Agents.AI.Purview`, `Microsoft.Agents.AI.DurableTask`

## Local repo

<!-- TODO: configure local clone path if needed -->

## Samples — go-to starting points

When the user needs a working example, start from the samples below.

**Agents (step-by-step, most useful):**
[GettingStarted/Agents](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/GettingStarted/Agents)
`Step01_Running` through `Step20` — covers basic agent, multi-turn, function tools, structured output, middleware, DI, observability, images, MCP, plugins, deep research.

**Workflows (graph-based orchestration):**
[GettingStarted/Workflows](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/GettingStarted/Workflows)
Sequential, concurrent, conditional, human-in-the-loop, checkpointing, shared states, loop, declarative.

**MCP integration:**
[GettingStarted/ModelContextProtocol](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/GettingStarted/ModelContextProtocol)

### Other sample folders (less common)

| Folder | What |
|---|---|
| `GettingStarted/AgentProviders` | Wiring different LLM providers |
| `GettingStarted/AgentWithMemory` | Persistent memory (Mem0) |
| `GettingStarted/AgentWithRAG` | RAG / file search |
| `GettingStarted/DevUI` | Visual debugging UI |
| `GettingStarted/DeclarativeAgents` | YAML/JSON agent definitions |
| `HostedAgents` | ASP.NET hosted agents |
| `Durable` | Durable task / long-running agents |

## Patterns used in Revu

### Agent-as-tool (nested agents)

Wrap a sub-agent as a tool on the outer agent:
```csharp
var investigator = reviewerClient
    .AsBuilder()
    .UseFunctionInvocation(null, fic => fic.MaximumIterationsPerRequest = 8)
    .Build()
    .AsAIAgent(new ChatClientAgentOptions { ... })
    .AsBuilder()
    .UseOpenTelemetry(...)
    .Build();

// Convert to AIFunction callable by the orchestrator
var tool = investigator.AsAIFunction(new AIFunctionFactoryOptions { Name = "Investigate" });
```

**`AsAIFunction()` limitations**: The parameter is hardcoded as `query` with description "Input query to invoke the agent." — you cannot customize the parameter name or description via `AIFunctionFactoryOptions` (only function-level `Name` and `Description`). Work around this by enhancing the outer agent's system prompt to explain the tool.

### DelegatingAIFunction for guardrails

Wrap `AsAIFunction()` output in a `DelegatingAIFunction` subclass to add budget caps, concurrency limits, logging, custom `Description`:
```csharp
private sealed class GuardedTool : DelegatingAIFunction
{
    private GuardedTool(AIFunction inner) : base(inner) { }
    public override string Description => "Custom description for the LLM";
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken ct)
    {
        // guardrail logic, then: return await base.InvokeCoreAsync(arguments, ct);
    }
}
```

### ChatHistoryProvider contract (IMPORTANT)

`ChatHistoryProvider.InvokingCoreAsync` must return **only prior conversation history** — NOT the current request messages. The framework appends `inputMessages` unconditionally after calling `InvokingCoreAsync`:
```csharp
// Framework internals (PrepareSessionAndMessagesAsync):
inputMessagesForChatClient.AddRange(await provider.InvokingAsync(...)); // history
inputMessagesForChatClient.AddRange(inputMessages);                     // current prompt
```

If `InvokingCoreAsync` returns `context.RequestMessages`, the prompt gets added TWICE — once as "history", once as the current request. This doubles input tokens.

**Correct pattern for write-only providers** (session capture to disk):
```csharp
protected override ValueTask<IEnumerable<ChatMessage>> InvokingCoreAsync(
    InvokingContext context, CancellationToken cancellationToken = default)
    => new([]);  // no history to contribute — capture happens in InvokedCoreAsync
```

### FunctionInvokingChatClient options

- `AllowConcurrentInvocation = true` on outer agent → parallel tool calls
- `MaximumIterationsPerRequest = N` on inner agent → cap tool-call rounds

### OTel: two complementary layers

`.UseOpenTelemetry()` on the `IChatClient` (MEAI) captures individual chat completion API calls. `.UseOpenTelemetry()` on the agent (MAF) captures agent-level spans (sessions, tool dispatch). These are different — both are useful.
