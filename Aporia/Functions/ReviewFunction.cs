using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Aporia.Git;
using Aporia.Infra;
using Aporia.Infra.Cosmos;
using Aporia.Infra.Telemetry;
using Aporia.Review;

namespace Aporia.Functions;

public class ReviewFunction(
    IServiceProvider sp,
    Reviewer reviewer,
    IReviewStore reviewStore,
    IOptions<AporiaOptions> options,
    ILogger<ReviewFunction> logger)
{
    public const string FunctionName = "ReviewProcessor";

    [Function(FunctionName)]
    public async Task Run([QueueTrigger("review-queue")] ReviewRequest req)
    {
        logger.LogInformation("Processing review for PR #{PrId} in {Project}", req.PullRequestId, req.Project);

        var git = sp.GetRequiredKeyedService<IGitConnector>(req.Provider);

        var config = await git.GetConfig(req);
        var diff = await git.GetDiff(req, config);

        Telemetry.RecordReview(req, diff, config.Review.Strategy ?? ReviewStrategy.Core);

        if (diff.Files.Count == 0)
        {
            logger.LogInformation("No new changes for PR #{PrId}, skipping review", req.PullRequestId);
            await reviewStore.SaveAsync(req, diff, ReviewStatus.Skipped);
            return;
        }

        var prContext = await git.GetPrContext(req);
        var findings = await reviewer.Review(req, diff, config, prContext);

        if (options.Value.EnablePostComments)
            await git.PostReview(req, diff, findings);

        logger.LogInformation("Review complete: {Count} findings for PR #{PrId}", findings.Findings.Count, req.PullRequestId);
        await reviewStore.SaveAsync(req, diff, ReviewStatus.Completed, findings);
    }
}
