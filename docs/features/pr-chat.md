# PR Chat

Conversational follow-up on pull request comments. Developers can ask Revu questions about
its review findings or mention `@revu` anywhere on a PR to start a conversation.

## How it activates

Chat triggers in two ways:

1. **Reply on a Revu thread** — any reply to a thread Revu created (finding or summary)
   activates chat automatically. No `@revu` mention needed.
2. **`@revu` mention** — mentioning `@revu` in any PR comment (new thread or reply) activates
   chat, even on threads Revu didn't create.

Comments without `@revu` on non-Revu threads are ignored.

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

## Self-reply prevention

Revu's own comments are filtered at two layers to prevent feedback loops:

1. **Webhook gate** (`AdoCommentWebhook.ToChatRequest`): review comments start with
   `<!-- revu:review -->`, chat replies start with `<!-- revu:chat -->`. Any comment starting
   with either marker is dropped before enqueue.
2. **Connector gate** (`AdoConnector.GetChatThreadContext`): any comment starting with
   `<!-- revu:` is rejected as a safety net, even if the webhook filter is bypassed.

Both markers are HTML comments — invisible in rendered PR views.

## Thread context

The chat agent receives:

- **Thread anchor**: file path and line number (if the thread is anchored to code)
- **Thread conversation**: all comments in the thread before the first `<!-- revu:chat -->`
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

Chat is enabled via the `Revu__EnableChat` setting. When disabled, `WebhookAdoComment` returns
200 without enqueuing.

```json
{ "Revu": { "EnableChat": true } }
```
