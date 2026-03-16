using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Revu.Git;
using Revu.Infra.Cosmos;
using Revu.Review;

namespace Revu.Functions;

public class ChatFunction(
    IServiceProvider sp,
    Reviewer reviewer,
    IReviewStore reviewStore,
    ILogger<ChatFunction> logger)
{
    [Function("ChatProcessor")]
    public async Task Run([QueueTrigger("chat-queue")] ChatRequest req)
    {
        logger.LogInformation("Processing chat for PR #{PrId}, comment {CommentId}", req.Review.PullRequestId, req.CommentId);

        var git = sp.GetRequiredKeyedService<IGitConnector>(req.Review.Provider);

        var threadTask = git.GetChatThreadContext(req.Review, req.CommentId);
        var snapshotTask = reviewStore.GetLatestSnapshotAsync(req.Review.RepositoryId, req.Review.PullRequestId);
        await Task.WhenAll(threadTask, snapshotTask);

        var threadContext = threadTask.Result;
        var snapshot = snapshotTask.Result;

        if (threadContext is null)
        {
            logger.LogInformation("Comment {CommentId} is not on a Revu thread and has no @revu mention, skipping", req.CommentId);
            return;
        }

        var reply = await reviewer.Chat(req.Review, git, snapshot, threadContext, req.UserMessage);
        await git.PostChatReply(req.Review, threadContext.ThreadId, reply);

        logger.LogInformation("Posted chat reply to thread {ThreadId} for PR #{PrId}", threadContext.ThreadId, req.Review.PullRequestId);
    }
}
