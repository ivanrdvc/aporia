# Customization

Theming, styling, and deep UI customization for CopilotKit chat components. v1 uses CSS variables + sub-component props. v2 uses a 4-level slot system with Tailwind.

---

## v1.x Customization

### CSS Variables

Apply via `style` prop on `CopilotKit` provider or any parent element:

```tsx
<div style={{
  "--copilot-kit-primary-color": "#4f46e5",
  "--copilot-kit-secondary-color": "#f3f4f6",
  "--copilot-kit-background-color": "#ffffff",
  "--copilot-kit-response-button-color": "#4f46e5",
  "--copilot-kit-response-button-background-color": "#e0e7ff",
  "--copilot-kit-separator-color": "#e5e7eb",
  "--copilot-kit-scrollbar-color": "#d1d5db",
} as React.CSSProperties}>
  <CopilotSidebar />
</div>
```

### CSS Classes

Override for fine-grained control:

```
.copilotKitSidebar, .copilotKitPopup, .copilotKitChat
.copilotKitHeader, .copilotKitMessages, .copilotKitInput
.copilotKitButton, .copilotKitResponseButton
.copilotKitMessage, .copilotKitAssistantMessage, .copilotKitUserMessage
.copilotKitMarkdown
```

(Inspect the DOM for exact class names — ~25 classes available.)

### Labels

```tsx
<CopilotSidebar
  labels={{
    title: "My Copilot",
    initial: "Hi! How can I help you?",
    placeholder: "Type your message...",
    stopGenerating: "Stop",
    regenerateResponse: "Try again",
  }}
/>
```

### Icons

```tsx
<CopilotSidebar
  icons={{
    openIcon: <MyOpenIcon />,
    closeIcon: <MyCloseIcon />,
    headerCloseIcon: <MyHeaderCloseIcon />,
    sendIcon: <MySendIcon />,
    activityIcon: <MyActivityIcon />,
    spinnerIcon: <MySpinnerIcon />,
    stopIcon: <MyStopIcon />,
    regenerateIcon: <MyRegenerateIcon />,
    pushToTalkIcon: <MyPTTIcon />,
  }}
/>
```

### Custom Sub-Components (v1)

Replace any part of the chat UI via component props:

```tsx
import { AssistantMessageProps } from "@copilotkit/react-ui";

function CustomAssistantMessage({ message, isLoading }: AssistantMessageProps) {
  const generativeUI = message?.generativeUI?.();
  return (
    <div className="my-msg">
      {generativeUI || <p>{message?.content}</p>}
      {isLoading && <span>Thinking...</span>}
    </div>
  );
}

<CopilotChat AssistantMessage={CustomAssistantMessage} />
```

### Markdown Tag Renderers (v1)

```tsx
<CopilotChat
  markdownTagRenderers={{
    reference: ({ children, ...props }) => (
      <a href={props.href} className="ref-link">{children}</a>
    ),
    chart: ({ children }) => <MyChartComponent data={children} />,
  }}
/>
```

---

## v2.x Slot System

v2 uses a 4-level customization system. Every slot accepts one of four value types:

### Level 1: Tailwind Class String

```tsx
<CopilotChat
  input="border-2 border-blue-500 rounded-xl"
  messageView="space-y-4 p-4"
/>
```

Classes merged with defaults via `tailwind-merge` (conflicts resolved intelligently).

### Level 2: Props Object

```tsx
<CopilotChat
  input={{
    className: "custom-input",
    autoFocus: false,
  }}
  messageView={{
    className: "custom-messages",
    assistantMessage: {
      onThumbsUp: (msg) => trackFeedback(msg.id, "positive"),
    },
  }}
/>
```

Props merged with defaults. Includes nested slot overrides.

### Level 3: Custom Component

```tsx
function CustomInput({ onSubmitMessage, isRunning, ...props }) {
  return (
    <div className="my-wrapper">
      <CopilotChatInput onSubmitMessage={onSubmitMessage} isRunning={isRunning} {...props} />
    </div>
  );
}

<CopilotChat input={CustomInput} />
```

Custom components receive all props that the default component would get.

### Level 4: Render Function (Children)

Full layout control via children render pattern:

```tsx
function CustomInput(props) {
  return (
    <CopilotChatInput {...props}>
      {({ textArea, sendButton, addMenuButton }) => (
        <div className="flex gap-2">
          {addMenuButton}
          <div className="flex-1">{textArea}</div>
          {sendButton}
        </div>
      )}
    </CopilotChatInput>
  );
}

<CopilotChat input={CustomInput} />
```

### Hiding Components

Return `null` from a component function:

```tsx
<CopilotChat
  input={{
    disclaimer: () => null,
    startTranscribeButton: () => null,
  }}
  messageView={{
    assistantMessage: {
      toolbar: () => null,
    },
  }}
/>
```

### Nested Slot Customization

Drill into deeply nested components:

