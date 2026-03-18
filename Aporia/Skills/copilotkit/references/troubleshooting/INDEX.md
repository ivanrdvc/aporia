# Troubleshooting

Investigations, bugs, patterns, and learnings.

| File | Description | Keywords |
|------|-------------|----------|
| [thread-restoration.md](thread-restoration.md) | Restoring saved threads with CopilotKit 1.50+ and MS Agent Framework: CosmosDB mapping, message format conversion, render function handling | thread, restore, messages, setMessages, CosmosDB, JsonProperty |
| [copilotkit-1.50-upgrade.md](copilotkit-1.50-upgrade.md) | Fix for CopilotKit 1.50 upgrade: backend tool results not in messages, use useAgent instead of useCopilotMessagesContext | copilotkit, upgrade, 1.50, graphql, useAgent, messages |
| [my-setup.md](my-setup.md) | My architecture: .NET backend + CopilotKit v2 direct connection, no Node.js middleware | setup, .NET, CopilotKit, architecture, v2 API |
| [removing-node-runtime.md](removing-node-runtime.md) | How to eliminate CopilotRuntime and connect directly to AG-UI backends | migration, v2 API, direct connection |
| [misc.md](misc.md) | Quick tips, small findings, one-liners | tips, misc |
| [llm-data-relay.md](llm-data-relay.md) | LLM token bottleneck when routing data through frontend tools - root cause + solutions | latency, tokens, show_data, streaming, filtering |
| [forwarded-props-bug.md](forwarded-props-bug.md) | JsonElement disposed bug in agent framework forwardedProps | bug, JsonElement, forwardedProps, agent-framework |
| [datacontent-state-snapshot-mismatch.md](datacontent-state-snapshot-mismatch.md) | DataContent not recognized as STATE_SNAPSHOT by CopilotKit | potential-issue, DataContent, STATE_SNAPSHOT, useCoAgentStateRender |
