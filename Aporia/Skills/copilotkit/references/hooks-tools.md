# Hooks — Tools & Actions

Hooks for making the copilot do things and render tool call results. Covers `useFrontendTool`, `useRenderToolCall`, `useDefaultTool`, `useHumanInTheLoop`, and the legacy `useCopilotAction`.

---

## Quick Reference

| Hook | What it does | Has handler? | Has render? | Package |
|---|---|---|---|---|
| `useFrontendTool` | Register a tool that executes on the frontend | Yes | Optional | `@copilotkit/react-core` |
| `useRenderToolCall` | Render UI for a backend-executed tool call | No | Yes | `@copilotkit/react-core` |
| `useDefaultTool` | Catch-all renderer for any unhandled tool | No | Yes | `@copilotkit/react-core` |
| `useHumanInTheLoop` | HITL tool — render UI, wait for user `respond()` | No (respond-based) | Yes | `@copilotkit/react-core` |
| `useCopilotAction` | **Legacy** — routes to one of the above | Varies | Varies | `@copilotkit/react-core` |

All hooks accept an optional `dependencies` array as second argument (like `useEffect` deps).

---

## 1. useFrontendTool

Register a tool that the LLM can call, executed entirely on the frontend. The result is sent back to the LLM.

```tsx
import { useFrontendTool } from "@copilotkit/react-core";

useFrontendTool({
  name: "addTodo",
  description: "Add a new todo item to the list",
  parameters: [
    { name: "title", type: "string", description: "The todo title", required: true },
    { name: "priority", type: "string", enum: ["low", "medium", "high"] },
  ],
  handler: async ({ title, priority }) => {
    const todo = await createTodo(title, priority);
    return `Created todo: ${todo.id}`;
  },
});
```

### With render (Generative UI)

```tsx
useFrontendTool({
  name: "showWeather",
  description: "Show weather for a city",
  parameters: [
    { name: "city", type: "string", required: true },
  ],
  handler: async ({ city }) => {
    const data = await fetchWeather(city);
    return JSON.stringify(data);
  },
  render: ({ args, status, result }) => {
    if (status === "inProgress") return <div>Loading weather for {args.city}...</div>;
    if (status === "executing") return <div>Fetching data...</div>;
    if (status === "complete") return <WeatherCard data={JSON.parse(result)} />;
  },
});
```

### Props

```ts
{
  name: string;
  description?: string;
  parameters?: Parameter[];  // see Parameter type below
  handler: (args: MappedArgs) => any | Promise<any>;
  render?: (props: ActionRenderProps) => React.ReactElement | string;
  followUp?: boolean | string;  // auto-follow-up after tool completes
  available?: "enabled" | "disabled";
}
```

### Render props (ActionRenderProps)

The `render` function receives a discriminated union based on `status`:

```ts
// While LLM is streaming arguments
{ name: string; args: Partial<Args>; status: "inProgress"; result: undefined }

// Handler is executing
{ name: string; args: Args; status: "executing"; result: undefined }

// Handler completed
{ name: string; args: Args; status: "complete"; result: string }
```

---

## 2. useRenderToolCall

Render-only: display UI when a backend tool call happens. No frontend handler — the tool runs on the backend (or is a backend action). This hook just renders the in-chat UI.

```tsx
import { useRenderToolCall } from "@copilotkit/react-core";

useRenderToolCall({
  name: "searchDatabase",
  description: "Search the database",
  parameters: [
    { name: "query", type: "string", required: true },
  ],
  render: ({ args, status, result }) => {
    if (status === "inProgress") return <div>Searching: {args.query}...</div>;
    if (status === "complete") return <SearchResults data={JSON.parse(result)} />;
    return <div>Executing search...</div>;
  },
});
```

### Props

```ts
{
  name: string;
  description?: string;
  parameters?: Parameter[];
  render: (props: ActionRenderProps) => React.ReactElement;
  available?: "enabled" | "disabled";
}
```

Same render props as `useFrontendTool` (status-based discriminated union).

---

## 3. useDefaultTool

Catch-all renderer for any tool call not matched by a specific `useRenderToolCall` or `useFrontendTool`. Uses `name: "*"` internally.

```tsx
import { useDefaultTool } from "@copilotkit/react-core";

useDefaultTool({
  render: ({ name, args, status, result }) => {
    return (
      <div className="tool-call">
        <strong>{name}</strong>
        <pre>{JSON.stringify(args, null, 2)}</pre>
        {status === "complete" && <div>Result: {result}</div>}
      </div>
    );
  },
});
```

The `render` function receives `name` in addition to the standard render props, since it matches any tool.

---

