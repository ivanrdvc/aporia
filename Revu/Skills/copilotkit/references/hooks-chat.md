# Hooks — Chat

Hooks for controlling the conversation experience. Covers `useCopilotChat`, `useCopilotChatHeadless_c`, and `useCopilotChatSuggestions`.

---

## Quick Reference

| Hook | What it does | Premium? | Package |
|---|---|---|---|
| `useCopilotChat` | Lightweight programmatic chat control | No | `@copilotkit/react-core` |
| `useCopilotChatHeadless_c` | Full headless UI with message access | Yes (`publicApiKey`) | `@copilotkit/react-core` |
| `useCopilotChatSuggestions` | AI-generated or static chat suggestions | No | `@copilotkit/react-core` |

---

## 1. useCopilotChat

Lightweight hook for programmatic chat control. Use this to control prebuilt chat components (`CopilotChat`, `CopilotSidebar`, `CopilotPopup`) without building custom UI. Open source — no `publicApiKey` required.

### Use cases

- Send messages programmatically (background operations, button triggers)
- Control prebuilt components (stop generation, reset, reload)
- Fire-and-forget messaging

```tsx
import { useCopilotChat } from "@copilotkit/react-core";
import { TextMessage, MessageRole } from "@copilotkit/runtime-client-gql";

function ChatController() {
  const { appendMessage, stopGeneration, reset, isLoading } = useCopilotChat();

  const sendMessage = async (content: string) => {
    await appendMessage(
      new TextMessage({ role: MessageRole.User, content })
    );
  };

  return (
    <div>
      <button onClick={() => sendMessage("Hello!")} disabled={isLoading}>Send</button>
      <button onClick={stopGeneration}>Stop</button>
      <button onClick={reset}>Reset</button>
    </div>
  );
}
```

### Return values

```ts
interface UseCopilotChatReturn {
  visibleMessages: DeprecatedGqlMessage[];  // Deprecated — old non-AG-UI format
  appendMessage: (message: DeprecatedGqlMessage, options?) => Promise<void>;  // Deprecated — use sendMessage via headless
  reloadMessages: (messageId: string) => Promise<void>;  // Regenerate response for a message
  stopGeneration: () => void;              // Stop current generation
  reset: () => void;                       // Clear all messages, reset state
  isLoading: boolean;                      // Whether generating a response
  runChatCompletion: () => Promise<Message[]>;  // Manually trigger completion
  mcpServers: MCPServerConfig[];           // Current MCP server configs
  setMcpServers: (servers: MCPServerConfig[]) => void;  // Update MCP configs
}
```

**Note:** `visibleMessages` and `appendMessage` use the deprecated `DeprecatedGqlMessage` format. For AG-UI `Message` format, use `useCopilotChatHeadless_c`.

### What it does NOT return

`useCopilotChat` deliberately excludes premium features: `messages` (AG-UI format), `sendMessage`, `suggestions`, `setSuggestions`, `generateSuggestions`, `isLoadingSuggestions`, `resetSuggestions`, `interrupt`, `setMessages`, `deleteMessage`. Use `useCopilotChatHeadless_c` for these.

---

## 2. useCopilotChatHeadless_c (Premium)

Full headless chat hook for building completely custom UI. Returns everything `useCopilotChat` does, plus direct message access, suggestions, and interrupt handling.

