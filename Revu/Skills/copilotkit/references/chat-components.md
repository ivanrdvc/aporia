# Chat Components

CopilotChat, CopilotSidebar, CopilotPopup — the three chat UI layouts. Same core, different chrome.

---

## Component Variants

| Component | Layout | Children | Toggle |
|---|---|---|---|
| `CopilotChat` | Raw panel, no chrome | No | No |
| `CopilotSidebar` | Collapsible side panel | Wraps children | Keyboard shortcut |
| `CopilotPopup` | Floating overlay | Lives alongside | Toggle button |

All three share common props. Use `CopilotChat` when embedding in your own container, `CopilotSidebar` for app-level layout, `CopilotPopup` for floating assistant.

---

## v1.x Props

### Shared Props (Chat / Sidebar / Popup)

```ts
{
  instructions?: string;           // System message for the copilot
  labels?: CopilotChatLabels;      // UI text labels (see customization.md)
  icons?: CopilotChatIcons;        // Custom icon overrides (see customization.md)
  className?: string;              // CSS class on root element
  children?: React.ReactNode;      // App content (Sidebar wraps this)
  makeSystemMessage?: (instructions: string) => string;
  showResponseButton?: boolean;    // Show suggestion buttons
  onSubmitMessage?: (message: string) => void;  // Intercept user messages

  // Custom sub-component overrides
  Messages?: React.ComponentType<MessagesProps>;
  Input?: React.ComponentType<InputProps>;
  ResponseButton?: React.ComponentType<SuggestionsProps>;
  Header?: React.ComponentType<HeaderProps>;
  AssistantMessage?: React.ComponentType<AssistantMessageProps>;
  UserMessage?: React.ComponentType<UserMessageProps>;
  ErrorMessage?: React.ComponentType<ErrorMessageProps>;
  RenderMessage?: React.ComponentType<RenderMessageProps>;
  ImageRenderer?: React.ComponentType<ImageRendererProps>;
  SuggestionsList?: React.ComponentType<RenderSuggestionsListProps>;
  markdownTagRenderers?: ComponentsMap;
}
```

### Sidebar-Specific Props

```ts
{
  clickOutsideToClose?: boolean;  // Default: true
  shortcut?: string;              // Keyboard shortcut to toggle
}
```

### Popup-Specific Props

```ts
{
  clickOutsideToClose?: boolean;
  hitEscapeToClose?: boolean;
  shortcut?: string;
}
```

### Key Sub-Component Prop Types

- `AssistantMessageProps` — `message`, `messages`, `isCurrentMessage`, `isLoading`, `isGenerating`, `onRegenerate`, `onCopy`, `onThumbsUp`, `onThumbsDown`, `feedback`, `markdownTagRenderers`
- `UserMessageProps` — `message`, `ImageRenderer`
- `MessagesProps` — `messages`, `inProgress`, all sub-component overrides
- `InputProps` — `inProgress`, `onSend`, `isVisible`, `onStop`, `onUpload`, `hideStopButton`, `chatReady`

---

## v2.x Props

### CopilotChat (v2)

```ts
{
  agentId?: string;        // Which agent to use (default: "default")
  threadId?: string;       // Conversation thread ID
  labels?: Record<string, string>;  // UI text labels (via CopilotChatConfigurationProvider)
  autoScroll?: boolean;    // Auto-scroll to bottom
  className?: string;

  // Slot overrides (see customization.md for full slot system)
  chatView?: SlotValue;
  messageView?: SlotValue;
  input?: SlotValue;
  scrollView?: SlotValue;
  suggestionView?: SlotValue;
  welcomeScreen?: SlotValue | false;  // false to hide
}
```

### v2 Granular Component Exports

All importable from `@copilotkitnext/react`:

**Chat layout:**
- `CopilotChat` — full wired chat (agent + view)
- `CopilotChatView` — display-only chat view (bring your own data)
- `CopilotSidebar` / `CopilotSidebarView`
- `CopilotPopup` / `CopilotPopupView`
- `CopilotModalHeader`

**Sub-components:**
- `CopilotChatInput` — text input with toolbar
- `CopilotChatAssistantMessage` — assistant message renderer
- `CopilotChatUserMessage` — user message renderer
- `CopilotChatMessageView` — message list
- `CopilotChatToolCallsView` — tool call display
- `CopilotChatSuggestionPill` / `CopilotChatSuggestionView` — suggestion UI
- `CopilotChatToggleButton` (+ open/close icon variants)
- `CopilotChatAudioRecorder` — voice input
- `CopilotChatScrollView` — scroll container
- `CopilotChatWelcomeScreen` — initial welcome screen

**Utility:**
- `WildcardToolCallRender` — catch-all tool call renderer
- `CopilotKitInspector` — dev inspector
- `MCPAppsActivityRenderer` — MCP apps activity display

---

## Usage Patterns

### Sidebar with content

```tsx
// v1
<CopilotSidebar instructions="Help the user with their tasks.">
  <MyApp />
</CopilotSidebar>

// v2
<CopilotKitProvider runtimeUrl="/api/copilotkit">
  <CopilotSidebar agentId="assistant">
    <MyApp />
  </CopilotSidebar>
</CopilotKitProvider>
```

### Popup overlay

```tsx
// v1
<CopilotPopup
  labels={{ title: "Assistant" }}
  shortcut="ctrl+/"
/>

// v2
<CopilotPopup agentId="assistant" />
```

### Raw chat in custom container

```tsx
// v1
<div className="my-chat-container">
  <CopilotChat instructions="..." />
</div>

// v2
<div className="my-chat-container">
  <CopilotChat agentId="assistant" />
</div>
```

### Multiple agents

```tsx
// v2 — different agents in different panels
<CopilotChat agentId="primary-assistant" />
<CopilotChat agentId="support-assistant" />
```

---

## Observability Hooks (v1, premium)

Available on CopilotChat/Sidebar/Popup when `publicApiKey` is set:

```ts
interface CopilotObservabilityHooks {
  onMessageSent?: (message: string) => void;
  onChatMinimized?: () => void;
  onChatExpanded?: () => void;
  onMessageRegenerated?: (messageId: string) => void;
  onMessageCopied?: (content: string) => void;
  onFeedbackGiven?: (messageId: string, type: "thumbsUp" | "thumbsDown") => void;
  onChatStarted?: () => void;
  onChatStopped?: () => void;
  onError?: (errorEvent: CopilotErrorEvent) => void;
}
```
