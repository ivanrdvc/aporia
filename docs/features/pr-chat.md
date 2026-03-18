# PR Chat

Conversational follow-up on pull request comments. Developers can ask Aporia questions about
its review findings or mention `@aporia` anywhere on a PR to start a conversation.

## How it activates

Chat triggers in two ways:

1. **Reply on a Aporia thread** — any reply to a thread Aporia created (finding or summary)
   activates chat automatically. No `@aporia` mention needed.
2. **`@aporia` mention** — mentioning `@aporia` in any PR comment (new thread or reply) activates
   chat, even on threads Aporia didn't create.

Comments without `@aporia` on non-Aporia threads are ignored.

## Pipeline

```
ADO comment webhook → WebhookAdoComment → chat-queue → ChatProcessor → LLM → PostChatReply
```

`WebhookAdoComment` receives the `ms.vss-code.git-pullrequest-comment-event` payload, extracts
the thread and comment IDs, and enqueues a `ChatRequest`. `ChatProcessor` fetches the thread
context from ADO, builds a system prompt with the thread anchor and prior messages, and runs
the chat agent.

## Session continuity

Chat continues the same Cosmos session as the review. Both use `conversationId` =
`pr-{repositoryId}-{pullRequestId}`. When a PR has been reviewed, the chat agent picks up with
full context: the review's system prompt, diffs, findings, and tool call history. When no prior
review exists, the agent starts fresh with its own tools.

## Filtering

Comments are filtered at three layers to minimize waste and prevent loops:

1. **Webhook gate** (`AdoCommentWebhook.ToChatRequest`):
   - Aporia's own comments (`<!-- aporia:review -->`, `<!-- aporia:chat -->`) are dropped before enqueue.
   - Top-level comments (`ParentCommentId == 0`) without `@aporia` are dropped — new threads
     need an explicit mention. Replies (`ParentCommentId > 0`) are let through because the
     webhook payload doesn't include thread properties, so we can't tell if it's a Aporia thread.
2. **Webhook function** (`WebhookFunction.RunAdoComment`): unregistered or disabled repos
   are dropped before enqueue.
3. **Connector gate** (`AdoConnector.GetChatThreadContext`): any comment starting with
   `<!-- aporia:` is rejected. On non-Aporia threads, `@aporia` mention is required.

The markers are HTML comments — invisible in rendered PR views.

## Thread context

The chat agent receives:

- **Thread anchor**: file path and line number (if the thread is anchored to code)
- **Thread conversation**: all comments in the thread before the first `<!-- aporia:chat -->`
  reply, to avoid duplicating what's already in the Cosmos session history

The agent has the same tools as the reviewer (`FetchFile`, `ListDirectory`, `SearchCode`,
`QueryCodeGraph`) and can investigate the codebase to answer questions.

## Webhook setup

Requires an ADO service hook subscription:

- **Event type**: `ms.vss-code.git-pullrequest-comment-event`
- **URL**: `{host}/api/webhook/ado/comment`
- **Scope**: per-repository or project-wide

The PR created/updated webhook (`git.pullrequest.created` / `git.pullrequest.updated`) is
separate and only triggers reviews, not chat.

## Configuration

Chat is enabled via the `Aporia__EnableChat` setting. When disabled, `WebhookAdoComment` returns
200 without enqueuing.

```json
{ "Aporia": { "EnableChat": true } }
```
