# Hooks — State & Context

Hooks for giving the copilot knowledge about your app. Covers `useCopilotReadable`, `useAgentContext` (v2), `useCopilotAdditionalInstructions`, `useMakeCopilotDocumentReadable`, and the `CopilotTask` class.

---

## Quick Reference

| Hook / Class | What it does | Returns |
|---|---|---|
| `useCopilotReadable` | Provide app state as context to the copilot | `string \| undefined` (context ID) |
| `useAgentContext` | Provide context to agents (v2) | `void` |
| `useCopilotAdditionalInstructions` | Add extra system instructions | `void` |
| `useMakeCopilotDocumentReadable` | Provide a document pointer as context | `string \| undefined` (document ID) |
| `CopilotTask` | Run a one-off task using context + actions (imperative, not a hook) | `Promise<void>` |

All hooks accept an optional `dependencies` array as second argument (like `useEffect` deps).

---

## 1. useCopilotReadable

Provide app state and other information to the copilot. The copilot sees this as context when responding to user input. Supports hierarchical (parent-child) context via `parentId`.

```tsx
import { useCopilotReadable } from "@copilotkit/react-core";

export function EmployeeList() {
  const [employees, setEmployees] = useState([
    { id: 1, name: "John Doe", role: "Developer" },
    { id: 2, name: "Jane Smith", role: "Designer" },
  ]);

  useCopilotReadable({
    description: "The list of employees",
    value: employees,
  });

  return <>{/* render employees */}</>;
}
```

### Hierarchical context (parent-child)

The hook returns a context ID. Pass it as `parentId` to create nested context, useful for list items and child components:

```tsx
function Employee({ employeeName, workProfile, metadata }: EmployeeProps) {
  const employeeContextId = useCopilotReadable({
    description: "Employee name",
    value: employeeName,
  });

  useCopilotReadable({
    description: "Work profile",
    value: workProfile.description(),
    parentId: employeeContextId,
  });

  useCopilotReadable({
    description: "Employee metadata",
    value: metadata.description(),
    parentId: employeeContextId,
  });

  return <>{/* render */}</>;
}
```

### Options

```ts
interface UseCopilotReadableOptions {
  description: string;        // Required — what this context represents
  value: any;                 // Required — the data (objects auto-stringified via JSON.stringify)
  parentId?: string;          // ID of parent context for hierarchical nesting
  categories?: string[];      // Filter which contexts are visible where (useful with CopilotTextarea)
  available?: "enabled" | "disabled";  // Toggle context availability
  convert?: (description: string, value: any) => string;  // Custom serializer (default: JSON.stringify)
}
```

### Return value

`string | undefined` — the context ID. Use this as `parentId` in child readables for hierarchical context.

### How it works

Internally calls `copilotkit.addContext({ description, value })` on mount and `copilotkit.removeContext(id)` on unmount. If the same `{ description, value }` already exists, it reuses the existing ID rather than creating a duplicate. When `available === "disabled"`, the context is removed.

---

## 2. useAgentContext (v2)

v2 hook for providing contextual information to agents. Auto-manages lifecycle (add on mount, update on change, remove on unmount).

```tsx
import { useAgentContext } from "@copilotkitnext/react";

function UserPreferences() {
  const userSettings = { theme: "dark", language: "en", timezone: "UTC-5" };

  useAgentContext({
    description: "User preferences and settings",
    value: userSettings,
  });

  return <div>User preferences loaded</div>;
}
```

### Parameters

```ts
{
  description: string;  // Required — what this context represents (helps agents understand usage)
  value: any;           // Required — the data (any serializable value)
}
```

### Key differences from useCopilotReadable

- **Package:** `@copilotkitnext/react` (v2) vs `@copilotkit/react-core` (v1)
- **No `parentId`:** `useAgentContext` doesn't support hierarchical nesting
- **No `categories` / `available` / `convert`:** Simpler API
- **Same core mechanism:** Both call `copilotkit.addContext()` / `copilotkit.removeContext()` on `CopilotKitCore`

### Multiple contexts

```tsx
useAgentContext({ description: "User info", value: userContext });
useAgentContext({ description: "App config", value: appContext });
```

### Memoize computed values

```tsx
const contextValue = useMemo(() => ({
  itemCount: items.length,
  totalValue: items.reduce((sum, item) => sum + item.price, 0),
}), [items]);

useAgentContext({ description: "Inventory stats", value: contextValue });
```

---

## 3. useCopilotAdditionalInstructions

Add extra system instructions to the copilot. Instructions are appended to the system prompt as a bulleted list.

```tsx
import { useCopilotAdditionalInstructions } from "@copilotkit/react-core";

export function AdminPanel() {
  useCopilotAdditionalInstructions({
    instructions: "Do not answer questions about the weather.",
  });
}
```

### Conditional instructions

```tsx
function ProtectedView({ isAdmin }: { isAdmin: boolean }) {
  useCopilotAdditionalInstructions({
    available: isAdmin ? "enabled" : "disabled",
    instructions: "The user is an admin. They can access all data.",
  });
}
```

### Options

```ts
interface UseCopilotAdditionalInstructionsOptions {
  instructions: string;                // Required — the instruction text
  available?: "enabled" | "disabled";  // Toggle availability (default: "enabled")
}
```

