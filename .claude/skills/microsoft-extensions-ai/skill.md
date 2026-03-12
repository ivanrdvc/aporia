---
name: microsoft-extensions-ai
description: Use when working with Microsoft.Extensions.AI (MEAI) — chat clients, structured output, response formats, JSON schema, or IChatClient patterns. Trigger on mentions of Microsoft.Extensions.AI, MEAI, IChatClient, ChatClient, GetResponseAsync, structured output, ChatResponseFormat, or ForJsonSchema.
---

# Microsoft Extensions AI

Abstraction layer over LLM providers. `IChatClient`, structured output, function calling,
middleware. The Agent Framework (MAF) builds on top of it.

## Structured output

**Always prefer structured output over manual JSON parsing.**

### `GetResponseAsync<T>()` — fully typed (preferred for direct IChatClient calls)

```csharp
var response = await chatClient.GetResponseAsync<T>(prompt, options: new(), cancellationToken: ct);
var result = response.Result; // typed T
```

**Use `options:` as a named parameter** — the `string` overload is ambiguous with a
`JsonSerializerOptions` sibling and won't compile without it.

### `ChatResponseFormat.ForJsonSchema<T>()` — schema constraint only

Use when deserialization is handled separately (e.g. MAF agent pipeline returning
`response.Text`).

```csharp
ResponseFormat = ChatResponseFormat.ForJsonSchema<T>(),
// then: response.Text.TryParseJson<T>()
```

**OpenAI strict mode**: Set `AdditionalProperties = new() { ["strict"] = true }` on `ChatOptions`.
Without it, OpenAI uses best-effort schema conformance (no constrained decoding) and output may not parse.