## 4. useHumanInTheLoop

Register a tool that renders UI and waits for the user to respond. The response is sent back to the LLM as the tool result.

```tsx
import { useHumanInTheLoop } from "@copilotkit/react-core";

useHumanInTheLoop({
  name: "approveExpense",
  description: "Request approval for an expense",
  parameters: [
    { name: "amount", type: "number", required: true },
    { name: "reason", type: "string", required: true },
  ],
  render: ({ args, status, respond }) => {
    if (status === "inProgress") return <div>Loading approval request...</div>;
    if (status === "complete") return <div>Approval submitted.</div>;

    // status === "executing" — show approval UI
    return (
      <div>
        <p>Approve ${args.amount} for "{args.reason}"?</p>
        <button onClick={() => respond("approved")}>Approve</button>
        <button onClick={() => respond("denied")}>Deny</button>
      </div>
    );
  },
});
```

### Render props (with respond)

```ts
// Streaming arguments
{ name, description, args: Partial<Args>, status: "inProgress", respond: undefined }

// Waiting for user response
{ name, description, args: Args, status: "executing", respond: (result: unknown) => Promise<void> }

// Response submitted
{ name, description, args: Args, status: "complete", result: string, respond: undefined }
```

`respond()` is only available when `status === "executing"`. Call it with any serializable value — it becomes the tool result sent back to the LLM.

---

## 5. useCopilotAction (Legacy)

Unified hook that internally routes to one of the specialized hooks above. Maintained for backwards compatibility. **Prefer the specific hooks for new code.**

```tsx
import { useCopilotAction } from "@copilotkit/react-core";

// Routes to useFrontendTool (has handler)
useCopilotAction({
  name: "sayHello",
  description: "Say hello to someone.",
  parameters: [{ name: "name", type: "string" }],
  handler: async ({ name }) => {
    alert(`Hello, ${name}!`);
  },
});
```

### Routing logic

`useCopilotAction` determines which hook to use:

1. `name === "*"` → `useRenderToolCall` (catch-all)
2. Has `renderAndWaitForResponse` or `renderAndWait` → `useHumanInTheLoop`
3. Has `available === "enabled"` or `"remote"`, or has `handler` → `useFrontendTool`
4. Has `available === "frontend"` or `"disabled"` → `useRenderToolCall`

**Warning:** The action type cannot change between renders. If it does, React will throw an error (hooks call order violation).

### renderAndWaitForResponse (HITL via useCopilotAction)

```tsx
useCopilotAction({
  name: "handleMeeting",
  description: "Handle a meeting by booking or canceling",
  parameters: [
    { name: "meeting", type: "string", required: true },
    { name: "date", type: "string", required: true },
    { name: "title", type: "string", required: true },
  ],
  renderAndWaitForResponse: ({ args, respond, status }) => {
    const { meeting, date, title } = args;
    return (
      <MeetingConfirmationDialog
        meeting={meeting}
        date={date}
        title={title}
        onConfirm={() => respond("meeting confirmed")}
        onCancel={() => respond("meeting canceled")}
      />
    );
  },
});
```

### renderAndWait pattern (HITL generative UI)

See [research-canvas example](examples/research-canvas.md) for a full working `renderAndWait` implementation — the `DeleteResources` action shows the standard pattern: render buttons only when `status === "executing"`, call `handler("YES")` / `handler("NO")` to resolve.

### Catch-all via useCopilotAction

```tsx
useCopilotAction({
  name: "*",
  render: ({ name, args, status, result }) => {
    return <div>Rendering action: {name}</div>;
  },
});
```

---

## 6. Parameter Type

v1.x hooks use the `Parameter` array format:

```ts
interface Parameter {
  name: string;
  type: "string" | "number" | "boolean" | "object" | "string[]" | "number[]";
  description?: string;
  required?: boolean;
  enum?: string[];           // allowed values
  attributes?: Parameter[];  // nested fields (for type: "object")
}
```

Complex parameter example:

```tsx
parameters: [
  { name: "query", type: "string", required: true },
  { name: "limit", type: "number" },
  {
    name: "filters",
    type: "object",
    attributes: [
      { name: "category", type: "string", enum: ["A", "B", "C"] },
      { name: "active", type: "boolean" },
    ],
  },
  { name: "tags", type: "string[]" },
]
```

CopilotKit auto-infers TypeScript types from this array — handler args are fully typed.

---

## 7. ToolCallStatus

The `status` field in render props uses these values:

| Status | Meaning | `args` | `result` |
|---|---|---|---|
| `"inProgress"` | LLM is still streaming arguments | `Partial<Args>` | `undefined` |
| `"executing"` | Handler is running (or HITL waiting) | `Args` (complete) | `undefined` |
| `"complete"` | Tool finished, result available | `Args` (complete) | `string` |

