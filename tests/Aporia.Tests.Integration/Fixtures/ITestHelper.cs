using Xunit.Abstractions;

namespace Aporia.Tests.Integration.Fixtures;

/// <summary>
/// Provider-agnostic test helper — builds requests,
/// fetches posted comments for assertions, and prints them for debugging.
/// </summary>
public interface ITestHelper
{
    ReviewRequest BuildRequest(int prId, string sourceBranch, string targetBranch = "refs/heads/main");
    Task<int> GetAporiaCommentCount(ReviewRequest req);
    Task PrintComments(ReviewRequest req, ITestOutputHelper output);
    Task<(int ThreadId, int CommentId)> PostCommentOnAporiaThread(ReviewRequest req, string message);
    Task<(int ThreadId, int CommentId, string Message)> FindLatestHumanComment(ReviewRequest req);
}
