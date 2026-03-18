---
keywords: [thread, restore, messages, setMessages, useAgent, CosmosDB, JsonProperty, MS Agent Framework]
---

# Thread Restoration

How to restore saved conversation threads with CopilotKit 1.50+ and MS Agent Framework backend.

---

## The Problem

When restoring saved threads from CosmosDB, tool messages display "No data" or don't appear in chat.

Two issues:
1. **Backend**: CosmosDB field names are lowercase, but C# properties are PascalCase
2. **Frontend**: Messages need conversion from MS Agent Framework format to CopilotKit format

---

## Backend Fix: CosmosDB Field Mapping

MS CosmosChatMessageStore saves messages with lowercase field names. Add `[JsonProperty]` attributes:

```csharp
public class CosmosMessageDocument
{
    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    [JsonProperty("conversationId")]
    public string ConversationId { get; set; } = string.Empty;

    [JsonProperty("timestamp")]
    public long Timestamp { get; set; }

    [JsonProperty("messageId")]
    public string MessageId { get; set; } = string.Empty;

    [JsonProperty("role")]
    public string Role { get; set; } = string.Empty;

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;
}
```

Return raw `Message` field - let frontend parse the MS Agent Framework JSON.

---

## Frontend Fix: Message Format Conversion

MS Agent Framework stores messages with nested `Contents` array containing typed items:

```json
{
  "Contents": [
    { "$type": "text", "Text": "Hello" },
    { "$type": "functionCall", "Name": "get_employees", "CallId": "123", "Arguments": "{}" },
    { "$type": "functionResult", "CallId": "123", "Result": [...] }
  ]
}
```

CopilotKit expects flat message format:

```typescript
// User/Assistant messages
{ id: string, role: 'user' | 'assistant', content: string, toolCalls?: [...] }

// Tool result messages  
{ id: string, role: 'tool', content: string, toolCallId: string }
```

### Conversion Function

```typescript
export function convertToCopilotMessages(messages: BackendMessage[]): CopilotMessage[] {
  return messages.map(msg => {
    const parsed = tryParseJson<AgentFrameworkMessage>(msg.content);
    
    if (!parsed?.Contents) {
      if (msg.role === 'tool') {
        return { id: msg.id, role: 'tool', content: msg.content || '', toolCallId: '' };
      }
      return { id: msg.id, role: msg.role, content: msg.content || '' };
    }

    if (msg.role === 'assistant') {
      const text = extractText(parsed.Contents);
      const funcCalls = parsed.Contents.filter(c => 
        c.$type === 'functionCall' || c.$type === 'functioncall'
      );
      
      if (funcCalls.length === 0) {
        return { id: msg.id, role: 'assistant', content: text };
      }
      
      const toolCalls = funcCalls.map(fc => ({
        id: fc.CallId || '',
        type: 'function',
        function: { name: fc.Name || '', arguments: fc.Arguments || '{}' },
      }));
      return { id: msg.id, role: 'assistant', content: text, toolCalls };
    }

    if (msg.role === 'tool') {
      const funcResult = parsed.Contents.find(c => 
        c.$type === 'functionResult' || c.$type === 'functionresult'
      );
      return {
        id: msg.id,
        role: 'tool',
        content: funcResult?.Result ? JSON.stringify(funcResult.Result) : '',
        toolCallId: funcResult?.CallId || '',
      };
    }

    return { id: msg.id, role: msg.role, content: extractText(parsed.Contents) };
  });
}
```

---

## Restoring Thread in UI

Use `useAgent` from v2 API to set messages:

```tsx
import { useCopilotContext } from "@copilotkit/react-core";
import { useAgent } from "@copilotkit/react-core/v2";
import { convertToCopilotMessages } from "../utils/messages";

const { agent } = useAgent({ agentId: "Teammate" });
const { setThreadId } = useCopilotContext();

const handleThreadClick = async (threadId: string) => {
  const messages = await loadThread(threadId);
  setThreadId(threadId);
  
  const copilotMessages = convertToCopilotMessages(messages);
  agent.setMessages(copilotMessages);
};
```

---

## Render Function: Live vs Restored

Tool render functions receive different args for live calls vs restored threads:

- **Live call**: `{ tool: "get_employees", target: "preview", title: "Employees" }`
- **Restored thread**: `{ data: [...], dataType: "employees", target: "preview", title: "Employees" }`

Handle both:

```tsx
render: ({ args, status }) => {
  const { tool, target, title, data: argsData, dataType } = args;
  
  let dataArray = [];
  let type = dataType || "employees";
  
  // Restored threads have data inline
  if (argsData && Array.isArray(argsData)) {
    dataArray = argsData;
  } 
  // Live calls need lookup from messages
  else if (tool) {
    const data = findToolResult(messagesRef.current, tool);
    if (data) {
      dataArray = parseToolData(data);
      type = TOOL_TO_TYPE[tool];
    }
  }
  
  // ... render with dataArray
}
```

---

## Custom CopilotSidebar Components

If using custom `Input` or `UserMessage` props on CopilotSidebar, ensure types are compatible:

```tsx
// ❌ Required prop doesn't match CopilotKit's optional
interface ChatDisplayProps {
  message: any;  // Required
}

// ✅ Make optional to match UserMessageProps
interface ChatDisplayProps {
  message?: any;  // Optional
}
```

---

## Key Files

| File | Purpose |
|------|---------|
| `CosmosMessageDocument.cs` | Backend DTO with JsonProperty attributes |
| `utils/messages.ts` | Frontend message conversion functions |
| `ThreadSideBar/index.tsx` | Thread list and restore logic |
| `CopilotIntegration.tsx` | Tool render handling for both formats |
