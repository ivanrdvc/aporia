using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Revu.Git;
using Revu.Infra.Cosmos;
using Revu.Tests.Integration.Fixtures;

using Xunit.Abstractions;

namespace Revu.Tests.Integration;

public class ChatTests(
    AppFixture fixture,
    ITestOutputHelper output) : IntegrationTestBase(fixture, output)
{
    private ReviewRequest TestEvent => GetTestEvent();

    private ReviewRequest GetTestEvent()
    {
        var config = Services.GetRequiredService<IConfiguration>();
        var prId = config.GetValue<int>("TestTarget:PrId");
        var branch = config.GetValue<string>("TestTarget:Branch")!;
        return TestHelper.BuildRequest(prId, branch);
    }

    /// <summary>
    /// Self-contained test: reviews, posts a synthetic comment, runs chat, verifies reply.
    /// </summary>
    [Fact]
    public async Task Chat_ReplyToFinding_PostsResponse()
    {
        await ResetReviewState(TestEvent);

        var config = await Git.GetConfig(TestEvent);
        var diff = await Git.GetDiff(TestEvent, config);
        var prContext = await Git.GetPrContext(TestEvent);
        var result = await Reviewer.Review(TestEvent, diff, config, prContext);
        await Git.PostReview(TestEvent, diff, result);

        var snapshot = new ReviewSnapshot(
            result,
            diff.Files.Select(f => new ReviewedFile(f.Path, f.Kind)).ToList(),
            prContext.Title,
            prContext.Description);
        await ReviewStore.SaveAsync(TestEvent.RepositoryId, TestEvent.PullRequestId, diff.Cursor,
            ReviewStatus.Completed, result.Findings.Count, TestEvent.ConversationId, snapshot);

        Output.WriteLine($"Review posted: {result.Findings.Count} findings");

        var commentId = await TestHelper.PostCommentOnRevuThread(TestEvent, "Why did you flag this? Can you explain?");
        Output.WriteLine($"Posted test comment: {commentId}");

        var threadContext = await Git.GetChatThreadContext(TestEvent, commentId);
        Assert.NotNull(threadContext);

        var loadedSnapshot = await ReviewStore.GetLatestSnapshotAsync(TestEvent.RepositoryId, TestEvent.PullRequestId);

        var reply = await Reviewer.Chat(TestEvent, Git, loadedSnapshot, threadContext, "Why did you flag this? Can you explain?");
        Output.WriteLine($"Chat reply ({reply.Length} chars):\n{reply}");

        await Git.PostChatReply(TestEvent, threadContext.ThreadId, reply);
        Output.WriteLine("Reply posted to thread");

        await PrintThreads(TestEvent);
        Output.WriteLine($"\nSessions: {SessionDirectory}");
    }

    /// <summary>
    /// Interactive test: assumes a review already ran and the user posted a real comment.
    /// Finds the latest human comment on a Revu thread and runs chat against it.
    /// Use: TestTarget__CommentId=N to target a specific comment, or omit to auto-detect.
    /// </summary>
    [Fact]
    public async Task Chat_Interactive()
    {
        var configSvc = Services.GetRequiredService<IConfiguration>();
        var explicitCommentId = configSvc.GetValue<int?>("TestTarget:CommentId");

        int commentId;
        string userMessage;

        if (explicitCommentId is > 0)
        {
            commentId = explicitCommentId.Value;
            Output.WriteLine($"Using explicit comment ID: {commentId}");
            userMessage = ""; // will be filled from thread context
        }
        else
        {
            // Auto-detect: find latest human comment on a Revu thread
            (commentId, userMessage) = await TestHelper.FindLatestHumanComment(TestEvent);
            Output.WriteLine($"Auto-detected comment ID: {commentId}");
        }

        var threadContext = await Git.GetChatThreadContext(TestEvent, commentId);
        Assert.NotNull(threadContext);
        Output.WriteLine($"Thread: {threadContext.ThreadId}, file: {threadContext.FilePath}, fingerprint: {threadContext.Fingerprint}");

        // Use the actual message from the thread if we didn't get it from auto-detect
        if (string.IsNullOrEmpty(userMessage))
            userMessage = threadContext.ThreadMessages.LastOrDefault(m => !m.StartsWith("<!-- revu:chat -->")) ?? "";

        Output.WriteLine($"User message: {userMessage}");

        var snapshot = await ReviewStore.GetLatestSnapshotAsync(TestEvent.RepositoryId, TestEvent.PullRequestId);
        Output.WriteLine($"Snapshot: {(snapshot is not null ? $"{snapshot.Result.Findings.Count} findings" : "none")}");

        var reply = await Reviewer.Chat(TestEvent, Git, snapshot, threadContext, userMessage);
        Output.WriteLine($"Chat reply ({reply.Length} chars):\n{reply}");

        await Git.PostChatReply(TestEvent, threadContext.ThreadId, reply);
        Output.WriteLine("Reply posted to thread");

        await PrintThreads(TestEvent);
        Output.WriteLine($"\nSessions: {SessionDirectory}");
    }
}
