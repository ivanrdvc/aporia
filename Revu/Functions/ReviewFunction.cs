using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using Revu.Git;
using Revu.Infra.Cosmos;
using Revu.Infra.Middleware;
using Revu.Review;

namespace Revu.Functions;

public class ReviewFunction(IGitConnector git, Reviewer reviewer, ILogger<ReviewFunction> logger)
{
    public const string FunctionName = "ReviewProcessor";

    [Function(FunctionName)]
    public async Task Run([QueueTrigger("review-queue")] ReviewRequest req, FunctionContext context)
    {
        logger.LogInformation("Processing review for PR #{PrId} in {Project}", req.PullRequestId, req.Project);
        context.Items[ReviewContext.RequestKey] = req;

        var config = await git.GetConfig(req);
        var diff = await git.GetDiff(req, config);
        ReviewContext.SetDiffStats(context, diff);

        if (diff.Files.Count == 0)
        {
            logger.LogInformation("No new changes for PR #{PrId}, skipping review", req.PullRequestId);
            ReviewContext.Set(context, req, ReviewStatus.Skipped, diff.IterationId, findingsCount: 0);
            return;
        }

        var findings = await reviewer.Review(req, diff, config);
        await git.PostReview(req, diff, findings);

        logger.LogInformation("Posted {Count} findings for PR #{PrId}", findings.Findings.Count, req.PullRequestId);
        ReviewContext.Set(context, req, ReviewStatus.Completed, diff.IterationId, findings.Findings.Count);
    }
}
