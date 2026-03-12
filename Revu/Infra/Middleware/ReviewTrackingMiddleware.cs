using System.Diagnostics;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Revu.Functions;
using Revu.Infra.Cosmos;

using static Revu.Infra.Telemetry.Telemetry;

namespace Revu.Infra.Middleware;

public class ReviewTrackingMiddleware : IFunctionsWorkerMiddleware
{
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        if (context.FunctionDefinition.Name != ReviewFunction.FunctionName)
        {
            await next(context);
            return;
        }

        var sw = Stopwatch.StartNew();
        Exception? pipelineError = null;

        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            pipelineError = ex;
            context.Items[ReviewContext.StatusKey] = ReviewStatus.Failed;
        }

        var tags = BuildTags(context);
        ReviewsProcessed.Add(1, tags);
        ReviewDuration.Record(sw.Elapsed.TotalSeconds, tags);
        RecordDiffAndFindings(context, tags);

        await TrySaveReviewEvent(context, sw.ElapsedMilliseconds);

        if (pipelineError is not null)
            throw pipelineError;
    }

    private static async Task TrySaveReviewEvent(FunctionContext context, long durationMs)
    {
        if (!context.Items.TryGetValue(ReviewContext.StatusKey, out var statusObj) || statusObj is not ReviewStatus status)
            return;

        var req = context.Items.TryGetValue(ReviewContext.RequestKey, out var r) ? r as ReviewRequest : null;
        if (req is null) return;

        var iterationId = context.Items.TryGetValue(ReviewContext.IterationIdKey, out var it) ? (int?)it : null;
        var findingsCount = context.Items.TryGetValue(ReviewContext.FindingsCountKey, out var fc) ? (int)fc : 0;

        try
        {
            var reviewStore = context.InstanceServices.GetRequiredService<IReviewStore>();
            var repoStore = context.InstanceServices.GetRequiredService<IRepoStore>();

            await reviewStore.SaveAsync(
                req.RepositoryId, req.PullRequestId, iterationId,
                status, findingsCount, durationMs, req.ConversationId);

            if (status != ReviewStatus.Failed)
                await repoStore.UpdateLastReviewedAsync(req.RepositoryId);
        }
        catch (Exception ex)
        {
            var logger = context.InstanceServices.GetRequiredService<ILogger<ReviewTrackingMiddleware>>();
            logger.LogWarning(ex, "Failed to persist review event for PR #{PrId}", req.PullRequestId);
        }
    }

    private static void RecordDiffAndFindings(FunctionContext context, TagList tags)
    {
        if (context.Items.TryGetValue(ReviewContext.DiffFilesKey, out var files))
            DiffFiles.Record((int)files, tags);
        if (context.Items.TryGetValue(ReviewContext.DiffSizeKey, out var size))
            DiffSize.Record((int)size, tags);
    }

    private static TagList BuildTags(FunctionContext context)
    {
        var tags = new TagList();
        var data = context.BindingContext.BindingData;

        if (data.TryGetValue("Project", out var project))
            tags.Add("project", project);
        if (data.TryGetValue("RepositoryId", out var repo))
            tags.Add("repository", repo);

        return tags;
    }
}

public static class ReviewContext
{
    public const string RequestKey = "review:request";
    public const string StatusKey = "review:status";
    public const string IterationIdKey = "review:iterationId";
    public const string FindingsCountKey = "review:findingsCount";
    public const string DiffFilesKey = "review:diffFiles";
    public const string DiffSizeKey = "review:diffSize";

    public static void SetDiffStats(FunctionContext context, Diff diff)
    {
        context.Items[DiffFilesKey] = diff.Files.Count;
        context.Items[DiffSizeKey] = diff.Files.Sum(f => f.Patch?.Length ?? 0);
    }

    public static void Set(FunctionContext context, ReviewRequest req, ReviewStatus status, int? iterationId, int findingsCount)
    {
        context.Items[RequestKey] = req;
        context.Items[StatusKey] = status;
        context.Items[IterationIdKey] = iterationId;
        context.Items[FindingsCountKey] = findingsCount;
    }
}
