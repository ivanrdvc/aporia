---
name: copilotkit-agui
description: "CopilotKit & AG-UI — hooks, components, provider setup, generative UI, agent integration. Load when reviewing React/Next.js code that uses CopilotKit or the AG-UI protocol."
---

# CopilotKit & AG-UI Protocol

Reference docs for CopilotKit and AG-UI. Do NOT read all files upfront. Use the decision tree below to load only what is needed.

## Decision Tree

Pick the first match. Only load additional files if the first doesn't answer the question.

**Choosing a generative UI pattern, or declarative / dynamic UI from agent output?**
→ [references/declarative-genui.md](references/declarative-genui.md) — three-pattern taxonomy (static vs open-ended/MCP Apps vs declarative), json-render catalog, spec format, registry, streaming, AG-UI integration

**Rendering tool results, generative UI, or displaying what an agent did?**
→ [references/hooks-tools.md](references/hooks-tools.md) — useFrontendTool, useRenderToolCall, useDefaultTool, render props, ToolCallStatus

**Agent needs human approval or input before continuing?**
→ [references/hooks-tools.md](references/hooks-tools.md) (useHumanInTheLoop section) or [references/hooks-agents.md](references/hooks-agents.md) (useLangGraphInterrupt section)

**Giving the copilot app state, context, or instructions?**
→ [references/hooks-state.md](references/hooks-state.md) — useCopilotReadable, useAgentContext, useCopilotAdditionalInstructions

**Working with agents — shared state, running agents, intermediate state?**
→ [references/hooks-agents.md](references/hooks-agents.md) — useAgent (v2), useCoAgent, useCoAgentStateRender

**Chat control, headless UI, suggestions, programmatic messages?**
→ [references/hooks-chat.md](references/hooks-chat.md) — useCopilotChat, useCopilotChatHeadless_c, useCopilotChatSuggestions

**CopilotKitCore API — events, subscriptions, tool/agent management?**
→ [references/core-api.md](references/core-api.md) — runAgent, connectAgent, addTool, subscribe, error codes

**Customizing chat appearance — theming, slots, labels, icons?**
→ [references/customization.md](references/customization.md) — v1 CSS variables, v2 slot system, full slot hierarchy

**Which chat component to use — Chat vs Sidebar vs Popup?**
→ [references/chat-components.md](references/chat-components.md) — component variants, v1+v2 props, sub-component exports

**Provider setup, runtimeUrl, v1 vs v2 differences?**
→ [references/provider-setup.md](references/provider-setup.md) — CopilotKit / CopilotKitProvider props, migration table

**Node.js CopilotRuntime setup or removing it?**
→ [references/node-runtime.md](references/node-runtime.md) — v1/v2 runtime setup, direct AG-UI connection without runtime (legacy, being phased out)

**Backend / runtime setup (.NET agent framework)?**
→ [references/dotnet-backend.md](references/dotnet-backend.md) — .NET AG-UI agent, endpoint setup, tool handling, state management

**Need to understand the underlying AG-UI protocol — event types, message format, state sync?**
→ [references/agui-protocol.md](references/agui-protocol.md) — events, messages, shared state, frontend tools lifecycle, gen-UI specs

**Need AG-UI client SDK details — AbstractAgent, HttpAgent, middleware?**
→ [references/agui-client.md](references/agui-client.md) — agent classes, config, methods, middleware, compactEvents

**End-to-end working example?**
→ [references/examples/index.md](references/examples/index.md) — read index first, only open relevant examples

**Stuck or debugging after reading reference docs?**
→ [references/troubleshooting/INDEX.md](references/troubleshooting/INDEX.md) — read index first, only open relevant entries