---

## 8. v2.x Equivalents

v2 (`@copilotkitnext/react`) exports these hooks. Key difference: v2 uses **Zod schemas** instead of `Parameter[]`.

| v1 hook | v2 equivalent |
|---|---|
| `useFrontendTool` | `useFrontendTool` (Zod params) |
| `useRenderToolCall` | `useRenderToolCall` (Zod params) |
| `useDefaultTool` | `useDefaultRenderTool` |
| `useHumanInTheLoop` | `useHumanInTheLoop` (Zod params) |
| — | `useRenderTool` (new) |
| — | `useRenderCustomMessages` (new) |
| — | `useRenderActivityMessage` (new) |

### v2 useFrontendTool with Zod

```tsx
import { useFrontendTool } from "@copilotkitnext/react";
import { z } from "zod";

useFrontendTool({
  name: "addTodo",
  description: "Add a new todo item",
  parameters: z.object({
    title: z.string().describe("The todo title"),
    priority: z.enum(["low", "medium", "high"]).optional(),
  }),
  handler: async ({ title, priority }) => {
    return `Created todo: ${title}`;
  },
  render: ({ args, status, result }) => {
    // same render props pattern as v1
  },
});
```

### v2 defineToolCallRenderer helper

Type-safe helper for defining render-only tool renderers (used by `useRenderToolCall` internally):

```tsx
import { defineToolCallRenderer } from "@copilotkitnext/react";
import { z } from "zod";

const myRenderer = defineToolCallRenderer({
  name: "searchTool",
  args: z.object({ query: z.string() }),
  render: ({ name, args, status, result }) => {
    return <SearchUI query={args.query} result={result} status={status} />;
  },
});

// Wildcard (no args schema needed)
const wildcardRenderer = defineToolCallRenderer({
  name: "*",
  render: ({ name, args, status, result }) => {
    return <GenericToolUI name={name} args={args} />;
  },
});
```

### v2 ReactToolCallRenderer type

```ts
interface ReactToolCallRenderer<T> {
  name: string;
  args: z.ZodSchema<T>;
  agentId?: string;  // scope renderer to specific agent
  render: React.ComponentType<
    | { name: string; args: Partial<T>; status: ToolCallStatus.InProgress; result: undefined }
    | { name: string; args: T; status: ToolCallStatus.Executing; result: undefined }
    | { name: string; args: T; status: ToolCallStatus.Complete; result: string }
  >;
}
```

### v2 ReactHumanInTheLoop type

```ts
type ReactHumanInTheLoop<T> = Omit<FrontendTool<T>, "handler"> & {
  render: React.ComponentType<
    | { name, description, args: Partial<T>, status: InProgress, result: undefined, respond: undefined }
    | { name, description, args: T, status: Executing, result: undefined, respond: (result: unknown) => Promise<void> }
    | { name, description, args: T, status: Complete, result: string, respond: undefined }
  >;
};
```

### agentId scoping (v2)

v2 renderers support `agentId` to scope a renderer to a specific agent:

```tsx
defineToolCallRenderer({
  name: "searchTool",
  agentId: "research_agent",  // only renders for this agent's tool calls
  args: z.object({ query: z.string() }),
  render: ({ args, status, result }) => { ... },
});
```

---

## 9. Common Patterns

### Conditional availability

```tsx
useFrontendTool({
  name: "deleteTodo",
  description: "Delete a todo item",
  parameters: [{ name: "id", type: "string", required: true }],
  handler: async ({ id }) => { ... },
  available: isAdmin ? "enabled" : "disabled",  // hide from LLM when disabled
});
```

### followUp

```tsx
useFrontendTool({
  name: "createReport",
  description: "Create a report",
  parameters: [...],
  handler: async (args) => { ... },
  followUp: true,  // LLM will automatically follow up after tool completes
  // or: followUp: "Now summarize the report you just created"
});
```

### Server-side actions (v1 runtime)

Tools can also be defined server-side in the CopilotRuntime:

```ts
const runtime = new CopilotRuntime({
  actions: [
    {
      name: "getImageUrl",
      description: "Get an image url for a topic",
      parameters: [{ name: "topic", type: "string" }],
      handler: async ({ topic }) => {
        const response = await fetch(`https://api.unsplash.com/search/photos?query=${topic}`);
        const data = await response.json();
        return data.results[0].urls.regular;
      },
    },
  ],
});
```

Use `useRenderToolCall` on the frontend to render UI for server-side actions.
