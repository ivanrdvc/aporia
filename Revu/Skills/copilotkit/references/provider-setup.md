# Provider & Setup

CopilotKit provider configuration for v1.x (`@copilotkit/react-core`) and v2.x (`@copilotkitnext/react`).

---

## v1.x Setup

```tsx
import { CopilotKit } from "@copilotkit/react-core";
import { CopilotSidebar } from "@copilotkit/react-ui";
import "@copilotkit/react-ui/styles.css";

export default function Layout({ children }: { children: React.ReactNode }) {
  return (
    <CopilotKit runtimeUrl="/api/copilotkit">
      <CopilotSidebar
        instructions="You are a helpful assistant."
        labels={{ title: "My Assistant", placeholder: "Ask anything..." }}
      >
        {children}
      </CopilotSidebar>
    </CopilotKit>
  );
}
```

### CopilotKit Props (v1)

```ts
{
  runtimeUrl: string;                 // Required — CopilotRuntime endpoint
  publicApiKey?: string;              // Premium features (cloud key)
  publicLicenseKey?: string;          // License key
  headers?: Record<string, string>;   // Custom HTTP headers
  properties?: Record<string, unknown>;  // App data forwarded to agents
  children: React.ReactNode;
  credentials?: RequestCredentials;   // HTTP-only cookies support
  agent?: string;                     // Default agent name
  threadId?: string;                  // Conversation thread ID
  showDevConsole?: boolean;           // Show developer console
  enableInspector?: boolean;          // Enable CopilotKit inspector
  forwardedParameters?: ForwardedParametersInput;  // LLM params (e.g., temperature)
  onError?: (error: CopilotErrorEvent) => void;

  // Premium
  transcribeAudioUrl?: string;
  textToSpeechUrl?: string;
  guardrails_c?: GuardrailsConfig;
  authConfig_c?: AuthConfig;
}
```

---

## v2.x Setup

```tsx
import { CopilotKitProvider, CopilotChat } from "@copilotkitnext/react";

export default function App() {
  return (
    <CopilotKitProvider runtimeUrl="/api/copilotkit">
      <CopilotChat agentId="default" />
    </CopilotKitProvider>
  );
}
```

### CopilotKitProvider Props (v2)

```ts
{
  runtimeUrl?: string;                // CopilotRuntime endpoint
  headers?: Record<string, string>;   // Custom HTTP headers
  properties?: Record<string, unknown>;  // App data forwarded to agents
  children: React.ReactNode;

  // Agent configuration
  agents__unsafe_dev_only?: Record<string, AbstractAgent>;  // Local dev agents (NOT for production)
  useSingleEndpoint?: boolean;        // Single route mode (replaces v1 runtimeTransport="single")

  // Tool rendering (provider-level)
  renderToolCalls?: ReactToolCallRenderer<any>[];   // Static tool renderers
  frontendTools?: ReactFrontendTool<any>[];         // Static frontend tools
  humanInTheLoop?: ReactHumanInTheLoop<any>[];      // Static HITL tools
}
```

### Single Endpoint Mode

v2 uses `useSingleEndpoint` instead of v1's `runtimeTransport="single"`:

```tsx
<CopilotKitProvider
  runtimeUrl="/api/copilotkit"
  useSingleEndpoint={true}
>
```

Pairs with `createCopilotEndpointSingleRoute` on the backend.

### Provider-Level Tool Renderers

Define tool renderers at the provider level (available to all agents):

```tsx
import { CopilotKitProvider, defineToolCallRenderer } from "@copilotkitnext/react";

const searchRenderer = defineToolCallRenderer({
  name: "search",
  render: ({ args, status }) => <SearchDisplay {...args} status={status} />,
});

<CopilotKitProvider
  runtimeUrl="/api/copilotkit"
  renderToolCalls={[searchRenderer]}
>
  <App />
</CopilotKitProvider>
```

---

## v2 Endpoint Routes

Default multi-route:
- `POST /agent/:agentId/run` — run an agent
- `POST /agent/:agentId/connect` — connect to existing session
- `POST /agent/:agentId/stop/:threadId` — stop an agent
- `GET /info` — discover available agents
- `POST /transcribe` — audio transcription

When `useSingleEndpoint={true}`, all routes collapse to a single POST endpoint.

---

## Migration (v1 → v2)

| What | v1.x | v2.x |
|---|---|---|
| Provider | `<CopilotKit>` from `@copilotkit/react-core` | `<CopilotKitProvider>` from `@copilotkitnext/react` |
| UI import | `@copilotkit/react-ui` (separate) | `@copilotkitnext/react` (single package) |
| Backend | `copilotRuntimeNextJSAppRouterEndpoint` + `OpenAIAdapter` | `createCopilotEndpoint` + `BasicAgent` + `InMemoryAgentRunner` (Hono) |
| Single endpoint | `runtimeTransport="single"` | `useSingleEndpoint={true}` |
| Agent class | `LangGraphAgent`, `LangGraphHttpAgent` | `BasicAgent` (built-in), any `AbstractAgent` |
| Context hook | `useCopilotContext()` | `useCopilotKit()` |
| CSS | `@copilotkit/react-ui/styles.css` | Built-in Tailwind, no CSS import needed |
| v1 accessing v2 | `import { BuiltInAgent } from "@copilotkit/runtime/v2"` | Native |
| MCP support | Via `mcpServers` on hooks | Via `MCPAppsMiddleware` on agent |
