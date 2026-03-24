namespace Aporia.DocWatch;

/// <summary>
/// Publishes documentation changes to a target repo — creates branches, pushes files,
/// and manages the persistent doc watch pull request.
/// </summary>
public interface IDocPublisher
{
    Task<string> CreateBranch(string repoId, string baseBranch, string newBranch, long? installationId = null);
    Task PushFiles(string repoId, string branch, IReadOnlyList<DocFile> files, string commitMessage, long? installationId = null);
    Task<int> CreatePullRequest(string repoId, string sourceBranch, string targetBranch, string title, string body, long? installationId = null);
    Task UpdatePullRequest(string repoId, int prNumber, string? title = null, string? body = null, long? installationId = null);
    Task<(int Number, string Branch, string Body)?> FindOpenPullRequest(string repoId, string label, long? installationId = null);
    Task AddComment(string repoId, int prNumber, string body, long? installationId = null);
}
