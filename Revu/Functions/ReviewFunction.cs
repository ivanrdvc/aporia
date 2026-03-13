using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using Revu.Git;
using Revu.Infra.Cosmos;
using Revu.Infra.Telemetry;
using Revu.Review;

namespace Revu.Functions;

public class ReviewFunction(
    IGitConnector git,
    Reviewer reviewer,
    IReviewStore reviewStore,
    ILogger<ReviewFunction> logger)
{
    public const string FunctionName = "ReviewProcessor";

    [Function(FunctionName)]
    public async Task Run([QueueTrigger("review-queue")] ReviewRequest req)
    {
        logger.LogInformation("Processing review for PR #{PrId} in {Project}", req.PullRequestId, req.Project);

        var config = await git.GetConfig(req);
        var diff = await git.GetDiff(req, config);

        Telemetry.RecordReview(req, diff);

        if (diff.Files.Count == 0)
        {
            logger.LogInformation("No new changes for PR #{PrId}, skipping review", req.PullRequestId);
            await reviewStore.SaveAsync(req.RepositoryId, req.PullRequestId, diff.Cursor, ReviewStatus.Skipped, 0, req.ConversationId);
            return;
        }

        var findings = await reviewer.Review(req, diff, config);
        await git.PostReview(req, diff, findings);

        logger.LogInformation("Posted {Count} findings for PR #{PrId}", findings.Findings.Count, req.PullRequestId);
        await reviewStore.SaveAsync(req.RepositoryId, req.PullRequestId, diff.Cursor, ReviewStatus.Completed, findings.Findings.Count, req.ConversationId);
    }
}
