---
keywords: [bug, JsonElement, forwardedProps, agent-framework, CopilotKit]
status: resolved
---

# ForwardedProps JsonElement Bug

## Problem

`forwardedProps` passed from CopilotKit frontend arrives as `undefined` in .NET agent.

## Flow

```
CopilotKit (properties prop)
  → Express (GraphQL variables.properties)
  → .NET Backend (forwardedProps in JSON body)
```

Request body correctly contains:
```json
{
  "forwardedProps": { "companyId": "11" },
  "messages": [...]
}
```

But agent receives `ValueKind: Undefined`.

## Root Cause

In `AGUIEndpointRouteBuilderExtensions.cs` (line 59):

```csharp
["ag_ui_forwarded_properties"] = input.ForwardedProperties,  // Bug
```

`ForwardedProperties` is a `JsonElement` (struct referencing request body's JsonDocument). After request body stream is disposed, the JsonElement becomes invalid.

## Fix

Clone the JsonElement before storing:

```csharp
["ag_ui_forwarded_properties"] = input.ForwardedProperties.Clone(),
```

## Frontend (works correctly)

```tsx
<CopilotKit properties={{ companyId }} ... >
```
