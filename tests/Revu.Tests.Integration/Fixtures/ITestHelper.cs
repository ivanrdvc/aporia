using Xunit.Abstractions;

namespace Revu.Tests.Integration.Fixtures;

/// <summary>
/// Provider-agnostic test helper — builds requests, cleans up PR comments,
/// fetches posted comments for assertions, and prints them for debugging.
/// </summary>
public interface ITestHelper
{
    ReviewRequest BuildRequest(int prId, string sourceBranch, string targetBranch = "refs/heads/main");
    Task CleanComments(ReviewRequest req);
    Task<int> GetRevuCommentCount(ReviewRequest req);
    Task PrintComments(ReviewRequest req, ITestOutputHelper output);
}