**Requires `publicApiKey`** — sign up free at [cloud.copilotkit.ai](https://cloud.copilotkit.ai).

```tsx
import { CopilotKit, useCopilotChatHeadless_c } from "@copilotkit/react-core";

function App() {
  return (
    <CopilotKit publicApiKey="your-free-public-license-key" runtimeUrl="/api/copilotkit">
      <HeadlessChat />
    </CopilotKit>
  );
}

function HeadlessChat() {
  const { messages, sendMessage, isLoading, suggestions } = useCopilotChatHeadless_c();

  const handleSend = async () => {
    await sendMessage({
      id: crypto.randomUUID(),
      role: "user",
      content: "Hello!",
    });
  };

  return (
    <div>
      {messages.map(msg => <div key={msg.id}>{msg.content}</div>)}
      {suggestions.map(s => (
        <button key={s.title} onClick={() => sendMessage({ id: crypto.randomUUID(), role: "user", content: s.message })}>
          {s.title}
        </button>
      ))}
      <button onClick={handleSend} disabled={isLoading}>Send</button>
    </div>
  );
}
```

### Return values (all of useCopilotChat, plus:)

```ts
interface UseCopilotChatReturn_c extends UseCopilotChatReturn {
  messages: Message[];                    // Messages in AG-UI format
  sendMessage: (message: Message, options?) => Promise<void>;  // Send AG-UI message
  setMessages: (messages: Message[] | DeprecatedGqlMessage[]) => void;  // Replace all messages
  deleteMessage: (messageId: string) => void;  // Remove message by ID
  suggestions: SuggestionItem[];          // Current suggestions
  setSuggestions: (suggestions: SuggestionItem[]) => void;  // Manually set suggestions
  generateSuggestions: () => Promise<void>;  // Trigger AI suggestion generation
  resetSuggestions: () => void;           // Clear all suggestions
  isLoadingSuggestions: boolean;          // Whether suggestions are generating
  interrupt: string | React.ReactElement | null;  // Interrupt content for HITL
}
```

### Behavior without publicApiKey

If `publicApiKey` is missing, the hook returns a non-functional fallback (empty arrays, no-op functions) and displays a banner error. It does NOT throw — it degrades gracefully.

---

## 3. useCopilotChatSuggestions

Configure AI-generated or static suggestions shown in the chat. Suggestions appear as clickable pills/buttons.

```tsx
import { useCopilotChatSuggestions } from "@copilotkit/react-core";

function ChatWithSuggestions() {
  useCopilotChatSuggestions({
    instructions: "Suggest helpful follow-up questions based on the conversation",
    maxSuggestions: 3,
  });

  return <CopilotChat />;
}
```

### v1 Configuration

```ts
interface CopilotChatSuggestionConfiguration {
  instructions: string;      // Prompt for the LLM to generate suggestions
  minSuggestions?: number;   // Minimum suggestions to generate (default: 1)
  maxSuggestions?: number;   // Maximum suggestions to generate (default: 3)
  className?: string;        // CSS class for suggestion buttons
}
```

### With useCopilotChatHeadless_c

```tsx
const { suggestions, isLoadingSuggestions, generateSuggestions } = useCopilotChatHeadless_c();

useCopilotChatSuggestions({
  instructions: "Suggest actions based on the current context",
  maxSuggestions: 3,
});

// suggestions array is populated automatically
// Can also call generateSuggestions() manually
```

### Suggestion type

```ts
interface CopilotChatSuggestion {
  title: string;      // Short text shown on the button
  message: string;    // Full message sent when clicked
  partial?: boolean;  // Whether still streaming
  isLoading?: boolean;
  className?: string;
}
```

---

## 4. v2 Architecture

### How v1 hooks bridge to v2

The v1 chat hooks internally use v2 core (`CopilotKitCore` from `@copilotkitnext/core`). The architecture:

- `useCopilotChat` → calls `useCopilotChatInternal` → uses `CopilotRuntimeClient` (v1 GQL) or v2 core agent API
- `useCopilotChatHeadless_c` → same internal, but exposes full return set
- `useCopilotChatSuggestions` → registers config via `addChatSuggestionConfiguration`

Users always import from `@copilotkit/react-core`. There are no separate v2 React exports for these hooks.

### v2 Suggestion Engine

In v2 core, the suggestion system is significantly more capable:

**Dynamic suggestions** (AI-generated, like v1):
```ts
{
  instructions: "Suggest follow-up questions",
  minSuggestions: 1,
  maxSuggestions: 3,
  available: "after-first-message",  // v2 only
  consumerAgentId: "default",        // v2 only — which agent's chat to show suggestions in
  providerAgentId: "default",        // v2 only — which agent generates suggestions
}
```

**Static suggestions** (pre-defined, v2 only):
```ts
{
  suggestions: [
    { title: "Get started", message: "Help me get started with the project" },
    { title: "Show examples", message: "Show me some example usage" },
  ],
  available: "before-first-message",  // Show only before first message
  consumerAgentId: "default",
}
```

### v2 Availability options

| Value | When shown |
|---|---|
| `"disabled"` | Never |
| `"before-first-message"` | Only when chat is empty (default for static) |
| `"after-first-message"` | Only after user has sent a message (default for dynamic) |
| `"always"` | Always visible |

### v2 Suggestion generation internals

The v2 `SuggestionEngine` uses a cloned agent to generate suggestions. It:
1. Clones the provider agent with the consumer agent's messages/state
2. Sends a prompt asking for suggestions via a `copilotkitSuggest` tool call
3. Streams results — partial suggestions show with `isLoading: true`
4. Finalizes when generation completes

### v2 Chat — useCopilotKit()

In v2, the primary hook for programmatic chat is `useCopilotKit()`:

```tsx
import { useCopilotKit } from "@copilotkitnext/react";

function MyComponent() {
  const { copilotkit } = useCopilotKit();

  // Access core directly
  copilotkit.addContext({ description: "...", value: "..." });
  copilotkit.runAgent({ agent });
  copilotkit.connectAgent({ agent });
}
```

This replaces v1's `useCopilotContext()` and provides direct access to the `CopilotKitCore` instance.

---

## 5. Common Patterns

### Programmatic send on button click

```tsx
const { appendMessage } = useCopilotChat();

<button onClick={() => appendMessage(
  new TextMessage({ role: MessageRole.User, content: "Summarize the page" })
)}>
  Summarize
</button>
```

### Auto-suggestions with context

```tsx
function SmartChat({ currentPage }) {
  useCopilotReadable({ description: "Current page", value: currentPage });

  useCopilotChatSuggestions({
    instructions: `Suggest 2-3 questions relevant to the ${currentPage} page`,
    maxSuggestions: 3,
  });

  return <CopilotSidebar />;
}
```

### Full headless custom UI

```tsx
function CustomChat() {
  const {
    messages, sendMessage, isLoading,
    suggestions, isLoadingSuggestions,
    stopGeneration, reset,
  } = useCopilotChatHeadless_c();

  return (
    <div className="custom-chat">
      <MessageList messages={messages} />
      <SuggestionBar suggestions={suggestions} loading={isLoadingSuggestions} />
      <InputBar
        onSend={(text) => sendMessage({ id: crypto.randomUUID(), role: "user", content: text })}
        onStop={stopGeneration}
        onReset={reset}
        disabled={isLoading}
      />
    </div>
  );
}
```

### MCP server configuration

```tsx
const { mcpServers, setMcpServers } = useCopilotChat();

// Add an MCP server dynamically
setMcpServers([
  ...mcpServers,
  { url: "http://localhost:3001/mcp", type: "http" },
]);
```