```tsx
<CopilotChat
  messageView={{
    assistantMessage: {
      className: "bg-blue-50",
      toolbar: "border-t mt-2",
      copyButton: "text-blue-600",
      thumbsUpButton: () => null,  // hide
    },
    userMessage: "bg-blue-100 rounded-xl",
  }}
  input={{
    textArea: "text-lg",
    sendButton: "bg-green-500",
  }}
/>
```

---

## Full Slot Hierarchy

```
CopilotChat
├── chatView
│   ├── messageView
│   │   ├── assistantMessage
│   │   │   ├── markdownRenderer
│   │   │   ├── toolbar
│   │   │   ├── copyButton
│   │   │   ├── thumbsUpButton
│   │   │   ├── thumbsDownButton
│   │   │   ├── readAloudButton
│   │   │   ├── regenerateButton
│   │   │   └── toolCallsView
│   │   ├── userMessage
│   │   │   ├── messageRenderer
│   │   │   ├── toolbar
│   │   │   ├── copyButton
│   │   │   ├── editButton
│   │   │   └── branchNavigation
│   │   └── cursor
│   ├── scrollView
│   │   ├── scrollToBottomButton
│   │   └── feather
│   ├── input
│   │   ├── textArea
│   │   ├── sendButton
│   │   ├── startTranscribeButton
│   │   ├── cancelTranscribeButton
│   │   ├── finishTranscribeButton
│   │   ├── addMenuButton
│   │   ├── audioRecorder
│   │   └── disclaimer
│   ├── suggestionView
│   │   ├── container
│   │   └── suggestion
│   └── welcomeScreen
│       └── welcomeMessage
```

---

## v2 Labels

Configure via `CopilotChatConfigurationProvider`:

```tsx
import { CopilotChatConfigurationProvider } from "@copilotkitnext/react";

<CopilotChatConfigurationProvider
  labels={{
    chatInputPlaceholder: "Type a message...",
    assistantMessageToolbarCopyMessageLabel: "Copy",
    assistantMessageToolbarThumbsUpLabel: "Good response",
    chatDisclaimerText: "AI may produce inaccurate information.",
  }}
>
  <CopilotChat agentId="default" />
</CopilotChatConfigurationProvider>
```

Available label keys:

```
chatInputPlaceholder
chatInputToolbarStartTranscribeButtonLabel
chatInputToolbarCancelTranscribeButtonLabel
chatInputToolbarFinishTranscribeButtonLabel
chatInputToolbarAddButtonLabel
chatInputToolbarToolsButtonLabel
assistantMessageToolbarCopyCodeLabel
assistantMessageToolbarCopyCodeCopiedLabel
assistantMessageToolbarCopyMessageLabel
assistantMessageToolbarThumbsUpLabel
assistantMessageToolbarThumbsDownLabel
assistantMessageToolbarReadAloudLabel
assistantMessageToolbarRegenerateLabel
userMessageToolbarCopyMessageLabel
userMessageToolbarEditMessageLabel
chatDisclaimerText
```

---

## Type Reference

### SlotValue

```ts
type SlotValue<C extends React.ComponentType<any>> =
  | C                              // Custom component
  | string                         // Tailwind class string
  | Partial<React.ComponentProps<C>>;  // Props object
```

### renderSlot (internal)

```ts
function renderSlot(slot, DefaultComponent, props) {
  if (typeof slot === "string")    → <Default {...props} className={twMerge(props.className, slot)} />
  if (isReactComponent(slot))      → <slot {...props} />
  if (isPropsObject(slot))         → <Default {...props} {...slot} />
  else                             → <Default {...props} />
}
```

---

## Examples

### Dark Theme (v2)

```tsx
<CopilotChat
  className="bg-gray-900 text-white"
  messageView={{
    className: "p-4",
    assistantMessage: {
      className: "bg-gray-800 rounded-xl p-4",
      toolbar: "border-gray-700",
    },
    userMessage: "bg-blue-600 text-white rounded-2xl px-4 py-2",
  }}
  input={{
    className: "bg-gray-800 border-gray-700",
    sendButton: "bg-blue-600 hover:bg-blue-700",
  }}
/>
```

### Minimal Interface (v2)

```tsx
<CopilotChat
  welcomeScreen={false}
  input={{
    disclaimer: () => null,
    startTranscribeButton: () => null,
    addMenuButton: () => null,
  }}
  scrollView={{
    scrollToBottomButton: () => null,
    feather: () => null,
  }}
  messageView={{
    assistantMessage: { toolbar: () => null },
  }}
/>
```

### Feedback-Focused (v2)

```tsx
<CopilotChat
  messageView={{
    assistantMessage: {
      onThumbsUp: (msg) => {
        analytics.track("positive_feedback", { messageId: msg.id });
      },
      onThumbsDown: (msg) => {
        showFeedbackModal(msg);
      },
      toolbar: "bg-yellow-50 border border-yellow-200 rounded-lg p-2",
      thumbsUpButton: "text-green-600 hover:text-green-800",
      thumbsDownButton: "text-red-600 hover:text-red-800",
    },
  }}
/>
```