### How instructions are formatted

Multiple instructions from different components are combined into the system prompt:

```
You are a helpful assistant.
Additionally, follow these instructions:
- Do not answer questions about the weather.
- The user is an admin. They can access all data.
```

### How it works

Internally appends to the `additionalInstructions` array in the copilot context on mount, and removes on unmount. When `available === "disabled"`, the instruction is not added.

---

## 4. useMakeCopilotDocumentReadable

Provide a document (large text content with metadata) as context. For document-level context that needs a name, source URL, or other pointer metadata.

```tsx
import { useMakeCopilotDocumentReadable } from "@copilotkit/react-core";

function DocumentViewer({ doc }) {
  useMakeCopilotDocumentReadable(
    {
      id: doc.id,
      name: doc.title,
      sourceApplication: "docs-app",
      getContents: () => ({ contents: doc.content }),
    },
    ["general"],  // categories
    [doc.id],     // dependencies
  );

  return <div>{doc.content}</div>;
}
```

### Signature

```ts
function useMakeCopilotDocumentReadable(
  document: DocumentPointer,
  categories?: string[],
  dependencies?: any[],
): string | undefined;
```

### DocumentPointer type

```ts
interface DocumentPointer {
  id: string;
  name: string;
  sourceApplication: string;
  getContents: () => { contents: string };
}
```

Returns a document ID. Uses `addDocumentContext` / `removeDocumentContext` internally.

---

## 5. CopilotTask (class)

Not a hook — an imperative class for running one-off tasks. Useful for button-triggered actions that should use the copilot's context and actions.

```tsx
import { CopilotTask, useCopilotContext } from "@copilotkit/react-core";

function GenerateButton() {
  const context = useCopilotContext();

  const generateReport = async () => {
    const task = new CopilotTask({
      instructions: "Generate a summary report of all employees",
    });
    await task.run(context);
  };

  return <button onClick={generateReport}>Generate Report</button>;
}
```

### With custom actions

```tsx
const task = new CopilotTask({
  instructions: "Set a random greeting message",
  actions: [
    {
      name: "setMessage",
      description: "Set the message",
      argumentAnnotations: [
        { name: "message", type: "string", description: "The message to display", required: true },
      ],
    },
  ],
});

await task.run(context, action);
```

### Config

```ts
interface CopilotTaskConfig {
  instructions: string;                    // Required — task instructions
  actions?: FrontendAction<any>[];         // Additional actions for this task
  includeCopilotReadable?: boolean;        // Include readable context (default: true)
  includeCopilotActions?: boolean;         // Include registered actions (default: true)
  forwardedParameters?: ForwardedParametersInput;  // LLM parameters (e.g., temperature)
}
```

### How it works

1. Collects context from `useCopilotReadable` (if `includeCopilotReadable` is true)
2. Collects actions from `useCopilotAction` / `useFrontendTool` (if `includeCopilotActions` is true)
3. Sends a single request to the runtime with `toolChoice: "required"` (forces the LLM to call an action)
4. Executes any returned function calls via the function call handler

---

## 6. Common Patterns

### Context that updates with state

```tsx
function Dashboard({ data }) {
  // Automatically re-registers when data changes
  useCopilotReadable({
    description: "Current dashboard metrics",
    value: data,
  });
}
```

### Page-level instructions

```tsx
function CheckoutPage() {
  useCopilotAdditionalInstructions({
    instructions: "The user is on the checkout page. Help them complete their purchase. Do not suggest other products.",
  });

  useCopilotReadable({
    description: "Current cart items",
    value: cartItems,
  });
}
```

### Conditional context based on feature flags

```tsx
function FeatureGatedContext({ features }) {
  useCopilotReadable({
    description: "Premium analytics data",
    value: analyticsData,
    available: features.premiumAnalytics ? "enabled" : "disabled",
  });
}
```

### Custom serialization

```tsx
useCopilotReadable({
  description: "Complex data structure",
  value: complexData,
  convert: (description, value) => {
    return `${description}: ${value.toSummaryString()}`;
  },
});
```

---

## 7. v2 Architecture Note

The v1 hooks (`useCopilotReadable`, `useCopilotAdditionalInstructions`) **already bridge to v2 core internally**. `useCopilotReadable` imports `useCopilotKit()` from `@copilotkitnext/react` and calls `copilotkit.addContext()` on the v2 `CopilotKitCore` instance.

There are **no separate v2 exports** of these hooks. Users import from `@copilotkit/react-core` regardless of whether the backend is v1 or v2 architecture.

### v2 Core API (for advanced/Angular usage)

If working with the v2 core directly (e.g., Angular, or custom React wrappers), the primitives are:

```ts
// CopilotKitCore (from @copilotkitnext/core)
core.addContext({ description: string, value: string }): string   // returns context ID
core.removeContext(id: string): void
core.context  // Record<string, { description, value }> — all registered context
```

Angular equivalent:

```ts
// Angular config via provideCopilotKit
import { provideCopilotKit } from "@copilotkitnext/angular";

provideCopilotKit({
  runtimeUrl: "/api/copilotkit",
  properties: { userId: "123" },  // custom properties sent with requests
});
```

In v2, context registered via `addContext()` is forwarded to agents as part of the AG-UI run request, same as v1.
