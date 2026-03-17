---
source: https://github.com/CopilotKit/CopilotKit/tree/main/examples/v1/research-canvas
extracted: 2026-02-14
scope: full-app
demonstrates: CoAgent shared state, CSS variable theming, generative UI with HITL (renderAndWait), LangGraph agent wiring
---

## Relevant structure

```
src/
  app/
    page.tsx              # CopilotKit provider, dynamic runtimeUrl
    Main.tsx              # CopilotChat + useCoAgent + useCopilotChatSuggestions
    api/copilotkit/
      route.ts            # CopilotRuntime with LangGraphHttpAgent/LangGraphAgent
  components/
    ResearchCanvas.tsx    # useCoAgent, useCoAgentStateRender, useCopilotAction (renderAndWait)
  lib/
    types.ts              # AgentState type (shared with Python agent)
```

## Key code

### Provider setup (page.tsx)

```tsx
import { CopilotKit } from "@copilotkit/react-core";

// agent name is dynamic — passed as prop to CopilotKit
<CopilotKit runtimeUrl={runtimeUrl} showDevConsole={false} agent={agent}>
  <Main />
</CopilotKit>
```

Note: `agent` prop on `<CopilotKit>` sets the default agent for all coagent hooks inside.

### CopilotChat with CSS variables (Main.tsx)

```tsx
import { CopilotChat } from "@copilotkit/react-ui";
import { useCopilotChatSuggestions } from "@copilotkit/react-ui";
import { useCoAgent } from "@copilotkit/react-core";

const { state, setState } = useCoAgent<AgentState>({
  name: agent,
  initialState: { model, research_question: "", resources: [], report: [], logs: [] },
});

useCopilotChatSuggestions({ instructions: "Lifespan of penguins" });

// CSS variables applied via style prop on a wrapper div
<div style={{
  "--copilot-kit-background-color": "#E0E9FD",
  "--copilot-kit-secondary-color": "#6766FC",
  "--copilot-kit-separator-color": "#b8b8b8",
  "--copilot-kit-primary-color": "#FFFFFF",
  "--copilot-kit-contrast-color": "#000000",
  "--copilot-kit-secondary-contrast-color": "#000",
} as any}>
  <CopilotChat
    className="h-full"
    onSubmitMessage={async (message) => {
      setState({ ...state, logs: [] }); // clear logs before new research
      await new Promise((resolve) => setTimeout(resolve, 30)); // brief delay for state propagation
    }}
    labels={{ initial: "Hi! How can I assist you with your research today?" }}
  />
</div>
```

### CoAgent state render + HITL action (ResearchCanvas.tsx)

```tsx
import { useCoAgent, useCoAgentStateRender, useCopilotAction } from "@copilotkit/react-core";

// Shared state — same agent name, state syncs between components
const { state, setState } = useCoAgent<AgentState>({ name: agent, initialState: { model } });

// Render agent progress as a chat message while agent is running
useCoAgentStateRender({
  name: agent,
  render: ({ state, nodeName, status }) => {
    if (!state.logs || state.logs.length === 0) return null;
    return <Progress logs={state.logs} />;
  },
});

// HITL: agent calls this action, UI renders confirmation, handler returns user choice
useCopilotAction({
  name: "DeleteResources",
  description: "Prompt the user for resource delete confirmation, and then perform resource deletion",
  available: "remote",  // only callable by the remote agent, not the LLM directly
  parameters: [{ name: "urls", type: "string[]" }],
  renderAndWait: ({ args, status, handler }) => (
    <div>
      <div>Delete these resources?</div>
      <Resources resources={resources.filter(r => (args.urls || []).includes(r.url))} />
      {status === "executing" && (
        <>
          <button onClick={() => handler("NO")}>Cancel</button>
          <button onClick={() => handler("YES")}>Delete</button>
        </>
      )}
    </div>
  ),
});
```

### Backend route (route.ts)

```ts
import { CopilotRuntime, EmptyAdapter, copilotRuntimeNextJSAppRouterEndpoint } from "@copilotkit/runtime";
import { LangGraphHttpAgent, LangGraphAgent } from "@copilotkit/runtime/langgraph";

// Local agents via HTTP (self-hosted Python agent)
const runtime = new CopilotRuntime({
  agents: {
    research_agent: new LangGraphHttpAgent({ url: `${baseUrl}/agents/research_agent` }),
  },
});

// OR: LangGraph Cloud agents (hosted deployment)
const runtime = new CopilotRuntime({
  agents: {
    research_agent: new LangGraphAgent({
      deploymentUrl,
      langsmithApiKey,
      graphId: "research_agent",
    }),
  },
});

const { handleRequest } = copilotRuntimeNextJSAppRouterEndpoint({
  runtime,
  serviceAdapter: new EmptyAdapter(), // no direct LLM — agent handles everything
  endpoint: "/api/copilotkit",
});
```

### Shared state type (types.ts)

```ts
export type AgentState = {
  model: string;
  research_question: string;
  report: string;
  resources: any[];
  logs: any[];
};
```

## Wiring patterns

- **`useCoAgent` called in two components** (Main.tsx and ResearchCanvas.tsx) with the same agent name — state is shared, both read/write to it. This is the coagent "shared state" pattern.
- **`agent` prop on `<CopilotKit>`** sets the default agent. All `useCoAgent({ name: agent })` calls inside inherit this.
- **`EmptyAdapter`** used because the agent handles all LLM calls — CopilotRuntime just proxies, no direct OpenAI adapter needed.
- **`available: "remote"`** on `useCopilotAction` means only the backend agent can invoke the action, not the chat LLM. Used for HITL flows where the agent decides when to ask for confirmation.
- **CSS variables on a wrapper div**, not on `<CopilotKit>` — both work, but wrapper div scopes the theming to just the chat panel.

## Gotchas

- `renderAndWait` handler only renders buttons when `status === "executing"` — otherwise the buttons would persist after the user clicks. This is the standard pattern for HITL generative UI.
- The 30ms delay after `setState` in `onSubmitMessage` is a workaround for state propagation timing — without it, the agent may see stale `logs`.
- `useCoAgent` in two components with the same agent name works because CopilotKit deduplicates internally — but both components must share the same `<CopilotKit>` provider ancestor.
