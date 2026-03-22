using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Aporia.Git;
using Aporia.Review;

namespace Aporia.Functions;

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

        // Issue comments don't carry branch info — resolve from the PR before tools need it
        if (req.Review.SourceBranch is "")
        {
            var branches = await git.GetPrBranches(req.Review);
            if (branches is null)
            {
                logger.LogWarning("Could not resolve branches for PR #{PrId}, skipping", req.Review.PullRequestId);
                return;
            }

            req = req with { Review = req.Review with { SourceBranch = branches.Value.Source, TargetBranch = branches.Value.Target } };
        }

        var threadContext = await git.GetChatThreadContext(req);

        if (threadContext is null)
        {
            logger.LogInformation("Thread {ThreadId} comment {CommentId} skipped — not an Aporia thread and no @aporia mention", req.ThreadId, req.CommentId);
            return;
        }

        var reply = await reviewer.Chat(req.Review, git, threadContext, req.UserMessage, ct);
        await git.PostChatReply(req, reply);

        logger.LogInformation("Posted chat reply to thread {ThreadId} for PR #{PrId}", threadContext.ThreadId, req.Review.PullRequestId);
    }
}
