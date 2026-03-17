using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Revu.Git;
using Revu.Review;

namespace Revu.Functions;

public class ChatFunction(
    IServiceProvider sp,
    Reviewer reviewer,
    ILogger<ChatFunction> logger)
{
    [Function("ChatProcessor")]
    public async Task Run([QueueTrigger("chat-queue")] ChatRequest req, CancellationToken ct)
    {
        logger.LogInformation("Processing chat for PR #{PrId}, comment {CommentId}", req.Review.PullRequestId, req.CommentId);

        var git = sp.GetRequiredKeyedService<IGitConnector>(req.Review.Provider);

        var threadContext = await git.GetChatThreadContext(req.Review, req.ThreadId, req.CommentId);

        if (threadContext is null)
        {
            logger.LogInformation("Thread {ThreadId} comment {CommentId} skipped — not a Revu thread and no @revu mention", req.ThreadId, req.CommentId);
            return;
        }

        var reply = await reviewer.Chat(req.Review, git, threadContext, req.UserMessage, ct);
        await git.PostChatReply(req.Review, threadContext.ThreadId, reply);

        logger.LogInformation("Posted chat reply to thread {ThreadId} for PR #{PrId}", threadContext.ThreadId, req.Review.PullRequestId);
    }
}
