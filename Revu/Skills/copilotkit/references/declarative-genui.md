# Generative UI

Three patterns for agent-driven UI, from most coupled to most open:

| Pattern | How it works | CopilotKit hook | Pros | Cons |
|---|---|---|---|---|
| **Static** (tool call mapping) | Agent calls a tool → frontend renders a pre-built component for that tool | `useRenderToolCall`, `useFrontendTool` (with `render`) | Full pixel-perfect control, simple | High coupling — new tool = new component. Frontend grows linearly with agent capabilities. |
| **Open-ended** (MCP Apps) | Agent provides raw HTML or an iframe URL embedded directly in the app | `MCPAppsActivityRenderer`, MCP server integration | Low coupling, can render anything | Unpredictable UI, styling issues, security concerns (XSS via iframe). Double-iframe sandboxing mitigates but doesn't eliminate. Primarily web-first. |
| **Declarative** (A2UI / json-render) | Agent generates a JSON spec describing UI components → frontend renderer maps spec to real components | `useRenderToolCall` or `useCoAgent` state + `<Renderer />` | Low coupling, agent composes freely within constrained vocabulary, cross-platform (same spec → different registries) | Cannot accommodate fully custom UI — constrained by spec. Still non-deterministic within spec bounds. |

**Pick static** when you have a small, stable set of tools and want exact control. **Pick open-ended** when the agent needs maximum freedom (prototyping, sandboxed widgets). **Pick declarative** when you want the agent to compose UI freely but within guardrails you define.

The **Agent States** pattern (bidirectional shared state via `useCoAgent`/`useAgent`) is orthogonal — it works alongside any of the three patterns above. The agent writes structured state, the frontend renders it however it wants.

---

# Declarative Generative UI (json-render)

Declarative gen-UI avoids the 1:1 mapping problem of static gen-UI (where every backend tool needs a hand-built frontend component). Instead, agents emit a structured JSON spec that describes the UI, and a renderer maps it to real components at runtime. The developer defines a **catalog** of available building blocks; the agent composes them freely.

Other declarative specs exist (A2UI from Google, Open-JSON-UI from OpenAI, MCP-UI from Microsoft + Shopify) but json-render is the most relevant to our stack — it's from Vercel Labs, React-native, and has an AG-UI integration.

Docs: https://json-render.dev/docs

---

## Core Architecture

```
Developer defines         Agent generates          Frontend renders
┌────────────┐         ┌──────────────┐         ┌──────────────┐
│  Catalog   │ ──prompt──► LLM outputs  │ ──spec──►  Renderer   │
│ (Zod types)│         │  JSON spec   │         │ (Registry)   │
└────────────┘         └──────────────┘         └──────────────┘
```

1. **Catalog** — Zod-typed component + action vocabulary. Becomes the LLM system prompt via `catalog.prompt()`.
2. **Spec** — Flat JSON tree the agent generates, constrained to catalog types.
3. **Registry** — Maps catalog types → real React components. Handles rendering.

The key insight: the catalog is the contract between developer and AI. The developer controls *what* components exist; the AI controls *how* they're composed.

---

## Catalog

Defines available components (with typed props and slots) and actions (with typed params).

```ts
import { defineCatalog } from '@json-render/core';
import { schema } from '@json-render/react';
import { z } from 'zod';

export const catalog = defineCatalog(schema, {
  components: {
    Card: {
      props: z.object({
        title: z.string(),
        description: z.string().nullable(),
      }),
      slots: ["default"],  // can have children
      description: "Container card with optional title",
    },
    Metric: {
      props: z.object({
        label: z.string(),
        value: z.string(),
        trend: z.enum(["up", "down", "flat"]).nullable(),
      }),
      description: "Single KPI metric display",
    },
    Button: {
      props: z.object({
        label: z.string(),
        action: z.string().nullable(),
      }),
      description: "Clickable button",
    },
  },
  actions: {
    submit: {
      params: z.object({ formId: z.string() }),
      description: "Submit a form",
    },
    navigate: {
      params: z.object({ url: z.string() }),
      description: "Navigate to a URL",
    },
  },
});
```

**Key methods on catalog instance:**

| Method | Purpose |
|---|---|
| `catalog.prompt(options?)` | Generate system prompt for the LLM (includes component descriptions, prop schemas, rules) |
| `catalog.validate(spec)` | Validate a spec against the catalog |
| `catalog.zodSchema()` | Get underlying Zod schema |
| `catalog.jsonSchema()` | Export as JSON Schema |

Pass `catalog.prompt()` as the system prompt — it tells the LLM exactly what components and actions it can use.

---

## Spec (What the Agent Generates)

Flat tree with `root` + `elements` map. Optimized for AI generation and streaming.

```json
{
  "root": "card-1",
  "elements": {
    "card-1": {
      "type": "Card",
      "props": { "title": "Dashboard" },
      "children": ["metric-1", "metric-2"]
    },
    "metric-1": {
      "type": "Metric",
      "props": { "label": "Revenue", "value": "$12.4k", "trend": "up" },
      "children": []
    },
    "metric-2": {
      "type": "Metric",
      "props": { "label": "Users", "value": "1,847", "trend": "flat" },
      "children": []
    }
  }
}
```

### Element structure

```ts
interface Element {
  type: string;           // Component name from catalog
  props: Record<string, any>;
  children: string[];     // IDs of child elements
  visible?: VisibilityCondition;  // Conditional rendering
}
```

---

## Registry (Maps Types → React Components)

```tsx
import { defineRegistry } from '@json-render/react';
import { catalog } from './catalog';

export const { registry, handlers, executeAction } = defineRegistry(catalog, {
  components: {
    Card: ({ props, children }) => (
      <div className="p-4 border rounded-lg">
        <h2 className="font-bold">{props.title}</h2>
        {props.description && <p className="text-muted">{props.description}</p>}
        {children}
      </div>
    ),
    Metric: ({ props }) => (
      <div className="flex justify-between">
        <span>{props.label}</span>
        <span className="font-mono">{props.value}</span>
      </div>
    ),
    Button: ({ props, emit }) => (
      <button onClick={() => emit("press")}>{props.label}</button>
    ),
  },
  actions: {
    submit: (params) => console.log('Submit:', params),
    navigate: (params) => window.location.href = params.url,
  },
});
```

Each component receives `ComponentContext`: `{ props, children, emit, loading, bindings }` — all type-safe from catalog.

---

## Rendering

```tsx
import { Renderer, StateProvider, VisibilityProvider, ActionProvider } from '@json-render/react';

<StateProvider initialState={{}}>
  <VisibilityProvider>
    <ActionProvider handlers={handlers}>
      <Renderer spec={spec} registry={registry} loading={isStreaming} />
    </ActionProvider>
  </VisibilityProvider>
</StateProvider>
```

---

## Data Binding

Props can reference runtime state using JSON Pointer (RFC 6901) paths.

| Expression | Usage | Scope |
|---|---|---|
| `{ "$state": "/path" }` | Read from state | Anywhere |
| `{ "$bindState": "/path" }` | Two-way bind (forms) | `value`/`checked` props |
| `{ "$item": "field" }` | Current repeat item field | Inside `repeat` |
| `{ "$index": true }` | Current repeat index | Inside `repeat` |
| `{ "$cond": ..., "$then": ..., "$else": ... }` | Conditional prop value | Anywhere |

```json
{
  "type": "Text",
  "props": { "content": { "$state": "/user/name" } },
  "children": []
}
```

---

## Streaming (SpecStream)

JSONL-based — each line is an RFC 6902 JSON Patch operation that progressively builds the spec.

```jsonl
{"op":"add","path":"/root","value":"card-1"}
{"op":"add","path":"/elements/card-1","value":{"type":"Card","props":{"title":"Dashboard"},"children":["metric-1"]}}
{"op":"add","path":"/elements/metric-1","value":{"type":"Metric","props":{"label":"Revenue","value":"$12.4k"},"children":[]}}
```

**React hook:**

```tsx
const { spec, isStreaming, send } = useUIStream({ api: '/api/generate' });
```

**Low-level:** `createSpecStreamCompiler` from `@json-render/core` — call `push(chunk)` to process patches incrementally.

---

## Generation Modes

| Mode | Output | Use case |
|---|---|---|
| **Generate** (default) | JSONL patches only, no prose | Dashboards, form builders, standalone UI |
| **Chat** | Text + JSONL patches mixed | Copilot chat with rich inline UI |

Generate: `catalog.prompt()` — Chat: `catalog.prompt({ mode: "chat" })`

In chat mode, use `pipeJsonRender` (server) to split text from patches, and `useJsonRenderMessage` (client) to extract the spec from message parts.

---

## AG-UI Integration

json-render specs can flow through AG-UI events. The agent generates a spec, and it arrives at the CopilotKit frontend via tool calls or state snapshots.

**Via tool calls:** Agent calls a tool with the spec as arguments → frontend renders via `useRenderToolCall` or `useFrontendTool`.

**Via shared state:** Agent emits spec as part of `STATE_SNAPSHOT` → frontend reads state via `useCoAgent` / `useAgent` and passes to `<Renderer />`.

Either way, the catalog stays on the frontend, the registry maps to real components, and the agent just generates JSON.

---

## Packages

| Package | Purpose |
|---|---|
| `@json-render/core` | Catalog, schema, spec validation, streaming compiler |
| `@json-render/react` | React schema, `Renderer`, `useUIStream`, providers |
| `@json-render/react-native` | React Native registry + components |
| `@json-render/codegen` | Export specs as standalone React components |

---

## Why Declarative Over Static

| | Static Gen-UI | Declarative (json-render) |
|---|---|---|
| New agent capability | Build a new React component + wire it up | Already covered if catalog has the building blocks |
| Frontend growth | Linear with agent capabilities | Fixed — catalog size stays manageable |
| Agent freedom | Pick from N exact components | Compose freely from catalog vocabulary |
| Consistency | Guaranteed (hand-built) | High (catalog-constrained, real components) |
| Cross-platform | Separate implementations per platform | Same spec → different registries |
